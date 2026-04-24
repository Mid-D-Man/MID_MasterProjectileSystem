// ProjectilePatternSO.cs
// Defines a shot pattern as a Catmull-Rom or Bezier spline.
// The spline is sampled at N points to produce spawn directions.
// The pattern editor (ProjectilePatternEditor.cs) visualises and edits this SO.
//
// Usage:
//   Assign to WeaponConfig.PatternOverride.
//   BatchSpawnHelper.BuildSpawnPoints() samples the pattern into SpawnPoint[].
//
// Coordinate convention:
//   All points are in LOCAL weapon space — (0,0) = barrel forward.
//   X = horizontal spread, Y = vertical spread.
//   The weapon transforms these to world space at spawn time.

using System;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    public enum PatternSplineType
    {
        /// Points are the actual path points — spline passes through all of them.
        CatmullRom,

        /// Points alternate: anchor, handle, anchor, handle...
        /// More precise but less intuitive.
        Bezier
    }

    [CreateAssetMenu(
        fileName = "ProjectilePattern",
        menuName  = "MidMan/Projectile System/Projectile Pattern",
        order     = 11)]
    public class ProjectilePatternSO : ScriptableObject
    {
        [Header("Spline Definition")]

        [Tooltip("Catmull-Rom: all points are on the curve (intuitive).\n" +
                 "Bezier: alternating anchor + control handle points (precise).")]
        [SerializeField] private PatternSplineType _splineType = PatternSplineType.CatmullRom;
        public PatternSplineType SplineType => _splineType;

        [Tooltip("Control points defining the pattern shape in local weapon space.\n" +
                 "X = horizontal spread angle, Y = vertical spread angle (degrees).\n" +
                 "Point at (0,0) = straight ahead. Range ±90 degrees each axis.")]
        [SerializeField] private Vector2[] _controlPoints = new Vector2[]
        {
            new Vector2(-15f, 0f),
            new Vector2(  0f, 0f),
            new Vector2( 15f, 0f)
        };
        public Vector2[] ControlPoints => _controlPoints;

        [Tooltip("Number of projectiles sampled from the spline.\n" +
                 "These become evenly-spaced spawn directions along the curve.")]
        [SerializeField, Range(1, 64)] private int _projectileCount = 3;
        public int ProjectileCount => _projectileCount;

        [Tooltip("Speed variation per projectile — adds ±SpeedVariance * baseSpeed randomness.\n" +
                 "0 = all projectiles same speed. 0.1 = ±10% random variation.")]
        [SerializeField, Range(0f, 0.5f)] private float _speedVariance = 0f;
        public float SpeedVariance => _speedVariance;

        [Tooltip("Random seed for speed variance. Use the same seed for deterministic patterns\n" +
                 "across server and client (pass WeaponFired.RngSeed here).")]
        [SerializeField] private uint _rngSeed = 12345;
        public uint RngSeed => _rngSeed;

        // ─────────────────────────────────────────────────────────────────────
        //  Spline evaluation
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Sample N evenly-spaced directions along the spline.
        /// Returns (angle_deg_horizontal, angle_deg_vertical) pairs in local weapon space.
        /// BatchSpawnHelper converts these to world-space Direction vectors.
        /// </summary>
        public Vector2[] SampleDirections(int count = -1)
        {
            int n = count > 0 ? count : _projectileCount;
            if (_controlPoints == null || _controlPoints.Length == 0)
                return new Vector2[n];

            var result = new Vector2[n];

            if (n == 1)
            {
                result[0] = EvaluateSpline(0.5f);
                return result;
            }

            for (int i = 0; i < n; i++)
            {
                float t = (float)i / (n - 1);
                result[i] = EvaluateSpline(t);
            }

            return result;
        }

        /// <summary>
        /// Evaluate the spline at parameter t (0 = start, 1 = end).
        /// Returns (horizontal_deg, vertical_deg) in local weapon space.
        /// </summary>
        public Vector2 EvaluateSpline(float t)
        {
            t = Mathf.Clamp01(t);

            if (_controlPoints == null || _controlPoints.Length == 0)
                return Vector2.zero;

            if (_controlPoints.Length == 1)
                return _controlPoints[0];

            return _splineType == PatternSplineType.CatmullRom
                ? EvaluateCatmullRom(t)
                : EvaluateBezier(t);
        }

        private Vector2 EvaluateCatmullRom(float t)
        {
            int n = _controlPoints.Length;
            if (n == 2) return Vector2.Lerp(_controlPoints[0], _controlPoints[1], t);

            float scaled = t * (n - 1);
            int   seg    = Mathf.Clamp((int)scaled, 0, n - 2);
            float segT   = scaled - seg;

            // p0, p1, p2, p3 — clamped at boundaries
            Vector2 p0 = _controlPoints[Mathf.Max(seg - 1, 0)];
            Vector2 p1 = _controlPoints[seg];
            Vector2 p2 = _controlPoints[Mathf.Min(seg + 1, n - 1)];
            Vector2 p3 = _controlPoints[Mathf.Min(seg + 2, n - 1)];

            float t2 = segT * segT;
            float t3 = t2  * segT;

            // Catmull-Rom basis
            return 0.5f * (
                2f  * p1 +
               (-p0 + p2)          * segT +
               (2f*p0 - 5f*p1 + 4f*p2 - p3) * t2 +
               (-p0 + 3f*p1 - 3f*p2 + p3)   * t3
            );
        }

        private Vector2 EvaluateBezier(float t)
        {
            // De Casteljau for arbitrary point count
            var pts = (Vector2[])_controlPoints.Clone();
            int n   = pts.Length;
            for (int r = 1; r < n; r++)
                for (int i = 0; i < n - r; i++)
                    pts[i] = Vector2.Lerp(pts[i], pts[i + 1], t);
            return pts[0];
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Speed variance (deterministic — use RngSeed from weapon fire event)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Get the speed multiplier for projectile i using a deterministic LCG.
        /// Pass the weapon's RngSeed so server and client compute identical values.
        /// Returns 1.0 when SpeedVariance == 0.
        /// </summary>
        public float GetSpeedMultiplier(int projectileIndex, uint seed)
        {
            if (_speedVariance <= 0f) return 1f;

            uint s = seed.wrapping_add_cs((uint)projectileIndex * 1664525u + 1013904223u);
            s = s.wrapping_mul_cs(1664525u).wrapping_add_cs(1013904223u);
            float rand01 = (s >> 8) / 16777216f;    // 0-1
            return 1f + (rand01 * 2f - 1f) * _speedVariance;  // 1 ± variance
        }
    }

    // Helper extension to avoid importing System.Runtime for wrapping math
    internal static class UIntWrapExtensions
    {
        internal static uint wrapping_add_cs(this uint a, uint b) => unchecked(a + b);
        internal static uint wrapping_mul_cs(this uint a, uint b) => unchecked(a * b);
    }
}
