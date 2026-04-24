use crate::{NativeProjectile, NativeProjectile3D};
use crate::config_store;

// ─────────────────────────────────────────────────────────────────────────────
//  Movement type constants — must match C# ProjectileMovementType exactly
// ─────────────────────────────────────────────────────────────────────────────
pub const MOVE_STRAIGHT:  u8 = 0;
pub const MOVE_ARCHING:   u8 = 1;
pub const MOVE_GUIDED:    u8 = 2;
pub const MOVE_TELEPORT:  u8 = 3;
pub const MOVE_WAVE:      u8 = 4;   // Sine/cosine lateral oscillation
pub const MOVE_CIRCULAR:  u8 = 5;   // Helical orbit around travel axis

// ─────────────────────────────────────────────────────────────────────────────
//  2D tick
// ─────────────────────────────────────────────────────────────────────────────

pub fn tick_all(projs: &mut [NativeProjectile], dt: f32) -> i32 {
    let mut died = 0i32;
    for p in projs.iter_mut() {
        if p.alive == 0 { continue; }

        p.lifetime -= dt;
        if p.lifetime <= 0.0 {
            p.alive = 0;
            died += 1;
            continue;
        }

        match p.movement_type {
            MOVE_STRAIGHT => tick_straight(p, dt),
            MOVE_ARCHING  => tick_arching(p, dt),
            MOVE_GUIDED   => tick_guided(p, dt),
            MOVE_TELEPORT => tick_teleport(p, dt),
            MOVE_WAVE     => tick_wave(p, dt),
            MOVE_CIRCULAR => tick_circular(p, dt),
            _             => tick_straight(p, dt),
        }

        tick_scale(p, dt);

        if p.movement_type != MOVE_TELEPORT {
            if p.vx != 0.0 || p.vy != 0.0 {
                p.angle_deg = p.vy.atan2(p.vx).to_degrees();
            }
        }

        if p.movement_type != MOVE_TELEPORT {
            let dx = p.vx * dt;
            let dy = p.vy * dt;
            p.travel_dist += (dx * dx + dy * dy).sqrt();
        }
    }
    died
}

// ── 2D movement implementations ───────────────────────────────────────────────

#[inline(always)]
fn tick_straight(p: &mut NativeProjectile, dt: f32) {
    p.vx += p.ax * dt;
    p.vy += p.ay * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
}

#[inline(always)]
fn tick_arching(p: &mut NativeProjectile, dt: f32) {
    p.vy += p.ay * dt;
    p.vx += p.ax * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
    p.curve_t += dt;
}

#[inline(always)]
fn tick_guided(p: &mut NativeProjectile, dt: f32) {
    let turn_rate = std::f32::consts::PI * dt; // 180 deg/sec
    let cur_angle = p.vy.atan2(p.vx);
    let tgt_angle = p.ay.atan2(p.ax);

    let mut delta = tgt_angle - cur_angle;
    if delta >  std::f32::consts::PI { delta -= std::f32::consts::TAU; }
    if delta < -std::f32::consts::PI { delta += std::f32::consts::TAU; }
    let delta = delta.clamp(-turn_rate, turn_rate);

    let new_angle = cur_angle + delta;
    let speed = (p.vx * p.vx + p.vy * p.vy).sqrt();
    p.vx = new_angle.cos() * speed;
    p.vy = new_angle.sin() * speed;
    p.x += p.vx * dt;
    p.y += p.vy * dt;
}

#[inline(always)]
fn tick_teleport(p: &mut NativeProjectile, dt: f32) {
    const INTERVAL: f32 = 0.12;
    p.curve_t += dt;
    if p.curve_t >= INTERVAL {
        p.curve_t -= INTERVAL;
        let speed = (p.vx * p.vx + p.vy * p.vy).sqrt().max(0.0001);
        let jump  = INTERVAL * speed;
        p.x += (p.vx / speed) * jump;
        p.y += (p.vy / speed) * jump;
        p.travel_dist += jump;
    }
}

/// Wave movement (2D): projectile moves forward while oscillating laterally.
/// curve_t is used as the wave phase accumulator.
/// ax stores the travel-perpendicular X component, ay stores Y component
/// (set once at spawn from the normalised perpendicular to the initial velocity).
/// The wave displacement is ADDED to the base position each tick — it does not
/// affect vx/vy so travel_dist remains meaningful.
#[inline(always)]
fn tick_wave(p: &mut NativeProjectile, dt: f32) {
    // Advance base position along travel direction
    p.x += p.vx * dt;
    p.y += p.vy * dt;

    // Advance phase accumulator
    p.curve_t += dt;

    // Look up wave params — if not registered, fall back to straight
    if let Some(wp) = config_store::get_wave(p.config_id) {
        let phase      = p.curve_t * wp.frequency * std::f32::consts::TAU + wp.phase_offset;
        let offset     = wp.amplitude * phase.sin();

        // ax/ay hold the normalised perpendicular direction (set at spawn by C#)
        p.x += p.ax * offset * dt;
        p.y += p.ay * offset * dt;
    }
}

/// Circular (helical) movement (2D): orbits around the forward travel axis.
/// In 2D this creates a figure-8 / oscillating path.
/// curve_t is the orbit angle accumulator in radians.
/// ax/ay hold the normalised perpendicular at spawn.
#[inline(always)]
fn tick_circular(p: &mut NativeProjectile, dt: f32) {
    // Base forward travel
    p.x += p.vx * dt;
    p.y += p.vy * dt;

    p.curve_t += dt;

    if let Some(cp) = config_store::get_circular(p.config_id) {
        let angle_rad  = p.curve_t * cp.angular_speed.to_radians()
                         + cp.start_angle_deg.to_radians();
        let orbit_x    = p.ax * angle_rad.cos() * cp.radius;
        let orbit_y    = p.ay * angle_rad.sin() * cp.radius;

        // In 2D circular, we offset from the base travel line each tick.
        // The previous frame's orbit offset is implicitly undone because we
        // recompute from the base position (x + vx*dt is already the new base).
        // NOTE: this means x/y include orbit displacement — travel_dist will be
        // slightly inflated. Acceptable for the use cases (visual flair).
        p.x += orbit_x * dt;
        p.y += orbit_y * dt;
    }
}

#[inline(always)]
fn tick_scale(p: &mut NativeProjectile, dt: f32) {
    if p.scale_speed == 0.0 { return; }
    let diff = p.scale_target - p.scale_x;
    if diff.abs() > 0.001 {
        p.scale_x += diff * p.scale_speed * dt;
        p.scale_y  = p.scale_x;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D tick
// ─────────────────────────────────────────────────────────────────────────────

pub fn tick_all_3d(projs: &mut [NativeProjectile3D], dt: f32) -> i32 {
    let mut died = 0i32;
    for p in projs.iter_mut() {
        if p.alive == 0 { continue; }

        p.lifetime -= dt;
        if p.lifetime <= 0.0 {
            p.alive = 0;
            died += 1;
            continue;
        }

        match p.movement_type {
            MOVE_STRAIGHT => tick_straight_3d(p, dt),
            MOVE_ARCHING  => tick_arching_3d(p, dt),
            MOVE_GUIDED   => tick_guided_3d(p, dt),
            MOVE_TELEPORT => tick_teleport_3d(p, dt),
            MOVE_WAVE     => tick_wave_3d(p, dt),
            MOVE_CIRCULAR => tick_circular_3d(p, dt),
            _             => tick_straight_3d(p, dt),
        }

        tick_scale_3d(p, dt);

        if p.movement_type != MOVE_TELEPORT {
            let dx = p.vx * dt;
            let dy = p.vy * dt;
            let dz = p.vz * dt;
            p.travel_dist += (dx*dx + dy*dy + dz*dz).sqrt();
        }
    }
    died
}

// ── 3D movement implementations ───────────────────────────────────────────────

#[inline(always)]
fn tick_straight_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.vx += p.ax * dt;
    p.vy += p.ay * dt;
    p.vz += p.az * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
    p.z  += p.vz * dt;
}

#[inline(always)]
fn tick_arching_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.vx += p.ax * dt;
    p.vy += p.ay * dt;
    p.vz += p.az * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
    p.z  += p.vz * dt;
    p.timer_t += dt;
}

#[inline(always)]
fn tick_guided_3d(p: &mut NativeProjectile3D, dt: f32) {
    let turn_rate = std::f32::consts::PI * dt;
    let speed = (p.vx*p.vx + p.vy*p.vy + p.vz*p.vz).sqrt().max(0.0001);

    let (cx, cy, cz) = (p.vx/speed, p.vy/speed, p.vz/speed);
    let tlen = (p.ax*p.ax + p.ay*p.ay + p.az*p.az).sqrt().max(0.0001);
    let (tx, ty, tz) = (p.ax/tlen, p.ay/tlen, p.az/tlen);

    let dot   = (cx*tx + cy*ty + cz*tz).clamp(-1.0, 1.0);
    let angle = dot.acos();

    if angle > 0.0001 {
        let t  = (turn_rate / angle).min(1.0);
        let nx = cx + (tx - cx) * t;
        let ny = cy + (ty - cy) * t;
        let nz = cz + (tz - cz) * t;
        let nlen = (nx*nx + ny*ny + nz*nz).sqrt().max(0.0001);
        p.vx = (nx/nlen) * speed;
        p.vy = (ny/nlen) * speed;
        p.vz = (nz/nlen) * speed;
    }

    p.x += p.vx * dt;
    p.y += p.vy * dt;
    p.z += p.vz * dt;
}

#[inline(always)]
fn tick_teleport_3d(p: &mut NativeProjectile3D, dt: f32) {
    const INTERVAL: f32 = 0.12;
    p.timer_t += dt;
    if p.timer_t >= INTERVAL {
        p.timer_t -= INTERVAL;
        let speed = (p.vx*p.vx + p.vy*p.vy + p.vz*p.vz).sqrt().max(0.0001);
        let jump  = INTERVAL * speed;
        p.x += (p.vx/speed) * jump;
        p.y += (p.vy/speed) * jump;
        p.z += (p.vz/speed) * jump;
        p.travel_dist += jump;
    }
}

/// Wave (3D): oscillates perpendicular to travel direction.
/// ax/ay/az holds the normalised perpendicular axis (set at spawn by C#).
/// For vertical wave: use world-up (0,1,0) as perp.
/// For horizontal wave: use world-right projected perpendicular to velocity.
#[inline(always)]
fn tick_wave_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.x += p.vx * dt;
    p.y += p.vy * dt;
    p.z += p.vz * dt;
    p.timer_t += dt;

    if let Some(wp) = config_store::get_wave(p.config_id) {
        let phase  = p.timer_t * wp.frequency * std::f32::consts::TAU + wp.phase_offset;
        let offset = wp.amplitude * phase.sin();
        p.x += p.ax * offset * dt;
        p.y += p.ay * offset * dt;
        p.z += p.az * offset * dt;
    }
}

/// Circular/helical (3D): orbits around the forward travel axis.
/// ax/ay/az stores the first perpendicular axis (set at spawn).
/// The second perpendicular is the cross product of forward × ax/ay/az.
/// Together they span the orbital plane.
#[inline(always)]
fn tick_circular_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.x += p.vx * dt;
    p.y += p.vy * dt;
    p.z += p.vz * dt;
    p.timer_t += dt;

    if let Some(cp) = config_store::get_circular(p.config_id) {
        let angle = p.timer_t * cp.angular_speed.to_radians()
                    + cp.start_angle_deg.to_radians();

        // Forward direction (normalised velocity)
        let speed = (p.vx*p.vx + p.vy*p.vy + p.vz*p.vz).sqrt().max(0.0001);
        let (fx, fy, fz) = (p.vx/speed, p.vy/speed, p.vz/speed);

        // First perp from ax/ay/az (set at spawn)
        let (ux, uy, uz) = (p.ax, p.ay, p.az);

        // Second perp = forward × first_perp
        let (vx, vy, vz) = (
            fy*uz - fz*uy,
            fz*ux - fx*uz,
            fx*uy - fy*ux,
        );

        let orbit_x = (ux * angle.cos() + vx * angle.sin()) * cp.radius;
        let orbit_y = (uy * angle.cos() + vy * angle.sin()) * cp.radius;
        let orbit_z = (uz * angle.cos() + vz * angle.sin()) * cp.radius;

        p.x += orbit_x * dt;
        p.y += orbit_y * dt;
        p.z += orbit_z * dt;
    }
}

#[inline(always)]
fn tick_scale_3d(p: &mut NativeProjectile3D, dt: f32) {
    if p.scale_speed == 0.0 { return; }
    let diff = p.scale_target - p.scale_x;
    if diff.abs() > 0.001 {
        let delta = diff * p.scale_speed * dt;
        p.scale_x += delta;
        p.scale_y += delta;
        p.scale_z += delta;
    }
}
