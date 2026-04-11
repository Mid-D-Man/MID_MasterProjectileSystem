// ProjectileManager.cs
// Owns the pinned NativeProjectile array, drives Rust every FixedUpdate,
// processes hits, and coordinates the trail pool + renderer.

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    public class ProjectileManager : MonoBehaviour
    {
        public static ProjectileManager Instance { get; private set; }

        // ── Config ────────────────────────────────────────────────────────────
        [Header("Capacity")]
        [Tooltip("Maximum simultaneous projectiles. Power of 2 preferred.")]
        [SerializeField] private int _maxProjectiles = 2048;
        [SerializeField] private int _maxHitsPerTick = 256;
        [SerializeField] private int _maxTargets     = 128;

        // ── Runtime arrays (pinned — no GC pressure during gameplay) ──────────
        private NativeProjectile[]  _projs;
        private HitResult[]         _hits;
        private CollisionTarget[]   _targets;

        private GCHandle _projHandle;
        private GCHandle _hitHandle;
        private GCHandle _targetHandle;

        private IntPtr _projPtr;
        private IntPtr _hitPtr;
        private IntPtr _targetPtr;

        // ── Bookkeeping ───────────────────────────────────────────────────────
        private int  _activeCount;   // slots used (alive + recently-dead)
        private uint _nextProjId = 1;
        private int  _targetCount;

        // ── Sub-systems ───────────────────────────────────────────────────────
        private TrailObjectPool      _trailPool;
        private ProjectileRenderer2D _renderer;

        // ── Events ────────────────────────────────────────────────────────────
        // WeaponNetworkBridge subscribes to confirm hits server-side
        public event Action<HitResult> OnHit;

        // ─────────────────────────────────────────────────────────────────────

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            AllocateAndPin();

            _trailPool = GetComponent<TrailObjectPool>();
            _renderer  = GetComponent<ProjectileRenderer2D>();
        }

        private void AllocateAndPin()
        {
            _projs   = new NativeProjectile[_maxProjectiles];
            _hits    = new HitResult[_maxHitsPerTick];
            _targets = new CollisionTarget[_maxTargets];

            _projHandle   = GCHandle.Alloc(_projs,   GCHandleType.Pinned);
            _hitHandle    = GCHandle.Alloc(_hits,    GCHandleType.Pinned);
            _targetHandle = GCHandle.Alloc(_targets, GCHandleType.Pinned);

            _projPtr   = _projHandle.AddrOfPinnedObject();
            _hitPtr    = _hitHandle.AddrOfPinnedObject();
            _targetPtr = _targetHandle.AddrOfPinnedObject();

            Debug.Log(
                $"[ProjectileManager] Pinned arrays: " +
                $"{_maxProjectiles} projectiles, {_maxHitsPerTick} hit slots, " +
                $"{_maxTargets} target slots.");
        }

        void OnDestroy()
        {
            if (_projHandle.IsAllocated)   _projHandle.Free();
            if (_hitHandle.IsAllocated)    _hitHandle.Free();
            if (_targetHandle.IsAllocated) _targetHandle.Free();
        }

        // ─── Main loop ────────────────────────────────────────────────────────

        void FixedUpdate()
        {
            if (_activeCount == 0) return;

            // 1. Tick physics in Rust
            int died = ProjectileLib.tick_projectiles(
                _projPtr, _activeCount, Time.fixedDeltaTime);

            // 2. Collision
            ProjectileLib.check_hits_grid(
                _projPtr,    _activeCount,
                _targetPtr,  _targetCount,
                _hitPtr,     _maxHitsPerTick,
                out int hitCount);

            // 3. Dispatch hits (server validates, client plays VFX)
            for (int i = 0; i < hitCount; i++)
            {
                OnHit?.Invoke(_hits[i]);
                HandlePiercingOrKill(ref _hits[i]);
            }

            // 4. Compact dead slots (swap-remove to keep array dense)
            CompactDeadSlots();

            // 5. Trail pool follows projectile positions
            _trailPool?.SyncToSimulation(_projs, _activeCount);

            // 6. Render — 1 draw call regardless of count
            _renderer?.Render(_projs, _activeCount);
        }

        // ─── Spawn ────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by WeaponNetworkBridge when a spawn RPC arrives.
        /// latencyComp is elapsed seconds since the spawn tick
        /// (used to forward-simulate the projectile to where it should be now).
        /// </summary>
        public void Spawn(
            ushort configId,
            Vector2 origin,
            float angleDeg,
            float speed,
            float latencyComp,
            ushort ownerId,
            uint seed)
        {
            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null) return;

            // Build request on stack — no alloc
            var req = new SpawnRequest
            {
                OriginX  = origin.x,
                OriginY  = origin.y,
                AngleDeg = angleDeg,
                Speed    = speed,
                ConfigId = configId,
                OwnerId  = ownerId,
                PatternId = (byte)cfg.Pattern,
            };

            // Temp buffer for spawned projectiles
            // Use a fixed-size stack array via stackalloc equivalent
            var tempBuf = new NativeProjectile[32];
            var tempHandle = GCHandle.Alloc(tempBuf, GCHandleType.Pinned);
            IntPtr tempPtr = tempHandle.AddrOfPinnedObject();

            var reqHandle = GCHandle.Alloc(req, GCHandleType.Pinned);
            IntPtr reqPtr = reqHandle.AddrOfPinnedObject();

            try
            {
                ProjectileLib.spawn_pattern(reqPtr, tempPtr, 32, out int spawnCount);

                for (int i = 0; i < spawnCount && _activeCount < _maxProjectiles; i++)
                {
                    var p = tempBuf[i];

                    // C# fills config-driven fields Rust doesn't know about
                    p.Lifetime      = cfg.Lifetime;
                    p.MaxLifetime   = cfg.Lifetime;
                    p.MovementType  = (byte)cfg.Movement;
                    p.PiercingType  = (byte)cfg.Piercing;
                    p.Ay            = cfg.GravityScale;
                    p.ScaleX        = cfg.FullSizeX * cfg.SpawnScaleFraction;
                    p.ScaleY        = cfg.FullSizeY * cfg.SpawnScaleFraction;
                    p.ScaleTarget   = cfg.FullSizeX; // grow to full X (uniform)
                    p.ScaleSpeed    = cfg.GrowthSpeed;
                    p.ProjId        = _nextProjId++;

                    // Latency compensation — forward-simulate
                    if (latencyComp > 0f)
                    {
                        p.X += p.Vx * latencyComp;
                        p.Y += p.Vy * latencyComp;
                        p.Lifetime -= latencyComp;
                        if (p.Lifetime <= 0f) continue; // don't bother spawning
                    }

                    _projs[_activeCount++] = p;
                }
            }
            finally
            {
                reqHandle.Free();
                tempHandle.Free();
            }
        }

        // ─── Targets ─────────────────────────────────────────────────────────

        /// <summary>Register or update a collision target (enemy, player wall).</summary>
        public void RegisterTarget(uint targetId, Vector2 pos, float radius)
        {
            // Find existing slot for this ID or allocate new
            for (int i = 0; i < _targetCount; i++)
            {
                if (_targets[i].TargetId == targetId)
                {
                    _targets[i].X      = pos.x;
                    _targets[i].Y      = pos.y;
                    _targets[i].Radius = radius;
                    _targets[i].Active = 1;
                    return;
                }
            }
            if (_targetCount >= _maxTargets) return;
            _targets[_targetCount++] = new CollisionTarget
            {
                X        = pos.x,
                Y        = pos.y,
                Radius   = radius,
                TargetId = targetId,
                Active   = 1,
            };
        }

        public void DeregisterTarget(uint targetId)
        {
            for (int i = 0; i < _targetCount; i++)
            {
                if (_targets[i].TargetId == targetId)
                {
                    _targets[i].Active = 0;
                    return;
                }
            }
        }

        // ─── Internals ────────────────────────────────────────────────────────

        private void HandlePiercingOrKill(ref HitResult hit)
        {
            int idx = (int)hit.ProjIndex;
            if (idx >= _activeCount) return;

            ref var p = ref _projs[idx];
            if (p.PiercingType == (byte)PiercingType.None)
            {
                p.Alive = 0; // kill on first hit
            }
            else
            {
                p.CollisionCount++;
                var cfg = ProjectileRegistry.Instance.Get(p.ConfigId);
                if (p.CollisionCount >= cfg.MaxCollisions)
                    p.Alive = 0;
            }
        }

        /// Swap-remove dead slots to keep the array dense without shifting.
        private void CompactDeadSlots()
        {
            int i = 0;
            while (i < _activeCount)
            {
                if (_projs[i].Alive == 0)
                {
                    // Notify trail pool this proj_id is gone
                    _trailPool?.NotifyDead(_projs[i].ProjId);

                    // Swap with last active slot
                    _activeCount--;
                    if (i < _activeCount)
                        _projs[i] = _projs[_activeCount];
                    // Don't increment i — re-check swapped slot
                }
                else
                {
                    i++;
                }
            }
        }
    }
}
