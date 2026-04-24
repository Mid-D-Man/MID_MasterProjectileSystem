// ServerProjectileAuthority.cs
// Server-only. Owns the Rust sim buffers (2D and 3D).
// Runs tick + collision every FixedUpdate.
// Sends position snapshots every N ticks for client reconciliation.
//
// Architecture:
//   - Two pinned GCHandle arrays: _projs2D[] and _projs3D[]
//   - Two pinned GCHandle arrays: _targets2D[] and _targets3D[]
//   - Hit buffer arrays pinned similarly
//   - RustSimAdapter processes HitResults → game events
//   - MID_ProjectileNetworkBridge subscribes to adapter events → sends RPCs
//   - TrailObjectPool.NotifyDead called per dead projId after compaction
//
// Snapshot model (client prediction + reconciliation):
//   Every _snapshotIntervalTicks FixedUpdates:
//     Build ProjectileSnapshot[] (projId, x, y, [z]) for all alive projectiles.
//     Pass to MID_ProjectileNetworkBridge.SendSnapshot() which sends ClientRpc.
//   Client receives snapshot → ClientPredictionManager.ReconcileSnapshot().

using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.InGame.ProjectileConfigs;
using MidManStudio.InGame.Managers;
using MidManStudio.Core.HelperFunctions;

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Snapshot structs (sent to clients for reconciliation)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal position snapshot for a single 2D projectile.
    /// Sent every N ticks for client prediction reconciliation.
    /// </summary>
    public struct ProjectileSnapshot2D : INetworkSerializable
    {
        public uint   ProjId;
        public float  X;
        public float  Y;
        public int    ServerTick;  // for interpolation ordering on the client

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ProjId);
            s.SerializeValue(ref X);
            s.SerializeValue(ref Y);
            s.SerializeValue(ref ServerTick);
        }
    }

    /// <summary>
    /// Minimal position snapshot for a single 3D projectile.
    /// </summary>
    public struct ProjectileSnapshot3D : INetworkSerializable
    {
        public uint  ProjId;
        public float X;
        public float Y;
        public float Z;
        public int   ServerTick;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ProjId);
            s.SerializeValue(ref X);
            s.SerializeValue(ref Y);
            s.SerializeValue(ref Z);
            s.SerializeValue(ref ServerTick);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ServerProjectileAuthority
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Server-only MonoBehaviour. Owns and drives the Rust sim buffers.
    /// Attach to a persistent networked GameObject (e.g. the NetworkManager host).
    /// </summary>
    public sealed class ServerProjectileAuthority : MonoBehaviour
    {
        #region Configuration

        [Header("Buffer Capacity")]
        [SerializeField] private int _maxProjectiles2D = 2048;
        [SerializeField] private int _maxProjectiles3D = 512;
        [SerializeField] private int _maxTargets2D     = 128;
        [SerializeField] private int _maxTargets3D     = 64;
        [SerializeField] private int _maxHitsPerTick   = 256;

        [Header("Collision Tuning")]
        [Tooltip("World units per collision grid cell. ~2× largest target radius.\n" +
                 "0 = use Rust default (4.0).")]
        [SerializeField] private float _cellSize2D = 4f;
        [SerializeField] private float _cellSize3D = 4f;

        [Header("Snapshot")]
        [Tooltip("Send position snapshot every N FixedUpdates.\n" +
                 "3-5 is recommended (not every frame — snapshot is coarse correction only).")]
        [SerializeField] private int _snapshotIntervalTicks = 4;

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region Owned References

        /// The adapter translates Rust hits → game damage events.
        /// Subscribe to adapter events from MID_ProjectileNetworkBridge.
        public RustSimAdapter Adapter { get; private set; }

        /// Reference set by MID_MasterProjectileSystem after construction.
        public TrailObjectPool TrailPool { get; set; }

        /// Reference set by MID_MasterProjectileSystem after construction.
        public MID_ProjectileNetworkBridge NetworkBridge { get; set; }

        #endregion

        #region 2D Sim Buffer

        private NativeProjectile[]  _projs2D;
        private CollisionTarget[]   _targets2D;
        private HitResult[]         _hits2D;
        private int                 _count2D;
        private int                 _targetCount2D;
        private uint                _nextProjId = 1;

        private GCHandle _pinProjs2D;
        private GCHandle _pinTargets2D;
        private GCHandle _pinHits2D;

        #endregion

        #region 3D Sim Buffer

        private NativeProjectile3D[] _projs3D;
        private CollisionTarget3D[]  _targets3D;
        private HitResult3D[]        _hits3D;
        private int                  _count3D;
        private int                  _targetCount3D;

        private GCHandle _pinProjs3D;
        private GCHandle _pinTargets3D;
        private GCHandle _pinHits3D;

        #endregion

        #region Snapshot Staging

        private ProjectileSnapshot2D[] _snapshots2D;
        private ProjectileSnapshot3D[] _snapshots3D;
        private int _fixedUpdateCounter;

        #endregion

        #region Public API — Active Counts

        public int ActiveCount2D => _count2D;
        public int ActiveCount3D => _count3D;
        public int MaxProjectiles2D => _maxProjectiles2D;
        public int MaxProjectiles3D => _maxProjectiles3D;

        #endregion

        #region Initialisation

        private void Awake()
        {
            Adapter = new RustSimAdapter();

            // Wire adapter → trail pool
            Adapter.OnProjectileDied += OnAdapterProjectileDied;

            AllocateBuffers();
            BatchSpawnHelper.Initialise();

            Log("ServerProjectileAuthority initialised.");
        }

        private void OnDestroy()
        {
            BatchSpawnHelper.Shutdown();
            FreeBuffers();
            ProjectileLib.clear_movement_params();
        }

        private void AllocateBuffers()
        {
            // 2D
            _projs2D   = new NativeProjectile[_maxProjectiles2D];
            _targets2D = new CollisionTarget[_maxTargets2D];
            _hits2D    = new HitResult[_maxHitsPerTick];
            _pinProjs2D   = GCHandle.Alloc(_projs2D,   GCHandleType.Pinned);
            _pinTargets2D = GCHandle.Alloc(_targets2D, GCHandleType.Pinned);
            _pinHits2D    = GCHandle.Alloc(_hits2D,    GCHandleType.Pinned);

            // 3D
            _projs3D   = new NativeProjectile3D[_maxProjectiles3D];
            _targets3D = new CollisionTarget3D[_maxTargets3D];
            _hits3D    = new HitResult3D[_maxHitsPerTick];
            _pinProjs3D   = GCHandle.Alloc(_projs3D,   GCHandleType.Pinned);
            _pinTargets3D = GCHandle.Alloc(_targets3D, GCHandleType.Pinned);
            _pinHits3D    = GCHandle.Alloc(_hits3D,    GCHandleType.Pinned);

            // Snapshot staging
            _snapshots2D = new ProjectileSnapshot2D[_maxProjectiles2D];
            _snapshots3D = new ProjectileSnapshot3D[_maxProjectiles3D];
        }

        private void FreeBuffers()
        {
            if (_pinProjs2D.IsAllocated)   _pinProjs2D.Free();
            if (_pinTargets2D.IsAllocated) _pinTargets2D.Free();
            if (_pinHits2D.IsAllocated)    _pinHits2D.Free();
            if (_pinProjs3D.IsAllocated)   _pinProjs3D.Free();
            if (_pinTargets3D.IsAllocated) _pinTargets3D.Free();
            if (_pinHits3D.IsAllocated)    _pinHits3D.Free();
        }

        #endregion

        #region FixedUpdate — Main Sim Loop

        private void FixedUpdate()
        {
            if (!IsServer()) return;

            float dt = Time.fixedDeltaTime;
            _fixedUpdateCounter++;

            // ── 2D sim ──────────────────────────────────────────────────────
            if (_count2D > 0)
            {
                Tick2D(dt);
                Collision2D();
                ProcessHits2D();
                CompactDead2D();
            }

            // ── 3D sim ──────────────────────────────────────────────────────
            if (_count3D > 0)
            {
                Tick3D(dt);
                Collision3D();
                ProcessHits3D();
                CompactDead3D();
            }

            // ── Snapshot (every N ticks) ─────────────────────────────────────
            if (_fixedUpdateCounter % _snapshotIntervalTicks == 0)
            {
                SendSnapshots();
            }
        }

        // ── 2D tick ───────────────────────────────────────────────────────────

        private void Tick2D(float dt)
        {
            IntPtr ptr = _pinProjs2D.AddrOfPinnedObject();
            int died   = ProjectileLib.tick_projectiles(ptr, _count2D, dt);

            if (died > 0)
                Log($"2D tick: {died} projectiles expired by lifetime.");
        }

        // ── 2D collision ──────────────────────────────────────────────────────

        private void Collision2D()
        {
            if (_targetCount2D == 0) return;

            ProjectileLib.check_hits_grid_ex(
                _pinProjs2D.AddrOfPinnedObject(),    _count2D,
                _pinTargets2D.AddrOfPinnedObject(),  _targetCount2D,
                _pinHits2D.AddrOfPinnedObject(),     _hits2D.Length,
                _cellSize2D,
                out int hitCount);

            // Mark hit projectiles dead / handle piercing in the buffer
            for (int i = 0; i < hitCount; i++)
            {
                ref var h  = ref _hits2D[i];
                int    idx = (int)h.ProjIndex;
                if (idx < 0 || idx >= _count2D) continue;

                // Determine headshot — game-specific geometry check
                // ServerProjectileAuthority delegates this to a virtual so
                // game code can override without modifying the package.
                bool headshot = CheckHeadshot2D(in h);

                // Adapter computes damage, fires event, handles piercing flag
                Adapter.ProcessHit(in h, headshot);

                // If adapter marked hasHit on the data, kill the NativeProjectile
                if (Adapter.IsRegistered(h.ProjId))
                {
                    // still alive (piercer with remaining collisions) — leave it
                }
                else
                {
                    // adapter removed it → mark dead in Rust buffer
                    if (idx < _count2D)
                        _projs2D[idx].Alive = 0;
                }
            }
        }

        // ── 2D hit post-processing ────────────────────────────────────────────

        private void ProcessHits2D()
        {
            // Additional per-projectile processing after collision:
            // Kill non-piercers whose adapter data was removed (alive=0 already set).
            // Guided projectile homing direction updates happen in TickDispatcher
            // subscriber (Tick_0_1) — not here.
        }

        // ── 2D compact ────────────────────────────────────────────────────────

        private void CompactDead2D()
        {
            int write = 0;
            for (int read = 0; read < _count2D; read++)
            {
                if (_projs2D[read].Alive == 0)
                {
                    uint deadId = _projs2D[read].ProjId;
                    Adapter.NotifyDead(deadId);
                    TrailPool?.NotifyDead(deadId);
                    continue;
                }

                if (write != read)
                    _projs2D[write] = _projs2D[read];

                write++;
            }
            _count2D = write;
        }

        // ── 3D tick ───────────────────────────────────────────────────────────

        private void Tick3D(float dt)
        {
            IntPtr ptr = _pinProjs3D.AddrOfPinnedObject();
            ProjectileLib.tick_projectiles_3d(ptr, _count3D, dt);
        }

        // ── 3D collision ──────────────────────────────────────────────────────

        private void Collision3D()
        {
            if (_targetCount3D == 0) return;

            ProjectileLib.check_hits_grid_3d(
                _pinProjs3D.AddrOfPinnedObject(),    _count3D,
                _pinTargets3D.AddrOfPinnedObject(),  _targetCount3D,
                _pinHits3D.AddrOfPinnedObject(),     _hits3D.Length,
                _cellSize3D,
                out int hitCount);

            for (int i = 0; i < hitCount; i++)
            {
                ref var h   = ref _hits3D[i];
                int     idx = (int)h.ProjIndex;
                if (idx < 0 || idx >= _count3D) continue;

                bool headshot = CheckHeadshot3D(in h);
                Adapter.ProcessHit3D(in h, headshot);

                if (!Adapter.IsRegistered(h.ProjId))
                {
                    if (idx < _count3D)
                        _projs3D[idx].Alive = 0;
                }
            }
        }

        private void ProcessHits3D() { /* same as 2D — placeholder for future */ }

        // ── 3D compact ────────────────────────────────────────────────────────

        private void CompactDead3D()
        {
            int write = 0;
            for (int read = 0; read < _count3D; read++)
            {
                if (_projs3D[read].Alive == 0)
                {
                    uint deadId = _projs3D[read].ProjId;
                    Adapter.NotifyDead(deadId);
                    TrailPool?.NotifyDead(deadId);
                    continue;
                }

                if (write != read)
                    _projs3D[write] = _projs3D[read];

                write++;
            }
            _count3D = write;
        }

        #endregion

        #region Snapshot

        private void SendSnapshots()
        {
            if (NetworkBridge == null) return;

            int serverTick = NetworkBridge.GetServerTick();

            // Build 2D snapshot
            int snap2DCount = 0;
            for (int i = 0; i < _count2D; i++)
            {
                if (_projs2D[i].Alive == 0) continue;
                _snapshots2D[snap2DCount++] = new ProjectileSnapshot2D
                {
                    ProjId     = _projs2D[i].ProjId,
                    X          = _projs2D[i].X,
                    Y          = _projs2D[i].Y,
                    ServerTick = serverTick
                };
            }

            // Build 3D snapshot
            int snap3DCount = 0;
            for (int i = 0; i < _count3D; i++)
            {
                if (_projs3D[i].Alive == 0) continue;
                _snapshots3D[snap3DCount++] = new ProjectileSnapshot3D
                {
                    ProjId     = _projs3D[i].ProjId,
                    X          = _projs3D[i].X,
                    Y          = _projs3D[i].Y,
                    Z          = _projs3D[i].Z,
                    ServerTick = serverTick
                };
            }

            if (snap2DCount > 0 || snap3DCount > 0)
            {
                NetworkBridge.SendSnapshotClientRpc(
                    _snapshots2D, snap2DCount,
                    _snapshots3D, snap3DCount);
            }
        }

        #endregion

        #region Public API — Spawn

        /// <summary>
        /// Add a 2D projectile to the sim buffer.
        /// BatchSpawnHelper calls this via IntPtr — this overload is for direct
        /// single-projectile spawns (e.g. from physics objects transitioning to Rust sim).
        /// </summary>
        public bool AddProjectile2D(in NativeProjectile proj, ServerProjectileData data)
        {
            if (_count2D >= _maxProjectiles2D)
            {
                LogWarning("2D sim buffer full — projectile dropped.");
                return false;
            }

            _projs2D[_count2D] = proj;

            // Assign the canonical proj ID
            data.projectileId_u32 = proj.ProjId;
            data.configId = proj.ConfigId;

            _count2D++;
            Adapter.Register(data);
            return true;
        }

        /// <summary>
        /// Add a 3D projectile to the sim buffer.
        /// </summary>
        public bool AddProjectile3D(in NativeProjectile3D proj, ServerProjectileData data)
        {
            if (_count3D >= _maxProjectiles3D)
            {
                LogWarning("3D sim buffer full — projectile dropped.");
                return false;
            }

            _projs3D[_count3D] = proj;
            data.projectileId_u32 = proj.ProjId;
            data.configId = proj.ConfigId;
            data.is3D = true;

            _count3D++;
            Adapter.Register(data);
            return true;
        }

        /// <summary>
        /// Called by BatchSpawnHelper after spawn_batch writes to the buffer.
        /// Updates activeCount and registers adapter data for each spawned projectile.
        /// </summary>
        public void NotifyBatchSpawned2D(
            int spawned, uint baseId, ServerProjectileData templateData)
        {
            for (uint i = 0; i < (uint)spawned; i++)
            {
                int   bufIdx = _count2D + (int)i;
                uint  projId = baseId + i;

                // Each spawned projectile gets its own ServerProjectileData clone
                var data = templateData.CloneForSpawn(projId,
                    _projs2D[bufIdx].ConfigId);

                // Roll crit per-projectile
                data.isCrit = UnityEngine.Random.value < data.critChance;

                Adapter.Register(data);
            }
            _count2D += spawned;
        }

        /// <summary>
        /// Called by BatchSpawnHelper after spawn_batch_3d writes to the 3D buffer.
        /// </summary>
        public void NotifyBatchSpawned3D(
            int spawned, uint baseId, ServerProjectileData templateData)
        {
            for (uint i = 0; i < (uint)spawned; i++)
            {
                int  bufIdx = _count3D + (int)i;
                uint projId = baseId + i;

                var data = templateData.CloneForSpawn(projId,
                    _projs3D[bufIdx].ConfigId);

                data.is3D   = true;
                data.isCrit = UnityEngine.Random.value < data.critChance;

                Adapter.Register(data);
            }
            _count3D += spawned;
        }

        /// <summary>
        /// Provides the pointer and remaining capacity for BatchSpawnHelper to write into.
        /// </summary>
        public (IntPtr ptr, int remaining) Get2DWriteHead()
        {
            IntPtr ptr = IntPtr.Add(
                _pinProjs2D.AddrOfPinnedObject(),
                _count2D * Marshal.SizeOf<NativeProjectile>());

            return (ptr, _maxProjectiles2D - _count2D);
        }

        /// <summary>
        /// Provides the 3D write head for BatchSpawnHelper.
        /// </summary>
        public (IntPtr ptr, int remaining) Get3DWriteHead()
        {
            IntPtr ptr = IntPtr.Add(
                _pinProjs3D.AddrOfPinnedObject(),
                _count3D * Marshal.SizeOf<NativeProjectile3D>());

            return (ptr, _maxProjectiles3D - _count3D);
        }

        /// <summary>
        /// Monotonically increasing ID counter. BatchSpawnHelper reads this
        /// as the base proj ID for each spawn event.
        /// </summary>
        public uint AllocateProjIds(int count)
        {
            uint base_id = _nextProjId;
            _nextProjId += (uint)count;
            return base_id;
        }

        #endregion

        #region Public API — Targets

        /// <summary>
        /// Register or update a 2D collision target.
        /// Call each FixedUpdate from the target's NetworkBehaviour
        /// (or via TickDispatcher at Tick_0_05 for lower precision targets).
        /// </summary>
        public void RegisterTarget2D(in CollisionTarget target)
        {
            for (int i = 0; i < _targetCount2D; i++)
            {
                if (_targets2D[i].TargetId != target.TargetId) continue;
                _targets2D[i] = target;
                return;
            }

            if (_targetCount2D >= _maxTargets2D)
            {
                LogWarning("2D target buffer full.");
                return;
            }

            _targets2D[_targetCount2D++] = target;
        }

        /// <summary>Register or update a 3D collision target.</summary>
        public void RegisterTarget3D(in CollisionTarget3D target)
        {
            for (int i = 0; i < _targetCount3D; i++)
            {
                if (_targets3D[i].TargetId != target.TargetId) continue;
                _targets3D[i] = target;
                return;
            }

            if (_targetCount3D >= _maxTargets3D)
            {
                LogWarning("3D target buffer full.");
                return;
            }

            _targets3D[_targetCount3D++] = target;
        }

        /// <summary>Deactivate a 2D target (e.g. player died or left).</summary>
        public void DeactivateTarget2D(uint targetId)
        {
            for (int i = 0; i < _targetCount2D; i++)
            {
                if (_targets2D[i].TargetId != targetId) continue;
                _targets2D[i].Active = 0;
                return;
            }
        }

        /// <summary>Deactivate a 3D target.</summary>
        public void DeactivateTarget3D(uint targetId)
        {
            for (int i = 0; i < _targetCount3D; i++)
            {
                if (_targets3D[i].TargetId != targetId) continue;
                _targets3D[i].Active = 0;
                return;
            }
        }

        /// <summary>Remove all targets (scene teardown).</summary>
        public void ClearAllTargets()
        {
            _targetCount2D = 0;
            _targetCount3D = 0;
        }

        #endregion

        #region Public API — Guided / Wave / Circular Direction Updates

        /// <summary>
        /// Update homing direction for a 2D guided projectile.
        /// Called from a TickDispatcher subscriber (Tick_0_1).
        /// projId must be an active projectile in the 2D buffer.
        /// </summary>
        public void SetAcceleration2D(uint projId, Vector2 accelDir)
        {
            for (int i = 0; i < _count2D; i++)
            {
                if (_projs2D[i].ProjId != projId) continue;
                Vector2 n = accelDir.normalized;
                _projs2D[i].ax = n.x;
                _projs2D[i].ay = n.y;
                return;
            }
        }

        /// <summary>Update homing / accel direction for a 3D projectile.</summary>
        public void SetAcceleration3D(uint projId, Vector3 accelDir)
        {
            for (int i = 0; i < _count3D; i++)
            {
                if (_projs3D[i].ProjId != projId) continue;
                Vector3 n = accelDir.normalized;
                _projs3D[i].ax = n.x;
                _projs3D[i].ay = n.y;
                _projs3D[i].az = n.z;
                return;
            }
        }

        #endregion

        #region State Save / Restore (client reconciliation support)

        /// <summary>
        /// Snapshot the 2D sim state into a byte array.
        /// Used by ClientPredictionManager for rollback reconciliation.
        /// Returned array is rented — caller must return it promptly.
        /// </summary>
        public int SaveState2D(byte[] buf)
        {
            if (buf == null || buf.Length < _count2D * 72) return 0;

            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            int written = ProjectileLib.save_state(
                _pinProjs2D.AddrOfPinnedObject(), _count2D,
                pin.AddrOfPinnedObject(), buf.Length);
            pin.Free();
            return written;
        }

        /// <summary>Restore the 2D sim state from a previously snapshotted byte array.</summary>
        public int RestoreState2D(byte[] buf, int byteCount)
        {
            if (buf == null || byteCount <= 0) return 0;

            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            ProjectileLib.restore_state(
                _pinProjs2D.AddrOfPinnedObject(), _maxProjectiles2D,
                pin.AddrOfPinnedObject(), byteCount,
                out int restored);
            pin.Free();
            _count2D = restored;
            return restored;
        }

        #endregion

        #region Headshot Detection — Virtual Hooks

        // These are virtual so game code can override headshot logic in a derived class
        // without modifying the package. Default implementation returns false.
        // Override in a game-side derived class:
        //
        //   public class MID_ServerProjectileAuthority : ServerProjectileAuthority
        //   {
        //       protected override bool CheckHeadshot2D(in HitResult hit)
        //       {
        //           // look up capsule collider by hit.TargetId, check hit.HitY vs head zone
        //           return hit.HitY > GetHeadZoneY(hit.TargetId);
        //       }
        //   }

        protected virtual bool CheckHeadshot2D(in HitResult hit)   => false;
        protected virtual bool CheckHeadshot3D(in HitResult3D hit) => false;

        #endregion

        #region Internal Events

        private void OnAdapterProjectileDied(uint projId)
        {
            // TrailPool notification happens in CompactDead — don't double-notify here.
            // This event is available for any other subscriber (e.g. achievement tracking).
        }

        #endregion

        #region Utilities

        private static bool IsServer()
        {
            return Unity.Netcode.NetworkManager.Singleton != null
                && Unity.Netcode.NetworkManager.Singleton.IsServer;
        }

        private void Log(string msg)
        {
            if (_enableLogs)
                MID_HelperFunctions.LogDebug(msg, nameof(ServerProjectileAuthority));
        }

        private void LogWarning(string msg)
        {
            MID_HelperFunctions.LogWarning(msg, nameof(ServerProjectileAuthority));
        }

        #endregion
    }
}
