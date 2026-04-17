// NativeProjectile.cs
// MUST match the Rust NativeProjectile struct in src/lib.rs exactly.
// Uses explicit field offsets — do not change without updating Rust side.
//
// iOS NOTE: On IL2CPP (iOS/Android), DllImport("projectile_core") resolves
// to the __Internal linker symbol — the static lib is linked directly into
// the app binary. No file extension, no path. Same source, same constants.

using System.Runtime.InteropServices;

namespace MidManStudio.Projectiles
{
    // ── NativeProjectile — 72 bytes ──────────────────────────────────────────
    // Layout verified: 15×f32(60) + 2×u16(4) + u32(4) + 4×u8(4) = 72 bytes
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct NativeProjectile
    {
        // ── Physics (Rust writes every tick) ──────────────────────────────────
        [FieldOffset( 0)] public float  X;
        [FieldOffset( 4)] public float  Y;
        [FieldOffset( 8)] public float  Vx;
        [FieldOffset(12)] public float  Vy;
        [FieldOffset(16)] public float  Ax;         // lateral accel / homing dir X
        [FieldOffset(20)] public float  Ay;         // gravity / homing dir Y
        [FieldOffset(24)] public float  AngleDeg;   // visual rotation
        [FieldOffset(28)] public float  CurveT;     // arc interpolation param

        // ── Visual (Rust updates, renderer reads) ─────────────────────────────
        [FieldOffset(32)] public float  ScaleX;
        [FieldOffset(36)] public float  ScaleY;
        [FieldOffset(40)] public float  ScaleTarget;
        [FieldOffset(44)] public float  ScaleSpeed;

        // ── Lifetime / distance ───────────────────────────────────────────────
        [FieldOffset(48)] public float  Lifetime;
        [FieldOffset(52)] public float  MaxLifetime;
        [FieldOffset(56)] public float  TravelDist;

        // ── Identity (C# writes once on spawn) ────────────────────────────────
        [FieldOffset(60)] public ushort ConfigId;
        [FieldOffset(62)] public ushort OwnerId;
        [FieldOffset(64)] public uint   ProjId;

        // ── State flags ────────────────────────────────────────────────────────
        [FieldOffset(68)] public byte   CollisionCount;
        [FieldOffset(69)] public byte   MovementType;  // see MovementType enum
        [FieldOffset(70)] public byte   PiercingType;  // see PiercingType enum
        [FieldOffset(71)] public byte   Alive;         // 0 = dead, 1 = alive
    }

    // ── HitResult — 24 bytes ─────────────────────────────────────────────────
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct HitResult
    {
        [FieldOffset( 0)] public uint  ProjId;
        [FieldOffset( 4)] public uint  ProjIndex;
        [FieldOffset( 8)] public uint  TargetId;
        [FieldOffset(12)] public float TravelDist;
        [FieldOffset(16)] public float HitX;
        [FieldOffset(20)] public float HitY;
    }

    // ── CollisionTarget — 20 bytes ────────────────────────────────────────────
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct CollisionTarget
    {
        [FieldOffset( 0)] public float X;
        [FieldOffset( 4)] public float Y;
        [FieldOffset( 8)] public float Radius;
        [FieldOffset(12)] public uint  TargetId;
        [FieldOffset(16)] public byte  Active;
        // 3 bytes padding at 17-19 matches Rust _pad: [u8; 3]
    }

    // ── SpawnRequest — 32 bytes ───────────────────────────────────────────────
    // FIXED: Rust struct is 32 bytes (includes RngSeed + BaseProjId).
    // Previously C# allocated only 24, causing Rust to read garbage for
    // rng_seed/base_proj_id — patterns were non-deterministic across clients.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct SpawnRequest
    {
        [FieldOffset( 0)] public float  OriginX;
        [FieldOffset( 4)] public float  OriginY;
        [FieldOffset( 8)] public float  AngleDeg;
        [FieldOffset(12)] public float  Speed;
        [FieldOffset(16)] public ushort ConfigId;
        [FieldOffset(18)] public ushort OwnerId;
        [FieldOffset(20)] public byte   PatternId;  // see PatternId enum
        // 3 bytes padding at 21-23 — matches Rust _pad: [u8; 3]
        [FieldOffset(24)] public uint   RngSeed;    // deterministic spread variance
        [FieldOffset(28)] public uint   BaseProjId; // C# assigns; Rust fills proj_id sequentially
    }

    // ── Enums matching Rust constants ─────────────────────────────────────────

    public enum MovementType : byte
    {
        Straight  = 0,
        Arching   = 1,
        Guided    = 2,
        Teleport  = 3,
    }

    public enum PiercingType : byte
    {
        None          = 0,
        Piercer       = 1,
        RandomPiercer = 2,
    }

    public enum PatternId : byte
    {
        Single  = 0,
        Spread3 = 1,
        Spread5 = 2,
        Spiral  = 3,
        Ring8   = 4,
    }
}
