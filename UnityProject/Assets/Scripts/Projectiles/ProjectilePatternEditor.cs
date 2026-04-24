// ProjectilePatternEditor.cs
// Custom editor for ProjectilePatternSO.
// Features:
//   - Interactive spline viewport showing the pattern shape
//   - Drag control points directly in the preview
//   - Simulate button: shows preview of where projectiles would go
//   - Point count slider with live preview update
//   - Export to CSV for external tooling

#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace MidManStudio.Projectiles.Editor
{
    [CustomEditor(typeof(ProjectilePatternSO))]
    public class ProjectilePatternEditor : UnityEditor.Editor
    {
        // ── Preview state ─────────────────────────────────────────────────────
        private bool      _showPreview        = true;
        private bool      _showSimulation     = false;
        private int       _simulationCount    = -1; // -1 = use config's own count
        private Rect      _previewRect;
        private int       _draggingPoint      = -1;
        private Vector2   _dragOffset;

        // ── Preview constants ─────────────────────────────────────────────────
        private const float PreviewHeight     = 300f;
        private const float PreviewPadding    = 30f;
        private const float AngleRange        = 90f;   // ±90° displayed
        private const float PointRadius       = 8f;
        private const int   SplineResolution  = 64;    // segments drawn for the curve

        private static readonly Color SplineColor     = new Color(0.3f, 0.8f, 1.0f);
        private static readonly Color PointColor      = new Color(1.0f, 0.8f, 0.2f);
        private static readonly Color PointHover      = new Color(1.0f, 1.0f, 0.5f);
        private static readonly Color SimRayColor     = new Color(0.2f, 1.0f, 0.4f, 0.7f);
        private static readonly Color GridColor       = new Color(0.3f, 0.3f, 0.3f, 0.5f);
        private static readonly Color OriginColor     = new Color(0.7f, 0.7f, 0.7f, 0.8f);

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var pattern = (ProjectilePatternSO)target;

            // ── Standard fields ───────────────────────────────────────────────
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_splineType"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_projectileCount"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_speedVariance"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_rngSeed"));

            EditorGUILayout.Space(8);

            // ── Control points ────────────────────────────────────────────────
            EditorGUILayout.LabelField("Control Points", EditorStyles.boldLabel);

            var pointsProp = serializedObject.FindProperty("_controlPoints");
            EditorGUI.indentLevel++;
            for (int i = 0; i < pointsProp.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();
                var elem = pointsProp.GetArrayElementAtIndex(i);
                EditorGUILayout.PropertyField(elem,
                    new GUIContent($"Point {i}  (H°, V°)"), true);

                if (GUILayout.Button("✕", GUILayout.Width(22), GUILayout.Height(18))
                    && pointsProp.arraySize > 1)
                {
                    pointsProp.DeleteArrayElementAtIndex(i);
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Point"))
            {
                pointsProp.InsertArrayElementAtIndex(pointsProp.arraySize);
                pointsProp.GetArrayElementAtIndex(pointsProp.arraySize - 1)
                          .vector2Value = Vector2.zero;
            }
            if (GUILayout.Button("Clear"))
            {
                pointsProp.ClearArray();
            }
            EditorGUILayout.EndHorizontal();

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(12);

            // ── Preview ───────────────────────────────────────────────────────
            _showPreview = EditorGUILayout.Foldout(_showPreview, "Pattern Preview", true,
                EditorStyles.foldoutHeader);

            if (_showPreview)
            {
                DrawPreviewHeader(pattern);
                DrawPreviewViewport(pattern);
            }

            // ── Simulation ────────────────────────────────────────────────────
            EditorGUILayout.Space(8);
            _showSimulation = EditorGUILayout.Foldout(_showSimulation, "Simulate (Gizmo in Scene)", true,
                EditorStyles.foldoutHeader);

            if (_showSimulation)
            {
                EditorGUILayout.HelpBox(
                    "Select a GameObject in the scene to preview pattern from that position.\n" +
                    "Projectile directions will be shown as green rays in the Scene view.",
                    MessageType.Info);

                _simulationCount = EditorGUILayout.IntField("Override Count (-1 = use config)",
                    _simulationCount);

                if (GUILayout.Button("Refresh Scene Gizmo"))
                    SceneView.RepaintAll();
            }
        }

        // ── Preview header controls ───────────────────────────────────────────

        private void DrawPreviewHeader(ProjectilePatternSO pattern)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                $"Spline: {pattern.SplineType}   |   Points: {pattern.ControlPoints?.Length ?? 0}" +
                $"   |   Projectiles: {pattern.ProjectileCount}",
                EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        // ── Main preview viewport ─────────────────────────────────────────────

        private void DrawPreviewViewport(ProjectilePatternSO pattern)
        {
            // Reserve rect for the viewport
            _previewRect = GUILayoutUtility.GetRect(
                GUIContent.none, GUIStyle.none,
                GUILayout.ExpandWidth(true), GUILayout.Height(PreviewHeight));

            if (Event.current.type == EventType.Repaint)
            {
                DrawPreviewBackground();
                DrawGrid();
                DrawOriginMarker();

                if (pattern.ControlPoints != null && pattern.ControlPoints.Length >= 2)
                    DrawSpline(pattern);

                DrawSamplePoints(pattern);
                DrawControlPoints(pattern);
            }

            HandleMouseInput(pattern);
        }

        // ── Drawing helpers ───────────────────────────────────────────────────

        private void DrawPreviewBackground()
        {
            EditorGUI.DrawRect(_previewRect, new Color(0.12f, 0.12f, 0.12f));
        }

        private void DrawGrid()
        {
            Handles.color = GridColor;

            // Centre lines
            Vector2 cx  = AngleToPreview(new Vector2(0f, -AngleRange));
            Vector2 cx2 = AngleToPreview(new Vector2(0f,  AngleRange));
            Handles.DrawLine(cx, cx2);

            Vector2 cy  = AngleToPreview(new Vector2(-AngleRange, 0f));
            Vector2 cy2 = AngleToPreview(new Vector2( AngleRange, 0f));
            Handles.DrawLine(cy, cy2);

            // Grid lines at ±30° and ±60°
            Handles.color = new Color(0.2f, 0.2f, 0.2f, 0.4f);
            foreach (float a in new[] { -60f, -30f, 30f, 60f })
            {
                Vector2 h1 = AngleToPreview(new Vector2(a, -AngleRange));
                Vector2 h2 = AngleToPreview(new Vector2(a,  AngleRange));
                Handles.DrawLine(h1, h2);

                Vector2 v1 = AngleToPreview(new Vector2(-AngleRange, a));
                Vector2 v2 = AngleToPreview(new Vector2( AngleRange, a));
                Handles.DrawLine(v1, v2);
            }
        }

        private void DrawOriginMarker()
        {
            Handles.color = OriginColor;
            Vector2 origin = AngleToPreview(Vector2.zero);
            Handles.DrawSolidDisc(origin, Vector3.forward, 3f);

            // Draw forward direction arrow
            Vector2 fwd = AngleToPreview(new Vector2(0f, 5f));
            Handles.DrawLine(origin, fwd);
        }

        private void DrawSpline(ProjectilePatternSO pattern)
        {
            Handles.color = SplineColor;
            Vector2 prev  = AngleToPreview(pattern.EvaluateSpline(0f));

            for (int i = 1; i <= SplineResolution; i++)
            {
                float   t    = (float)i / SplineResolution;
                Vector2 curr = AngleToPreview(pattern.EvaluateSpline(t));
                Handles.DrawLine(prev, curr);
                prev = curr;
            }
        }

        private void DrawSamplePoints(ProjectilePatternSO pattern)
        {
            var samples = pattern.SampleDirections();
            Handles.color = SimRayColor;

            Vector2 origin = AngleToPreview(Vector2.zero);

            foreach (var dir in samples)
            {
                // Draw a ray from origin in the sample direction
                Vector2 end = AngleToPreview(dir * 0.8f);
                Handles.DrawLine(origin, end);
                Handles.DrawSolidDisc(end, Vector3.forward, 3f);
            }
        }

        private void DrawControlPoints(ProjectilePatternSO pattern)
        {
            if (pattern.ControlPoints == null) return;

            Vector2 mousePos = Event.current.mousePosition;

            for (int i = 0; i < pattern.ControlPoints.Length; i++)
            {
                Vector2 screenPos = AngleToPreview(pattern.ControlPoints[i]);
                bool    hovered   = Vector2.Distance(mousePos, screenPos) < PointRadius + 4f;

                Handles.color = hovered ? PointHover : PointColor;
                Handles.DrawSolidDisc(screenPos, Vector3.forward, PointRadius);

                // Index label
                GUI.Label(new Rect(screenPos.x + PointRadius, screenPos.y - 8f, 30f, 16f),
                    i.ToString(), EditorStyles.miniLabel);
            }
        }

        // ── Mouse input — drag control points ────────────────────────────────

        private void HandleMouseInput(ProjectilePatternSO pattern)
        {
            Event e = Event.current;
            if (!_previewRect.Contains(e.mousePosition)) return;

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                // Find nearest point
                for (int i = 0; i < pattern.ControlPoints.Length; i++)
                {
                    Vector2 screenPos = AngleToPreview(pattern.ControlPoints[i]);
                    if (Vector2.Distance(e.mousePosition, screenPos) <= PointRadius + 4f)
                    {
                        _draggingPoint = i;
                        _dragOffset    = screenPos - e.mousePosition;
                        e.Use();
                        break;
                    }
                }
            }

            if (e.type == EventType.MouseDrag && _draggingPoint >= 0)
            {
                Vector2 newAngle = PreviewToAngle(e.mousePosition + _dragOffset);
                newAngle.x = Mathf.Clamp(newAngle.x, -AngleRange, AngleRange);
                newAngle.y = Mathf.Clamp(newAngle.y, -AngleRange, AngleRange);

                Undo.RecordObject(target, "Move Pattern Point");
                serializedObject.Update();
                serializedObject.FindProperty("_controlPoints")
                                .GetArrayElementAtIndex(_draggingPoint)
                                .vector2Value = newAngle;
                serializedObject.ApplyModifiedProperties();

                e.Use();
                Repaint();
            }

            if (e.type == EventType.MouseUp)
            {
                _draggingPoint = -1;
                e.Use();
            }
        }

        // ── Coordinate conversion ─────────────────────────────────────────────

        /// Convert angle space (±AngleRange, ±AngleRange) to preview rect pixel space.
        private Vector2 AngleToPreview(Vector2 angleDeg)
        {
            float u = (angleDeg.x + AngleRange) / (2f * AngleRange);
            float v = 1f - (angleDeg.y + AngleRange) / (2f * AngleRange);

            return new Vector2(
                _previewRect.x + PreviewPadding + u * (_previewRect.width  - 2f * PreviewPadding),
                _previewRect.y + PreviewPadding + v * (_previewRect.height - 2f * PreviewPadding));
        }

        /// Convert preview rect pixel space back to angle space.
        private Vector2 PreviewToAngle(Vector2 screenPos)
        {
            float u = (screenPos.x - _previewRect.x - PreviewPadding)
                      / (_previewRect.width - 2f * PreviewPadding);
            float v = (screenPos.y - _previewRect.y - PreviewPadding)
                      / (_previewRect.height - 2f * PreviewPadding);

            return new Vector2(
                u * (2f * AngleRange) - AngleRange,
               (1f - v) * (2f * AngleRange) - AngleRange);
        }

        // ── Scene gizmo ───────────────────────────────────────────────────────

        private void OnSceneGUI()
        {
            if (!_showSimulation) return;
            var pattern = (ProjectilePatternSO)target;

            Transform t = Selection.activeTransform;
            if (t == null) return;

            int    count   = _simulationCount > 0 ? _simulationCount : pattern.ProjectileCount;
            var    samples = pattern.SampleDirections(count);
            float  rayLen  = 5f;

            Handles.color = SimRayColor;

            foreach (var angleDeg in samples)
            {
                // Rotate weapon forward by horizontal and vertical angles
                Vector3 dir = Quaternion.Euler(-angleDeg.y, angleDeg.x, 0f) * t.forward;
                Handles.DrawLine(t.position, t.position + dir * rayLen);
                Handles.DrawSolidDisc(t.position + dir * rayLen, dir, 0.05f);
            }
        }
    }
}
#endif
