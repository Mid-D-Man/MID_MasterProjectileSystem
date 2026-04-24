// SimulationMode.cs
// Isolated enum file — imported by MID_MasterProjectileSystem, ProjectileTypeRouter,
// ServerProjectileAuthority, LocalProjectileManager, and BatchSpawnHelper.
// Kept in its own file so no circular dependencies form at the enum level.

namespace MidManStudio.Projectiles
{
    /// <summary>
    /// Determines how a projectile is simulated and networked.
    /// Assigned per-spawn by ProjectileTypeRouter based on weapon context and projectile config.
    ///
    /// IMPORTANT: Shooting mode (burst, shotgun, single, auto) is a WEAPON property.
    /// SimulationMode is a PHYSICS/NETWORK property — it answers "who runs the math".
    /// </summary>
    public enum SimulationMode : byte
    {
        /// <summary>
        /// Instant hitscan. Server casts Physics2D/3D ray on fire.
        /// Client visual travels to predetermined endpoint and vanishes.
        /// Zero ongoing simulation cost. Best for: SMG, pistol, rifle, shotgun (fireRate >= 10).
        /// </summary>
        Raycast = 0,

        /// <summary>
        /// Server runs full Rust 2D tick + spatial-grid collision every FixedUpdate.
        /// Clients predict with origin + dir * speed * elapsed. Snapshots reconcile drift.
        /// No NetworkTransform. Best for: bullets, plasma, piercing rounds, basic 2D projectiles.
        /// </summary>
        RustSim2D = 1,

        /// <summary>
        /// Server runs Rust 3D tick loop with NativeProjectile3D buffer.
        /// Architecture identical to RustSim2D — separate struct and Rust entry points.
        /// Best for: 3D bullets, 3D missiles, 3D arrows.
        /// </summary>
        RustSim3D = 2,

        /// <summary>
        /// Unity Rigidbody2D/3D. Server owns physics, clients receive position via ObjectNetSync
        /// (NetworkTransform). Low spawn rate ONLY — never use on bullets.
        /// Best for: rockets, grenades, bouncy projectiles, sticky, explosive area.
        /// </summary>
        PhysicsObject = 3,

        /// <summary>
        /// Single player / offline / practice mode. Full Rust sim, no NGO, no RPCs.
        /// LocalProjectileManager owns tick, collision, render, trail.
        /// </summary>
        LocalOnly = 4
    }

    /// <summary>
    /// Network authority model for a projectile batch.
    /// Passed alongside SimulationMode to distinguish offline vs online paths.
    /// </summary>
    public enum NetworkVariant : byte
    {
        /// <summary>No network — LocalOnly mode.</summary>
        None = 0,

        /// <summary>Server authoritative — server runs sim, clients predict + reconcile.</summary>
        ServerAuth = 1
    }

    /// <summary>
    /// Movement behaviour types that map directly to Rust NativeProjectile.movement_type.
    /// Byte values must match the Rust constants in simulation.rs exactly.
    /// </summary>
    public enum ProjectileMovementType : byte
    {
        Straight  = 0,  // Constant velocity + optional linear accel (gravity, wind)
        Arching   = 1,  // Same as straight but timer_t advances for visual interpolation
        Guided    = 2,  // Turns toward ax/ay(/az) target direction each tick
        Teleport  = 3   // Discrete jumps every ~0.12s; position is not interpolated
    }

    /// <summary>
    /// Piercing capability. Maps to NativeProjectile.piercing_type.
    /// Collision count handling lives in C# (RustSimAdapter), not Rust.
    /// </summary>
    public enum ProjectilePiercingType : byte
    {
        None   = 0,  // Dies on first hit
        Piecer = 1,  // Survives up to MaxCollisions hits
        Random = 2   // CollisionsRemaining = random(1, MaxCollisions) at spawn
    }
}
