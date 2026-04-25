// MID_MasterProjectileSystem.cs
// Top-level orchestrator. Single entry point for all projectile fire events.
//
// Responsibilities:
//   - Initialise all subsystems in correct order
//   - Expose Fire() as the ONLY external entry point for spawning projectiles
//   - Route fire events to the correct handler based on SimulationMode
//   - Expose RegisterTarget / DeactivateTarget for collision registration
//   - Expose SaveState / RestoreState for reconciliation support
//   - Tear down cleanly on destroy
//
// Setup (attach this MonoBehaviour to a persistent GameObject):
//   1. Assign all serialized references in inspector
//   2. Call SetLocalPlayerMidId() from your player spawn flow
//   3. Register all ProjectileConfigSO assets with ProjectileRegistry (auto or manual)
//   4. Weapon scripts call Fire() — nothing else needs to know about subsystems
//
// Fire flow summary:
//   Offline  → LocalProjectileManager.Spawn2D/3D()
//   Raycast  → RaycastProjectileHandler (weapon owns the cast, passes result)
//   RustSim  → BatchSpawnHelper → ServerProjectileAuthority (server)
//              ClientPredictionManager (client prediction visual)
//              MID_ProjectileNetworkBridge.FireServerRpc (client → server)
//   Physics  → ObjectNetSync path (existing, unchanged)

using System;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.Core.Singleton;
using MidManStudio.Core.HelperFunctions;
using MidManStudio.InGame.ProjectileConfigs;
using MidManStudio.InGame.Managers;

namespace MidManStudio.Projectiles
{
    public sealed class MID_MasterProjectileSystem : Singleton<MID_MasterProjectileSystem>
    {
        #region Serialized References

        [Header("Core Systems")]
        [SerializeField] private ProjectileRegistry          _registry;
        [SerializeField] private ServerProjectileAuthority   _authority;
        [SerializeField] private LocalProjectileManager      _localManager;
        [SerializeField] private MID_ProjectileNetworkBridge _networkBridge;
        [SerializeField] private ClientPredictionManager     _predictionManager;
        [SerializeField] private RaycastProjectileHandler    _raycastHandler;

        [Header("Visual Systems")]
        [SerializeField] private ProjectileRenderer2D        _renderer2D;
        [SerializeField] private ProjectileRenderer3D        _renderer3D;
        [SerializeField] private TrailObjectPool             _trailPool;
        [SerializeField] private ProjectileImpactHandler     _impactHandler;

        [Header("Mode")]
        [Tooltip("True = networked multiplayer. False = offline/local only.\n" +
                 "Set automatically based on NetworkManager state if left default.")]
        [SerializeField] private bool _forceOfflineMode = false;

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region State

        private bool _initialised = false;

        #endregion

        #region Properties

        public bool IsNetworked =>
            !_forceOfflineMode
            && NetworkManager.Singleton != null
            && (NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient);

        public bool IsServer =>
            NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

        #endregion

        #region Initialisation

        protected override void Awake()
        {
            base.Awake();
            Initialise();
        }

        private void Initialise()
        {
            if (_initialised) return;

            // 1. Validate Rust struct sizes (catastrophic if wrong — crash loudly)
            try
            {
                ProjectileLib.ValidateStructSizes();
            }
            catch (InvalidOperationException ex)
            {
                Debug.LogError($"[MID_MasterProjectileSystem] Fatal: {ex.Message}");
                enabled = false;
                return;
            }

            // 2. Initialise batch spawn helper (pins temp buffers)
            BatchSpawnHelper.Initialise();

            // 3. Wire subsystem references
            if (_authority != null)
            {
                _authority.TrailPool     = _trailPool;
                _authority.NetworkBridge = _networkBridge;
            }

            if (_networkBridge != null)
            {
                _networkBridge.Authority    = _authority;
                _networkBridge.Prediction   = _predictionManager;
                _networkBridge.RaycastHandler = _raycastHandler;
                _networkBridge.ImpactHandler  = _impactHandler;
            }

            // 4. Subscribe to adapter events → RPC forwarding
            // (MID_ProjectileNetworkBridge subscribes its own OnNetworkSpawn)

            _initialised = true;
            Log("Initialised. Mode: " + (IsNetworked ? "Networked" : "Offline"));
        }

        private void OnDestroy()
        {
            BatchSpawnHelper.Shutdown();
            ProjectileLib.clear_movement_params();
        }

        #endregion

        #region Public API — Identity

        /// <summary>
        /// Set the local player's MID ID so ClientPredictionManager knows
        /// which prediction visuals belong to this client.
        /// Call from your player spawn or login flow.
        /// </summary>
        public void SetLocalPlayerMidId(ulong midId)
        {
            _predictionManager?.SetLocalPlayerMidId(midId);
        }

        #endregion

        #region Public API — Fire

        /// <summary>
        /// Primary entry point for ALL projectile fire events.
        ///
        /// The weapon script calls this with:
        ///   - configId:    registered config from ProjectileRegistry
        ///   - spawnPoints: pre-computed from ProjectilePatternSO.SampleDirections()
        ///                  transformed to world space at the barrel
        ///   - context:     fire rate, owner info, damage multiplier etc.
        ///
        /// Routing:
        ///   Offline  → LocalProjectileManager
        ///   Raycast  → RegisterRaycastFire() (weapon did the cast, passes result here)
        ///   RustSim  → client fires FireServerRpc; server spawns into buffer
        ///   Physics  → caller handles ObjectNetSync directly (unchanged path)
        /// </summary>
        public void Fire(
            ushort           configId,
            SpawnPoint[]     spawnPoints,
            int              count,
            WeaponFireContext context)
        {
            if (!_initialised) { LogWarning("Fire() called before initialisation."); return; }

            var cfg = _registry != null ? _registry.Get(configId) : null;
            if (cfg == null)
            {
                LogWarning($"Fire(): configId {configId} not registered.");
                return;
            }

            var routing = ProjectileTypeRouter.Route(cfg, context);

            Log($"Fire: configId={configId} mode={routing.Mode} count={count}");

            switch (routing.Mode)
            {
                case SimulationMode.LocalOnly:
                    FireLocal(configId, spawnPoints, count, context, cfg);
                    break;

                case SimulationMode.RustSim2D:
                case SimulationMode.RustSim3D:
                    FireNetworkedSim(configId, spawnPoints, count, context, cfg, routing);
                    break;

                case SimulationMode.Raycast:
                    LogWarning("Fire() called with Raycast mode — use RegisterRaycastFire() instead.");
                    break;

                case SimulationMode.PhysicsObject:
                    // Caller handles ObjectNetSync.Initialize() + NetworkObject.Spawn()
                    // This system does not manage physics objects.
                    LogWarning("PhysicsObject mode — caller must spawn NetworkObject directly.");
                    break;
            }
        }

        // ── Offline ────────────────────────────────────────────────────────────

        private void FireLocal(
            ushort           configId,
            SpawnPoint[]     spawnPoints,
            int              count,
            WeaponFireContext context,
            ProjectileConfigSO cfg)
        {
            if (_localManager == null)
            {
                LogWarning("FireLocal: LocalProjectileManager not assigned.");
                return;
            }

            if (cfg.Is3D)
            {
                _localManager.Spawn3D(spawnPoints, count, configId,
                    (uint)context.OwnerMidId, context.DamageMultiplier);
            }
            else
            {
                _localManager.Spawn2D(spawnPoints, count, configId,
                    (uint)context.OwnerMidId, context.DamageMultiplier);
            }
        }

        // ── Networked Rust sim ─────────────────────────────────────────────────

        private void FireNetworkedSim(
            ushort           configId,
            SpawnPoint[]     spawnPoints,
            int              count,
            WeaponFireContext context,
            ProjectileConfigSO cfg,
            RoutingResult    routing)
        {
            if (_networkBridge == null)
            {
                LogWarning("FireNetworkedSim: NetworkBridge not assigned.");
                return;
            }

            // Use the first spawn point's direction as the canonical direction for RPC
            Vector3 origin    = count > 0 ? spawnPoints[0].Origin    : Vector3.zero;
            Vector3 direction = count > 0 ? spawnPoints[0].Direction : Vector3.forward;
            float   speed     = count > 0 ? spawnPoints[0].Speed     : cfg.ResolveSpeed();

            int serverTick = _networkBridge.GetServerTick();

            var request = new ProjectileFireRequest
            {
                ConfigId               = configId,
                Origin                 = origin,
                Direction              = direction,
                Speed                  = speed,
                RngSeed                = (uint)UnityEngine.Random.Range(0, int.MaxValue),
                ProjectileCount        = (byte)Mathf.Min(count, 255),
                OwnerMidId             = context.OwnerMidId,
                FiredByNetworkObjectId = context.FiredByNetworkObjectId,
                IsBotOwner             = context.IsBotOwner,
                WeaponLevel            = context.WeaponLevel,
                DamageMultiplier       = context.DamageMultiplier,
                ClientFireTick         = serverTick
            };

            if (IsServer)
            {
                // Server fires directly into the authority — no RPC round trip
                _networkBridge.FireServerRpc(request);
            }
            else
            {
                // Client: send RPC to server, spawn local prediction visual
                _networkBridge.FireServerRpc(request);

                // Client-side prediction visual is spawned when
                // SpawnConfirmedClientRpc comes back. No visual spawned speculatively
                // to avoid double-visual if server rejects.
                // If you want zero-latency local visual, spawn speculatively here
                // and reconcile on SpawnConfirmed.
            }
        }

        #endregion

        #region Public API — Raycast (weapon-owned cast)

        /// <summary>
        /// Called by a weapon script after it has already cast the ray itself.
        /// The weapon passes the result — this system handles visual + RPC.
        ///
        /// Online server: RaycastProjectileHandler validates and broadcasts.
        /// Online client: sends RaycastFireServerRpc with hit data.
        /// Offline:       RaycastProjectileHandler.OfflineHandleFire().
        /// </summary>
        public void RegisterRaycastFire(
            RaycastFireResult result,
            ushort            configId,
            WeaponFireContext  context)
        {
            if (!_initialised) return;

            var cfg = _registry?.Get(configId);
            if (cfg == null) return;

            if (!IsNetworked)
            {
                _raycastHandler?.OfflineHandleFire(
                    result, configId,
                    (uint)context.OwnerMidId,
                    context.DamageMultiplier);
                return;
            }

            if (IsServer)
            {
                _raycastHandler?.ServerHandleFire(result, context, configId);
            }
            else
            {
                // Client sends RPC with hit data — server validates
                _networkBridge?.RaycastFireServerRpc(
                    new ProjectileFireRequest
                    {
                        ConfigId               = configId,
                        Origin                 = result.Origin,
                        Direction              = result.Direction,
                        OwnerMidId             = context.OwnerMidId,
                        FiredByNetworkObjectId = context.FiredByNetworkObjectId,
                        IsBotOwner             = context.IsBotOwner,
                        WeaponLevel            = context.WeaponLevel,
                        DamageMultiplier       = context.DamageMultiplier,
                        ClientFireTick         = _networkBridge.GetServerTick()
                    },
                    result.HitPoint,
                    result.DidHit,
                    result.IsHeadshot,
                    result.HitTargetNetworkId);

                // Immediately spawn local visual so the shooter sees instant feedback
                _raycastHandler?.OfflineHandleFire(
                    result, configId, (uint)context.OwnerMidId, 1f);
            }
        }

        #endregion

        #region Public API — Targets

        /// <summary>
        /// Register a 2D collision target with the server sim.
        /// Call from each damageable character's FixedUpdate (or TickDispatcher Tick_0_05).
        /// Online: only meaningful on server (ServerProjectileAuthority owns targets).
        /// Offline: registers with LocalProjectileManager.
        /// </summary>
        public void RegisterTarget2D(in CollisionTarget target)
        {
            if (!IsNetworked)
            {
                // Offline: handled via LocalProjectileManager.RegisterTarget()
                return;
            }

            if (IsServer)
                _authority?.RegisterTarget2D(target);
        }

        /// <summary>Register a 3D collision target.</summary>
        public void RegisterTarget3D(in CollisionTarget3D target)
        {
            if (!IsNetworked) return;
            if (IsServer) _authority?.RegisterTarget3D(target);
        }

        /// <summary>Deactivate a 2D target (e.g. player died).</summary>
        public void DeactivateTarget2D(uint targetId)
        {
            if (IsServer) _authority?.DeactivateTarget2D(targetId);
        }

        /// <summary>Deactivate a 3D target.</summary>
        public void DeactivateTarget3D(uint targetId)
        {
            if (IsServer) _authority?.DeactivateTarget3D(targetId);
        }

        /// <summary>Remove all targets (scene teardown).</summary>
        public void ClearAllTargets()
        {
            if (IsServer) _authority?.ClearAllTargets();
        }

        #endregion

        #region Public API — State (reconciliation)

        /// <summary>
        /// Snapshot 2D sim state for client reconciliation.
        /// Called by ClientPredictionManager — not typically called by game code.
        /// </summary>
        public int SaveState2D(byte[] buf) =>
            _authority?.SaveState2D(buf) ?? 0;

        /// <summary>Restore 2D sim state from snapshot.</summary>
        public int RestoreState2D(byte[] buf, int byteCount) =>
            _authority?.RestoreState2D(buf, byteCount) ?? 0;

        #endregion

        #region Public API — Guided Projectile Homing

        /// <summary>
        /// Update homing direction for a guided 2D projectile.
        /// Call from a TickDispatcher subscriber (Tick_0_1 is sufficient).
        /// Online: server only. Offline: updates local sim directly.
        /// </summary>
        public void SetHomingDirection2D(uint projId, Vector2 worldDir)
        {
            if (IsServer || !IsNetworked)
                _authority?.SetAcceleration2D(projId, worldDir);
        }

        /// <summary>Update homing direction for a guided 3D projectile.</summary>
        public void SetHomingDirection3D(uint projId, Vector3 worldDir)
        {
            if (IsServer || !IsNetworked)
                _authority?.SetAcceleration3D(projId, worldDir);
        }

        #endregion

        #region Debug

        [ContextMenu("Log System Status")]
        private void LogStatus()
        {
            Debug.Log(
                $"=== MID_MasterProjectileSystem ===\n" +
                $"Initialised:   {_initialised}\n" +
                $"Networked:     {IsNetworked}\n" +
                $"Is Server:     {IsServer}\n" +
                $"Active 2D:     {_authority?.ActiveCount2D ?? _localManager?.ActiveCount2D ?? 0}\n" +
                $"Active 3D:     {_authority?.ActiveCount3D ?? 0}\n" +
                $"Registry:      {_registry?.Count ?? 0} configs\n");
        }

        private void Log(string msg)
        {
            if (_enableLogs)
                MID_HelperFunctions.LogDebug(msg, nameof(MID_MasterProjectileSystem));
        }

        private void LogWarning(string msg)
        {
            MID_HelperFunctions.LogWarning(msg, nameof(MID_MasterProjectileSystem));
        }

        #endregion
    }
}
