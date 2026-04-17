using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    public class ProjectileManager : MonoBehaviour
    {
        public static ProjectileManager Instance { get; private set; }

        [Header("Capacity")]
        [SerializeField] private int _maxProjectiles = 2048;
        [SerializeField] private int _maxHitsPerTick = 256;
        [SerializeField] private int _maxTargets     = 128;

        private NativeProjectile[]  _projs;
        private HitResult[]         _hits;
        private CollisionTarget[]   _targets;

        private GCHandle _projHandle;
        private GCHandle _hitHandle;
        private GCHandle _targetHandle;

        private IntPtr _projPtr;
        private IntPtr _hitPtr;
        private IntPtr _targetPtr;

        private int  _activeCount;
        private uint _nextProjId = 1;
        private int  _targetCount;

        private TrailObjectPool      _trailPool;
        private ProjectileRenderer2D _renderer;

        public event Action<HitResult> OnHit;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
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
        }

        void OnDestroy()
        {
            if (_projHandle.IsAllocated)   _projHandle.Free();
            if (_hitHandle.IsAllocated)    _hitHandle.Free();
            if (_targetHandle.IsAllocated) _targetHandle.Free();
        }

        void FixedUpdate()
        {
            if (_activeCount == 0) return;

            ProjectileLib.tick_projectiles(_projPtr, _activeCount, Time.fixedDeltaTime);

            ProjectileLib.check_hits_grid(
                _projPtr,   _activeCount,
                _targetPtr, _targetCount,
                _hitPtr,    _maxHitsPerTick,
                out int hitCount);

            for (int i = 0; i < hitCount; i++)
            {
                OnHit?.Invoke(_hits[i]);
                HandlePiercingOrKill(ref _hits[i]);
            }

            CompactDeadSlots();
            _trailPool?.SyncToSimulation(_projs, _activeCount);
            _renderer?.Render(_projs, _activeCount);
        }

        /// <summary>
        /// Spawn projectiles from a config. Called by WeaponNetworkBridge.
        /// seed is passed from the server RPC so all clients get identical patterns.
        /// </summary>
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

            // BaseProjId tells Rust where to start assigning proj_ids
            // so IDs are globally unique across all spawn calls.
            var req = new SpawnRequest
            {
                OriginX    = origin.x,
                OriginY    = origin.y,
                AngleDeg   = angleDeg,
                Speed      = speed,
                ConfigId   = configId,
                OwnerId    = ownerId,
                PatternId  = (byte)cfg.Pattern,
                RngSeed    = seed,          // deterministic spread variance
                BaseProjId = _nextProjId,   // Rust fills proj_id from here upward
            };

            var tempBuf    = new NativeProjectile[32];
            var tempHandle = GCHandle.Alloc(tempBuf, GCHandleType.Pinned);
            IntPtr tempPtr = tempHandle.AddrOfPinnedObject();

            var reqHandle = GCHandle.Alloc(req, GCHandleType.Pinned);
            IntPtr reqPtr = reqHandle.AddrOfPinnedObject();

            try
            {
                ProjectileLib.spawn_pattern(reqPtr, tempPtr, 32, out int spawnCount);

                // Advance ID counter past however many Rust will have assigned
                _nextProjId += (uint)spawnCount;

                for (int i = 0; i < spawnCount && _activeCount < _maxProjectiles; i++)
                {
                    var p = tempBuf[i];

                    p.Lifetime     = cfg.Lifetime;
                    p.MaxLifetime  = cfg.Lifetime;
                    p.MovementType = (byte)cfg.Movement;
                    p.PiercingType = (byte)cfg.Piercing;
                    p.Ay           = cfg.GravityScale;

                    p.ScaleX      = cfg.FullSizeX * cfg.SpawnScaleFraction;
                    p.ScaleY      = cfg.FullSizeY * cfg.SpawnScaleFraction;
                    p.ScaleTarget = cfg.FullSizeX;
                    p.ScaleSpeed  = cfg.SpawnScaleFraction < 0.999f ? cfg.GrowthSpeed : 0f;

                    if (latencyComp > 0f)
                    {
                        p.X        += p.Vx * latencyComp;
                        p.Y        += p.Vy * latencyComp;
                        p.Lifetime -= latencyComp;
                        if (p.Lifetime <= 0f) continue;
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

        public void RegisterTarget(uint targetId, Vector2 pos, float radius)
        {
            for (int i = 0; i < _targetCount; i++)
            {
                if (_targets[i].TargetId != targetId) continue;
                _targets[i].X      = pos.x;
                _targets[i].Y      = pos.y;
                _targets[i].Radius = radius;
                _targets[i].Active = 1;
                return;
            }
            if (_targetCount >= _maxTargets) return;
            _targets[_targetCount++] = new CollisionTarget
            {
                X = pos.x, Y = pos.y, Radius = radius,
                TargetId = targetId, Active = 1,
            };
        }

        public void DeregisterTarget(uint targetId)
        {
            for (int i = 0; i < _targetCount; i++)
                if (_targets[i].TargetId == targetId) { _targets[i].Active = 0; return; }
        }

        private void HandlePiercingOrKill(ref HitResult hit)
        {
            int idx = (int)hit.ProjIndex;
            if (idx >= _activeCount) return;
            ref var p = ref _projs[idx];

            if (p.PiercingType == (byte)PiercingType.None)
            {
                p.Alive = 0;
            }
            else
            {
                p.CollisionCount++;
                var cfg = ProjectileRegistry.Instance.Get(p.ConfigId);
                if (p.CollisionCount >= cfg.MaxCollisions) p.Alive = 0;
            }
        }

        private void CompactDeadSlots()
        {
            int i = 0;
            while (i < _activeCount)
            {
                if (_projs[i].Alive == 0)
                {
                    _trailPool?.NotifyDead(_projs[i].ProjId);
                    _activeCount--;
                    if (i < _activeCount) _projs[i] = _projs[_activeCount];
                }
                else { i++; }
            }
        }

        // ── Bench-friendly accessors ──────────────────────────────────────────

        public int ActiveCount => _activeCount;
        public int MaxProjectiles => _maxProjectiles;
    }
}
