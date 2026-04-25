// ProjectileRenderer3D.cs
// 3D equivalent of ProjectileRenderer2D.
// Combined mesh path ONLY — no GPU instancing.
// Reads NativeProjectile3D array from LocalProjectileManager or
// ServerProjectileAuthority's client-side render buffer.
// Called from LateUpdate — same pattern as ProjectileRenderer2D.
//
// Rotation:
//   NativeProjectile3D has no angle_deg field.
//   Rotation is derived from velocity (Vx, Vy, Vz) each frame.
//   ProjectileRenderer3D computes LookRotation per projectile.
//
// Scale:
//   Uses ScaleX (uniform — ScaleX == ScaleY == ScaleZ for all Rust sim projectiles).

using UnityEngine;

namespace MidManStudio.Projectiles
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class ProjectileRenderer3D : MonoBehaviour
    {
        #region Configuration

        [Header("Rendering")]
        [SerializeField] private Material _atlasMaterial;

        [Tooltip("Maximum number of 3D projectiles rendered per frame.\n" +
                 "Each projectile = 4 vertices + 6 indices in the combined mesh.")]
        [SerializeField] private int _maxQuads = 512;

        [Header("Fade")]
        [Tooltip("Projectile alpha fades in over this fraction of its lifetime (0 = no fade-in).")]
        [SerializeField, Range(0f, 0.3f)] private float _fadeInFraction = 0.1f;

        [Tooltip("Projectile alpha fades out over the last fraction of its lifetime.")]
        [SerializeField, Range(0f, 0.3f)] private float _fadeOutFraction = 0.15f;

        #endregion

        #region Mesh State

        private Mesh      _mesh;
        private Vector3[] _verts;
        private Vector2[] _uvs;
        private Color32[] _cols;
        private int[]     _tris;

        private MeshFilter   _filter;
        private MeshRenderer _rend;

        #endregion

        #region Initialisation

        private void Awake()
        {
            _filter = GetComponent<MeshFilter>();
            _rend   = GetComponent<MeshRenderer>();

            _mesh = new Mesh { name = "ProjectileCombined3D" };
            _mesh.MarkDynamic();
            _filter.mesh = _mesh;

            if (_atlasMaterial != null)
                _rend.sharedMaterial = _atlasMaterial;

            int cap = _maxQuads;
            _verts = new Vector3[cap * 4];
            _uvs   = new Vector2[cap * 4];
            _cols  = new Color32[cap * 4];
            _tris  = new int[cap * 6];
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Build and submit the combined mesh for all alive 3D projectiles.
        /// Call from LateUpdate — must be called every display frame to avoid flicker.
        /// </summary>
        public void Render(NativeProjectile3D[] projs, int count)
        {
            if (_atlasMaterial == null || projs == null || count == 0)
            {
                _mesh.Clear();
                return;
            }

            var reg = ProjectileRegistry.Instance;
            int qi  = 0;

            for (int i = 0; i < count && qi < _maxQuads; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = reg.Get(p.ConfigId);
                if (cfg == null || !cfg.UseSprite) continue;

                // Derive rotation from velocity
                Vector3 vel = new Vector3(p.Vx, p.Vy, p.Vz);
                Quaternion rot = vel.sqrMagnitude > 0.0001f
                    ? Quaternion.LookRotation(vel.normalized, Vector3.up)
                    : Quaternion.identity;

                float scale = p.ScaleX;
                float hx    = scale * 0.5f;
                float hy    = cfg.FullSizeY * 0.5f;

                // Build a camera-facing quad at the projectile position
                // Right and up vectors from rotation
                Vector3 right = rot * Vector3.right   * hx;
                Vector3 up    = rot * Vector3.up      * hy;
                Vector3 pos   = new Vector3(p.X, p.Y, p.Z);

                int vBase = qi * 4;
                _verts[vBase + 0] = pos - right - up;
                _verts[vBase + 1] = pos + right - up;
                _verts[vBase + 2] = pos + right + up;
                _verts[vBase + 3] = pos - right + up;

                // Atlas UVs
                Vector4 uv = reg.GetUVRect(p.ConfigId);
                _uvs[vBase + 0] = new Vector2(uv.x,        uv.y       );
                _uvs[vBase + 1] = new Vector2(uv.x + uv.z, uv.y       );
                _uvs[vBase + 2] = new Vector2(uv.x + uv.z, uv.y + uv.w);
                _uvs[vBase + 3] = new Vector2(uv.x,        uv.y + uv.w);

                // Lifetime-based alpha fade
                Color32 col = ComputeTint(p.Lifetime, p.MaxLifetime,
                    _fadeInFraction, _fadeOutFraction);
                _cols[vBase + 0] = col;
                _cols[vBase + 1] = col;
                _cols[vBase + 2] = col;
                _cols[vBase + 3] = col;

                // Indices
                int tBase = qi * 6;
                _tris[tBase + 0] = vBase;
                _tris[tBase + 1] = vBase + 1;
                _tris[tBase + 2] = vBase + 2;
                _tris[tBase + 3] = vBase;
                _tris[tBase + 4] = vBase + 2;
                _tris[tBase + 5] = vBase + 3;

                qi++;
            }

            _mesh.Clear();
            if (qi == 0) return;

            _mesh.SetVertices(_verts, 0, qi * 4);
            _mesh.SetUVs(0,    _uvs,  0, qi * 4);
            _mesh.SetColors(_cols,    0, qi * 4);
            _mesh.SetTriangles(_tris, 0, qi * 6, 0);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 10000f);
        }

        #endregion

        #region Helpers

        private static Color32 ComputeTint(
            float lifetime, float maxLifetime,
            float fadeInFrac, float fadeOutFrac)
        {
            if (maxLifetime <= 0f) return new Color32(255, 255, 255, 255);

            float progress  = 1f - lifetime / maxLifetime; // 0=just spawned, 1=dying
            float fadeInEnd = fadeInFrac;
            float fadeOutStart = 1f - fadeOutFrac;

            float alpha = 1f;
            if (progress < fadeInEnd && fadeInEnd > 0f)
                alpha = progress / fadeInEnd;
            else if (progress > fadeOutStart && fadeOutFrac > 0f)
                alpha = 1f - (progress - fadeOutStart) / fadeOutFrac;

            byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha) * 255f);
            return new Color32(255, 255, 255, a);
        }

        #endregion
    }
}
