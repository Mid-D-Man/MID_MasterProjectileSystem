// BurstProjectileSimulation.cs
// Burst/Jobs-based projectile manager — drop-in comparison with ProjectileManager.
//
// API surface is intentionally identical to ProjectileManager so you can swap
// one for the other in WeaponNetworkBridge / benchmarks without touching callers.
//
// Architecture:
//   NativeArray<NativeProjectile> — persistent, pinned for the lifetime of the scene.
//   BurstTickJob      (IJobParallelFor) — physics update, runs at FixedUpdate.
//   BurstCollisionJob (IJob)           — spatial-grid hit detection.
//   BurstCompactJob   (IJob)           — swap-compact dead slots, drains trail pool.
//   BurstSpawnJob     (IJobParallelFor)— pattern-free spawn into a temp array.
//
// Frame order:
//   1. FixedUpdate: schedule Tick job.
//   2. Complete Tick.
//   3. Schedule Collision job.
//   4. Complete Collision, process HitResults on main thread.
//   5. Schedule Compact job.
//   6. Complete Compact, release trail slots for dead proj ids.
//   7. Renderer.Render() reads NativeArray directly (no copy).
//
// Requires: com.unity.burst, com.unity.collections, com.unity.mathematics

using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    public class BurstProjectileSimulation : MonoBehaviour
    {
        public static BurstProjectileSimulation Instance { get; private set; }

        // ── Config ────────────────────────────────────────────────────────────

        [Header("Capacity")]
        [SerializeField] int _maxProjectiles  = 2048;
        [SerializeField] int _maxHitsPerTick  = 256;
        [SerializeField] int _maxTargets      = 128;
        [SerializeField] int _maxDeadPerFrame = 512;

        [Header("Grid")]
        [Tooltip("World units per collision grid cell. 0 = auto (4.0). Set to ~2x largest target radius.")]
        [SerializeField] float _cellSize = 4f;

        [Header("Debug")]
        [SerializeField] bool _logFrameTimes = false;

        // ── Native arrays (persistent) ────────────────────────────────────────

        NativeArray<NativeProjectile> _projs;
        NativeArray<CollisionTarget>  _targets;
        NativeArray<HitResult>        _hits;
        NativeArray<int>              _hitCount;     // length 1
        NativeArray<int>              _gridBuffer;   // collision grid scratch
        NativeArray<int>              _activeCount;  // length 1
        NativeArray<uint>             _deadIds;
        NativeArray<int>              _deadCount;    // length 1

        // ── State ─────────────────────────────────────────────────────────────

        int  _targetCount;
        uint _nextProjId = 1;

        // Cached result copy for OnHit dispatch (main thread reads after job.Complete).
        HitResult[] _hitCache;

        // ── Renderer / trail hook (same pattern as ProjectileManager) ─────────

        TrailObjectPool      _trailPool;
        ProjectileRenderer2D _renderer;

        // ── Events ────────────────────────────────────────────────────────────

        public event Action<HitResult> OnHit;

        // ── Public accessors ──────────────────────────────────────────────────

        public int MaxProjectiles => _maxProjectiles;
        public int ActiveCount    => _activeCount.IsCreated ? _activeCount[0] : 0;

        /// Direct read-only view of the live projectile array for external renderers.
        /// Valid between FixedUpdate Complete() calls only — do not cache across frames.
        public NativeArray<NativeProjectile>.ReadOnly ProjectilesReadOnly =>
            _projs.AsReadOnly();

        // ─────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            AllocateArrays();
            _trailPool = GetComponent<TrailObjectPool>();
            _renderer  = GetComponent<ProjectileRenderer2D>();
        }

        void OnDestroy()
        {
            DisposeArrays();
        }

        void FixedUpdate()
        {
            if (ActiveCount == 0) return;

            int active = _activeCount[0];
            float dt   = Time.fixedDeltaTime;

            // ── Tick ──────────────────────────────────────────────────────────
            var tickJob = new BurstTickJob
            {
                Projectiles = _projs,
                DeltaTime   = dt,
            }.Schedule(active, 64);

            // ── Collision (depends on tick) ───────────────────────────────────
            _hitCount[0] = 0;

            // Reset grid keys to EMPTY before job runs.
            // We do this on the main thread before scheduling to avoid a
            // dependency chain (a reset job would just add latency).
            ResetGridKeys();

            var colJob = new BurstCollisionJob
            {
                Projectiles = _projs,
                Targets     = _targets,
                OutHits     = _hits,
                HitCountOut = _hitCount,
                CellSize    = _cellSize,
                GridBuffer  = _gridBuffer,
            }.Schedule(tickJob);

            colJob.Complete();

            // ── Process hits (main thread) ────────────────────────────────────
            int hitCnt = _hitCount[0];
            EnsureHitCache(hitCnt);
            for (int i = 0; i < hitCnt; i++)
            {
                _hitCache[i] = _hits[i];
                HandlePiercingOrKill(ref _hits.GetRef(i));
            }
            for (int i = 0; i < hitCnt; i++)
                OnHit?.Invoke(_hitCache[i]);

            // ── Compact dead slots ────────────────────────────────────────────
            _deadCount[0] = 0;
            new BurstCompactJob
            {
                Projectiles      = _projs,
                ActiveCountInOut = _activeCount,
                DeadProjIds      = _deadIds,
                DeadCountOut     = _deadCount,
            }.Schedule().Complete();

            // Release trail slots for each projectile that died this frame.
            int dead = _deadCount[0];
            if (_trailPool != null)
                for (int i = 0; i < dead; i++)
                    _trailPool.NotifyDead(_deadIds[i]);

            // ── Render ────────────────────────────────────────────────────────
            _renderer?.Render(_projs, _activeCount[0]);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────────────

        /// Spawn projectiles — identical call signature to ProjectileManager.Spawn.
        /// Burst parallel job fills structs directly into the live array.
        public void Spawn(
            ushort  configId,
            Vector2 origin,
            float   angleDeg,
            float   speed,
            float   latencyComp,
            ushort  ownerId,
            uint    seed)
        {
            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null) return;

            int patternCount = PatternCountFor(cfg.Pattern);
            int freeSlots    = _maxProjectiles - _activeCount[0];
            int spawnCount   = Mathf.Min(patternCount, freeSlots);
            if (spawnCount <= 0) return;

            // Temp array — Burst writes spawn results here, main thread copies in.
            var temp       = new NativeArray<NativeProjectile>(spawnCount, Allocator.TempJob);
            float scaleX   = cfg.FullSizeX * cfg.SpawnScaleFraction;
            float scaleY   = cfg.FullSizeY * cfg.SpawnScaleFraction;
            float scaleSpd = cfg.SpawnScaleFraction < 0.999f ? cfg.GrowthSpeed : 0f;

            new BurstSpawnJob
            {
                Out          = temp,
                OriginX      = origin.x,
                OriginY      = origin.y,
                AngleDeg     = angleDeg,
                Speed        = speed,
                SpreadDeg    = SpreadDegFor(cfg.Pattern),
                Lifetime     = cfg.Lifetime,
                ConfigId     = configId,
                OwnerId      = ownerId,
                BaseProjId   = _nextProjId,
                MovementType = (byte)cfg.Movement,
                Ay           = cfg.GravityScale,
                ScaleX       = scaleX,
                ScaleY       = scaleY,
                ScaleTarget  = cfg.FullSizeX,
                ScaleSpeed   = scaleSpd,
            }.Schedule(spawnCount, 4).Complete();

            _nextProjId += (uint)spawnCount;

            // Copy temp results into the live array, applying latency compensation.
            int active = _activeCount[0];
            for (int i = 0; i < spawnCount; i++)
            {
                var p = temp[i];
                if (latencyComp > 0f)
                {
                    p.X        += p.Vx * latencyComp;
                    p.Y        += p.Vy * latencyComp;
                    p.Lifetime -= latencyComp;
                    if (p.Lifetime <= 0f) continue;
                }
                _projs[active++] = p;
            }
            _activeCount[0] = active;
            temp.Dispose();
        }

        public void RegisterTarget(uint targetId, Vector2 pos, float radius)
        {
            for (int i = 0; i < _targetCount; i++)
            {
                if (_targets[i].TargetId != targetId) continue;
                _targets[i] = new CollisionTarget { X = pos.x, Y = pos.y,
                    Radius = radius, TargetId = targetId, Active = 1 };
                return;
            }
            if (_targetCount >= _maxTargets) return;
            _targets[_targetCount++] = new CollisionTarget
                { X = pos.x, Y = pos.y, Radius = radius, TargetId = targetId, Active = 1 };
        }

        public void DeregisterTarget(uint targetId)
        {
            for (int i = 0; i < _targetCount; i++)
                if (_targets[i].TargetId == targetId)
                {
                    var t = _targets[i]; t.Active = 0; _targets[i] = t;
                    return;
                }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Save / Restore (rollback netcode support)
        // ─────────────────────────────────────────────────────────────────────

        /// Snapshot current state into a caller-owned byte array.
        /// Returns bytes written (= ActiveCount * 72), or 0 on failure.
        public int SaveState(byte[] buf)
        {
            int active  = _activeCount[0];
            int needed  = active * 72;
            if (buf == null || buf.Length < needed) return 0;

            var bh = GCHandle.Alloc(buf, GCHandleType.Pinned);
            unsafe
            {
                var src = (byte*)Unity.Collections.LowLevel.Unsafe
                              .NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(_projs);
                var dst = (byte*)bh.AddrOfPinnedObject().ToPointer();
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(dst, src, needed);
            }
            bh.Free();
            return needed;
        }

        /// Restore state from a snapshot.  Returns number of projectiles restored.
        public int RestoreState(byte[] buf, int byteCount)
        {
            if (buf == null || byteCount <= 0) return 0;
            int count = byteCount / 72;
            count = Mathf.Min(count, _maxProjectiles);

            var bh = GCHandle.Alloc(buf, GCHandleType.Pinned);
            unsafe
            {
                var src = (byte*)bh.AddrOfPinnedObject().ToPointer();
                var dst = (byte*)Unity.Collections.LowLevel.Unsafe
                              .NativeArrayUnsafeUtility.GetUnsafePtr(_projs);
                Unity.Collections.LowLevel.Unsafe.UnsafeUtility.MemCpy(dst, src, count * 72);
            }
            bh.Free();
            _activeCount[0] = count;
            return count;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Internals
        // ─────────────────────────────────────────────────────────────────────

        void AllocateArrays()
        {
            _projs       = new NativeArray<NativeProjectile>(_maxProjectiles, Allocator.Persistent);
            _targets     = new NativeArray<CollisionTarget>(_maxTargets,      Allocator.Persistent);
            _hits        = new NativeArray<HitResult>(_maxHitsPerTick,        Allocator.Persistent);
            _hitCount    = new NativeArray<int>(1,                            Allocator.Persistent);
            _gridBuffer  = new NativeArray<int>(BurstCollisionJob.GRID_TOTAL_INTS, Allocator.Persistent);
            _activeCount = new NativeArray<int>(1,                            Allocator.Persistent);
            _deadIds     = new NativeArray<uint>(_maxDeadPerFrame,            Allocator.Persistent);
            _deadCount   = new NativeArray<int>(1,                            Allocator.Persistent);

            // GridBuffer keys must start as EMPTY.
            ResetGridKeys();
        }

        void DisposeArrays()
        {
            if (_projs.IsCreated)       _projs.Dispose();
            if (_targets.IsCreated)     _targets.Dispose();
            if (_hits.IsCreated)        _hits.Dispose();
            if (_hitCount.IsCreated)    _hitCount.Dispose();
            if (_gridBuffer.IsCreated)  _gridBuffer.Dispose();
            if (_activeCount.IsCreated) _activeCount.Dispose();
            if (_deadIds.IsCreated)     _deadIds.Dispose();
            if (_deadCount.IsCreated)   _deadCount.Dispose();
        }

        void ResetGridKeys()
        {
            const int EMPTY = unchecked((int)0xFFFF_FFFF);
            for (int i = 0; i < BurstCollisionJob.GRID_BUCKETS; i++)
                _gridBuffer[i] = EMPTY;
        }

        void HandlePiercingOrKill(ref HitResult hit)
        {
            int idx = (int)hit.ProjIndex;
            if (idx >= _activeCount[0]) return;

            var p = _projs[idx];
            if (p.PiercingType == (byte)PiercingType.None)
            {
                p.Alive = 0;
            }
            else
            {
                p.CollisionCount++;
                var cfg = ProjectileRegistry.Instance.Get(p.ConfigId);
                if (cfg != null && p.CollisionCount >= cfg.MaxCollisions) p.Alive = 0;
            }
            _projs[idx] = p;
        }

        void EnsureHitCache(int count)
        {
            if (_hitCache == null || _hitCache.Length < count)
                _hitCache = new HitResult[Mathf.Max(count, _maxHitsPerTick)];
        }

        // ── Pattern helpers ───────────────────────────────────────────────────

        static int PatternCountFor(PatternId p) => p switch
        {
            PatternId.Single  => 1,
            PatternId.Spread3 => 3,
            PatternId.Spread5 => 5,
            PatternId.Spiral  => 12,
            PatternId.Ring8   => 8,
            _                 => 1,
        };

        static float SpreadDegFor(PatternId p) => p switch
        {
            PatternId.Spread3 => 20f,
            PatternId.Spread5 => 15f,
            _                 => 0f,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  NativeArray<T> ref helper — avoids struct copy on hit write-back.
    //  Burst-safe because it uses UnsafeUtility directly.
    // ─────────────────────────────────────────────────────────────────────────

    internal static class NativeArrayExt
    {
        public static unsafe ref T GetRef<T>(
            this NativeArray<T> array, int index) where T : unmanaged
        {
            return ref ((T*)Unity.Collections.LowLevel.Unsafe
                .NativeArrayUnsafeUtility.GetUnsafePtr(array))[index];
        }
    }
}
