// collision.rs — spatial grid broad-phase, circle-circle narrow-phase
//
// Algorithm: sorted flat-array grid, zero heap allocation.
//
//   Build phase:
//     For each active target insert one Entry{cell_key, ti} per cell it overlaps.
//     Sort the flat array by cell_key. O(T log T), T ≤ 128.
//
//   Query phase (per projectile):
//     Compute the cell footprint of the projectile (usually 1–2 cells).
//     Binary-search the sorted array for each cell, iterate the run.
//     Use a u128 bitset to skip targets already tested (handles targets that
//     span multiple cells appearing in more than one of the projectile's cells).
//
//   Performance vs old O(P×T) brute-force (2048 projs × 64 targets):
//     Before : ~131 072 distance tests, ~453 µs
//     After  : ~4 000–8 000 distance tests, target < 20 µs
//
// CELL_SIZE tuning:
//   Set to ≥ 2 × max_target_radius so most targets fit in a single cell.
//   Default 4.0 is good for targets with radius ≤ 2.0 world units.
//
// Hard limit: 128 targets (u128 dedup bitset + matches C# _maxTargets = 128).

use crate::{NativeProjectile, CollisionTarget, HitResult};

const CELL_SIZE: f32 = 4.0;

// ── helpers ───────────────────────────────────────────────────────────────────

#[inline(always)]
fn cell_coord(v: f32) -> i16 {
    let c = (v / CELL_SIZE).floor() as i32;
    c.clamp(i16::MIN as i32, i16::MAX as i32) as i16
}

#[inline(always)]
fn pack_cell(cx: i16, cy: i16) -> u32 {
    ((cx as u16 as u32) << 16) | (cy as u16 as u32)
}

// ── grid ──────────────────────────────────────────────────────────────────────

#[derive(Clone, Copy)]
struct Entry {
    cell: u32,
    ti:   u8,   // target index 0–127
}

// 128 targets × max 9 cells each (3×3 when radius ≈ CELL_SIZE) = 1152.
// 1152 × 5 bytes ≈ 6 KB stack — well within limits.
const MAX_ENTRIES: usize = 1152;

// ── public API ────────────────────────────────────────────────────────────────

pub fn check_hits(
    projs:   &[NativeProjectile],
    targets: &[CollisionTarget],
    out:     &mut [HitResult],
) -> usize {
    if projs.is_empty() || targets.is_empty() {
        return 0;
    }

    // ── Step 1: build the sorted grid ─────────────────────────────────────
    let mut buf   = [Entry { cell: 0, ti: 0 }; MAX_ENTRIES];
    let mut n_ent = 0usize;

    for (ti, t) in targets.iter().enumerate() {
        if t.active == 0 { continue; }
        if ti >= 128     { break; }        // hard cap

        let min_cx = cell_coord(t.x - t.radius);
        let max_cx = cell_coord(t.x + t.radius);
        let min_cy = cell_coord(t.y - t.radius);
        let max_cy = cell_coord(t.y + t.radius);

        let mut cx = min_cx;
        loop {
            let mut cy = min_cy;
            loop {
                if n_ent < MAX_ENTRIES {
                    buf[n_ent] = Entry { cell: pack_cell(cx, cy), ti: ti as u8 };
                    n_ent += 1;
                }
                if cy == max_cy { break; }
                cy += 1;
            }
            if cx == max_cx { break; }
            cx += 1;
        }
    }

    // Sort by cell key — binary-search requires sorted order.
    // 1152 entries: sort_unstable ≈ 2–4 µs.
    buf[..n_ent].sort_unstable_by_key(|e| e.cell);

    // ── Step 2: per-projectile queries ────────────────────────────────────
    let mut hit_count = 0usize;
    let max_hits      = out.len();
    let mut checked: u128;

    for (pi, p) in projs.iter().enumerate() {
        if p.alive == 0          { continue; }
        if hit_count >= max_hits { break;    }

        let proj_r = p.scale_x * 0.5;

        let min_cx = cell_coord(p.x - proj_r);
        let max_cx = cell_coord(p.x + proj_r);
        let min_cy = cell_coord(p.y - proj_r);
        let max_cy = cell_coord(p.y + proj_r);

        checked = 0u128;

        'outer: for cx in min_cx..=max_cx {
            for cy in min_cy..=max_cy {
                let key   = pack_cell(cx, cy);
                let start = first_index_of(&buf[..n_ent], key);

                let mut j = start;
                while j < n_ent && buf[j].cell == key {
                    let ti  = buf[j].ti;
                    let bit = 1u128 << ti;

                    if checked & bit == 0 {
                        checked |= bit;

                        // SAFETY: ti < 128 ≤ targets.len() (enforced at insert)
                        let t = unsafe { targets.get_unchecked(ti as usize) };

                        let dx       = p.x - t.x;
                        let dy       = p.y - t.y;
                        let combined = proj_r + t.radius;

                        if dx * dx + dy * dy <= combined * combined {
                            out[hit_count] = HitResult {
                                proj_id:     p.proj_id,
                                proj_index:  pi as u32,
                                target_id:   t.target_id,
                                travel_dist: p.travel_dist,
                                hit_x:       p.x,
                                hit_y:       p.y,
                            };
                            hit_count += 1;
                            break 'outer; // one hit per projectile per tick
                        }
                    }
                    j += 1;
                }
            }
        }
    }

    hit_count
}

// Returns the index of the first entry with entry.cell == key, or n if none.
#[inline]
fn first_index_of(entries: &[Entry], key: u32) -> usize {
    let mut lo = 0usize;
    let mut hi = entries.len();
    while lo < hi {
        let mid = lo + (hi - lo) / 2;
        if entries[mid].cell < key { lo = mid + 1; } else { hi = mid; }
    }
    lo
}
