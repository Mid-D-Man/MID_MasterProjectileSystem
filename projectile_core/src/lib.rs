// lib.rs — all public FFI exports live here
// Unity calls these via P/Invoke

mod simulation;
mod collision;
mod patterns;
mod state;

pub use simulation::*;
pub use collision::*;
pub use patterns::*;
pub use state::*;

use std::slice;

// ─────────────────────────────────────────────
//  Shared data types
// ─────────────────────────────────────────────

/// Matches NativeProjectile.cs exactly — do not reorder fields
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct NativeProjectile {
    // ── physics (Rust owns, updates every tick) ──
    pub x: f32,
    pub y: f32,
    pub vx: f32,
    pub vy: f32,
    pub ax: f32,            // lateral acceleration (curvature / homing)
    pub ay: f32,
    pub angle_deg: f32,     // visual rotation, updated by sim
    pub curve_t: f32,       // interpolation param for arching movement

    // ── visual (Rust updates, renderer reads) ──
    pub scale_x: f32,
    pub scale_y: f32,
    pub scale_target: f32,
    pub scale_speed: f32,   // lerp speed toward target

    // ── lifetime / distance (Rust owns) ──
    pub lifetime: f32,
    pub max_lifetime: f32,
    pub travel_dist: f32,   // accumulated distance for damage falloff

    // ── identity (C# writes once on spawn, Rust reads) ──
    pub config_id: u16,
    pub owner_id: u16,
    pub proj_id: u32,

    // ── state flags (1 byte each for alignment) ──
    pub collision_count: u8,
    pub movement_type: u8,  // 0=straight 1=arching 2=guided 3=teleport
    pub piercing_type: u8,  // 0=none 1=piercer 2=random
    pub alive: u8,          // 0=dead 1=alive
}
// sizeof: 16×f32 (64) + u16×2 (4) + u32 (4) + u8×4 (4) = 76 bytes

/// Returned by check_hits_grid — tells C# what happened
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct HitResult {
    pub proj_id: u32,
    pub proj_index: u32,       // index in projectile array
    pub target_id: u32,        // C# maps this to NetworkObject ID
    pub travel_dist: f32,      // so C# can compute damage falloff
    pub hit_x: f32,
    pub hit_y: f32,
}

/// One target (enemy, player, wall) registered with the collision system
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct CollisionTarget {
    pub x: f32,
    pub y: f32,
    pub radius: f32,
    pub target_id: u32,
    pub active: u8,
    pub _pad: [u8; 3],
}

// ─────────────────────────────────────────────
//  Top-level tick — called every FixedUpdate
// ─────────────────────────────────────────────

/// Advance the entire projectile simulation by dt seconds.
/// `projs`  — pointer to C#-owned NativeArray<NativeProjectile>
/// `count`  — number of slots (alive + dead)
/// Returns the number of projectiles that died this tick
/// (so C# can recycle trail slots without scanning the array itself).
#[no_mangle]
pub unsafe extern "C" fn tick_projectiles(
    projs: *mut NativeProjectile,
    count: i32,
    dt: f32,
) -> i32 {
    if projs.is_null() || count <= 0 {
        return 0;
    }
    let slice = slice::from_raw_parts_mut(projs, count as usize);
    simulation::tick_all(slice, dt)
}

// ─────────────────────────────────────────────
//  Collision
// ─────────────────────────────────────────────

/// Broad-phase grid collision check.
/// `out_hits`      — caller-allocated array (max_hits slots)
/// `out_hit_count` — how many HitResults were written
#[no_mangle]
pub unsafe extern "C" fn check_hits_grid(
    projs: *const NativeProjectile,
    proj_count: i32,
    targets: *const CollisionTarget,
    target_count: i32,
    out_hits: *mut HitResult,
    max_hits: i32,
    out_hit_count: *mut i32,
) {
    if projs.is_null() || targets.is_null() || out_hits.is_null() {
        if !out_hit_count.is_null() { *out_hit_count = 0; }
        return;
    }
    let projs_s   = slice::from_raw_parts(projs, proj_count as usize);
    let targets_s = slice::from_raw_parts(targets, target_count as usize);
    let hits_s    = slice::from_raw_parts_mut(out_hits, max_hits as usize);
    let count = collision::check_hits(projs_s, targets_s, hits_s);
    *out_hit_count = count as i32;
}

// ─────────────────────────────────────────────
//  Spawn / patterns
// ─────────────────────────────────────────────

/// Spawn data sent from C# to seed a pattern.
#[repr(C)]
pub struct SpawnRequest {
    pub origin_x: f32,
    pub origin_y: f32,
    pub angle_deg: f32,       // base firing direction
    pub speed: f32,
    pub config_id: u16,
    pub owner_id: u16,
    pub pattern_id: u8,       // 0=single 1=spread3 2=spread5 3=spiral 4=ring8
    pub _pad: [u8; 3],
    pub rng_seed: u32,
    pub base_proj_id: u32,    // C# assigns IDs; Rust fills proj_id sequentially
}

/// Write up to `max_out` new NativeProjectiles into `out_projs`.
/// Returns how many were written.
#[no_mangle]
pub unsafe extern "C" fn spawn_pattern(
    req: *const SpawnRequest,
    out_projs: *mut NativeProjectile,
    max_out: i32,
    out_count: *mut i32,
) {
    if req.is_null() || out_projs.is_null() {
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let req_ref = &*req;
    let out_s   = slice::from_raw_parts_mut(out_projs, max_out as usize);
    let count   = patterns::generate(req_ref, out_s);
    *out_count  = count as i32;
}

// ─────────────────────────────────────────────
//  State save / restore (for reconciliation)
// ─────────────────────────────────────────────

/// Serialise current projectile state into `buf`.
/// Returns bytes written, or 0 if buf is too small.
#[no_mangle]
pub unsafe extern "C" fn save_state(
    projs: *const NativeProjectile,
    count: i32,
    buf: *mut u8,
    buf_len: i32,
) -> i32 {
    if projs.is_null() || buf.is_null() { return 0; }
    let slice = slice::from_raw_parts(projs, count as usize);
    state::save(slice, buf, buf_len as usize) as i32
}

/// Deserialise back into `out_projs`.  Returns projectile count restored.
#[no_mangle]
pub unsafe extern "C" fn restore_state(
    out_projs: *mut NativeProjectile,
    max_count: i32,
    buf: *const u8,
    buf_len: i32,
    out_count: *mut i32,
) {
    if out_projs.is_null() || buf.is_null() { 
        if !out_count.is_null() { *out_count = 0; }
        return;
    }
    let out_s = slice::from_raw_parts_mut(out_projs, max_count as usize);
    let n     = state::restore(out_s, buf, buf_len as usize);
    if !out_count.is_null() { *out_count = n as i32; }
  }
