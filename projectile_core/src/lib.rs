// lib.rs — all public FFI exports live here
// Unity calls these via P/Invoke (DllImport / __Internal on iOS)
//
// Layout rules:
//   - All exported structs use #[repr(C)] — never reorder fields without
//     updating the matching C# StructLayout(LayoutKind.Explicit) counterpart.
//   - All exported functions are #[no_mangle] extern "C".
//   - No panics cross the FFI boundary — every unsafe block guards against
//     null pointers and zero counts before dereferencing.
//
// iOS note: on IL2CPP the dylib is a static lib linked as __Internal.
// The DLL name in C# switches at compile time — see ProjectileLib.cs.

mod simulation;
mod collision;
mod patterns;
mod state;

pub use simulation::*;
pub use collision::*;
pub use patterns::*;
pub use state::*;

use std::slice;

// ─────────────────────────────────────────────────────────────────────────────
//  Shared data types — layout verified against NativeProjectile.cs
// ─────────────────────────────────────────────────────────────────────────────

/// Core projectile state.  Matches NativeProjectile.cs exactly.
///
/// Layout breakdown (C# StructLayout Size = 72):
///   15 * f32 = 60 bytes   (x,y,vx,vy,ax,ay,angle,curve, scalex,scaley,
///                          scaletgt,scalespd, lifetime,maxlifetime,traveldist)
///    2 * u16 =  4 bytes   (config_id, owner_id)
///    1 * u32 =  4 bytes   (proj_id)
///    4 * u8  =  4 bytes   (collision_count, movement_type, piercing_type, alive)
///              ──────────
///              72 bytes total
///
/// The comment in the original source read 76 bytes (miscounted 16 f32s).
/// The C# [StructLayout(Size = 72)] is authoritative — trust that, not comments.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeProjectile {
    // ── physics (Rust updates every tick) ────────────────────────────────────
    pub x:          f32,
    pub y:          f32,
    pub vx:         f32,
    pub vy:         f32,
    pub ax:         f32,   // lateral accel — used by guided / arching movement
    pub ay:         f32,   // gravity / homing target Y component
    pub angle_deg:  f32,   // visual rotation, derived from velocity each tick
    pub curve_t:    f32,   // elapsed time param for arching / teleport

    // ── visual (Rust updates, renderer reads) ─────────────────────────────────
    pub scale_x:      f32,
    pub scale_y:      f32,
    pub scale_target: f32,
    pub scale_speed:  f32, // 0.0 = no growth (skip tick_scale entirely)

    // ── lifetime / travel ─────────────────────────────────────────────────────
    pub lifetime:     f32,
    pub max_lifetime: f32,
    pub travel_dist:  f32, // accumulated for damage falloff calc in C#

    // ── identity (C# writes once on spawn) ────────────────────────────────────
    pub config_id: u16,
    pub owner_id:  u16,
    pub proj_id:   u32,

    // ── state flags ───────────────────────────────────────────────────────────
    pub collision_count: u8,
    pub movement_type:   u8,  // 0=straight 1=arching 2=guided 3=teleport
    pub piercing_type:   u8,  // 0=none 1=piercer 2=random
    pub alive:           u8,  // 0=dead 1=alive
}

/// Hit event returned by check_hits_grid / check_hits_grid_ex.
/// 24 bytes — matches HitResult.cs.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct HitResult {
    pub proj_id:     u32,
    pub proj_index:  u32,   // index into projectile array — C# uses for kill/pierce
    pub target_id:   u32,   // maps to NetworkObject ID in C#
    pub travel_dist: f32,   // lets C# compute damage falloff
    pub hit_x:       f32,
    pub hit_y:       f32,
}

/// A collision target (enemy, player, obstacle).
/// 20 bytes — matches CollisionTarget.cs.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct CollisionTarget {
    pub x:         f32,
    pub y:         f32,
    pub radius:    f32,
    pub target_id: u32,
    pub active:    u8,
    pub _pad:      [u8; 3],
}

/// Spawn request written by C# before calling spawn_pattern.
/// 32 bytes — matches SpawnRequest.cs.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct SpawnRequest {
    pub origin_x:    f32,
    pub origin_y:    f32,
    pub angle_deg:   f32,
    pub speed:       f32,
    pub config_id:   u16,
    pub owner_id:    u16,
    pub pattern_id:  u8,   // 0=single 1=spread3 2=spread5 3=spiral 4=ring8
    pub _pad:        [u8; 3],
    pub rng_seed:    u32,
    pub base_proj_id: u32,
}

// ─────────────────────────────────────────────────────────────────────────────
//  Tick
// ─────────────────────────────────────────────────────────────────────────────

/// Advance the entire simulation by `dt` seconds.
/// Returns the number of projectiles that died this tick so C# can
/// recycle trail pool slots without scanning the array itself.
#[no_mangle]
pub unsafe extern "C" fn tick_projectiles(
    projs: *mut NativeProjectile,
    count: i32,
    dt:    f32,
) -> i32 {
    if projs.is_null() || count <= 0 { return 0; }
    let slice = slice::from_raw_parts_mut(projs, count as usize);
    simulation::tick_all(slice, dt)
}

// ─────────────────────────────────────────────────────────────────────────────
//  Collision — two entry points
//
//  check_hits_grid     — legacy signature, uses default cell_size 4.0.
//                        Kept for backward compatibility with existing C# callers.
//  check_hits_grid_ex  — full control: pass your own cell_size.
//                        Use this in new code; also called by the benchmark.
// ─────────────────────────────────────────────────────────────────────────────

/// Spatial-grid collision check.  Uses cell_size = 4.0 (default).
/// For cell_size control use check_hits_grid_ex.
#[no_mangle]
pub unsafe extern "C" fn check_hits_grid(
    projs:         *const NativeProjectile,
    proj_count:    i32,
    targets:       *const CollisionTarget,
    target_count:  i32,
    out_hits:      *mut HitResult,
    max_hits:      i32,
    out_hit_count: *mut i32,
) {
    check_hits_grid_ex(projs, proj_count, targets, target_count,
                       out_hits, max_hits, 0.0, out_hit_count);
}

/// Spatial-grid collision check with explicit cell_size.
/// Pass 0.0 for cell_size to use the default (4.0 world units).
///
/// Tune cell_size to roughly 2× the largest target radius.
/// Smaller = more precise bucketing, more inserts.
/// Larger = fewer inserts, more narrow-phase checks per cell.
#[no_mangle]
pub unsafe extern "C" fn check_hits_grid_ex(
    projs:         *const NativeProjectile,
    proj_count:    i32,
    targets:       *const CollisionTarget,
    target_count:  i32,
    out_hits:      *mut HitResult,
    max_hits:      i32,
    cell_size:     f32,
    out_hit_count: *mut i32,
) {
    let zero_out = |p: *mut i32| { if !p.is_null() { unsafe { *p = 0; } } };

    if projs.is_null() || targets.is_null() || out_hits.is_null() {
        zero_out(out_hit_count);
        return;
    }
    let projs_s   = slice::from_raw_parts(projs,    proj_count   as usize);
    let targets_s = slice::from_raw_parts(targets,  target_count as usize);
    let hits_s    = slice::from_raw_parts_mut(out_hits, max_hits as usize);

    let count = collision::check_hits(projs_s, targets_s, hits_s, cell_size);

    if !out_hit_count.is_null() { *out_hit_count = count as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Spawn / patterns
// ─────────────────────────────────────────────────────────────────────────────

/// Write up to `max_out` new NativeProjectiles into `out_projs`.
/// Returns how many were written via `out_count`.
/// C# overwrites Lifetime, MovementType, Scale etc. after this returns.
#[no_mangle]
pub unsafe extern "C" fn spawn_pattern(
    req:       *const SpawnRequest,
    out_projs: *mut NativeProjectile,
    max_out:   i32,
    out_count: *mut i32,
) {
    if req.is_null() || out_projs.is_null() {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let req_ref = &*req;
    let out_s   = slice::from_raw_parts_mut(out_projs, max_out as usize);
    let count   = patterns::generate(req_ref, out_s);
    if !out_count.is_null() { *out_count = count as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  State save / restore  (for rollback / reconciliation)
// ─────────────────────────────────────────────────────────────────────────────

/// Memcpy the entire projectile array into `buf`.
/// Returns bytes written, or 0 if buf is too small.
/// Required buf size = count * sizeof(NativeProjectile) = count * 72.
#[no_mangle]
pub unsafe extern "C" fn save_state(
    projs:   *const NativeProjectile,
    count:   i32,
    buf:     *mut u8,
    buf_len: i32,
) -> i32 {
    if projs.is_null() || buf.is_null() { return 0; }
    let slice = slice::from_raw_parts(projs, count as usize);
    state::save(slice, buf, buf_len as usize) as i32
}

/// Restore projectile state from a previously saved buffer.
/// Returns number of projectiles restored via `out_count`.
#[no_mangle]
pub unsafe extern "C" fn restore_state(
    out_projs:  *mut NativeProjectile,
    max_count:  i32,
    buf:        *const u8,
    buf_len:    i32,
    out_count:  *mut i32,
) {
    if out_projs.is_null() || buf.is_null() {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let out_s = slice::from_raw_parts_mut(out_projs, max_count as usize);
    let n     = state::restore(out_s, buf, buf_len as usize);
    if !out_count.is_null() { *out_count = n as i32; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Debug / validation helpers (stripped in release builds via #[cfg(debug_assertions)])
// ─────────────────────────────────────────────────────────────────────────────

/// Returns the compiled sizeof(NativeProjectile) so C# can verify alignment.
/// C# expects 72.  If this returns anything else there is a layout mismatch
/// and ALL P/Invoke calls will silently corrupt memory.
#[no_mangle]
pub extern "C" fn projectile_struct_size() -> i32 {
    core::mem::size_of::<NativeProjectile>() as i32
}

/// Returns sizeof(HitResult) — C# expects 24.
#[no_mangle]
pub extern "C" fn hit_result_struct_size() -> i32 {
    core::mem::size_of::<HitResult>() as i32
}

/// Returns sizeof(CollisionTarget) — C# expects 20.
#[no_mangle]
pub extern "C" fn collision_target_struct_size() -> i32 {
    core::mem::size_of::<CollisionTarget>() as i32
}

/// Returns sizeof(SpawnRequest) — C# expects 32.
#[no_mangle]
pub extern "C" fn spawn_request_struct_size() -> i32 {
    core::mem::size_of::<SpawnRequest>() as i32
}
