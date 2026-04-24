use crate::{NativeProjectile, NativeProjectile3D};

// ─────────────────────────────────────────────────────────────────────────────
//  Movement type constants (shared 2D/3D)
// ─────────────────────────────────────────────────────────────────────────────
const MOVE_STRAIGHT: u8 = 0;
const MOVE_ARCHING:  u8 = 1;
const MOVE_GUIDED:   u8 = 2;
const MOVE_TELEPORT: u8 = 3;

// ─────────────────────────────────────────────────────────────────────────────
//  2D tick (unchanged)
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
            _             => tick_straight(p, dt),
        }

        tick_scale(p, dt);

        // Angle (skipped for teleport — position is discrete, angle would be misleading)
        if p.movement_type != MOVE_TELEPORT {
            if p.vx != 0.0 || p.vy != 0.0 {
                p.angle_deg = p.vy.atan2(p.vx).to_degrees();
            }
        }

        // Travel distance (teleport handles it internally)
        if p.movement_type != MOVE_TELEPORT {
            let dx = p.vx * dt;
            let dy = p.vy * dt;
            p.travel_dist += (dx * dx + dy * dy).sqrt();
        }
    }
    died
}

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
    let turn_rate = 180.0f32.to_radians() * dt;
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

#[inline(always)]
fn tick_scale(p: &mut NativeProjectile, dt: f32) {
    if p.scale_speed == 0.0 { return; }
    let diff = p.scale_target - p.scale_x;
    if diff.abs() > 0.001 {
        p.scale_x += diff * p.scale_speed * dt;
        p.scale_y  = p.scale_x; // uniform scale
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  3D tick (new)
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
            _             => tick_straight_3d(p, dt),
        }

        tick_scale_3d(p, dt);

        // Accumulate travel distance (teleport handles it internally)
        if p.movement_type != MOVE_TELEPORT {
            let dx = p.vx * dt;
            let dy = p.vy * dt;
            let dz = p.vz * dt;
            p.travel_dist += (dx * dx + dy * dy + dz * dz).sqrt();
        }
    }
    died
}

/// Straight 3D: constant accel + linear velocity.
/// ax/ay/az = constant acceleration (gravity in ay, wind, etc.)
#[inline(always)]
fn tick_straight_3d(p: &mut NativeProjectile3D, dt: f32) {
    p.vx += p.ax * dt;
    p.vy += p.ay * dt;
    p.vz += p.az * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
    p.z  += p.vz * dt;
}

/// Arching 3D: same as straight, also advances timer_t so C# can
/// read it for visual interpolation (e.g. a parabolic arrow arc).
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

/// Guided 3D: spherically turns velocity toward (ax, ay, az) target direction.
/// ax/ay/az must be a normalized (or near-normalized) homing direction set by C#.
/// C# updates ax/ay/az each frame or via TickDispatcher to track the target.
#[inline(always)]
fn tick_guided_3d(p: &mut NativeProjectile3D, dt: f32) {
    let turn_rate = 180.0f32.to_radians() * dt;

    let speed = (p.vx*p.vx + p.vy*p.vy + p.vz*p.vz).sqrt().max(0.0001);

    // Current direction unit vector
    let (cx, cy, cz) = (p.vx / speed, p.vy / speed, p.vz / speed);

    // Target direction from ax/ay/az (normalise defensively)
    let tlen = (p.ax*p.ax + p.ay*p.ay + p.az*p.az).sqrt().max(0.0001);
    let (tx, ty, tz) = (p.ax / tlen, p.ay / tlen, p.az / tlen);

    // Angle between current and target directions
    let dot   = (cx*tx + cy*ty + cz*tz).clamp(-1.0, 1.0);
    let angle = dot.acos();

    if angle > 0.0001 {
        // Fraction of the full angle to rotate this tick
        let t = (turn_rate / angle).min(1.0);
        // Linear interpolate direction then renormalize (cheap approximation of slerp)
        let nx = cx + (tx - cx) * t;
        let ny = cy + (ty - cy) * t;
        let nz = cz + (tz - cz) * t;
        let nlen = (nx*nx + ny*ny + nz*nz).sqrt().max(0.0001);
        p.vx = (nx / nlen) * speed;
        p.vy = (ny / nlen) * speed;
        p.vz = (nz / nlen) * speed;
    }

    p.x += p.vx * dt;
    p.y += p.vy * dt;
    p.z += p.vz * dt;
}

/// Teleport 3D: discrete jumps every INTERVAL seconds.
/// Uses timer_t as the interval accumulator.
#[inline(always)]
fn tick_teleport_3d(p: &mut NativeProjectile3D, dt: f32) {
    const INTERVAL: f32 = 0.12;
    p.timer_t += dt;
    if p.timer_t >= INTERVAL {
        p.timer_t -= INTERVAL;
        let speed = (p.vx*p.vx + p.vy*p.vy + p.vz*p.vz).sqrt().max(0.0001);
        let jump  = INTERVAL * speed;
        p.x += (p.vx / speed) * jump;
        p.y += (p.vy / speed) * jump;
        p.z += (p.vz / speed) * jump;
        p.travel_dist += jump;
    }
}

/// Scale growth 3D: grows scale_x/y/z uniformly toward scale_target.
/// Zero-cost when scale_speed == 0.0 (default — no growth configured).
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
