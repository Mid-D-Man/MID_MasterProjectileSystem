// NativeProjectile3D.cs
// C# mirror of Rust NativeProjectile3D struct (projectile_core/src/lib.rs).
//
// Layout: [StructLayout(LayoutKind.Explicit, Size = 84)]
// CRITICAL: Field offsets here must exactly match the Rust repr(C) struct.
//           A mismatch causes silent memory corruption on every P/Invoke call.
//           ProjectileLib.ValidateStructSizes() catches this at startup.
//
// Field map vs 2D NativeProjectile:
//   + Z position/velocity/acceleration added
//   + scale_z added (uniform scale — tick_scale_3d sets x/y/z identically)
//   + timer_t replaces curve_t (same semantic: arching elapsed time / teleport interval)
//   - angle_deg removed: C# derives visual rotation from velocity direction each frame
//   Total size: 84 bytes (2D is 72 bytes)

using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace MidManStudio.Projectiles
{
    /// <summary>
    /// Unmanaged 3D projectile state. Pinned in GCHandle or stored in NativeArray.
    /// Rust tick_projectiles_3d() and check_hits_grid_3d() read/write this directly.
    ///
    /// C# NEVER moves a projectile — all position/velocity updates belong to Rust.
    /// C# writes: ax/ay/az for guided homing target direction (via TickDispatcher subscriber).
    /// C# reads: x/y/z for rendering, travel_dist for damage falloff, alive for compaction.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 84)]
    public struct NativeProjectile3D
    {
        // ── Position ─────────────────────────────────────────────────────────
        [FieldOffset(0)]  public float X;
        [FieldOffset(4)]  public float Y;
        [FieldOffset(8)]  public float Z;

        // ── Velocity ─────────────────────────────────────────────────────────
        [FieldOffset(12)] public float Vx;
        [FieldOffset(16)] public float Vy;
        [FieldOffset(20)] public float Vz;

        // ── Acceleration (straight/arching gravity, guided homing direction) ─
        // movement_type = Guided: C# writes normalised target direction each tick via TickDispatcher.
        // movement_type = Straight/Arching: constant accel (gravity in Ay, wind, etc.)
        [FieldOffset(24)] public float Ax;
        [FieldOffset(28)] public float Ay;
        [FieldOffset(32)] public float Az;

        // ── Scale (opt-in growth — zero cost when ScaleSpeed == 0) ──────────
        [FieldOffset(36)] public float ScaleX;
        [FieldOffset(40)] public float ScaleY;
        [FieldOffset(44)] public float ScaleZ;
        [FieldOffset(48)] public float ScaleTarget;  // target size (only used when ScaleSpeed > 0)
        [FieldOffset(52)] public float ScaleSpeed;   // 0.0 = no growth, skip tick_scale_3d entirely

        // ── Lifetime & travel ────────────────────────────────────────────────
        [FieldOffset(56)] public float Lifetime;     // decremented each tick; 0 = dead
        [FieldOffset(60)] public float MaxLifetime;  // set once at spawn from config
        [FieldOffset(64)] public float TravelDist;   // accumulated for damage falloff in RustSimAdapter
        [FieldOffset(68)] public float TimerT;       // arching elapsed time / teleport interval accumulator

        // ── Identity (written once at spawn, never changed by Rust) ─────────
        [FieldOffset(72)] public ushort ConfigId;
        [FieldOffset(74)] public ushort OwnerId;
        [FieldOffset(76)] public uint   ProjId;

        // ── Flags (Rust writes Alive=0 on death; C# sets initial values) ────
        [FieldOffset(80)] public byte CollisionCount;
        [FieldOffset(81)] public byte MovementType;  // ProjectileMovementType cast to byte
        [FieldOffset(82)] public byte PiercingType;  // ProjectilePiercingType cast to byte
        [FieldOffset(83)] public byte Alive;         // 1 = alive, 0 = dead (set by Rust on lifetime expiry or by C# on piercing kill)

        // ─────────────────────────────────────────────────────────────────────
        //  Convenience helpers (no Rust equivalent — pure C# convenience)
        // ─────────────────────────────────────────────────────────────────────

        public bool IsAlive => Alive != 0;

        /// Uniform scale shorthand — reads ScaleX (all three are always equal for uniform scale).
        public float CollisionRadius => ScaleX * 0.5f;

        /// Derive visual rotation as a Unity Quaternion from the velocity direction.
        /// Called by ProjectileRenderer3D each LateUpdate — Rust does NOT compute angle_deg for 3D.
        public UnityEngine.Quaternion VisualRotation()
        {
            var v = new UnityEngine.Vector3(Vx, Vy, Vz);
            if (v.sqrMagnitude < 0.0001f)
                return UnityEngine.Quaternion.identity;
            return UnityEngine.Quaternion.LookRotation(v.normalized, UnityEngine.Vector3.up);
        }

        /// World-space position as a Unity Vector3.
        public UnityEngine.Vector3 Position => new UnityEngine.Vector3(X, Y, Z);

        /// Set guided homing direction. C# calls this from a TickDispatcher subscriber
        /// (Tick_0_1 is sufficient — homing updates don't need per-FixedUpdate precision).
        /// dir does not need to be normalised — Rust normalises it in tick_guided_3d.
        public void SetHomingDirection(UnityEngine.Vector3 dir)
        {
            Ax = dir.x;
            Ay = dir.y;
            Az = dir.z;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  3D hit result — returned from check_hits_grid_3d
    //  Must match Rust HitResult3D exactly. Size = 28 bytes.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hit event from the Rust 3D spatial-grid collision check.
    /// RustSimAdapter.ProcessHit3D() uses this to look up ServerProjectileData and apply damage.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct HitResult3D
    {
        [FieldOffset(0)]  public uint  ProjId;       // matches NativeProjectile3D.ProjId
        [FieldOffset(4)]  public uint  ProjIndex;    // index in the 3D sim buffer (for kill/pierce)
        [FieldOffset(8)]  public uint  TargetId;     // maps to NetworkObject ID in C#
        [FieldOffset(12)] public float TravelDist;   // for damage falloff
        [FieldOffset(16)] public float HitX;
        [FieldOffset(20)] public float HitY;
        [FieldOffset(24)] public float HitZ;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  3D collision target — registered with ServerProjectileAuthority
    //  Must match Rust CollisionTarget3D exactly. Size = 24 bytes.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A hittable sphere in 3D world space. Registered/updated by character controllers,
    /// vehicles, or any damageable NetworkObject each frame (or via TickDispatcher).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct CollisionTarget3D
    {
        [FieldOffset(0)]  public float X;
        [FieldOffset(4)]  public float Y;
        [FieldOffset(8)]  public float Z;
        [FieldOffset(12)] public float Radius;
        [FieldOffset(16)] public uint  TargetId;  // NetworkObject ID
        [FieldOffset(20)] public byte  Active;    // 1 = in play, 0 = skip (dead/stunned/invincible)
        // 3 bytes implicit padding to Size = 24
    }
}
