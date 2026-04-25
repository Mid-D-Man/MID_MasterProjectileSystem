// RaycastProjectileHandler.cs
// Handles the visual and network layer for raycast-mode projectiles.
//
// IMPORTANT ARCHITECTURE NOTE:
//   The weapon script owns Physics2D.Raycast() / Physics.Raycast().
//   This class does NOT cast rays. It receives the result.
//
//   Weapon fire flow:
//     1. Weapon.Fire() → Physics2D.Raycast(...)
//     2. Weapon.Fire() → MID_MasterProjectileSystem.RegisterRaycastFire(result, context)
//     3. MID_MasterProjectileSystem routes to this handler
//
//   Server path:
//     - Validates the hit (anti-cheat: server re-verifies on FireServerRpc)
//     - Sends HitConfirmedClientRpc with (origin, hitPoint, hitTargetId)
//
//   Client path:
//     - Spawns a visual projectile from pool at origin
//     - Visual travels toward hitPoint at configured speed
//     - On arrival: impact effect + return to pool
//     - No physics, no collision — purely cosmetic
//
//   Online: server-authoritative. Client fires FireServerRpc, server validates, sends back.
//   Offline: hit is applied directly, visual is spawned locally.

using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using MidManStudio.InGame.ProjectileConfigs;
using MidManStudio.InGame.Managers;
using MidManStudio.Core.PoolSystems;
using MidManStudio.Core.HelperFunctions;

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Raycast fire data (passed from weapon to handler)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Result of a weapon-side Physics2D.Raycast. Passed to RegisterRaycastFire().
    /// The weapon fills this — the handler never casts a ray itself.
    /// </summary>
    public struct RaycastFireResult
    {
        /// World-space origin of the ray (barrel tip).
        public Vector3 Origin;

        /// Normalised direction of the ray.
        public Vector3 Direction;

        /// Point where the ray hit something (or origin + direction * maxRange if miss).
        public Vector3 HitPoint;

        /// True if the ray actually hit a registered collision target.
        public bool DidHit;

        /// NetworkObject ID of the hit target (0 if miss).
        /// Set by the weapon from the hit collider's NetworkObject component.
        public ulong HitTargetNetworkId;

        /// True if the hit point was within the headshot zone.
        /// Determined by the weapon using its own headshot detection logic.
        public bool IsHeadshot;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  RaycastProjectileHandler
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles visual + network messaging for raycast-mode projectiles.
    /// Server validates hits, clients see cosmetic visuals.
    /// </summary>
    public sealed class RaycastProjectileHandler : NetworkBehaviour
    {
        #region Configuration

        [Header("Visual")]
        [Tooltip("Speed at which the visual projectile travels toward the hit point.\n" +
                 "Has no gameplay effect — purely cosmetic velocity.")]
        [SerializeField] private float _visualTravelSpeed = 40f;

        [SerializeField] private PoolableObjectType _visualPoolType
            = PoolableObjectType.ProjectileVisual_;

        [Header("Server Validation")]
        [Tooltip("Maximum tolerance (world units) between client-reported hit point\n" +
                 "and server's re-verified hit point. Hits outside this tolerance are rejected.")]
        [SerializeField] private float _hitValidationTolerance = 2f;

        [SerializeField] private LayerMask _serverRaycastLayers = -1;

        [Header("Debug")]
        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region Events

        /// <summary>
        /// Fired on the server when a raycast hit is confirmed valid.
        /// Subscribe from your damage system.
        /// </summary>
        public event Action<ProjectileHitPayload> OnServerHitConfirmed;

        #endregion

        #region Active Visuals

        private sealed class ActiveVisual
        {
            public int        VisualId;
            public GameObject Obj;
            public Vector3    Origin;
            public Vector3    HitPoint;
            public float      Speed;
            public ushort     ConfigId;
        }

        private readonly List<ActiveVisual> _activeVisuals = new(64);
        private int _nextVisualId = 1;

        #endregion

        #region Server — Handle Fire (called by MID_MasterProjectileSystem on server)

        /// <summary>
        /// Called by MID_MasterProjectileSystem when the server receives a raycast fire RPC.
        /// Re-validates the hit server-side then broadcasts to clients.
        /// </summary>
        public void ServerHandleFire(
            RaycastFireResult clientResult,
            WeaponFireContext  context,
            ushort             configId)
        {
            if (!IsServer) return;

            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null) return;

            // Server re-casts the ray to validate client-reported hit position
            bool serverConfirmed = false;
            Vector3 serverHitPoint = clientResult.HitPoint;
            ulong   serverTargetId = 0;
            bool    serverHeadshot = false;

            if (clientResult.DidHit)
            {
                serverConfirmed = ValidateHitServer(
                    clientResult, out serverHitPoint,
                    out serverTargetId, out serverHeadshot);

                if (!serverConfirmed)
                {
                    Log($"Hit rejected: client reported {clientResult.HitPoint}, " +
                        $"server got {serverHitPoint}");
                }
            }

            if (serverConfirmed && serverTargetId != 0)
            {
                // Build and fire server hit event for damage system
                float damage = cfg.EvaluateDamage(0f); // raycast = point-blank always
                if (serverHeadshot) damage *= cfg.HeadshotMultiplier;
                bool isCrit = UnityEngine.Random.value < cfg.CritChance;
                if (isCrit) damage *= cfg.CritMultiplier;
                damage *= context.DamageMultiplier;

                // We need a ServerProjectileData shell for the payload
                // Raycast hits don't have persistent projectiles — build a minimal one
                var gameData = BuildRaycastGameData(context, configId, cfg);

                var payload = new ProjectileHitPayload
                {
                    ProjId                 = 0, // no persistent sim projectile
                    ConfigId               = configId,
                    Is3D                   = cfg.Is3D,
                    TargetId               = (uint)serverTargetId,
                    Damage                 = damage,
                    IsHeadshot             = serverHeadshot,
                    IsCrit                 = isCrit,
                    HitPosition            = serverHitPoint,
                    OwnerMidId             = context.OwnerMidId,
                    FiredByNetworkObjectId = context.FiredByNetworkObjectId,
                    IsBotOwner             = context.IsBotOwner,
                    WeaponLevel            = context.WeaponLevel,
                    GameData               = gameData
                };

                OnServerHitConfirmed?.Invoke(payload);
            }

            // Notify all clients to spawn visual (regardless of hit validity)
            SpawnVisualClientRpc(
                clientResult.Origin,
                serverConfirmed ? serverHitPoint : clientResult.HitPoint,
                configId,
                serverConfirmed && serverTargetId != 0,
                _nextVisualId++);
        }

        #endregion

        #region Server Validation

        private bool ValidateHitServer(
            RaycastFireResult clientResult,
            out Vector3       serverHitPoint,
            out ulong         serverTargetId,
            out bool          serverHeadshot)
        {
            serverHitPoint = clientResult.HitPoint;
            serverTargetId = 0;
            serverHeadshot = false;

            // Server re-casts from client origin (we trust origin, not hit point)
            RaycastHit2D serverHit = Physics2D.Raycast(
                clientResult.Origin,
                clientResult.Direction,
                1000f,
                _serverRaycastLayers);

            if (!serverHit.collider)
                return false;

            serverHitPoint = serverHit.point;

            // Tolerance check
            float dist = Vector3.Distance(serverHitPoint, clientResult.HitPoint);
            if (dist > _hitValidationTolerance)
                return false;

            // Get NetworkObject ID from hit collider
            var netObj = serverHit.collider.GetComponentInParent<NetworkObject>();
            if (netObj != null)
                serverTargetId = netObj.NetworkObjectId;

            serverHeadshot = clientResult.IsHeadshot; // trust client headshot for now
            // — override with server capsule zone check in your derived class

            return true;
        }

        #endregion

        #region Client — Visual

        [ClientRpc]
        private void SpawnVisualClientRpc(
            Vector3 origin,
            Vector3 hitPoint,
            ushort  configId,
            bool    confirmedHit,
            int     visualId)
        {
            SpawnVisualLocal(origin, hitPoint, configId, visualId);
        }

        private void SpawnVisualLocal(
            Vector3 origin, Vector3 hitPoint,
            ushort configId, int visualId)
        {
            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null || !cfg.UseSprite) return;

            Vector3 dir = (hitPoint - origin).normalized;
            Quaternion rot = dir.sqrMagnitude > 0.001f
                ? Quaternion.LookRotation(Vector3.forward, dir)
                : Quaternion.identity;

            var obj = LocalObjectPool.Instance.GetObject(
                _visualPoolType, origin, rot);

            if (obj == null) return;

            // Initialise the visual script if present
            var vis = obj.GetComponent<ProjectileVisual_>();
            vis?.InitializeClientVisual(
                MID_AllProjectileNames.none, // offline visual — no enum lookup
                origin, dir, _visualTravelSpeed);

            _activeVisuals.Add(new ActiveVisual
            {
                VisualId = visualId,
                Obj      = obj,
                Origin   = origin,
                HitPoint = hitPoint,
                Speed    = _visualTravelSpeed,
                ConfigId = configId
            });
        }

        #endregion

        #region Update — Visual Movement

        private void Update()
        {
            if (_activeVisuals.Count == 0) return;

            var toRemove = new List<int>();

            foreach (var v in _activeVisuals)
            {
                if (v.Obj == null) { toRemove.Add(v.VisualId); continue; }

                v.Obj.transform.position = Vector3.MoveTowards(
                    v.Obj.transform.position,
                    v.HitPoint,
                    v.Speed * Time.deltaTime);

                if (Vector3.Distance(v.Obj.transform.position, v.HitPoint) < 0.05f)
                {
                    PlayImpactEffect(v);
                    ReturnVisual(v);
                    toRemove.Add(v.VisualId);
                }
            }

            _activeVisuals.RemoveAll(v => toRemove.Contains(v.VisualId));
        }

        #endregion

        #region Visual Cleanup

        private void PlayImpactEffect(ActiveVisual v)
        {
            var cfg = ProjectileRegistry.Instance.Get(v.ConfigId);
            if (cfg == null) return;

            LocalParticlePool.Instance?.GetObject(
                cfg.ImpactEffectType, v.HitPoint, Quaternion.identity);
        }

        private void ReturnVisual(ActiveVisual v)
        {
            if (v.Obj == null) return;
            LocalObjectPool.Instance?.ReturnObject(v.Obj, _visualPoolType);
        }

        #endregion

        #region Offline Support

        /// <summary>
        /// Handle a raycast fire in offline/LocalOnly mode.
        /// No RPCs — applies damage immediately and spawns a local visual.
        /// </summary>
        public void OfflineHandleFire(
            RaycastFireResult result,
            ushort            configId,
            uint              ownerLocalId,
            float             damageMultiplier)
        {
            var cfg = ProjectileRegistry.Instance.Get(configId);
            if (cfg == null) return;

            if (result.DidHit && LocalProjectileManager.Instance != null)
            {
                float normDist = 0f; // raycast = point-blank always
                float damage   = cfg.EvaluateDamage(normDist);
                if (result.IsHeadshot) damage *= cfg.HeadshotMultiplier;
                bool isCrit = UnityEngine.Random.value < cfg.CritChance;
                if (isCrit) damage *= cfg.CritMultiplier;
                damage *= damageMultiplier;

                // Look up the target in LocalProjectileManager by NetworkObjectId
                // (offline targets use GetInstanceID — caller must match the convention)
                uint localTargetId = (uint)result.HitTargetNetworkId; // cast — see note above

                var payload = new LocalHitPayload
                {
                    ProjId       = 0,
                    ConfigId     = configId,
                    Is3D         = cfg.Is3D,
                    Damage       = damage,
                    IsHeadshot   = result.IsHeadshot,
                    IsCrit       = isCrit,
                    HitPosition  = result.HitPoint,
                    OwnerLocalId = ownerLocalId
                };

                LocalProjectileManager.Instance.OnHit?.Invoke(payload);
            }

            // Always spawn a local visual
            SpawnVisualLocal(result.Origin, result.HitPoint, configId, _nextVisualId++);
        }

        #endregion

        #region Helpers

        private static ServerProjectileData BuildRaycastGameData(
            WeaponFireContext context, ushort configId, ProjectileConfigSO cfg)
        {
            // Minimal shell — raycast hits have no persistent sim projectile
            return new ServerProjectileData(
                MID_AllProjectileNames.none,
                context.OwnerMidId,
                context.FiredByNetworkObjectId,
                context.IsBotOwner,
                context.WeaponLevel,
                Vector2.zero,
                context.DamageMultiplier,
                cfg);
        }

        private void Log(string msg)
        {
            if (_enableLogs)
                MID_HelperFunctions.LogDebug(msg, nameof(RaycastProjectileHandler));
        }

        #endregion
    }
}
