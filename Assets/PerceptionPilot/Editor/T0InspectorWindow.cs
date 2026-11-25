using UnityEditor;
using UnityEngine;

namespace PerceptionPilot.EditorTools
{
    /// <summary>
    /// Check and correct T0 for every take. The plot shows the body's sideways path (turns) or height
    /// (stairs) against time, with T0 (red) and the cutoffs (grey: -500, -250, 0 ms).
    /// Rule of thumb: T0 belongs where the path visibly leaves the regular straight-walking wave.
    /// If unsure, move it EARLIER: a late T0 would let the "0 ms" cutoff show part of the actual path change.
    /// </summary>
    public class T0InspectorWindow : EditorWindow
    {
        ManeuverLibrary _lib;
        int _index;
        float[] _sig;
        int _sigFor = -1;
        static readonly float[] Cutoffs = { -0.5f, -0.25f, 0f };

        [MenuItem("Window/Perception Pilot/T0 Inspector")]
        [MenuItem("Tools/Perception Pilot/5. T0 Inspector (check each take)")]
        static void Open()
        {
            var w = GetWindow<T0InspectorWindow>("T0 Inspector");
            w.minSize = new Vector2(520, 360);
            if (Selection.activeObject is ManeuverLibrary l) w._lib = l;
        }

        void OnGUI()
        {
            _lib = (ManeuverLibrary)EditorGUILayout.ObjectField("Library", _lib, typeof(ManeuverLibrary), false);
            if (_lib == null || _lib.entries.Count == 0) { EditorGUILayout.HelpBox("Assign a library with entries.", MessageType.Info); return; }

            _index = Mathf.Clamp(_index, 0, _lib.entries.Count - 1);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("< Prev", GUILayout.Width(70))) _index = Mathf.Max(0, _index - 1);
                GUILayout.Label($"{_index + 1} / {_lib.entries.Count}", GUILayout.Width(70));
                if (GUILayout.Button("Next >", GUILayout.Width(70))) _index = Mathf.Min(_lib.entries.Count - 1, _index + 1);
                if (GUILayout.Button("Next unlocked", GUILayout.Width(110)))
                {
                    for (int i = _index + 1; i < _lib.entries.Count; i++)
                        if (_lib.entries[i] != null && !_lib.entries[i].t0Locked) { _index = i; break; }
                }
            }

            var e = _lib.entries[_index];
            if (e == null || e.clip == null) { EditorGUILayout.HelpBox("Empty entry.", MessageType.Warning); return; }

            EditorGUI.BeginChangeCheck();
            var type = (ManeuverType)EditorGUILayout.EnumPopup("Type", e.type);
            string walker = EditorGUILayout.TextField("Walker", e.walkerId);
            int take = EditorGUILayout.IntField("Take", e.take);
            bool include = EditorGUILayout.Toggle("Include", e.include);
            float t0 = EditorGUILayout.Slider("T0 (s)" + (e.t0IsPseudo ? "  [pseudo]" : ""), e.t0, 0f, e.clip.length);
            bool locked = EditorGUILayout.Toggle("T0 checked / locked", e.t0Locked);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_lib, "Edit maneuver entry");
                if (type != e.type) _sigFor = -1;
                e.type = type; e.walkerId = walker; e.take = take; e.include = include;
                if (!Mathf.Approximately(t0, e.t0)) { e.t0 = t0; locked = true; }
                e.t0Locked = locked;
                EditorUtility.SetDirty(_lib);
            }

            EditorGUILayout.LabelField("Clip", $"{e.clip.name}   ({e.clip.length:0.00} s)   measured turn {e.measuredTurnDeg:0} deg");
            EditorGUILayout.HelpBox(string.IsNullOrEmpty(e.analysisNote) ? "Not analysed yet (menu step 2 or 3)." : e.analysisNote, MessageType.None);

            if (_sigFor != _index || _sig == null)
            {
                _sig = T0Analyzer.Signal(e.clip, e.type, T0Analyzer.Settings.Default);
                _sigFor = _index;
            }
            Rect r = GUILayoutUtility.GetRect(200, 10000, 160, 10000);
            DrawPlot(r, e);

            EditorGUILayout.HelpBox("Red = T0, grey = cutoffs (-500/-250/0 ms). Put T0 where the path leaves the regular straight-walking wave; " +
                                    "if unsure, earlier is safer. Click inside the plot to set T0.", MessageType.Info);
        }

        void DrawPlot(Rect r, ManeuverEntry e)
        {
            EditorGUI.DrawRect(r, new Color(0.12f, 0.12f, 0.12f));
            if (_sig == null || _sig.Length < 2)
            {
                GUI.Label(r, "  No humanoid root curves (run menu step 1 on this FBX).");
                return;
            }

            float len = e.clip.length, fs = T0Analyzer.Settings.Default.sampleRate;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var v in _sig) { lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v); }
            if (hi - lo < 0.05f) { float m = 0.5f * (hi + lo); lo = m - 0.025f; hi = m + 0.025f; }
            float pad = 0.08f * (hi - lo); lo -= pad; hi += pad;

            float X(float t) => r.x + r.width * (t / len);
            float Y(float v) => r.yMax - r.height * ((v - lo) / (hi - lo));

            Handles.BeginGUI();
            Handles.color = new Color(1, 1, 1, 0.15f);
            Handles.DrawLine(new Vector3(r.x, Y(0)), new Vector3(r.xMax, Y(0)));

            int step = Mathf.Max(1, _sig.Length / 1500);
            var pts = new Vector3[(_sig.Length + step - 1) / step];
            for (int i = 0, k = 0; i < _sig.Length && k < pts.Length; i += step, k++)
                pts[k] = new Vector3(X(i / fs), Y(_sig[i]));
            Handles.color = new Color(0.35f, 0.8f, 1f);
            Handles.DrawAAPolyLine(2f, pts);

            if (e.t0 > 0f)
            {
                Handles.color = new Color(0.6f, 0.6f, 0.6f, 0.8f);
                foreach (var c in Cutoffs)
                {
                    float t = e.t0 + c;
                    if (t > 0f) Handles.DrawLine(new Vector3(X(t), r.y), new Vector3(X(t), r.yMax));
                }
                Handles.color = Color.red;
                Handles.DrawAAPolyLine(2.5f, new Vector3(X(e.t0), r.y), new Vector3(X(e.t0), r.yMax));
            }
            Handles.EndGUI();

            GUI.Label(new Rect(r.x + 4, r.y + 2, 400, 18),
                ManeuverInfo.IsStairs(e.type) ? "height (m) vs time" : "sideways displacement (m, + = right) vs time");

            var ev = Event.current;
            if ((ev.type == EventType.MouseDown || ev.type == EventType.MouseDrag) && ev.button == 0 && r.Contains(ev.mousePosition))
            {
                Undo.RecordObject(_lib, "Set T0");
                e.t0 = Mathf.Clamp((ev.mousePosition.x - r.x) / r.width * len, 0f, len);
                e.t0Locked = true;
                EditorUtility.SetDirty(_lib);
                ev.Use();
                Repaint();
            }
        }
    }
}
