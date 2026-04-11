use crate::NativeProjectile;

const MOVE_STRAIGHT: u8 = 0;
const MOVE_ARCHING:  u8 = 1;
const MOVE_GUIDED:   u8 = 2;
const MOVE_TELEPORT: u8 = 3;

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

        // Only runs if C# set scale_speed > 0 in Spawn()
        // Projectiles that don't grow have scale_speed = 0.0 and skip this entirely
        tick_scale(p, dt);

        if p.movement_type != MOVE_TELEPORT {
            if p.vx != 0.0 || p.vy != 0.0 {
                p.angle_deg = p.vy.atan2(p.vx).to_degrees();
            }
        }

        let dx = p.vx * dt;
        let dy = p.vy * dt;
        p.travel_dist += (dx * dx + dy * dy).sqrt();
    }
    died
}

fn tick_straight(p: &mut NativeProjectile, dt: f32) {
    p.vx += p.ax * dt;
    p.vy += p.ay * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
}

fn tick_arching(p: &mut NativeProjectile, dt: f32) {
    p.vy += p.ay * dt;
    p.vx += p.ax * dt;
    p.x  += p.vx * dt;
    p.y  += p.vy * dt;
    p.curve_t += dt;
}

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

fn tick_teleport(p: &mut NativeProjectile, dt: f32) {
    let interval = 0.12f32;
    p.curve_t += dt;
    if p.curve_t >= interval {
        p.curve_t -= interval;
        let jump_dist = interval * (p.vx * p.vx + p.vy * p.vy).sqrt();
        let len = (p.vx * p.vx + p.vy * p.vy).sqrt().max(0.0001);
        p.x += (p.vx / len) * jump_dist;
        p.y += (p.vy / len) * jump_dist;
        p.travel_dist += jump_dist;
    }
}

fn tick_scale(p: &mut NativeProjectile, dt: f32) {
    // scale_speed of 0.0 means "no growth configured" — C# only sets
    // a non-zero value when SpawnScaleFraction < 1.0 on the config SO.
    // This is the gate that keeps all non-growing projectiles from being affected.
    if p.scale_speed == 0.0 { return; }

    let diff = p.scale_target - p.scale_x;
    if diff.abs() > 0.001 {
        p.scale_x += diff * p.scale_speed * dt;
        p.scale_y  = p.scale_x; // uniform scale — split if you need non-uniform
    }
                               }
