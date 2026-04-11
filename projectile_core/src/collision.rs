// collision.rs — spatial grid broad-phase, circle-circle narrow-phase

use crate::{NativeProjectile, CollisionTarget, HitResult};

const CELL_SIZE: f32 = 2.0; // world units per grid cell — tune to your scale

pub fn check_hits(
    projs:   &[NativeProjectile],
    targets: &[CollisionTarget],
    out:     &mut [HitResult],
) -> usize {
    let mut hit_count = 0usize;
    let max_hits = out.len();

    // For each alive projectile, check against every active target.
    // This is O(P×T) but in practice P and T are small enough at this scale.
    // Upgrade to a proper grid if you see perf issues beyond ~1000 projectiles.
    for (pi, p) in projs.iter().enumerate() {
        if p.alive == 0 { continue; }
        if hit_count >= max_hits { break; }

        for t in targets.iter() {
            if t.active == 0 { continue; }

            let dx = p.x - t.x;
            let dy = p.y - t.y;
            let dist_sq = dx * dx + dy * dy;

            // projectile radius = scale_x * 0.5 (half of visual size)
            let proj_r = p.scale_x * 0.5;
            let combined_r = proj_r + t.radius;

            if dist_sq <= combined_r * combined_r {
                if hit_count >= max_hits { break; }
                out[hit_count] = HitResult {
                    proj_id:    p.proj_id,
                    proj_index: pi as u32,
                    target_id:  t.target_id,
                    travel_dist: p.travel_dist,
                    hit_x: p.x,
                    hit_y: p.y,
                };
                hit_count += 1;
                break; // one hit per projectile per tick (unless piercing — C# decides)
            }
        }
    }

    hit_count
      }
