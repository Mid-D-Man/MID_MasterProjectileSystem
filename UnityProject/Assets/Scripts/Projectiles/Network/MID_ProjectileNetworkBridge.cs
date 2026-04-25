// MID_ProjectileNetworkBridge.cs
// ALL NGO RPCs live here and ONLY RPCs.
// Zero game logic — pure messaging layer.
//
// Responsibilities:
//   FireServerRpc          — client fires a sim projectile (RustSim2D/3D)
//   RaycastFireServerRpc   — client fires a raycast weapon hit
//   SpawnConfirmedClientRpc — server confirms a projectile spawned (clients start prediction)
//   HitConfirmedClientRpc  — server confirms a hit (clients play impact, stop prediction visual)
//   SendSnapshotClientRpc  — server sends position snapshots for reconciliation
//
// What this class does NOT do:
//   - Compute damage (RustSimAdapter does that)
//   - Spawn into Rust buffers (ServerProjectileAuthority does that)
//   - Move prediction visuals (ClientPredictionManager does that)
//   - Apply damage to health components (game layer does that)
//
// MID ID note:
//   ownerMidId is NOT NetworkObject.OwnerClientId.
//   Players: ownerMidId == their NGO client ID (they coincide for players).
//   Bots: ownerMidId is a stable 100-999 range ID assigned at bot spawn.
//   This prevents bot projectiles from being attributed to the server (ownerId=0).
//   isBotOwner flag disambiguates the two cases throughout the system.

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
    //  Network serialisable fire request
    //  Sent from client to server on every weapon fire.
    //  Kept minimal — the server re-derives most data from configId.
    // ─────────────────────────────────────────────────────────────────────────

    public struct ProjectileFireRequest : INetworkSerializable
    {
        /// Registered config ID (ushort — compact, Rust-compatible).
        public ushort ConfigId;

        /// World-space barrel tip position.
        public Vector3 Origin;

        /// Normalised travel direction.
        public Vector3 Direction;

        /// Speed — sent so server can apply latency compensation correctly.
        /// Derived from config at the client, verified against config range on server.
        public float Speed;

        /// Random seed for deterministic batch spawning (patterns, speed variance).
        /// Client generates this; server uses the SAME seed so all clients agree.
        public uint RngSeed;

        /// Number of projectiles in this fire event (e.g. 8 for shotgun).
        public byte ProjectileCount;

        /// MID ID of the firing entity.
        public ulong OwnerMidId;

        /// NetworkObject ID of the weapon/character.
        public ulong FiredByNetworkObjectId;

        /// True if the firer is a bot.
        public bool IsBotOwner;

        /// Weapon level.
        public byte WeaponLevel;

        /// Damage multiplier from power-ups.
        public float DamageMultiplier;

        /// Server tick at which the client fired (for latency compensation).
        public int ClientFireTick;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ConfigId);
            s.SerializeValue(ref Origin);
            s.SerializeValue(ref Direction);
            s.SerializeValue(ref Speed);
            s.SerializeValue(ref RngSeed);
            s.SerializeValue(ref ProjectileCount);
            s.SerializeValue(ref OwnerMidId);
            s.SerializeValue(ref FiredByNetworkObjectId);
            s.SerializeValue(ref IsBotOwner);
            s.SerializeValue(ref WeaponLevel);
            s.SerializeValue(ref DamageMultiplier);
            s.SerializeValue(ref ClientFireTick);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Spawn confirmation — server → all clients
    //  Clients use this to bind their prediction visual to a confirmed projId.
    // ─────────────────────────────────────────────────────────────────────────

    public struct SpawnConfirmation : INetworkSerializable
    {
        /// Base proj ID assigned by server. Clients add [0..count-1] for each pellet.
        public uint BaseProjId;

        /// Number of projectiles spawned.
        public byte ProjectileCount;

        /// Config ID — clients need this to spawn the correct visual.
        public ushort ConfigId;

        /// Server tick at which the projectile(s) entered the sim buffer.
        public int ServerSpawnTick;

        /// Origin and direction — clients recompute prediction from these.
        public Vector3 Origin;
        public Vector3 Direction;
        public float   Speed;

        /// MID ID of the owner — clients filter their own projectiles for prediction.
        public ulong OwnerMidId;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref BaseProjId);
            s.SerializeValue(ref ProjectileCount);
            s.SerializeValue(ref ConfigId);
            s.SerializeValue(ref ServerSpawnTick);
            s.SerializeValue(ref Origin);
            s.SerializeValue(ref Direction);
            s.SerializeValue(ref Speed);
            s.SerializeValue(ref OwnerMidId);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Hit confirmation — server → all clients
    // ─────────────────────────────────────────────────────────────────────────

    public struct HitConfirmation : INetworkSerializable
    {
        /// Which projectile hit (0 = raycast hit, no persistent projectile).
        public uint   ProjId;

        /// Which target was hit.
        public ulong  TargetNetworkId;

        /// Damage dealt (server-authoritative).
        public float  Damage;

        /// Impact position — clients play impact effect here.
        public Vector3 HitPosition;

        /// Was it a headshot?
        public bool IsHeadshot;

        /// Was it a critical hit?
        public bool IsCrit;

        /// Config ID — clients choose correct impact effect.
        public ushort ConfigId;

        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref ProjId);
            s.SerializeValue(ref TargetNetworkId);
            s.SerializeValue(ref Damage);
            s.SerializeValue(ref HitPosition);
            s.SerializeValue(ref IsHeadshot);
            s.SerializeValue(ref IsCrit);
            s.SerializeValue(ref ConfigId);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Bridge
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// All NGO RPCs for the projectile system live here.
    /// Attach to the same persistent networked GameObject as ServerProjectileAuthority.
    /// </summary>
    public sealed class MID_ProjectileNetworkBridge : NetworkBehaviour
    {
        #region References (set by MID_MasterProjectileSystem after construction)

        public ServerProjectileAuthority Authority { get; set; }
        public ClientPredictionManager   Prediction { get; set; }
        public RaycastProjectileHandler  RaycastHandler { get; set; }
        public ProjectileImpactHandler   ImpactHandler { get; set; }

        #endregion

        #region Events (game layer subscribes to these)

        /// <summary>
        /// Fired on all clients when the server confirms a hit.
        /// Game's HUD, audio, screen-shake systems subscribe here.
        /// </summary>
        public event Action<HitConfirmation> OnHitConfirmedLocal;

        #endregion

        #region Debug

        [SerializeField] private bool _enableLogs = false;

        #endregion

        #region Lifecycle

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            // Wire server adapter events → outbound RPCs
            if (IsServer && Authority != null)
            {
                Authority.Adapter.OnProjectileHit += ServerOnProjectileHit;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && Authority != null)
            {
                Authority.Adapter.OnProjectileHit -= ServerOnProjectileHit;
            }
            base.OnNetworkDespawn();
        }

        #endregion

        #region Server → Client hit routing

        /// <summary>
        /// Called by RustSimAdapter event on the server.
        /// Converts the payload to a HitConfirmation RPC.
        /// </summary>
        private void ServerOnProjectileHit(ProjectileHitPayload payload)
        {
            if (!IsServer) return;

            var confirm = new HitConfirmation
            {
                ProjId          = payload.ProjId,
                TargetNetworkId = payload.TargetId, // uint → ulong safe cast
                Damage          = payload.Damage,
                HitPosition     = payload.HitPosition,
                IsHeadshot      = payload.IsHeadshot,
                IsCrit          = payload.IsCrit,
                ConfigId        = payload.ConfigId
            };

            HitConfirmedClientRpc(confirm);
        }

        #endregion

        #region Client → Server: Sim Projectile Fire

        /// <summary>
        /// Client calls this when a weapon fires a RustSim2D/3D projectile.
        /// Do NOT call for raycast weapons — use RaycastFireServerRpc.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void FireServerRpc(
            ProjectileFireRequest request,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer) return;

            // Validate speed against config (anti-cheat: client cannot inflate speed)
            var cfg = ProjectileRegistry.Instance.Get(request.ConfigId);
            if (cfg == null)
            {
                LogWarning($"FireServerRpc: unknown configId {request.ConfigId}");
                return;
            }

            float clampedSpeed = Mathf.Clamp(request.Speed, cfg.MinSpeed, cfg.MaxSpeed);

            // Build WeaponFireContext from the request
            var context = new WeaponFireContext
            {
                FireRate               = 0f, // not needed server-side for routing
                ProjectileCount        = request.ProjectileCount,
                IsNetworked            = true,
                IsRaycastWeapon        = false,
                LatencyCompensation    = ComputeLatencyComp(rpcParams, request.ClientFireTick),
                OwnerMidId             = request.OwnerMidId,
                FiredByNetworkObjectId = request.FiredByNetworkObjectId,
                IsBotOwner             = request.IsBotOwner,
                WeaponLevel            = request.WeaponLevel,
                DamageMultiplier       = request.DamageMultiplier
            };

            // Build spawn points — single direction for server (pattern was computed client-side)
            // For server, we re-derive a single forward direction and trust the count.
            // Pattern spread is cosmetic — server only needs to know how many exist.
            var spawnPts = BuildServerSpawnPoints(
                request.Origin, request.Direction, clampedSpeed, request.ProjectileCount);

            var rustParams  = ProjectileRegistry.Instance.GetRustSpawnParams(
                request.ConfigId, clampedSpeed);

            bool is3D  = cfg.Is3D;
            uint baseId = Authority.AllocateProjIds(request.ProjectileCount);

            // Build ServerProjectileData template
            var dataTemplate = new ServerProjectileData(
                MID_AllProjectileNames.none, // game layer fills this via derived class
                request.OwnerMidId,
                request.FiredByNetworkObjectId,
                request.IsBotOwner,
                request.WeaponLevel,
                request.Origin,
                request.DamageMultiplier,
                cfg);

            int written;
            if (!is3D)
            {
                var (writePtr, remaining) = Authority.Get2DWriteHead();
                written = BatchSpawnHelper.SpawnBatch2D(
                    spawnPts, request.ProjectileCount, null, rustParams,
                    request.ConfigId, 0, baseId, writePtr, remaining,
                    context.LatencyCompensation);
                Authority.NotifyBatchSpawned2D(written, baseId, dataTemplate);
            }
            else
            {
                var (writePtr, remaining) = Authority.Get3DWriteHead();
                written = BatchSpawnHelper.SpawnBatch3D(
                    spawnPts, request.ProjectileCount, rustParams,
                    request.ConfigId, 0, baseId, writePtr, remaining,
                    context.LatencyCompensation);
                Authority.NotifyBatchSpawned3D(written, baseId, dataTemplate);
            }

            if (written <= 0)
            {
                LogWarning("FireServerRpc: no projectiles written to buffer (buffer full?)");
                return;
            }

            Log($"FireServerRpc confirmed: configId={request.ConfigId} " +
                $"count={written} baseId={baseId} owner={request.OwnerMidId}");

            // Confirm to all clients
            var confirm = new SpawnConfirmation
            {
                BaseProjId      = baseId,
                ProjectileCount = (byte)written,
                ConfigId        = request.ConfigId,
                ServerSpawnTick = GetServerTick(),
                Origin          = request.Origin,
                Direction       = request.Direction,
                Speed           = clampedSpeed,
                OwnerMidId      = request.OwnerMidId
            };

            SpawnConfirmedClientRpc(confirm);
        }

        #endregion

        #region Client → Server: Raycast Fire

        /// <summary>
        /// Client calls this when a raycast weapon fires.
        /// Server validates hit, then broadcasts HitConfirmedClientRpc.
        /// </summary>
        [ServerRpc(RequireOwnership = false)]
        public void RaycastFireServerRpc(
            ProjectileFireRequest request,
            Vector3 clientHitPoint,
            bool    clientDidHit,
            bool    clientIsHeadshot,
            ulong   clientHitTargetId,
            ServerRpcParams rpcParams = default)
        {
            if (!IsServer) return;
            if (RaycastHandler == null) return;

            var result = new RaycastFireResult
            {
                Origin            = request.Origin,
                Direction         = request.Direction,
                HitPoint          = clientHitPoint,
                DidHit            = clientDidHit,
                HitTargetNetworkId = clientHitTargetId,
                IsHeadshot        = clientIsHeadshot
            };

            var context = new WeaponFireContext
            {
                IsRaycastWeapon        = true,
                IsNetworked            = true,
                OwnerMidId             = request.OwnerMidId,
                FiredByNetworkObjectId = request.FiredByNetworkObjectId,
                IsBotOwner             = request.IsBotOwner,
                WeaponLevel            = request.WeaponLevel,
                DamageMultiplier       = request.DamageMultiplier
            };

            RaycastHandler.ServerHandleFire(result, context, request.ConfigId);
        }

        #endregion

        #region Server → All Clients: Spawn Confirmed

        /// <summary>
        /// Server tells all clients a projectile batch was confirmed.
        /// Clients bind their prediction visual to the confirmed proj IDs.
        /// </summary>
        [ClientRpc]
        public void SpawnConfirmedClientRpc(SpawnConfirmation confirmation)
        {
            if (IsServer) return; // server already has the data

            Log($"SpawnConfirmedClientRpc: baseId={confirmation.BaseProjId} " +
                $"count={confirmation.ProjectileCount}");

            Prediction?.OnSpawnConfirmed(confirmation);
        }

        #endregion

        #region Server → All Clients: Hit Confirmed

        /// <summary>
        /// Server confirms a projectile hit.
        /// All clients: snap prediction visual to hitPos, play impact effect.
        /// </summary>
        [ClientRpc]
        public void HitConfirmedClientRpc(HitConfirmation confirmation)
        {
            Log($"HitConfirmedClientRpc: projId={confirmation.ProjId} " +
                $"damage={confirmation.Damage:F1} headshot={confirmation.IsHeadshot}");

            // Stop prediction visual for this projectile
            if (!IsServer)
                Prediction?.OnHitConfirmed(confirmation);

            // Play impact effect on all clients (including server-as-host)
            ImpactHandler?.PlayImpact(
                confirmation.HitPosition,
                confirmation.ConfigId,
                confirmation.IsHeadshot);

            // Fire local event for game HUD / audio / screen-shake
            OnHitConfirmedLocal?.Invoke(confirmation);
        }

        #endregion

        #region Server → All Clients: Position Snapshot

        /// <summary>
        /// Server sends position snapshots for client prediction reconciliation.
        /// Called every N FixedUpdates by ServerProjectileAuthority.
        /// </summary>
        [ClientRpc]
        public void SendSnapshotClientRpc(
            ProjectileSnapshot2D[] snapshots2D, int count2D,
            ProjectileSnapshot3D[] snapshots3D, int count3D)
        {
            if (IsServer) return; // server already has authoritative positions

            Prediction?.ReconcileSnapshot(snapshots2D, count2D, snapshots3D, count3D);
        }

        // Internal overload called by ServerProjectileAuthority
        internal void SendSnapshotClientRpc(
            ProjectileSnapshot2D[] snapshots2D, int count2D,
            ProjectileSnapshot3D[] snapshots3D, int count3D,
            ClientRpcParams _ = default)
        {
            // Build truncated arrays for the RPC (avoid sending dead space)
            var slice2D = new ProjectileSnapshot2D[count2D];
            var slice3D = new ProjectileSnapshot3D[count3D];
            Array.Copy(snapshots2D, slice2D, count2D);
            Array.Copy(snapshots3D, slice3D, count3D);
            SendSnapshotClientRpc(slice2D, count2D, slice3D, count3D);
        }

        #endregion

        #region Utility

        /// <summary>Returns the current server network tick.</summary>
        public int GetServerTick()
        {
            return NetworkManager.Singleton != null
                ? NetworkManager.Singleton.ServerTime.Tick
                : 0;
        }

        /// <summary>
        /// Compute latency compensation in seconds based on RTT.
        /// Applied to initial projectile position at spawn — offsets by
        /// how far the projectile would have travelled during client→server RTT.
        /// </summary>
        private float ComputeLatencyComp(ServerRpcParams rpc, int clientTick)
        {
            if (NetworkManager.Singleton == null) return 0f;

            int serverTick = GetServerTick();
            int deltaTicks = serverTick - clientTick;

            // deltaTicks * tickInterval = approximate one-way latency
            // The projectile should be offset by this distance
            float tickInterval = 1f / NetworkManager.Singleton.NetworkTickSystem.TickRate;
            float latency = Mathf.Clamp(deltaTicks * tickInterval, 0f, 0.5f);

            return latency;
        }

        /// <summary>
        /// Build server-side spawn points — single forward direction per projectile.
        /// Server doesn't need pattern spread (it's visual only).
        /// All pellets in a shotgun burst converge toward the same hit point on server.
        /// </summary>
        private static SpawnPoint[] BuildServerSpawnPoints(
            Vector3 origin, Vector3 direction, float speed, int count)
        {
            var pts = new SpawnPoint[count];
            for (int i = 0; i < count; i++)
            {
                pts[i] = new SpawnPoint
                {
                    Origin    = origin,
                    Direction = direction.normalized,
                    Speed     = speed
                };
            }
            return pts;
        }

        private void Log(string msg)
        {
            if (_enableLogs)
                MID_HelperFunctions.LogDebug(msg, nameof(MID_ProjectileNetworkBridge));
        }

        private void LogWarning(string msg)
        {
            MID_HelperFunctions.LogWarning(msg, nameof(MID_ProjectileNetworkBridge));
        }

        #endregion
    }
}
