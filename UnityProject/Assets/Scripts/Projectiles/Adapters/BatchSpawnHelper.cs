// BatchSpawnHelper.cs
// Spawn path decider and batch builder.
//
// Problem this solves:
//   Rust spawn_pattern has 928µs per-call FFI overhead.
//   Spawning 8 shotgun pellets individually = 8 × 928µs = 7.4ms wasted on FFI, not math.
//
// Solution:
//   1. C# or Burst builds ALL spawn structs in one array (using pattern library).
//   2. Call spawn_batch ONCE — single FFI crossing for any number of projectiles.
//   3. For 8+ projectiles, BurstSpawnJob fills the array in parallel before the FFI call.
//   4. For < 8 projectiles, a simple C# loop fills the array — Burst scheduling overhead
//      would cost more than it saves at small counts.
//
// Custom shot patterns:
//   Add a static method to ProjectilePatternLibrary (or directly here).
//   The pattern produces a SpawnPoint[] (world-space origins + directions).
//   BatchSpawnHelper maps SpawnPoint[] → NativeProjectile[] → spawn_batch.
//   Rust never needs to know about the pattern.

using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using MidManStudio.InGame.ProjectileConfigs;

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Spawn point — output of pattern library, input to batch builder
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A single spawn point produced by a shot pattern calculation.
    /// The pattern library fills an array of these; BatchSpawnHelper maps them to NativeProjectile structs.
    /// </summary>
    public struct SpawnPoint
    {
        /// <summary>World-space spawn origin.</summary>
        public Vector3 Origin;

        /// <summary>Normalised travel direction.</summary>
        public Vector3 Direction;

        /// <summary>Speed for this specific projectile (may vary per pellet for spread randomness).</summary>
        public float Speed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Batch threshold
    // ─────────────────────────────────────────────────────────────────────────

    public static class BatchSpawnHelper
    {
        /// <summary>
        /// Minimum projectile count per spawn event to justify BurstSpawnJob scheduling overhead.
        /// Below this threshold, a C# loop is faster than scheduling a job.
        /// Derived from benchmark: Burst job overhead ~0.4µs scheduling + ~0.04µs per projectile.
        /// C# loop: ~0.1µs per projectile. Crossover ≈ 4-6 projectiles; use 8 as conservative threshold.
        /// </summary>
        public const int BurstThreshold = 8;

        // ── Pinned temp buffer for C# fill path ──────────────────────────────
        // Reused across spawns — avoids allocating a new array every fire event.
        // Size covers worst-case pattern count. Not thread-safe — main thread only.
        private static readonly NativeProjectile[]   _temp2D = new NativeProjectile[256];
        private static readonly NativeProjectile3D[] _temp3D = new NativeProjectile3D[256];
        private static GCHandle _pin2D;
        private static GCHandle _pin3D;
        private static bool     _pinsAllocated;

        // ─────────────────────────────────────────────────────────────────────
        //  Initialisation — called once by MID_MasterProjectileSystem.Awake()
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Pin temp buffers so their addresses are stable across FFI calls.
        /// Must be called before any spawn. Unpinned automatically on app quit.
        /// </summary>
        public static void Initialise()
        {
            if (_pinsAllocated) return;
            _pin2D = GCHandle.Alloc(_temp2D, GCHandleType.Pinned);
            _pin3D = GCHandle.Alloc(_temp3D, GCHandleType.Pinned);
            _pinsAllocated = true;
        }

        /// <summary>Unpin temp buffers. Called by MID_MasterProjectileSystem.OnDestroy().</summary>
        public static void Shutdown()
        {
            if (!_pinsAllocated) return;
            if (_pin2D.IsAllocated) _pin2D.Free();
            if (_pin3D.IsAllocated) _pin3D.Free();
            _pinsAllocated = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  2D Spawn
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Spawn a batch of 2D projectiles from pre-computed spawn points.
        ///
        /// Flow:
        ///   1. Fill _temp2D with NativeProjectile structs (Burst if count >= BurstThreshold).
        ///   2. Call spawn_batch once to copy them into the server sim buffer.
        ///   3. Return how many were written so ServerProjectileAuthority can update activeCount.
        ///
        /// projsOutPtr  — pointer to current end of the 2D sim buffer (base + activeCount * 72)
        /// bufferRemaining — remaining capacity (maxProjectiles - activeCount)
        /// nextProjId   — base proj ID; each spawn point gets nextProjId + i
        /// </summary>
        public static int SpawnBatch2D(
            SpawnPoint[]                     spawnPoints,
            int                              count,
            ProjectileConfigScriptableObject config,
            RustSpawnParams                  rustParams,
            ushort                           configId,
            ushort                           ownerId,
            uint                             nextProjId,
            IntPtr                           projsOutPtr,
            int                              bufferRemaining,
            float                            latencyCompensation = 0f)
        {
            if (!_pinsAllocated)
            {
                Debug.LogError("[BatchSpawnHelper] Not initialised — call Initialise() first.");
                return 0;
            }

            int n = Mathf.Min(count, Mathf.Min(_temp2D.Length, bufferRemaining));
            if (n <= 0) return 0;

            if (n >= BurstThreshold)
            {
                FillBurst2D(spawnPoints, n, rustParams, configId, ownerId, nextProjId);
            }
            else
            {
                FillManaged2D(spawnPoints, n, rustParams, configId, ownerId, nextProjId);
            }

            // Apply latency compensation — offset each spawn position along travel direction
            if (latencyCompensation > 0f)
            {
                for (int i = 0; i < n; i++)
                {
                    ref var p = ref _temp2D[i];
                    if (p.Alive == 0) continue;
                    p.X += p.Vx * latencyCompensation;
                    p.Y += p.Vy * latencyCompensation;
                    p.Lifetime -= latencyCompensation;
                    if (p.Lifetime <= 0f) p.Alive = 0;
                }
            }

            // Single FFI call — all projectiles in one crossing
            ProjectileLib.spawn_batch(
                _pin2D.AddrOfPinnedObject(), n,
                projsOutPtr, bufferRemaining,
                out int written);

            return written;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  3D Spawn
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Spawn a batch of 3D projectiles from pre-computed spawn points.
        /// Identical flow to SpawnBatch2D but fills NativeProjectile3D structs.
        /// </summary>
        public static int SpawnBatch3D(
            SpawnPoint[]                     spawnPoints,
            int                              count,
            RustSpawnParams                  rustParams,
            ushort                           configId,
            ushort                           ownerId,
            uint                             nextProjId,
            IntPtr                           projsOutPtr,
            int                              bufferRemaining,
            float                            latencyCompensation = 0f)
        {
            if (!_pinsAllocated)
            {
                Debug.LogError("[BatchSpawnHelper] Not initialised — call Initialise() first.");
                return 0;
            }

            int n = Mathf.Min(count, Mathf.Min(_temp3D.Length, bufferRemaining));
            if (n <= 0) return 0;

            if (n >= BurstThreshold)
            {
                FillBurst3D(spawnPoints, n, rustParams, configId, ownerId, nextProjId);
            }
            else
            {
                FillManaged3D(spawnPoints, n, rustParams, configId, ownerId, nextProjId);
            }

            if (latencyCompensation > 0f)
            {
                for (int i = 0; i < n; i++)
                {
                    ref var p = ref _temp3D[i];
                    if (p.Alive == 0) continue;
                    p.X += p.Vx * latencyCompensation;
                    p.Y += p.Vy * latencyCompensation;
                    p.Z += p.Vz * latencyCompensation;
                    p.Lifetime -= latencyCompensation;
                    if (p.Lifetime <= 0f) p.Alive = 0;
                }
            }

            ProjectileLib.spawn_batch_3d(
                _pin3D.AddrOfPinnedObject(), n,
                projsOutPtr, bufferRemaining,
                out int written);

            return written;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Managed fill paths (count < BurstThreshold)
        // ─────────────────────────────────────────────────────────────────────

        private static void FillManaged2D(
            SpawnPoint[] pts, int n, RustSpawnParams p,
            ushort configId, ushort ownerId, uint baseId)
        {
            for (int i = 0; i < n; i++)
            {
                float speed = pts[i].Speed > 0f ? pts[i].Speed : p.Speed;
                _temp2D[i] = new NativeProjectile
                {
                    X           = pts[i].Origin.x,
                    Y           = pts[i].Origin.y,
                    Vx          = pts[i].Direction.x * speed,
                    Vy          = pts[i].Direction.y * speed,
                    Ax          = 0f,
                    Ay          = p.GravityAy,
                    AngleDeg    = Mathf.Atan2(pts[i].Direction.y, pts[i].Direction.x) * Mathf.Rad2Deg,
                    CurveT      = 0f,
                    ScaleX      = p.ScaleStart,
                    ScaleY      = p.ScaleStart,
                    ScaleTarget = p.ScaleTarget,
                    ScaleSpeed  = p.ScaleSpeed,
                    Lifetime    = p.Lifetime,
                    MaxLifetime = p.Lifetime,
                    TravelDist  = 0f,
                    ConfigId    = configId,
                    OwnerId     = ownerId,
                    ProjId      = baseId + (uint)i,
                    CollisionCount = 0,
                    MovementType   = p.MovementType,
                    PiercingType   = p.PiercingType,
                    Alive          = 1
                };
            }
        }

        private static void FillManaged3D(
            SpawnPoint[] pts, int n, RustSpawnParams p,
            ushort configId, ushort ownerId, uint baseId)
        {
            for (int i = 0; i < n; i++)
            {
                float speed = pts[i].Speed > 0f ? pts[i].Speed : p.Speed;
                _temp3D[i] = new NativeProjectile3D
                {
                    X           = pts[i].Origin.x,
                    Y           = pts[i].Origin.y,
                    Z           = pts[i].Origin.z,
                    Vx          = pts[i].Direction.x * speed,
                    Vy          = pts[i].Direction.y * speed,
                    Vz          = pts[i].Direction.z * speed,
                    Ax          = 0f,
                    Ay          = p.GravityAy,
                    Az          = 0f,
                    ScaleX      = p.ScaleStart,
                    ScaleY      = p.ScaleStart,
                    ScaleZ      = p.ScaleStart,
                    ScaleTarget = p.ScaleTarget,
                    ScaleSpeed  = p.ScaleSpeed,
                    Lifetime    = p.Lifetime,
                    MaxLifetime = p.Lifetime,
                    TravelDist  = 0f,
                    TimerT      = 0f,
                    ConfigId    = configId,
                    OwnerId     = ownerId,
                    ProjId      = baseId + (uint)i,
                    CollisionCount = 0,
                    MovementType   = p.MovementType,
                    PiercingType   = p.PiercingType,
                    Alive          = 1
                };
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Burst fill paths (count >= BurstThreshold)
        // ─────────────────────────────────────────────────────────────────────

        private static void FillBurst2D(
            SpawnPoint[] pts, int n, RustSpawnParams p,
            ushort configId, ushort ownerId, uint baseId)
        {
            using var nativePts = new NativeArray<SpawnPoint>(pts, Allocator.TempJob);
            using var nativeOut = new NativeArray<NativeProjectile>(n, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            new BurstFill2DJob
            {
                SpawnPoints    = nativePts,
                Out            = nativeOut,
                DefaultSpeed   = p.Speed,
                MovementType   = p.MovementType,
                PiercingType   = p.PiercingType,
                GravityAy      = p.GravityAy,
                Lifetime       = p.Lifetime,
                ScaleStart     = p.ScaleStart,
                ScaleTarget    = p.ScaleTarget,
                ScaleSpeed     = p.ScaleSpeed,
                ConfigId       = configId,
                OwnerId        = ownerId,
                BaseId         = baseId
            }.Schedule(n, 64).Complete();

            nativeOut.CopyTo(_temp2D);
        }

        private static void FillBurst3D(
            SpawnPoint[] pts, int n, RustSpawnParams p,
            ushort configId, ushort ownerId, uint baseId)
        {
            using var nativePts = new NativeArray<SpawnPoint>(pts, Allocator.TempJob);
            using var nativeOut = new NativeArray<NativeProjectile3D>(n, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            new BurstFill3DJob
            {
                SpawnPoints    = nativePts,
                Out            = nativeOut,
                DefaultSpeed   = p.Speed,
                MovementType   = p.MovementType,
                PiercingType   = p.PiercingType,
                GravityAy      = p.GravityAy,
                Lifetime       = p.Lifetime,
                ScaleStart     = p.ScaleStart,
                ScaleTarget    = p.ScaleTarget,
                ScaleSpeed     = p.ScaleSpeed,
                ConfigId       = configId,
                OwnerId        = ownerId,
                BaseId         = baseId
            }.Schedule(n, 64).Complete();

            nativeOut.CopyTo(_temp3D);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Burst jobs — parallel struct init for 8+ projectiles
    //  [BurstCompile] on both class and method required for Burst to compile them.
    //  No managed references allowed inside job Execute() methods.
    // ─────────────────────────────────────────────────────────────────────────

    [BurstCompile]
    public struct BurstFill2DJob : IJobParallelFor
    {
        [ReadOnly]  public NativeArray<SpawnPoint>      SpawnPoints;
        [WriteOnly] public NativeArray<NativeProjectile> Out;

        public float  DefaultSpeed;
        public byte   MovementType;
        public byte   PiercingType;
        public float  GravityAy;
        public float  Lifetime;
        public float  ScaleStart;
        public float  ScaleTarget;
        public float  ScaleSpeed;
        public ushort ConfigId;
        public ushort OwnerId;
        public uint   BaseId;

        [BurstCompile]
        public void Execute(int i)
        {
            var pt    = SpawnPoints[i];
            float spd = pt.Speed > 0f ? pt.Speed : DefaultSpeed;
            float ang  = math.atan2(pt.Direction.y, pt.Direction.x) * math.degrees(1f);

            Out[i] = new NativeProjectile
            {
                X           = pt.Origin.x,
                Y           = pt.Origin.y,
                Vx          = pt.Direction.x * spd,
                Vy          = pt.Direction.y * spd,
                Ax          = 0f,
                Ay          = GravityAy,
                AngleDeg    = ang,
                CurveT      = 0f,
                ScaleX      = ScaleStart,
                ScaleY      = ScaleStart,
                ScaleTarget = ScaleTarget,
                ScaleSpeed  = ScaleSpeed,
                Lifetime    = Lifetime,
                MaxLifetime = Lifetime,
                TravelDist  = 0f,
                ConfigId    = ConfigId,
                OwnerId     = OwnerId,
                ProjId      = BaseId + (uint)i,
                CollisionCount = 0,
                MovementType   = MovementType,
                PiercingType   = PiercingType,
                Alive          = 1
            };
        }
    }

    [BurstCompile]
    public struct BurstFill3DJob : IJobParallelFor
    {
        [ReadOnly]  public NativeArray<SpawnPoint>        SpawnPoints;
        [WriteOnly] public NativeArray<NativeProjectile3D> Out;

        public float  DefaultSpeed;
        public byte   MovementType;
        public byte   PiercingType;
        public float  GravityAy;
        public float  Lifetime;
        public float  ScaleStart;
        public float  ScaleTarget;
        public float  ScaleSpeed;
        public ushort ConfigId;
        public ushort OwnerId;
        public uint   BaseId;

        [BurstCompile]
        public void Execute(int i)
        {
            var pt    = SpawnPoints[i];
            float spd = pt.Speed > 0f ? pt.Speed : DefaultSpeed;

            Out[i] = new NativeProjectile3D
            {
                X           = pt.Origin.x,
                Y           = pt.Origin.y,
                Z           = pt.Origin.z,
                Vx          = pt.Direction.x * spd,
                Vy          = pt.Direction.y * spd,
                Vz          = pt.Direction.z * spd,
                Ax          = 0f,
                Ay          = GravityAy,
                Az          = 0f,
                ScaleX      = ScaleStart,
                ScaleY      = ScaleStart,
                ScaleZ      = ScaleStart,
                ScaleTarget = ScaleTarget,
                ScaleSpeed  = ScaleSpeed,
                Lifetime    = Lifetime,
                MaxLifetime = Lifetime,
                TravelDist  = 0f,
                TimerT      = 0f,
                ConfigId    = ConfigId,
                OwnerId     = OwnerId,
                ProjId      = BaseId + (uint)i,
                CollisionCount = 0,
                MovementType   = MovementType,
                PiercingType   = PiercingType,
                Alive          = 1
            };
        }
    }
}
