// Per-eye gaze directions for the RP island. (From QuestRP / RPSimulation, unchanged.)
//
// A single gaze direction used for both eyes puts the island "at infinity": correct for far objects,
// but when looking at something near, the eyes converge and each eye would look through a different
// part of its island (feels like crossed eyes, reduces the shared field). Here both eyes' islands are
// aimed at the same fixation point, at the distance the user is looking.
//
// The shader measures directions in each eye's own view space. On headsets whose eye views are not
// parallel (canted displays), those spaces are rotated relative to the head, so the per-eye rotation
// reported by the rig is folded in here. Without it the two islands diverge by twice the cant angle.

using UnityEngine;

namespace PassthroughCameraSamples.RPSimulation
{
    public static class RPGazeMath
    {
        public static readonly int HeadToGazeEyeId = Shader.PropertyToID("_HeadToGazeEye");

        public struct EyeGeometry
        {
            /// <summary>Eye positions relative to the centre eye, in head space (metres).</summary>
            public Vector3 LeftPosition, RightPosition;
            /// <summary>Eye view rotations relative to the centre eye (identity when the views are parallel).</summary>
            public Quaternion LeftRotation, RightRotation;

            public float IpdMillimetres => (RightPosition - LeftPosition).magnitude * 1000f;
            /// <summary>Angle between the two eye views in degrees (0 = parallel).</summary>
            public float CantDegrees => Quaternion.Angle(LeftRotation, RightRotation);
        }

        public static EyeGeometry GetEyeGeometry(OVRCameraRig rig)
        {
            var g = new EyeGeometry
            {
                LeftPosition = new Vector3(-0.032f, 0f, 0f),
                RightPosition = new Vector3(0.032f, 0f, 0f),
                LeftRotation = Quaternion.identity,
                RightRotation = Quaternion.identity,
            };
            if (rig == null || rig.centerEyeAnchor == null || rig.leftEyeAnchor == null || rig.rightEyeAnchor == null)
            {
                return g;
            }

            var inv = Quaternion.Inverse(rig.centerEyeAnchor.localRotation);
            var l = inv * (rig.leftEyeAnchor.localPosition - rig.centerEyeAnchor.localPosition);
            var r = inv * (rig.rightEyeAnchor.localPosition - rig.centerEyeAnchor.localPosition);
            // Before tracking starts the anchors coincide; keep the defaults then.
            if ((r - l).sqrMagnitude > 1e-6f)
            {
                g.LeftPosition = l;
                g.RightPosition = r;
                g.LeftRotation = inv * rig.leftEyeAnchor.localRotation;
                g.RightRotation = inv * rig.rightEyeAnchor.localRotation;
            }
            return g;
        }

        /// <summary>
        /// Fills <paramref name="eyeToGaze"/> (length 2: left, right) with rotations taking directions in
        /// each eye's view space into that eye's gaze space, both eyes aimed at the point
        /// <paramref name="distance"/> metres along <paramref name="gazeHeadSpace"/> from the centre eye.
        /// </summary>
        public static void ComputeEyeMatrices(Vector3 gazeHeadSpace, float distance, in EyeGeometry eyes, Matrix4x4[] eyeToGaze)
        {
            var fixation = gazeHeadSpace.normalized * Mathf.Max(distance, 0.1f);
            eyeToGaze[0] = ToGazeSpace(fixation - eyes.LeftPosition) * Matrix4x4.Rotate(eyes.LeftRotation);
            eyeToGaze[1] = ToGazeSpace(fixation - eyes.RightPosition) * Matrix4x4.Rotate(eyes.RightRotation);
        }

        // Head-space direction -> gaze space.
        private static Matrix4x4 ToGazeSpace(Vector3 direction)
        {
            return Matrix4x4.Rotate(Quaternion.Inverse(Quaternion.LookRotation(direction.normalized, Vector3.up)));
        }
    }
}
