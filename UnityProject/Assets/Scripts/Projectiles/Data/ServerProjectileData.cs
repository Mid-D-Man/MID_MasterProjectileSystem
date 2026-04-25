// ServerProjectileData.cs — UPDATE
//
// Changes from plan:
//   + projectileId_u32 (uint) — matches NativeProjectile.proj_id exactly
//   + configId (ushort)       — matches NativeProjectile.config_id
//   + is3D (bool)
//   + isCrit (bool)           — pre-rolled at spawn, applied in RustSimAdapter
//   + critChance (float)      — cached from config so RustSimAdapter doesn't need config ref
//   + CloneForSpawn()         — creates per-projectile copy from a template
//   - REMOVED UpdatePosition() — position is owned by Rust, C# never moves it
//   - REMOVED Vector2 position/direction/velocity (Rust owns these)
//   + Vector3 position3D / direction3D for 3D spawns (spawn-time only, not updated)
//   All damage fields KEPT — these never go to Rust
//   All owner/kill/weapon type fields KEPT
//
// ServerProjectileData is C#-only game data that lives alongside NativeProjectile.
// Rust does not know this struct exists.

using UnityEngine;
using Unity.Netcode;
using MidManStudio.InGame.GameItemData;
using MidManStudio.InGame.ProjectileConfigs;
using MidManStudio.InGame.NetworkDataStructures;

namespace MidManStudio.Projectiles
{
    /// <summary>
    /// Server-side gameplay data for a single projectile.
    /// Lives in RustSimAdapter._projData, keyed by projectileId_u32.
    /// Contains everything the damage system needs — nothing Rust needs.
    /// Position is owned by Rust (NativeProjectile.x/y) — never duplicated here.
    /// </summary>
    public class ServerProjectileData
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Identity — links this to the Rust NativeProjectile
        // ─────────────────────────────────────────────────────────────────────

        /// Matches NativeProjectile.proj_id. Primary key in RustSimAdapter dictionary.
        public uint   projectileId_u32;

        /// Matches NativeProjectile.config_id. Used for config lookups on hit.
        public ushort configId;

        /// True if this projectile uses the 3D sim buffer (NativeProjectile3D).
        public bool is3D;

        /// The enum name — used by ProjectileConfigManager.GetProjectileConfig().
        public MID_AllProjectileNames projectileName;

        // ─────────────────────────────────────────────────────────────────────
        //  Owner identity
        // ─────────────────────────────────────────────────────────────────────

        /// MID ID of the firing entity (player or bot ID 100-999).
        public ulong ownerClientId;

        /// NetworkObject ID of the weapon/character that fired.
        public ulong firedByNetworkObjectId;

        /// True if the owner is a bot.
        public bool isBotOwner;

        /// Weapon level — affects kill effect probability and damage tier.
        public byte weaponLevel;

        // ─────────────────────────────────────────────────────────────────────
        //  Damage (C# only — Rust never reads these)
        // ─────────────────────────────────────────────────────────────────────

        /// Multiplier from power-ups or abilities. Applied in RustSimAdapter.ComputeDamage.
        public float damageMultiplier;

        /// Pre-rolled crit flag. Set once at spawn by ServerProjectileAuthority.
        /// RustSimAdapter applies CritMultiplier when true.
        public bool isCrit;

        /// Cached crit chance from config (avoids config lookup per spawn).
        public float critChance;

        // ─────────────────────────────────────────────────────────────────────
        //  Collision tracking (C# only — Rust tracks alive/collision_count
        //  separately in NativeProjectile.collision_count)
        // ─────────────────────────────────────────────────────────────────────

        /// Remaining pierce-through collisions. Decremented by RustSimAdapter.HandlePiercing.
        /// When 0, projectile is killed after the next hit.
        public byte collisionsRemaining;

        /// Set true by RustSimAdapter when the projectile should die.
        /// ServerProjectileAuthority reads this flag to set NativeProjectile.alive = 0.
        public bool hasHit;

        // ─────────────────────────────────────────────────────────────────────
        //  Spawn position (stored for distance-based damage curve, not updated)
        //  Actual current position lives in NativeProjectile.x/y(/z) — Rust owns it.
        // ─────────────────────────────────────────────────────────────────────

        /// World-space spawn origin. Used to compute max range falloff if needed.
        public Vector2 spawnPosition2D;
        public Vector3 spawnPosition3D;

        // ─────────────────────────────────────────────────────────────────────
        //  Game-specific attribution (passed through to ProjectileHitPayload)
        //  These are game-layer enums — the package treats them as opaque ints.
        //  MID_ProjectileNetworkBridge reads them from the payload for RPCs.
        // ─────────────────────────────────────────────────────────────────────

        public MID_PlayerKillTypeAndPoints    killType;
        public MID_PlayerAndBotDamageType     damageType;
        public MID_WhatWeaponDamagedPlayer    weaponType;

        // ─────────────────────────────────────────────────────────────────────
        //  Construction
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Create ServerProjectileData from a spawn request.
        /// Call this once per weapon fire event to create a template,
        /// then call CloneForSpawn() for each individual projectile in the batch.
        /// </summary>
        public ServerProjectileData(
            MID_AllProjectileNames  name,
            ulong                   ownerMidId,
            ulong                   firedById,
            bool                    isBot,
            byte                    level,
            Vector2                 spawnPos2D,
            float                   damageMultiplierIn,
            ProjectileConfigSO      config,
            MID_PlayerKillTypeAndPoints  killTypeIn  = default,
            MID_PlayerAndBotDamageType   damageTypeIn = default,
            MID_WhatWeaponDamagedPlayer  weaponTypeIn = default)
        {
            projectileName         = name;
            ownerClientId          = ownerMidId;
            firedByNetworkObjectId = firedById;
            isBotOwner             = isBot;
            weaponLevel            = level;
            spawnPosition2D        = spawnPos2D;
            damageMultiplier       = damageMultiplierIn;
            critChance             = config != null ? config.CritChance : 0f;
            killType               = killTypeIn;
            damageType             = damageTypeIn;
            weaponType             = weaponTypeIn;

            // Piercing — initialised from config
            if (config != null)
            {
                switch (config.PiercingType)
                {
                    case ProjectilePiercingType.None:
                        collisionsRemaining = 1;
                        break;
                    case ProjectilePiercingType.Piecer:
                        collisionsRemaining = config.MaxCollisions;
                        break;
                    case ProjectilePiercingType.Random:
                        collisionsRemaining = (byte)UnityEngine.Random.Range(
                            1, config.MaxCollisions + 1);
                        break;
                }
            }
            else
            {
                collisionsRemaining = 1;
            }

            // isCrit and projectileId_u32 are set per-projectile in CloneForSpawn
            isCrit          = false;
            projectileId_u32 = 0;
            configId         = 0;
            hasHit           = false;
            is3D             = false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Clone for batch spawn
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Create a per-projectile clone from this template.
        /// Called by ServerProjectileAuthority.NotifyBatchSpawned2D/3D for each
        /// projectile in the batch.
        /// Crit is NOT rolled here — ServerProjectileAuthority rolls it after
        /// CloneForSpawn so it can apply per-projectile randomness.
        /// </summary>
        public ServerProjectileData CloneForSpawn(uint projId, ushort cfgId)
        {
            var clone = (ServerProjectileData)MemberwiseClone();
            clone.projectileId_u32   = projId;
            clone.configId           = cfgId;
            clone.hasHit             = false;
            clone.isCrit             = false; // rolled by caller after this returns
            return clone;
        }

        /// <summary>
        /// Convenience overload — also sets is3D and 3D spawn position.
        /// </summary>
        public ServerProjectileData CloneForSpawn3D(
            uint projId, ushort cfgId, Vector3 spawnPos3D)
        {
            var clone = CloneForSpawn(projId, cfgId);
            clone.is3D           = true;
            clone.spawnPosition3D = spawnPos3D;
            return clone;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Expiry check (C# side only — Rust manages lifetime and collision_count
        //  in NativeProjectile. This is a secondary C# check for cases where
        //  game logic kills a projectile before Rust does, e.g. entering a zone.)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// True if C# game logic has determined this projectile should die.
        /// Does NOT check Rust state — call after checking NativeProjectile.Alive.
        /// </summary>
        public bool IsDead()
        {
            if (hasHit && collisionsRemaining <= 0) return true;
            return false;
        }
    }
}
