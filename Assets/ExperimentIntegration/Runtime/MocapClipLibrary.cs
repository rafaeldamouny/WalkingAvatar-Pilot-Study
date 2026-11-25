using System;
using System.Collections.Generic;
using PerceptionPilot;
using UnityEngine;

namespace WalkingEyeExperiment
{
    /// <summary>
    /// Recorded human walking (CMU Graphics Lab Motion Capture Database) for the perception pilot.
    /// Clips live in Resources/Mocap: manifest.json + one .bytes file per clip (float32, frames x joints x 3).
    /// Positions are the recorded skeleton's joint centres (forward kinematics of the CMU ASF/AMC data), in metres,
    /// Unity axes, with the walking direction before T0 along +Z and the root at T0 on the origin.
    /// They are skeleton joints, not raw optical-marker trajectories.
    /// </summary>
    public sealed class MocapClipLibrary
    {
        [Serializable] public class LeadIn { public string donor, donorDescription; public float seamRelT0, seamPoseRmsM; }
        [Serializable] public class ClipInfo
        {
            public string id, maneuver, file, source, trial, notes, skeleton;
            public int frames, subject;
            public float fps, t0, startLevel, measuredTurnDeg, measuredRiseM, t0InSourceS, recordedTargetStartsRelT0;
            public bool t0IsPseudo;
            public LeadIn[] leadIn;
        }
        /// <summary>Recorded CMU skeleton of one subject: joint names, parent indices, rest offsets (m, Unity axes).</summary>
        [Serializable] public class Skeleton { public string id; public string[] names; public int[] parents; public float[] offsets; }
        [Serializable] class Manifest { public string[] joints; public string attribution, conversion; public ClipInfo[] clips; public Skeleton[] skeletons; }

        public sealed class Clip
        {
            public ClipInfo info;
            public ManeuverType maneuver;
            public Vector3[] data;    // frames * joints
            public int jointCount;
            public Skeleton skeleton;     // recorded skeleton (for the realistic avatar)
            public Quaternion[] rotations; // frames * skeleton joints, local rotations
            public int rotationCount;
            public bool HasRotations => rotations != null && skeleton != null;

            /// <summary>Local joint rotations of the recorded skeleton at clip time t, slerped.</summary>
            public void SampleRotations(float t, Quaternion[] local)
            {
                float f = Mathf.Clamp(t * info.fps, 0f, info.frames - 1);
                int i0 = Mathf.FloorToInt(f), i1 = Mathf.Min(i0 + 1, info.frames - 1);
                float u = f - i0;
                int a = i0 * rotationCount, b = i1 * rotationCount;
                for (int j = 0; j < rotationCount; j++) local[j] = Quaternion.Slerp(rotations[a + j], rotations[b + j], u);
            }
            public float Duration => (info.frames - 1) / info.fps;
            float legLength = -1f;
            /// <summary>Recorded hip-knee-ankle length (m), averaged over both legs at T0.</summary>
            public float LegLength
            {
                get
                {
                    if (legLength > 0f) return legLength;
                    int f = Mathf.Clamp(Mathf.RoundToInt(T0 * info.fps), 0, info.frames - 1) * jointCount;
                    Vector3 P(int j) => data[f + j];
                    legLength = (Vector3.Distance(P(LHip), P(LKnee)) + Vector3.Distance(P(LKnee), P(LAnkle))
                               + Vector3.Distance(P(RHip), P(RKnee)) + Vector3.Distance(P(RKnee), P(RAnkle))) * .5f;
                    return legLength;
                }
            }
            /// <summary>Clip time (s) of the maneuver onset.</summary>
            public float T0 => info.t0;
            public string Spliced => info.leadIn == null || info.leadIn.Length == 0 ? "" : string.Join("+", Array.ConvertAll(info.leadIn, l => l.donor));

            /// <summary>Joint positions at clip time t (seconds), clamped to the clip, linearly interpolated.</summary>
            public void Sample(float t, Vector3[] joints)
            {
                float f = Mathf.Clamp(t * info.fps, 0f, info.frames - 1);
                int i0 = Mathf.FloorToInt(f), i1 = Mathf.Min(i0 + 1, info.frames - 1);
                float u = f - i0;
                int a = i0 * jointCount, b = i1 * jointCount;
                for (int j = 0; j < jointCount; j++) joints[j] = Vector3.LerpUnclamped(data[a + j], data[b + j], u);
            }
        }

        public static readonly string[] JointNames =
        {
            "Hips", "Spine", "Spine1", "Neck", "Neck1", "Head", "HeadTop",
            "LeftArm", "LeftForeArm", "LeftHand", "RightArm", "RightForeArm", "RightHand",
            "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase", "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase",
            "LeftHandIndex1", "RightHandIndex1"
        };
        public const int Hips = 0, Spine = 1, Spine1 = 2, Neck = 3, Neck1 = 4, Head = 5, HeadTop = 6,
            LShoulder = 7, LElbow = 8, LWrist = 9, RShoulder = 10, RElbow = 11, RWrist = 12,
            LHip = 13, LKnee = 14, LAnkle = 15, LToe = 16, RHip = 17, RKnee = 18, RAnkle = 19, RToe = 20,
            LFinger = 21, RFinger = 22;

        public string Attribution { get; private set; }
        public string Error { get; private set; }
        public readonly List<Clip> Clips = new List<Clip>();
        readonly Dictionary<ManeuverType, List<Clip>> byManeuver = new Dictionary<ManeuverType, List<Clip>>();

        static MocapClipLibrary cached;
        public static MocapClipLibrary Load()
        {
            if (cached != null) return cached;
            var lib = new MocapClipLibrary();
            var manifestAsset = Resources.Load<TextAsset>("Mocap/manifest");
            if (!manifestAsset) { lib.Error = "Resources/Mocap/manifest.json is missing"; return cached = lib; }
            var manifest = JsonUtility.FromJson<Manifest>(manifestAsset.text);
            if (manifest?.joints == null || manifest.joints.Length != JointNames.Length) { lib.Error = "Mocap manifest joint list does not match"; return cached = lib; }
            lib.Attribution = manifest.attribution;
            foreach (var info in manifest.clips)
            {
                if (!Enum.TryParse(info.maneuver, out ManeuverType type)) { Debug.LogWarning("[Mocap] Unknown maneuver " + info.maneuver); continue; }
                var bytes = Resources.Load<TextAsset>("Mocap/" + info.file);
                int n = info.frames * JointNames.Length;
                if (!bytes || bytes.bytes.Length != n * 12) { Debug.LogError("[Mocap] Missing or wrong-size data for " + info.id); continue; }
                var raw = bytes.bytes;
                var data = new Vector3[n];
                for (int k = 0; k < n; k++)
                    data[k] = new Vector3(BitConverter.ToSingle(raw, k * 12), BitConverter.ToSingle(raw, k * 12 + 4), BitConverter.ToSingle(raw, k * 12 + 8));
                var clip = new Clip { info = info, maneuver = type, data = data, jointCount = JointNames.Length };
                if (manifest.skeletons != null)
                    foreach (var sk in manifest.skeletons) if (sk.id == info.skeleton) clip.skeleton = sk;
                var rotAsset = Resources.Load<TextAsset>("Mocap/" + info.file + "_rot");
                if (clip.skeleton != null && rotAsset)
                {
                    int k = clip.skeleton.names.Length, m = info.frames * k;
                    var rb = rotAsset.bytes;
                    if (rb.Length == m * 16)
                    {
                        clip.rotations = new Quaternion[m]; clip.rotationCount = k;
                        for (int q = 0; q < m; q++)
                            clip.rotations[q] = new Quaternion(BitConverter.ToSingle(rb, q * 16), BitConverter.ToSingle(rb, q * 16 + 4),
                                                               BitConverter.ToSingle(rb, q * 16 + 8), BitConverter.ToSingle(rb, q * 16 + 12));
                    }
                    else Debug.LogError("[Mocap] Wrong-size rotation data for " + info.id);
                    Resources.UnloadAsset(rotAsset);
                }
                else Debug.LogWarning("[Mocap] No joint rotations for " + info.id + " (realistic avatar cannot play it)");
                lib.Clips.Add(clip);
                if (!lib.byManeuver.TryGetValue(type, out var list)) lib.byManeuver[type] = list = new List<Clip>();
                list.Add(clip);
                Resources.UnloadAsset(bytes);
            }
            if (lib.Clips.Count == 0) lib.Error = "No mocap clips could be loaded";
            Debug.Log($"[Mocap] Loaded {lib.Clips.Count} recorded clips");
            return cached = lib;
        }

        public bool Has(ManeuverType m) => byManeuver.TryGetValue(m, out var l) && l.Count > 0;
        public IReadOnlyList<Clip> For(ManeuverType m) => byManeuver.TryGetValue(m, out var l) ? l : (IReadOnlyList<Clip>)Array.Empty<Clip>();
    }
}
