using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace PerceptionPilot
{
    public enum CaptureUpAxis { ZUp, YUp, YDown }

    /// <summary>
    /// Real recorded joint positions (FreeMoCap saved_data/csv/body_trajectories.csv), converted to Unity
    /// space (metres, Y up, floor at 0). This is the source of the point-light display: the dots are the
    /// recorded person's joints, not points attached to a retargeted avatar.
    /// </summary>
    public class PointLightTrajectory
    {
        /// <summary>Classic 13-marker set (Johansson-style).</summary>
        public static readonly string[] CoreMarkers =
        {
            "head_center",
            "left_shoulder", "right_shoulder",
            "left_elbow", "right_elbow",
            "left_wrist", "right_wrist",
            "left_hip", "right_hip",
            "left_knee", "right_knee",
            "left_ankle", "right_ankle"
        };
        public static readonly string[] FootMarkers = { "left_foot_index", "right_foot_index" };

        public int FrameCount { get; private set; }
        public float Fps { get; private set; }
        public float Duration => FrameCount > 1 ? (FrameCount - 1) / Fps : 0f;
        public string[] Names { get; private set; }
        public int MarkerCount => Names.Length;
        public float UnitScale { get; private set; }
        public string Note { get; private set; } = "";

        Vector3[][] _m;     // [frame][marker]
        Vector3[] _root;    // hip midpoint per frame

        static readonly Dictionary<string, PointLightTrajectory> Cache = new Dictionary<string, PointLightTrajectory>();

        public static PointLightTrajectory Get(TextAsset csv, float fps, CaptureUpAxis up, float scaleOverride, bool includeFeet)
        {
            if (csv == null) return null;
            string key = csv.GetInstanceID() + "|" + fps + "|" + up + "|" + scaleOverride + "|" + includeFeet;
            if (Cache.TryGetValue(key, out var t)) return t;
            t = Parse(csv.text, fps, up, scaleOverride, includeFeet);
            Cache[key] = t;
            return t;
        }

        public static PointLightTrajectory Parse(string text, float fps, CaptureUpAxis up, float scaleOverride, bool includeFeet)
        {
            var lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 3) throw new Exception("CSV has no data rows.");

            string header = lines[0].Trim().TrimStart('#').Trim();
            var cols = header.Split(',');
            for (int i = 0; i < cols.Length; i++) cols[i] = cols[i].Trim().ToLowerInvariant();

            int Col(string name) => Array.IndexOf(cols, name);

            var wanted = new List<string>(CoreMarkers);
            if (includeFeet) wanted.AddRange(FootMarkers);

            // Column index of x for each marker (y, z follow). Head falls back to ear midpoint.
            var xi = new List<int[]>();
            var names = new List<string>();
            var note = new List<string>();
            foreach (var w in wanted)
            {
                int c = Col(w + "_x");
                if (c >= 0) { xi.Add(new[] { c }); names.Add(w); continue; }
                if (w == "head_center")
                {
                    int l = Col("left_ear_x"), r = Col("right_ear_x");
                    if (l >= 0 && r >= 0) { xi.Add(new[] { l, r }); names.Add(w); note.Add("head = ear midpoint"); continue; }
                }
                note.Add("missing " + w);
            }
            int lh = names.IndexOf("left_hip"), rh = names.IndexOf("right_hip");
            if (lh < 0 || rh < 0) throw new Exception("CSV needs left_hip and right_hip columns.");
            int[] floorCols = { Col("left_heel_x"), Col("right_heel_x"), Col("left_foot_index_x"), Col("right_foot_index_x"),
                                Col("left_ankle_x"), Col("right_ankle_x") };

            var rawFrames = new List<Vector3[]>();
            var floorH = new List<float>();
            var ci = CultureInfo.InvariantCulture;

            for (int li = 1; li < lines.Length; li++)
            {
                var s = lines[li].Trim();
                if (s.Length == 0 || s[0] == '#') continue;
                var v = s.Split(',');
                float F(int idx) => idx >= 0 && idx < v.Length && float.TryParse(v[idx], NumberStyles.Float, ci, out float f) ? f : float.NaN;
                Vector3 P(int c) => new Vector3(F(c), F(c + 1), F(c + 2));

                var frame = new Vector3[names.Count];
                for (int m = 0; m < names.Count; m++)
                {
                    var src = xi[m];
                    frame[m] = src.Length == 1 ? P(src[0]) : 0.5f * (P(src[0]) + P(src[1]));
                }
                rawFrames.Add(frame);

                float minUp = float.PositiveInfinity;
                foreach (var fc in floorCols)
                    if (fc >= 0) { var p = P(fc); float u = UpComponent(p, up); if (!float.IsNaN(u)) minUp = Mathf.Min(minUp, u); }
                if (!float.IsInfinity(minUp)) floorH.Add(minUp);
            }
            if (rawFrames.Count < 2) throw new Exception("CSV has fewer than 2 frames.");

            // Units: FreeMoCap 3D output is in millimetres. Detect from typical hip-to-ankle length.
            float scale = scaleOverride;
            if (scale <= 0f)
            {
                int la = names.IndexOf("left_ankle");
                float legLen = 0f; int n = 0;
                if (la >= 0)
                    foreach (var f in rawFrames)
                    {
                        float d = Vector3.Distance(f[lh], f[la]);
                        if (!float.IsNaN(d)) { legLen += d; n++; }
                    }
                legLen = n > 0 ? legLen / n : 0f;
                scale = legLen > 10f ? 0.001f : 1f;
                note.Add(scale < 1f ? "units: mm -> m" : "units: m");
            }

            // Floor: low percentile of the lowest foot point, so feet rest on the real floor.
            float floor = 0f;
            if (floorH.Count > 0)
            {
                floorH.Sort();
                floor = floorH[Mathf.Clamp(Mathf.RoundToInt(0.02f * floorH.Count), 0, floorH.Count - 1)];
            }

            var t = new PointLightTrajectory
            {
                Fps = fps > 0f ? fps : 30f,
                Names = names.ToArray(),
                UnitScale = scale,
                FrameCount = rawFrames.Count,
                _m = new Vector3[rawFrames.Count][],
                _root = new Vector3[rawFrames.Count]
            };

            var last = new Vector3[names.Count];
            bool[] haveLast = new bool[names.Count];
            int nanFilled = 0;
            for (int fi = 0; fi < rawFrames.Count; fi++)
            {
                var outF = new Vector3[names.Count];
                for (int m = 0; m < names.Count; m++)
                {
                    var p = rawFrames[fi][m];
                    if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z))
                    {
                        nanFilled++;
                        outF[m] = haveLast[m] ? last[m] : Vector3.zero;
                        continue;
                    }
                    var u = ToUnity(p, up, scale);
                    u.y -= floor * scale;
                    outF[m] = u; last[m] = u; haveLast[m] = true;
                }
                t._m[fi] = outF;
                t._root[fi] = 0.5f * (outF[lh] + outF[rh]);
            }
            if (nanFilled > 0) note.Add($"{nanFilled} missing points held from previous frame");
            t.Note = string.Join("; ", note);
            return t;
        }

        static float UpComponent(Vector3 p, CaptureUpAxis up)
        {
            switch (up)
            {
                case CaptureUpAxis.ZUp: return p.z;
                case CaptureUpAxis.YUp: return p.y;
                default: return -p.y;
            }
        }

        // Right-handed capture space -> Unity (left-handed, Y up). Swapping two axes keeps left/right correct.
        static Vector3 ToUnity(Vector3 p, CaptureUpAxis up, float s)
        {
            switch (up)
            {
                case CaptureUpAxis.ZUp: return new Vector3(p.x, p.z, p.y) * s;
                case CaptureUpAxis.YUp: return new Vector3(p.x, p.y, -p.z) * s;
                default: return new Vector3(p.x, -p.y, p.z) * s;
            }
        }

        void Index(float t, out int i0, out int i1, out float a)
        {
            float f = Mathf.Clamp(t * Fps, 0f, FrameCount - 1);
            i0 = Mathf.FloorToInt(f);
            i1 = Mathf.Min(i0 + 1, FrameCount - 1);
            a = f - i0;
        }

        public Vector3 Marker(int m, float t)
        {
            Index(t, out int i0, out int i1, out float a);
            return Vector3.LerpUnclamped(_m[i0][m], _m[i1][m], a);
        }

        public Vector3 Root(float t)
        {
            Index(t, out int i0, out int i1, out float a);
            return Vector3.LerpUnclamped(_root[i0], _root[i1], a);
        }

        /// <summary>Root (hip midpoint) sampled at a fixed rate, for onset analysis.</summary>
        public void SampleRoot(float rate, out Vector2[] xz, out float[] y)
        {
            int n = Mathf.FloorToInt(Duration * rate) + 1;
            xz = new Vector2[n]; y = new float[n];
            for (int i = 0; i < n; i++)
            {
                var r = Root(i / rate);
                xz[i] = new Vector2(r.x, r.z);
                y[i] = r.y;
            }
        }
    }
}
