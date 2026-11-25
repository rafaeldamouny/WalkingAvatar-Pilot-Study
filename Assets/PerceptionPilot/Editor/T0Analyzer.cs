using System;
using UnityEditor;
using UnityEngine;

namespace PerceptionPilot.EditorTools
{
    /// <summary>
    /// Finds T0 = the first SUSTAINED change of the body's path, from the humanoid root (centre of mass) curves.
    ///  - Turns: lateral velocity relative to the initial straight-walking direction.
    ///  - Stairs: vertical velocity.
    /// Head/trunk/pelvis cues are expected BEFORE T0; T0 is the reference the cutoffs are measured from.
    /// Method: (1) rough onset from a sustained velocity change relative to straight walking;
    /// (2) refinement: the straight walking before the rough onset is fitted as a line plus the gait-sway
    /// sinusoid and extrapolated; T0 is the first sustained departure of the real path from that pattern
    /// (> 4 x fit RMS for 0.15 s, back-tracked to 1 x RMS).
    /// On synthetic gait with 3 cm sway this lands 75-220 ms after the mathematical start of a smooth turn,
    /// i.e. at the first physically detectable path change (consistent across takes, +/- ~50 ms).
    /// Always check the result: Window > Perception Pilot > T0 Inspector.
    /// </summary>
    public static class T0Analyzer
    {
        public struct Settings
        {
            public float sampleRate, baselineStart, baselineEnd, k, sustain, smooth, minTurnDisplacement, minStairRise;
            public static Settings Default => new Settings
            {
                sampleRate = 120f, baselineStart = 0.2f, baselineEnd = 1.2f, k = 2.5f, sustain = 0.25f,
                smooth = 0.10f, minTurnDisplacement = 0.25f, minStairRise = 0.20f
            };
        }

        public struct Result
        {
            public bool hasCurves, found;
            public float t0, turnDeg, lateralEnd, verticalEnd;
            public string note;
        }

        static AnimationCurve Curve(AnimationClip clip, string prop)
        {
            var b = EditorCurveBinding.FloatCurve("", typeof(Animator), prop);
            return AnimationUtility.GetEditorCurve(clip, b);
        }

        /// <summary>Lateral (turns, + = right) or vertical (stairs) root displacement relative to the initial
        /// straight walking, sampled at s.sampleRate. Used by the T0 Inspector plot.</summary>
        public static float[] Signal(AnimationClip clip, ManeuverType type, Settings s)
        {
            var cx = Curve(clip, "RootT.x"); var cy = Curve(clip, "RootT.y"); var cz = Curve(clip, "RootT.z");
            if (cx == null || cy == null || cz == null) return null;
            float len = clip.length;
            int n = Mathf.FloorToInt(len * s.sampleRate) + 1;
            if (n < 20) return null;
            float b0t = s.baselineStart, b1t = s.baselineEnd;
            if (len < b1t + 0.8f) { b0t = 0.05f * len; b1t = 0.3f * len; }
            Vector2 P(float t) => new Vector2(cx.Evaluate(t), cz.Evaluate(t));
            Vector2 p0 = P(b0t), dir = P(b1t) - p0;
            if (dir.sqrMagnitude < 1e-6f) return null;
            dir.Normalize();
            var right = new Vector2(dir.y, -dir.x);
            float y0 = cy.Evaluate(b0t);
            bool stairs = ManeuverInfo.IsStairs(type);
            var sig = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / s.sampleRate;
                sig[i] = stairs ? cy.Evaluate(t) - y0 : Vector2.Dot(P(t) - p0, right);
            }
            return sig;
        }

        public static Result Analyze(AnimationClip clip, ManeuverType type, Settings s)
        {
            var res = new Result();
            var cx = Curve(clip, "RootT.x");
            var cy = Curve(clip, "RootT.y");
            var cz = Curve(clip, "RootT.z");
            if (cx == null || cy == null || cz == null)
            {
                res.note = "No humanoid root curves - import the clip as Humanoid (menu step 1).";
                return res;
            }
            float clipLen = clip.length;
            int nn = Mathf.FloorToInt(clipLen * s.sampleRate) + 1;
            var pp = new Vector2[nn];
            var yy = new float[nn];
            for (int i = 0; i < nn; i++)
            {
                float t = i / s.sampleRate;
                pp[i] = new Vector2(cx.Evaluate(t), cz.Evaluate(t));
                yy[i] = cy.Evaluate(t);
            }
            var r = AnalyzeArrays(pp, yy, type, s);
            r.hasCurves = true;
            return r;
        }

        /// <summary>Recorded joint data (FreeMoCap CSV): T0 from the hip-midpoint path.</summary>
        public static Result AnalyzeTrajectory(PointLightTrajectory traj, ManeuverType type, Settings s)
        {
            traj.SampleRoot(s.sampleRate, out var p, out var y);
            var r = AnalyzeArrays(p, y, type, s);
            r.hasCurves = true;
            return r;
        }

        /// <summary>p = horizontal root path (x, z), y = root height, both sampled at s.sampleRate.</summary>
        public static Result AnalyzeArrays(Vector2[] p, float[] y, ManeuverType type, Settings s)
        {
            var res = new Result();
            int n = p.Length;
            float len = (n - 1) / s.sampleRate;
            if (n < 20) { res.note = "Clip too short."; return res; }

            float b0t = s.baselineStart, b1t = s.baselineEnd;
            if (len < b1t + 0.8f) { b0t = 0.05f * len; b1t = 0.3f * len; }
            int b0 = Mathf.Clamp(Mathf.RoundToInt(b0t * s.sampleRate), 0, n - 2);
            int b1 = Mathf.Clamp(Mathf.RoundToInt(b1t * s.sampleRate), b0 + 2, n - 1);

            Vector2 dir = p[b1] - p[b0];
            if (dir.sqrMagnitude < 1e-6f) { res.note = "No forward motion in the baseline window."; return res; }
            dir.Normalize();
            var right = new Vector2(dir.y, -dir.x);

            bool stairs = ManeuverInfo.IsStairs(type);
            var sig = new float[n];
            for (int i = 0; i < n; i++)
                sig[i] = stairs ? y[i] - y[b0] : Vector2.Dot(p[i] - p[b0], right);

            res.lateralEnd = Vector2.Dot(p[n - 1] - p[b0], right);
            res.verticalEnd = y[n - 1] - y[b0];

            // Final heading vs. baseline heading
            int tail = Mathf.Min(n - 1, Mathf.RoundToInt(0.6f * s.sampleRate));
            Vector2 fin = p[n - 1] - p[n - 1 - tail];
            if (fin.sqrMagnitude > 1e-6f)
                res.turnDeg = Mathf.Atan2(Vector2.Dot(fin, right), Vector2.Dot(fin, dir)) * Mathf.Rad2Deg;

            // Velocity, smoothed (centred moving average)
            var v = new float[n];
            for (int i = 1; i < n - 1; i++) v[i] = (sig[i + 1] - sig[i - 1]) * s.sampleRate * 0.5f;
            v[0] = v[1]; v[n - 1] = v[n - 2];
            int half = Mathf.Max(1, Mathf.RoundToInt(s.smooth * s.sampleRate * 0.5f));
            var vs = new float[n];
            for (int i = 0; i < n; i++)
            {
                float sum = 0f; int c = 0;
                for (int j = i - half; j <= i + half; j++) if (j >= 0 && j < n) { sum += v[j]; c++; }
                vs[i] = sum / c;
            }

            float mean = 0f; for (int i = b0; i <= b1; i++) mean += vs[i]; mean /= (b1 - b0 + 1);
            float ss = 0f; for (int i = b0; i <= b1; i++) ss += (vs[i] - mean) * (vs[i] - mean);
            float sd = Mathf.Max(Mathf.Sqrt(ss / (b1 - b0)), 0.02f);
            float hi = mean + s.k * sd, lo = mean - s.k * sd;
            int need = Mathf.Max(1, Mathf.RoundToInt(s.sustain * s.sampleRate));

            int onset = -1;
            int run = 0, runSign = 0, runStart = -1;
            for (int i = b1; i < n; i++)
            {
                int sign = vs[i] > hi ? 1 : (vs[i] < lo ? -1 : 0);
                if (sign != 0 && sign == runSign) { run++; }
                else if (sign != 0) { runSign = sign; run = 1; runStart = i; }
                else { runSign = 0; run = 0; }
                if (run >= need) { onset = runStart; break; }
            }

            float minDisp = stairs ? s.minStairRise : s.minTurnDisplacement;
            float endDisp = stairs ? res.verticalEnd : res.lateralEnd;

            if (onset < 0 || Mathf.Abs(endDisp) < minDisp)
            {
                res.found = false;
                res.note = type == ManeuverType.Straight
                    ? "Straight: no sustained deviation (good)."
                    : $"No sustained {(stairs ? "vertical" : "lateral")} change found (end displacement {endDisp:0.00} m).";
                return res;
            }

            // Rough onset: back-track to where velocity left the +/-1 SD band.
            float band = mean + runSign * sd;
            int rough = onset;
            while (rough > b1 && (runSign > 0 ? vs[rough - 1] > band : vs[rough - 1] < band)) rough--;

            // Refinement: fit straight walking as line + gait-sway sinusoid on the pre-onset part,
            // extrapolate, and find the first sustained departure of the real path from it.
            int fitEnd = Mathf.Max(b1, rough - Mathf.RoundToInt(0.35f * s.sampleRate));
            int refined = RefineWithSwayModel(sig, b0, fitEnd, n, s.sampleRate,
                stairs ? 1.2f : 0.6f, stairs ? 2.6f : 1.4f, out string refineNote);

            res.found = true;
            res.t0 = (refined >= 0 ? refined : rough) / s.sampleRate;
            string def = refined >= 0 ? "T0 = first sustained departure from the sway-corrected straight-walking path"
                                      : "T0 from velocity threshold (sway fit failed: " + refineNote + ")";
            res.note = type == ManeuverType.Straight ? "WARNING: straight clip has a sustained deviation. " + def : def;
            return res;
        }

        // Fits y = a + b t + c sin(2 pi f t) + d cos(2 pi f t) on [i0, i1) with a grid search over f,
        // then returns the first index after i1 where |residual| > 4 rms for 0.15 s, back-tracked to |residual| <= rms.
        static int RefineWithSwayModel(float[] sig, int i0, int i1, int n, float fs, float fLo, float fHi, out string note)
        {
            note = "";
            if (i1 - i0 < Mathf.RoundToInt(0.6f * fs)) { note = "pre-onset window too short"; return -1; }

            double bestErr = double.MaxValue, bestF = 0; double[] best = null;
            for (double f = fLo; f <= fHi + 1e-9; f += 0.005)
            {
                var c = FitLineSine(sig, i0, i1, fs, f, out double err);
                if (c != null && err < bestErr) { bestErr = err; bestF = f; best = c; }
            }
            if (best == null) { note = "least-squares failed"; return -1; }

            double rms = Math.Max(Math.Sqrt(bestErr / (i1 - i0)), 0.003);
            double thr = 4.0 * rms;
            int need = Mathf.Max(1, Mathf.RoundToInt(0.15f * fs));

            int run = 0, runSign = 0, runStart = -1, onset = -1;
            for (int i = i1; i < n; i++)
            {
                double r = sig[i] - Model(best, i / fs, bestF);
                int sign = r > thr ? 1 : (r < -thr ? -1 : 0);
                if (sign != 0 && sign == runSign) run++;
                else if (sign != 0) { runSign = sign; run = 1; runStart = i; }
                else { runSign = 0; run = 0; }
                if (run >= need) { onset = runStart; break; }
            }
            if (onset < 0) { note = "no departure from the sway model"; return -1; }

            int k = onset;
            while (k > i1)
            {
                double r = sig[k - 1] - Model(best, (k - 1) / fs, bestF);
                if (runSign > 0 ? r > rms : r < -rms) k--; else break;
            }
            return k;
        }

        static double Model(double[] c, double t, double f)
        {
            double w = 2.0 * Math.PI * f * t;
            return c[0] + c[1] * t + c[2] * Math.Sin(w) + c[3] * Math.Cos(w);
        }

        static double[] FitLineSine(float[] y, int i0, int i1, float fs, double f, out double err)
        {
            var A = new double[4, 4];
            var b = new double[4];
            var row = new double[4];
            for (int i = i0; i < i1; i++)
            {
                double t = i / fs, w = 2.0 * Math.PI * f * t;
                row[0] = 1; row[1] = t; row[2] = Math.Sin(w); row[3] = Math.Cos(w);
                for (int r = 0; r < 4; r++)
                {
                    b[r] += row[r] * y[i];
                    for (int c = 0; c < 4; c++) A[r, c] += row[r] * row[c];
                }
            }
            var x = Solve4(A, b);
            err = double.MaxValue;
            if (x == null) return null;
            err = 0;
            for (int i = i0; i < i1; i++)
            {
                double d = y[i] - Model(x, i / fs, f);
                err += d * d;
            }
            return x;
        }

        static double[] Solve4(double[,] A, double[] b)
        {
            const int N = 4;
            var M = new double[N, N + 1];
            for (int r = 0; r < N; r++) { for (int c = 0; c < N; c++) M[r, c] = A[r, c]; M[r, N] = b[r]; }
            for (int col = 0; col < N; col++)
            {
                int piv = col;
                for (int r = col + 1; r < N; r++) if (Math.Abs(M[r, col]) > Math.Abs(M[piv, col])) piv = r;
                if (Math.Abs(M[piv, col]) < 1e-12) return null;
                if (piv != col) for (int c = 0; c <= N; c++) { double tmp = M[col, c]; M[col, c] = M[piv, c]; M[piv, c] = tmp; }
                for (int r = 0; r < N; r++)
                {
                    if (r == col) continue;
                    double fct = M[r, col] / M[col, col];
                    for (int c = col; c <= N; c++) M[r, c] -= fct * M[col, c];
                }
            }
            var x = new double[N];
            for (int r = 0; r < N; r++) x[r] = M[r, N] / M[r, r];
            return x;
        }
    }
}
