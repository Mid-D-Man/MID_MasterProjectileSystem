// ProjectileConfigSO.cs — CORE PACKAGE VERSION
//
// This is the lean, game-agnostic projectile configuration for the MID Projectile System.
// It contains ONLY what the simulation system, renderer, trail system, and network layer need.
//
// EXTENSION PATTERN — game-specific logic:
//   Do not modify this file for game-specific fields.
//   Instead, create a derived class in your game assembly:
//
//   [CreateAssetMenu(...)]
//   public class MyGameProjectileConfig : ProjectileConfigSO
//   {
//       [Header("Game-Specific")]
//       public LegendaryTier legendaryTier;
//       public AudioClip flightSound;
//       public StatusEffectType statusEffect;
//       // etc.
//   }
//
//   The package systems (BatchSpawnHelper, ProjectileTypeRouter, TrailObjectPool,
//   ProjectileRenderer2D/3D) only ever reference ProjectileConfigSO — your derived
//   class slots in transparently because C# polymorphism handles the rest.
//
// DAMAGE PROFILES:
//   Damage is defined as an AnimationCurve over normalised travel distance (0-1).
//   If the curve is flat (all keys same value), the system detects this and skips
//   evaluation — effectively a constant. No min/max random range needed.
//   Use the ProjectileDamageProfileEditor window to visualise and edit curves.
//
// TRAIL:
//   All trail data lives in the TrailConfig struct below.
//   ProjectileTrailOptimizer reads these fields and applies them only when changed.

using UnityEngine;
using System;

namespace MidManStudio.Projectiles
{
    [CreateAssetMenu(
        fileName = "ProjectileConfig",
        menuName  = "MidMan/Projectile System/Projectile Config",
        order     = 10)]
    public class ProjectileConfigSO : ScriptableObject
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Identity (assigned by ProjectileRegistry at registration)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Assigned by ProjectileRegistry when this config is registered.
        /// Used as the key in C# → Rust config ID lookups.
        /// Do not set this manually — it is set at runtime by the registry.
        /// </summary>
        [HideInInspector] public ushort ConfigId;

        // ─────────────────────────────────────────────────────────────────────
        //  Simulation routing
        // ─────────────────────────────────────────────────────────────────────

        [Header("Simulation")]

        [Tooltip("Is this a 3D projectile?\n" +
                 "FALSE: 2D Rust sim — NativeProjectile, tick_projectiles, check_hits_grid.\n" +
                 "TRUE:  3D Rust sim — NativeProjectile3D, tick_projectiles_3d, check_hits_grid_3d.")]
        [SerializeField] private bool _is3D = false;
        public bool Is3D => _is3D;

        [Tooltip("Override the automatic SimulationMode decision for this config.\n\n" +
                 "Leave as RustSim2D to let ProjectileTypeRouter decide from weapon context.\n" +
                 "Override to force a specific mode — e.g. PhysicsObject for a grenade config\n" +
                 "regardless of the weapon's fire rate.")]
        [SerializeField] private SimulationMode _preferredSimMode = SimulationMode.RustSim2D;
        public SimulationMode PreferredSimMode => _preferredSimMode;

        /// <summary>True when PreferredSimMode is an explicit override (not the default).</summary>
        public bool HasSimModeOverride => _preferredSimMode != SimulationMode.RustSim2D;

        // ─────────────────────────────────────────────────────────────────────
        //  Movement (Rust-facing)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Movement")]

        [Tooltip("How this projectile moves. Straight is most performant.\n\n" +
                 "Wave and Circular require RegisterMovementParams() to be called at startup\n" +
                 "via ProjectileLib — see ProjectileRegistry for the registration flow.")]
        [SerializeField] private ProjectileMovementType _movementType = ProjectileMovementType.Straight;
        public ProjectileMovementType MovementType => _movementType;

        [Tooltip("Projectile speed in world units per second.\n\n" +
                 "If MinSpeed == MaxSpeed, a random range is skipped and the value is used directly.\n" +
                 "Set both to the same value for a deterministic speed.")]
        [SerializeField] private float _minSpeed = 10f;
        [SerializeField] private float _maxSpeed = 10f;
        public float MinSpeed => _minSpeed;
        public float MaxSpeed => _maxSpeed;

        /// <summary>
        /// Resolves speed — skips Random.Range when min == max.
        /// Call this at spawn time, store the result in NativeProjectile.Vx/Vy.
        /// </summary>
        public float ResolveSpeed()
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            return _minSpeed == _maxSpeed
                ? _minSpeed
                : UnityEngine.Random.Range(_minSpeed, _maxSpeed);
        }

        [Tooltip("Maximum lifetime in seconds before the projectile is automatically killed.")]
        [SerializeField] private float _lifetime = 3f;
        public float Lifetime => _lifetime;

        [Tooltip("Gravity / downward acceleration applied each tick (maps to Rust NativeProjectile.ay).\n" +
                 "0 = no gravity. Positive = falls down. Used by Arching movement type.")]
        [SerializeField] private float _gravityScale = 0f;
        public float GravityScale => _gravityScale;

        [Tooltip("Maximum travel distance in world units. Projectile is killed when exceeded.\n" +
                 "Also used to normalise distance for damage profile evaluation (0 = spawn, 1 = max range).")]
        [SerializeField] private float _maxRange = 50f;
        public float MaxRange => _maxRange;

        // ─────────────────────────────────────────────────────────────────────
        //  Piercing (Rust-facing)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Piercing")]

        [SerializeField] private ProjectilePiercingType _piercingType = ProjectilePiercingType.None;
        public ProjectilePiercingType PiercingType => _piercingType;

        [Tooltip("Maximum number of targets this projectile can hit before dying.\n" +
                 "Only relevant when PiercingType is Piecer or Random.")]
        [SerializeField, Range(1, 16)] private byte _maxCollisions = 1;
        public byte MaxCollisions => _maxCollisions;

        // ─────────────────────────────────────────────────────────────────────
        //  Scale growth (Rust-facing, opt-in)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Scale Growth (Optional)")]

        [Tooltip("Enable scale growth — projectile starts small and grows to full size.\n" +
                 "FALSE (default): ScaleSpeed = 0 — Rust skips tick_scale entirely. Zero CPU cost.\n" +
                 "TRUE: ScaleX starts at SpawnScale, grows to FullSize at GrowthSpeed.")]
        [SerializeField] private bool _useScaleGrowth = false;
        public bool UseScaleGrowth => _useScaleGrowth;

        [SerializeField] private float _fullSizeX = 0.2f;
        [SerializeField] private float _fullSizeY = 0.2f;
        [SerializeField, Range(0.01f, 1f)] private float _spawnScaleFraction = 0.2f;
        [SerializeField, Range(1f, 30f)]   private float _growthSpeed = 8f;

        public float FullSizeX          => _fullSizeX;
        public float FullSizeY          => _fullSizeY;
        public float SpawnScaleFraction => _spawnScaleFraction;
        public float GrowthSpeed        => _growthSpeed;

        // ─────────────────────────────────────────────────────────────────────
        //  Damage profile (C#-only — never goes to Rust)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Damage Profile")]

        [Tooltip("Damage as a function of normalised travel distance (x = 0 to 1).\n\n" +
                 "x=0 = point-blank, x=1 = max range (MaxRange field above).\n" +
                 "Flat curve = constant damage regardless of distance.\n\n" +
                 "Use Window > MidMan > Damage Profile Editor to visualise this curve.")]
        [SerializeField] private AnimationCurve _damageCurve =
            AnimationCurve.Constant(0f, 1f, 25f);
        public AnimationCurve DamageCurve => _damageCurve;

        [Tooltip("Headshot damage multiplier applied on top of the damage curve value.\n" +
                 "1.0 = no headshot bonus.")]
        [SerializeField, Range(1f, 5f)] private float _headshotMultiplier = 2f;
        public float HeadshotMultiplier => _headshotMultiplier;

        [Tooltip("Chance (0-1) to deal a critical hit. 0 = never crit, 1 = always crit.")]
        [SerializeField, Range(0f, 1f)] private float _critChance = 0f;
        public float CritChance => _critChance;

        [Tooltip("Damage multiplier when a critical hit is rolled.")]
        [SerializeField, Range(1f, 5f)] private float _critMultiplier = 1.5f;
        public float CritMultiplier => _critMultiplier;

        /// <summary>
        /// Evaluate the damage curve at a normalised travel distance.
        /// Clamps distance to 0-1. Returns raw curve value before crit/headshot.
        /// </summary>
        public float EvaluateDamage(float normalisedDistance)
        {
            return _damageCurve.Evaluate(Mathf.Clamp01(normalisedDistance));
        }

        /// <summary>
        /// True when the damage curve is constant across its full range.
        /// Callers can skip curve evaluation and use DamageCurve.Evaluate(0) directly.
        /// </summary>
        public bool IsDamageConstant()
        {
            if (_damageCurve.length == 0) return true;
            float first = _damageCurve.Evaluate(0f);
            return Mathf.Approximately(_damageCurve.Evaluate(0.5f), first)
                && Mathf.Approximately(_damageCurve.Evaluate(1f),   first);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Visual (C#-only — never goes to Rust)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Visual")]

        [Tooltip("Sprite drawn for this projectile. Null = trail-only (no quad rendered).")]
        [SerializeField] private Sprite _sprite;
        public Sprite ProjectileSprite => _sprite;

        [Tooltip("If false, no mesh quad is rendered. Useful when the trail IS the visual.")]
        [SerializeField] private bool _useSprite = true;
        public bool UseSprite => _useSprite;

        [Tooltip("Optional custom mesh shape. Null = default quad.")]
        [SerializeField] private ProjectileShapeSO _customShape;
        public ProjectileShapeSO CustomShape => _customShape;

        // ─────────────────────────────────────────────────────────────────────
        //  Trail (C#-only — consumed by ProjectileTrailOptimizer)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Trail")]

        [SerializeField] private bool _hasTrail = true;
        public bool HasTrail => _hasTrail;

        [Tooltip("Trail material. Must be assigned if HasTrail is true.")]
        [SerializeField] private Material _trailMaterial;
        public Material TrailMaterial => _trailMaterial;

        [Tooltip("When false, uses the material's own colour. When true, applies the gradient below.")]
        [SerializeField] private bool _useGradientOverride = false;
        public bool UseGradientOverride => _useGradientOverride;

        [Tooltip("Gradient applied to the trail when UseGradientOverride is true.")]
        [SerializeField] private Gradient _trailGradient;
        public Gradient TrailGradient => _trailGradient;

        [Tooltip("Trail length in seconds. Shorter = less geometry = better performance.")]
        [SerializeField, Range(0.02f, 2f)] private float _trailTime = 0.15f;
        public float TrailTime => _trailTime;

        [SerializeField, Range(0f, 1f)] private float _trailStartWidth = 0.08f;
        [SerializeField, Range(0f, 1f)] private float _trailEndWidth   = 0f;
        public float TrailStartWidth => _trailStartWidth;
        public float TrailEndWidth   => _trailEndWidth;

        [Tooltip("Minimum world-space distance between trail vertices.\n" +
                 "Higher values = fewer vertices = better performance.\n" +
                 "Default 0.1 is a good starting point for fast projectiles.")]
        [SerializeField, Range(0.01f, 2f)] private float _trailMinVertexDistance = 0.1f;
        public float TrailMinVertexDistance => _trailMinVertexDistance;

        [Tooltip("Number of cap vertices on the trail end. 0 = flat cap, 2-4 = smooth rounded cap.")]
        [SerializeField, Range(0, 4)] private int _trailCapVertices = 2;
        public int TrailCapVertices => _trailCapVertices;

        [Tooltip("Shared trail material flag — use one material instance across all projectiles\n" +
                 "of this type for better draw call batching.")]
        [SerializeField] private bool _useSharedTrailMaterial = true;
        public bool UseSharedTrailMaterial => _useSharedTrailMaterial;

        // ─────────────────────────────────────────────────────────────────────
        //  Impact (C#-only — consumed by ProjectileImpactHandler)
        // ─────────────────────────────────────────────────────────────────────

        [Header("Impact")]

        [Tooltip("Particle pool type for the impact effect. Resolved at runtime by LocalParticlePool.")]
        [SerializeField] private PoolableParticleType _impactEffectType;
        public PoolableParticleType ImpactEffectType => _impactEffectType;

        // ─────────────────────────────────────────────────────────────────────
        //  Spawn params helper — consumed by BatchSpawnHelper
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Produces the minimal Rust-facing spawn parameters from this config.
        /// Called once per spawn event by BatchSpawnHelper, not per projectile.
        /// speedOverride: pass > 0 to override the config's speed resolution.
        /// </summary>
        public RustSpawnParams GetRustSpawnParams(float speedOverride = -1f)
        {
            float speed = speedOverride > 0f ? speedOverride : ResolveSpeed();

            float scaleStart  = _useScaleGrowth ? _fullSizeX * _spawnScaleFraction : 1f;
            float scaleTarget = _useScaleGrowth ? _fullSizeX : 1f;
            float scaleSpeed  = _useScaleGrowth ? _growthSpeed : 0f;

            return new RustSpawnParams
            {
                Speed         = speed,
                MovementType  = (byte)_movementType,
                PiercingType  = (byte)_piercingType,
                MaxCollisions = _maxCollisions,
                Lifetime      = _lifetime,
                GravityAy     = _gravityScale,
                ScaleStart    = scaleStart,
                ScaleTarget   = scaleTarget,
                ScaleSpeed    = scaleSpeed,
                Is3D          = _is3D
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Wave/Circular movement registration helpers
        // ─────────────────────────────────────────────────────────────────────

        [Header("Wave Movement (only used when MovementType = Wave)")]
        [SerializeField] private float _waveAmplitude   = 1f;
        [SerializeField] private float _waveFrequency   = 2f;
        [SerializeField] private float _wavePhaseOffset = 0f;
        [SerializeField] private bool  _waveVertical    = false;

        [Header("Circular Movement (only used when MovementType = Circular)")]
        [SerializeField] private float _circularRadius       = 0.5f;
        [SerializeField] private float _circularAngularSpeed = 180f;
        [SerializeField] private float _circularStartAngle   = 0f;

        /// <summary>
        /// Register movement params with Rust for Wave or Circular movement types.
        /// Called by ProjectileRegistry after assigning ConfigId.
        /// No-op for other movement types.
        /// </summary>
        public void RegisterMovementParams()
        {
            switch (_movementType)
            {
                case ProjectileMovementType.Wave:
                    ProjectileLib.register_wave_params(
                        ConfigId, _waveAmplitude, _waveFrequency,
                        _wavePhaseOffset, _waveVertical ? (byte)1 : (byte)0);
                    break;

                case ProjectileMovementType.Circular:
                    ProjectileLib.register_circular_params(
                        ConfigId, _circularRadius,
                        _circularAngularSpeed, _circularStartAngle);
                    break;
            }
        }

        /// <summary>Unregister movement params from Rust on config unload.</summary>
        public void UnregisterMovementParams()
        {
            switch (_movementType)
            {
                case ProjectileMovementType.Wave:
                    ProjectileLib.unregister_wave_params(ConfigId);
                    break;
                case ProjectileMovementType.Circular:
                    ProjectileLib.unregister_circular_params(ConfigId);
                    break;
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            _maxSpeed = Mathf.Max(_maxSpeed, _minSpeed);
            if (_fullSizeX <= 0f) _fullSizeX = 0.2f;
            if (_fullSizeY <= 0f) _fullSizeY = 0.2f;
        }
#endif
    }
}
