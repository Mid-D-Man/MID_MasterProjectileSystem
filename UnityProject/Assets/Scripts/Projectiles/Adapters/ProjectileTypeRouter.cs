// ProjectileTypeRouter.cs
// Pure static class — no MonoBehaviour, no state, no dependencies on scene objects.
// Single responsibility: given a weapon context + projectile config, return SimulationMode.
//
// IMPORTANT design rule:
//   Shooting MODE (burst, auto, shotgun, single) = weapon property, never touches this class.
//   SimulationMode = physics/network property — answers "who runs the simulation math".
//   A shotgun firing 8 pellets can use Raycast mode. An SMG can use RustSim2D.
//   These are orthogonal decisions.
//
// Decision priority (highest to lowest):
//   1. Config.HasSimModeOverride         — explicit per-config override wins always
//   2. Config.get_ProjectileType         — rocket/fireball force PhysicsObject
//   3. Config.get_ProjectileExtraPhysicsType — bouncy/sticky force PhysicsObject
//   4. WeaponContext.IsNetworked == false — offline/practice forces LocalOnly
//   5. WeaponContext.FireRate >= threshold AND config allows raycast → Raycast
//   6. Config.get_Is3D                   → RustSim3D
//   7. Default                           → RustSim2D

using MidManStudio.InGame.ProjectileConfigs;

namespace MidManStudio.Projectiles
{
    /// <summary>
    /// Context provided by the weapon system when requesting a projectile spawn.
    /// The weapon owns fire rate, burst count, and networking state.
    /// The projectile config owns physics type, piercing, and lifetime.
    /// </summary>
    public struct WeaponFireContext
    {
        /// <summary>
        /// Rounds per second at which this weapon fires. Used to decide Raycast vs RustSim.
        /// Burst fire: use the burst-internal fire rate, not the burst repeat rate.
        /// </summary>
        public float FireRate;
/// <summary>
/// True when the weapon script handles Physics2D/3D.Raycast itself.
/// When true, the projectile system handles visual + RPC only — it does NOT cast a ray.
/// The weapon calls MID_MasterProjectileSystem.RegisterRaycastHit() with the result.
/// </summary>
public bool IsRaycastWeapon;
        /// <summary>
        /// Number of projectiles fired in this single fire event (e.g. 8 for shotgun pellets).
        /// Used by BatchSpawnHelper to choose Burst vs C# fill path, not for SimulationMode routing.
        /// </summary>
        public int   ProjectileCount;

        /// <summary>
        /// True if this is a networked multiplayer session. False for local/offline play.
        /// When false, router always returns LocalOnly regardless of other parameters.
        /// </summary>
        public bool  IsNetworked;

        /// <summary>
        /// Optional latency compensation in seconds. Applied to initial projectile position
        /// at spawn time (position += dir * speed * latencyComp). Zero = no compensation.
        /// </summary>
        public float LatencyCompensation;

        /// <summary>
        /// MID ID of the firing entity (player or bot). Stored in ServerProjectileData.
        /// </summary>
        public ulong OwnerMidId;

        /// <summary>
        /// NetworkObject ID of the weapon/character that fired. Used for self-collision checks.
        /// </summary>
        public ulong FiredByNetworkObjectId;

        /// <summary>True if the owner is a bot (MID ID 100-999).</summary>
        public bool  IsBotOwner;

        /// <summary>Weapon level — affects kill effect probability in ServerProjectileData.</summary>
        public byte  WeaponLevel;

        /// <summary>
        /// Damage multiplier from power-ups or abilities. Applied at spawn in ServerProjectileData.
        /// </summary>
        public float DamageMultiplier;
    }

    /// <summary>
    /// Routing result — SimulationMode plus metadata consumed by MID_MasterProjectileSystem.
    /// </summary>
    public struct RoutingResult
    {
        public SimulationMode Mode;
        public NetworkVariant Network;

        /// <summary>
        /// True when the result came from a per-config PreferredSimMode override.
        /// Informational — callers don't need to act on this.
        /// </summary>
        public bool WasOverridden;
    }

    /// <summary>
    /// Routes a fire event to the correct SimulationMode.
    /// All methods are pure functions — deterministic, no side effects.
    /// </summary>
    public static class ProjectileTypeRouter
    {
        // ── Thresholds ────────────────────────────────────────────────────────

        /// <summary>
        /// Minimum fire rate (rounds/sec) for a projectile to be eligible for Raycast mode.
        /// Matches the benchmark finding: raycast recommended for fireRate >= 10.
        /// Weapons below this threshold use RustSim for visible projectile travel.
        /// </summary>
        public const float RaycastFireRateThreshold = 10f;

        // ─────────────────────────────────────────────────────────────────────
        //  Primary routing entry point
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Determine SimulationMode and NetworkVariant for a fire event.
        /// Called by MID_MasterProjectileSystem.Fire() before spawning anything.
        /// </summary>
        /// <param name="config">Projectile config SO (from ProjectileConfigManager).</param>
        /// <param name="context">Weapon fire context (fire rate, networking, owner).</param>
        public static RoutingResult Route(
            ProjectileConfigScriptableObject config,
            WeaponFireContext context)
        {
            // Offline/practice — no network, no authority
            if (!context.IsNetworked)
            {
                return new RoutingResult
                {
                    Mode           = SimulationMode.LocalOnly,
                    Network        = NetworkVariant.None,
                    WasOverridden  = false
                };
            }

            // Per-config explicit override wins over all automatic routing
            if (config.HasSimModeOverride)
            {
                return new RoutingResult
                {
                    Mode           = config.get_PreferredSimMode,
                    Network        = NetworkVariant.ServerAuth,
                    WasOverridden  = true
                };
            }

            var mode = ComputeMode(config, context);

            return new RoutingResult
            {
                Mode          = mode,
                Network       = NetworkVariant.ServerAuth,
                WasOverridden = false
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Internal routing logic
        // ─────────────────────────────────────────────────────────────────────

        private static SimulationMode ComputeMode(
            ProjectileConfigScriptableObject config,
            WeaponFireContext context)
        {
            // Physics-dependent types always go to PhysicsObject regardless of fire rate.
            // These use Unity Rigidbody2D/3D via ObjectNetSync, not the Rust sim buffer.
            if (RequiresPhysicsObject(config))
                return SimulationMode.PhysicsObject;

            // High fire rate basic projectiles → instant hitscan (Raycast).
            // Raycast is only valid when the projectile config is eligible:
            //   no piercing, no special physics, no exotic movement.
            if (context.FireRate >= RaycastFireRateThreshold && IsRaycastEligible(config))
                return SimulationMode.Raycast;

            // 3D sim path
            if (config.get_Is3D)
                return SimulationMode.RustSim3D;

            // Default: 2D Rust simulation
            return SimulationMode.RustSim2D;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Eligibility checks
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// True when the projectile type requires Unity physics (Rigidbody2D/3D) and
        /// therefore cannot be simulated in the Rust tick buffer.
        /// Rockets/fireballs need explosion mechanics; bouncy/sticky need Physics material.
        /// </summary>
        public static bool RequiresPhysicsObject(ProjectileConfigScriptableObject config)
        {
            // Rocket and fireball types need Unity physics for explosion and arc mechanics
            switch (config.get_ProjectileType)
            {
                case ProjectileConfigScriptableObject.ProjectileType.rocket:
                case ProjectileConfigScriptableObject.ProjectileType.fireBall:
                    return true;
            }

            // Bouncy and sticky require Physics2D material interactions
            switch (config.get_ProjectileExtraPhysicsType)
            {
                case ProjectileConfigScriptableObject.ProjectileExtraPhysicsType.bouncy:
                case ProjectileConfigScriptableObject.ProjectileExtraPhysicsType.sticky:
                    return true;
            }

            // Explosive class needs area-damage physics query on impact
            switch (config.get_ProjectileClass)
            {
                case ProjectileConfigScriptableObject.ProjectileClass.exploder:
                case ProjectileConfigScriptableObject.ProjectileClass.explosiveEffector:
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when the projectile is safe for instant hitscan (Raycast mode).
        /// Piercing rounds, physics projectiles, and exotic movement types are ineligible
        /// because Raycast has no ongoing position state to apply those behaviours to.
        /// </summary>
        public static bool IsRaycastEligible(ProjectileConfigScriptableObject config)
        {
            // Piercing requires per-tick collision counting — Raycast has no ongoing state
            if (config.get_ProjectilePirecingAbility !=
                ProjectileConfigScriptableObject.ProjectilePirecingCapabilities.nonePirecer)
                return false;

            // Physics-dependent types are already caught by RequiresPhysicsObject,
            // but double-check here for safety
            if (RequiresPhysicsObject(config))
                return false;

            // 3D projectiles use Rust3D sim — Raycast is 2D Physics2D.Raycast only
            if (config.get_Is3D)
                return false;

            return true;
        }

        /// <summary>
        /// Helper used by editor tooling and debug panels to explain a routing decision.
        /// Not called at runtime.
        /// </summary>
        public static string ExplainRoute(
            ProjectileConfigScriptableObject config,
            WeaponFireContext context)
        {
            if (!context.IsNetworked)
                return "LocalOnly — not a networked session.";

            if (config.HasSimModeOverride)
                return $"Config override: {config.get_PreferredSimMode}.";

            if (RequiresPhysicsObject(config))
                return $"PhysicsObject — type '{config.get_ProjectileType}' " +
                       $"or physics '{config.get_ProjectileExtraPhysicsType}' requires Unity Rigidbody.";

            if (context.FireRate >= RaycastFireRateThreshold && IsRaycastEligible(config))
                return $"Raycast — fireRate {context.FireRate:F1} >= {RaycastFireRateThreshold} " +
                       $"and projectile has no piercing or special physics.";

            if (config.get_Is3D)
                return "RustSim3D — Is3D flag set on config.";

            return "RustSim2D — default.";
        }
    }
}
