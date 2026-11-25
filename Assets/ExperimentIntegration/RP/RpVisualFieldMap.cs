using System;
using System.IO;
using UnityEngine;

namespace WalkingEyeExperiment
{
    // ---------------------------------------------------------------------------------------------------------------
    // Visual-field map data (JSON). Coordinates are the participant's own visual field, per eye, around fixation:
    //   azimuth 0° = right, 90° = up (superior), 180° = left, 270° = down; eccentricity in degrees from fixation.
    //   For the RIGHT eye, 0° is temporal; for the LEFT eye, 0° is nasal.
    // Boundary: radii along meridians (e.g. 24 meridians at 15°, as in Neugebauer et al. 2024), linearly interpolated
    // around the circle. Optional residual islands. Optional per-location sensitivity (dB) replaces binary visibility.
    // ---------------------------------------------------------------------------------------------------------------
    [Serializable] public class RpIsland { public float azimuthDeg, eccentricityDeg, radiusDeg; }
    [Serializable] public class RpSensitivityPoint { public float xDeg, yDeg, db; }
    [Serializable] public class RpEyeField
    {
        public float[] meridiansDeg;   // optional; default = equally spaced from 0° in steps of 360/radiiDeg.Length
        public float[] radiiDeg;       // boundary eccentricity per meridian (50% point of the soft edge)
        public RpIsland[] islands;     // optional residual islands outside the central field
        public RpSensitivityPoint[] points; // optional; if present, visibility comes from these instead of radii/islands
    }
    [Serializable] public class RpFieldMapFile
    {
        public string id;
        public bool synthetic = true;  // must be false only for maps derived from real participant measurements
        public string description, source, measuredWith;
        public float edgeDeg = 2f;     // total width of the soft boundary (Neugebauer et al. 2024 used ~2°)
        public float dbBlind = 0f, dbFull = 25f; // sensitivity mapping: dbBlind -> invisible, dbFull -> fully visible
        public float interpolationRadiusDeg = 8f;
        public RpEyeField left, right;
    }

    /// <summary>Builds the polar visibility texture used by the overlay shader (rows: left eye then right eye).</summary>
    public static class RpVisualFieldMap
    {
        public const int AzimuthBins = 360;      // 1° per texel
        public const float EccStepDeg = .5f;     // 0.5° per texel
        public const int EccBins = 180;          // 0..90°

        public static RpFieldMapFile Circular(float diameterDeg, float edgeDeg)
        {
            var eye = new RpEyeField { radiiDeg = new[] { diameterDeg * .5f } };
            return new RpFieldMapFile { id = $"circular_{diameterDeg:0.#}deg", synthetic = true, edgeDeg = edgeDeg,
                description = "Circular field for engineering tests (not a patient field)", left = eye, right = eye };
        }

        /// <summary>Loads a map by name: persistentDataPath/RPFields/name.json (per-participant files pushed to the
        /// device) takes precedence over Resources/RPFields/name.json (built into the app).</summary>
        public static RpFieldMapFile Load(string name, out string origin, out string error)
        {
            origin = ""; error = "";
            if (string.IsNullOrWhiteSpace(name)) { error = "no map name"; return null; }
            string json = null;
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "RPFields", name + ".json");
                if (File.Exists(path)) { json = File.ReadAllText(path); origin = path; }
            }
            catch (Exception e) { error = e.Message; }
            if (json == null)
            {
                var asset = Resources.Load<TextAsset>("RPFields/" + name);
                if (asset) { json = asset.text; origin = "Resources/RPFields/" + name; }
            }
            if (json == null) { error = $"map '{name}' not found"; return null; }
            try
            {
                var map = JsonUtility.FromJson<RpFieldMapFile>(json);
                if (map == null) { error = "empty map"; return null; }
                if (map.left == null && map.right == null) { error = "map has no eye fields"; return null; }
                if (map.left == null) { map.left = map.right; Debug.LogWarning($"[RPSim] {name}: no left-eye field, using the right eye's"); }
                if (map.right == null) { map.right = map.left; Debug.LogWarning($"[RPSim] {name}: no right-eye field, using the left eye's"); }
                if (string.IsNullOrEmpty(map.id)) map.id = name;
                return map;
            }
            catch (Exception e) { error = "JSON: " + e.Message; return null; }
        }

        public static Texture2D BuildTexture(RpFieldMapFile map, Texture2D reuse = null)
        {
            var tex = reuse;
            if (!tex || tex.width != AzimuthBins || tex.height != 2 * EccBins)
            {
                tex = new Texture2D(AzimuthBins, 2 * EccBins, TextureFormat.R8, false, true)
                { name = "RP visual field map", wrapModeU = TextureWrapMode.Repeat, wrapModeV = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            }
            var px = new byte[AzimuthBins * 2 * EccBins];
            Fill(px, 0, map.left, map);
            Fill(px, EccBins, map.right, map);
            tex.SetPixelData(px, 0);
            tex.Apply(false, false);
            return tex;
        }

        static void Fill(byte[] px, int rowOffset, RpEyeField eye, RpFieldMapFile map)
        {
            float edge = Mathf.Max(.05f, map.edgeDeg);
            bool useSensitivity = eye.points != null && eye.points.Length > 0;
            for (int a = 0; a < AzimuthBins; a++)
            {
                float az = a + .5f;
                float boundary = useSensitivity ? 0f : BoundaryAt(eye, az);
                float ca = Mathf.Cos(az * Mathf.Deg2Rad), sa = Mathf.Sin(az * Mathf.Deg2Rad);
                for (int e = 0; e < EccBins; e++)
                {
                    float ecc = (e + .5f) * EccStepDeg;
                    float vis;
                    if (useSensitivity) vis = SensitivityAt(eye.points, ecc * ca, ecc * sa, map);
                    else
                    {
                        vis = 1f - Smooth(boundary - edge * .5f, boundary + edge * .5f, ecc);
                        if (eye.islands != null)
                            foreach (var isl in eye.islands)
                            {
                                float ix = isl.eccentricityDeg * Mathf.Cos(isl.azimuthDeg * Mathf.Deg2Rad);
                                float iy = isl.eccentricityDeg * Mathf.Sin(isl.azimuthDeg * Mathf.Deg2Rad);
                                float d = Mathf.Sqrt((ecc * ca - ix) * (ecc * ca - ix) + (ecc * sa - iy) * (ecc * sa - iy));
                                vis = Mathf.Max(vis, 1f - Smooth(isl.radiusDeg - edge * .5f, isl.radiusDeg + edge * .5f, d));
                            }
                    }
                    px[(rowOffset + e) * AzimuthBins + a] = (byte)Mathf.RoundToInt(Mathf.Clamp01(vis) * 255f);
                }
            }
        }

        static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / Mathf.Max(1e-5f, e1 - e0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>Boundary radius at an azimuth, linear interpolation between neighbouring meridians (circular).</summary>
        public static float BoundaryAt(RpEyeField eye, float azDeg)
        {
            var r = eye.radiiDeg;
            if (r == null || r.Length == 0) return 0f;
            if (r.Length == 1) return r[0];
            int n = r.Length;
            float[] m = eye.meridiansDeg;
            if (m == null || m.Length != n) { m = new float[n]; for (int i = 0; i < n; i++) m[i] = i * 360f / n; }
            azDeg = Mathf.Repeat(azDeg, 360f);
            for (int i = 0; i < n; i++)
            {
                float a0 = Mathf.Repeat(m[i], 360f), a1 = Mathf.Repeat(m[(i + 1) % n], 360f);
                float span = Mathf.Repeat(a1 - a0, 360f); if (span <= 0f) span = 360f;
                float off = Mathf.Repeat(azDeg - a0, 360f);
                if (off <= span) return Mathf.Lerp(r[i], r[(i + 1) % n], off / span);
            }
            return r[0];
        }

        static float SensitivityAt(RpSensitivityPoint[] pts, float x, float y, RpFieldMapFile map)
        {
            // Gaussian-weighted average (sigma = interpolationRadiusDeg / 2) of the points within interpolationRadiusDeg;
            // no measured point nearby = not seen.
            float wsum = 0f, vsum = 0f, R = Mathf.Max(.5f, map.interpolationRadiusDeg), s2 = 2f * (R * .5f) * (R * .5f);
            foreach (var p in pts)
            {
                float dx = x - p.xDeg, dy = y - p.yDeg, d2 = dx * dx + dy * dy;
                if (d2 > R * R) continue;
                float v = Mathf.Clamp01((p.db - map.dbBlind) / Mathf.Max(.01f, map.dbFull - map.dbBlind));
                float w = Mathf.Exp(-d2 / s2);
                wsum += w; vsum += w * v;
            }
            return wsum > 1e-4f ? vsum / wsum * Mathf.Clamp01(wsum / .25f) : 0f; // fade out where only distant points remain
        }
    }
}
