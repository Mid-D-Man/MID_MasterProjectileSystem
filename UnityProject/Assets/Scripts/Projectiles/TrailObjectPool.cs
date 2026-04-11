// TrailObjectPool.cs
// Pool of featherweight GameObjects (Transform + TrailRenderer only).
// No scripts, no physics, no Update loops on the trail GOs themselves.

using System.Collections.Generic;
using UnityEngine;

namespace MidManStudio.Projectiles
{
    [RequireComponent(typeof(ProjectileManager))]
    public class TrailObjectPool : MonoBehaviour
    {
        [SerializeField] private int _poolSize = 256;

        // Each slot: the TrailRenderer GO + which proj_id owns it
        private TrailRenderer[] _trails;
        private uint[]          _assignedIds;   // proj_id, 0 = free
        private bool[]          _inUse;

        // Fast lookup: proj_id → slot index
        private Dictionary<uint, int> _idToSlot = new Dictionary<uint, int>(256);

        // ─────────────────────────────────────────────────────────────────────

        void Awake()
        {
            _trails      = new TrailRenderer[_poolSize];
            _assignedIds = new uint[_poolSize];
            _inUse       = new bool[_poolSize];

            for (int i = 0; i < _poolSize; i++)
            {
                var go = new GameObject($"Trail_{i}");
                go.transform.SetParent(transform);
                go.hideFlags = HideFlags.HideInHierarchy;

                var tr = go.AddComponent<TrailRenderer>();
                tr.enabled         = false;
                tr.autodestruct    = false;
                tr.emitting        = false;

                _trails[i] = tr;
            }
        }

        // ─── Called by ProjectileManager each FixedUpdate ─────────────────────

        public void SyncToSimulation(NativeProjectile[] projs, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ref var p = ref projs[i];
                if (p.Alive == 0) continue;

                var cfg = ProjectileRegistry.Instance.Get(p.ConfigId);
                if (!cfg.HasTrail) continue;

                if (!_idToSlot.TryGetValue(p.ProjId, out int slot))
                {
                    slot = AcquireSlot(p.ProjId, cfg);
                    if (slot < 0) continue; // pool exhausted
                }

                _trails[slot].transform.position =
                    new Vector3(p.X, p.Y, 0f);
            }
        }

        public void NotifyDead(uint projId)
        {
            if (!_idToSlot.TryGetValue(projId, out int slot)) return;

            // Stop emitting but let the trail fade out naturally
            _trails[slot].emitting = false;

            // Release slot after trail time elapses
            // (simple approach: release immediately; trail fade is visual only)
            _idToSlot.Remove(projId);
            _assignedIds[slot] = 0;
            _inUse[slot]       = false;

            // Disable after a delay equal to trail time
            StartCoroutine(DisableAfterDelay(_trails[slot],
                _trails[slot].time + 0.05f));
        }

        // ─── Internals ────────────────────────────────────────────────────────

        private int AcquireSlot(uint projId, ProjectileConfigSO cfg)
        {
            for (int i = 0; i < _poolSize; i++)
            {
                if (_inUse[i]) continue;

                _inUse[i]       = true;
                _assignedIds[i] = projId;
                _idToSlot[projId] = i;

                ApplyConfig(_trails[i], cfg);
                _trails[i].Clear();
                _trails[i].enabled = true;
                _trails[i].emitting = true;

                return i;
            }
            // Pool full — not catastrophic, just no trail for this bullet
            return -1;
        }

        private static void ApplyConfig(TrailRenderer tr, ProjectileConfigSO cfg)
        {
            tr.material     = cfg.TrailMaterial;
            tr.colorGradient = cfg.TrailColorGradient;
            tr.time         = cfg.TrailTime;
            tr.startWidth   = cfg.TrailStartWidth;
            tr.endWidth     = cfg.TrailEndWidth;
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tr.receiveShadows    = false;
        }

        private System.Collections.IEnumerator DisableAfterDelay(
            TrailRenderer tr, float delay)
        {
            yield return new WaitForSeconds(delay);
            tr.enabled = false;
        }
    }
}
