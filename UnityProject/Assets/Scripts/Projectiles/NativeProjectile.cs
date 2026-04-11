// NativeProjectile.cs
// MUST match the Rust NativeProjectile struct in src/lib.rs exactly.
// Uses explicit field offsets — do not change without updating Rust side.

using System.Runtime.InteropServices;

namespace MidManStudio.Projectiles
{
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct NativeProjectile
    {
        // ── Physics (Rust writes every tick) ─────────────
        [FieldOffset(0)]  public float X;
        [FieldOffset(4)]  public float Y;
        [FieldOffset(8)]  public float Vx;
        [FieldOffset(12)] public float Vy;
        [FieldOffset(16)] public float Ax;          // lateral accel / homing dir X
        [FieldOffset(20)] public float Ay;          // gravity / homing dir Y
        [FieldOffset(24)] public float AngleDeg;    // visual rotation
        [FieldOffset(28)] public float CurveT;      // arc interpolation param

        // ── Visual (Rust updates, renderer reads) ─────────
        [FieldOffset(32)] public float ScaleX;
        [FieldOffset(36)] public float ScaleY;
        [FieldOffset(40)] public float ScaleTarget;
        [FieldOffset(44)] public float ScaleSpeed;

        // ── Lifetime / distance ───────────────────────────
        [FieldOffset(48)] public float Lifetime;
        [FieldOffset(52)] public float MaxLifetime;
        [FieldOffset(56)] public float TravelDist;

        // ── Identity (C# writes once on spawn) ────────────
        [FieldOffset(60)] public ushort ConfigId;
        [FieldOffset(62)] public ushort OwnerId;
        [FieldOffset(64)] public uint   ProjId;

        // ── State flags ────────────────────────────────────
        [FieldOffset(68)] public byte CollisionCount;
        [FieldOffset(69)] public byte MovementType;  // see MovementType enum
        [FieldOffset(70)] public byte PiercingType;  // see PiercingType enum
        [FieldOffset(71)] public byte Alive;         // 0 = dead, 1 = alive
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct HitResult
    {
        [FieldOffset(0)]  public uint  ProjId;
        [FieldOffset(4)]  public uint  ProjIndex;
        [FieldOffset(8)]  public uint  TargetId;
        [FieldOffset(12)] public float TravelDist;
        [FieldOffset(16)] public float HitX;
        [FieldOffset(20)] public float HitY;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct CollisionTarget
    {
        [FieldOffset(0)]  public float X;
        [FieldOffset(4)]  public float Y;
        [FieldOffset(8)]  public float Radius;
        [FieldOffset(12)] public uint  TargetId;
        [FieldOffset(16)] public byte  Active;
        // 3 bytes padding at 17-19 matches Rust _pad: [u8; 3]
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct SpawnRequest
    {
        [FieldOffset(0)]  public float  OriginX;
        [FieldOffset(4)]  public float  OriginY;
        [FieldOffset(8)]  public float  AngleDeg;
        [FieldOffset(12)] public float  Speed;
        [FieldOffset(16)] public ushort ConfigId;
        [FieldOffset(18)] public ushort OwnerId;
        [FieldOffset(20)] public byte   PatternId;  // see PatternId enum
        // 3 bytes pad at 21-23
        // NOTE: RngSeed and BaseProjId added below; extend Size if you add them
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
        None         = 0,
        Piercer      = 1,
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
