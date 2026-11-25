using System.Collections.Generic;
using UnityEngine;

namespace WalkingEyeExperiment
{
    /// <summary>
    /// Draws one recorded skeleton pose as the realistic character (bones posed from the joints), a 3D point-light figure
    /// (21 dots on recorded joint centres) or a stick figure (14 connected sticks, no dots). All three are driven by the same joint
    /// positions, so only the appearance differs.
    /// Joint transforms are kept for gaze body-region proxies (AvatarRegionRig) and the research logger.
    /// </summary>
    public sealed class MocapFigure : MonoBehaviour
    {
        public enum Mode { Realistic, PointLight, StickFigure }

        [Tooltip("Dot diameter in metres (0.04 m is ~0.57° at 4 m).")] public float pointDiameter = .04f;
        [Tooltip("Stick thickness in metres.")] public float stickWidth = .025f;
        public Color color = new Color(.96f, .98f, 1f, 1f);

        public Mode CurrentMode { get; private set; }
        public Transform[] Joints { get; private set; }
        public readonly Dictionary<HumanBodyBones, Transform> BoneMap = new Dictionary<HumanBodyBones, Transform>();

        // Point-light set (21 recorded joints): head centre and top of head, neck base (top of thorax), mid-back, pelvis
        // centre, shoulders, elbows, wrists, hands (finger base), hips, knees, ankles, toes.
        static readonly int[] PointJoints =
        {
            -1, MocapClipLibrary.HeadTop, MocapClipLibrary.Spine1, MocapClipLibrary.Spine, MocapClipLibrary.Hips,
            MocapClipLibrary.LShoulder, MocapClipLibrary.RShoulder, MocapClipLibrary.LElbow, MocapClipLibrary.RElbow,
            MocapClipLibrary.LWrist, MocapClipLibrary.RWrist, MocapClipLibrary.LFinger, MocapClipLibrary.RFinger,
            MocapClipLibrary.LHip, MocapClipLibrary.RHip, MocapClipLibrary.LKnee, MocapClipLibrary.RKnee,
            MocapClipLibrary.LAnkle, MocapClipLibrary.RAnkle, MocapClipLibrary.LToe, MocapClipLibrary.RToe
        };
        // Sticks between the same landmarks; -1 = head centre, -2 = neck base (mid-shoulders), -3 = mid-hips.
        static readonly int[,] Sticks =
        {
            { -2, -1 }, { MocapClipLibrary.LShoulder, MocapClipLibrary.RShoulder }, { -2, -3 }, { MocapClipLibrary.LHip, MocapClipLibrary.RHip },
            { MocapClipLibrary.LShoulder, MocapClipLibrary.LElbow }, { MocapClipLibrary.LElbow, MocapClipLibrary.LWrist },
            { MocapClipLibrary.RShoulder, MocapClipLibrary.RElbow }, { MocapClipLibrary.RElbow, MocapClipLibrary.RWrist },
            { MocapClipLibrary.LHip, MocapClipLibrary.LKnee }, { MocapClipLibrary.LKnee, MocapClipLibrary.LAnkle }, { MocapClipLibrary.LAnkle, MocapClipLibrary.LToe },
            { MocapClipLibrary.RHip, MocapClipLibrary.RKnee }, { MocapClipLibrary.RKnee, MocapClipLibrary.RAnkle }, { MocapClipLibrary.RAnkle, MocapClipLibrary.RToe }
        };

        Transform pointRoot, stickRoot;
        MocapAvatarDriver avatar;
        public bool HasAvatar => avatar && avatar.Valid;

        /// <summary>Attach the humanoid character used for the Realistic appearance.</summary>
        public void AttachAvatar(MocapAvatarDriver driver)
        {
            avatar = driver;
            if (!avatar) return;
            avatar.transform.SetParent(transform, true);
            avatar.gameObject.SetActive(true);
            avatar.SetVisible(CurrentMode == Mode.Realistic);
        }

        public MocapAvatarDriver Avatar => avatar;
        readonly List<Transform> points = new List<Transform>();
        readonly List<Transform> sticks = new List<Transform>();
        Material material;
        Vector3[] pose;

        public static MocapFigure Create(Mode mode)
        {
            var go = new GameObject("Mocap Figure");
            var f = go.AddComponent<MocapFigure>();
            f.Build();
            f.SetMode(mode);
            return f;
        }

        void Build()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            material = new Material(shader) { color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);

            Joints = new Transform[MocapClipLibrary.JointNames.Length];
            var jointRoot = new GameObject("Joints").transform; jointRoot.SetParent(transform, false);
            for (int j = 0; j < Joints.Length; j++)
            {
                Joints[j] = new GameObject(MocapClipLibrary.JointNames[j]).transform;
                Joints[j].SetParent(jointRoot, false);
            }
            headCentre = new GameObject("HeadCentre").transform; headCentre.SetParent(jointRoot, false);
            BoneMap[HumanBodyBones.Hips] = Joints[MocapClipLibrary.Hips];
            BoneMap[HumanBodyBones.Spine] = Joints[MocapClipLibrary.Spine];
            BoneMap[HumanBodyBones.Chest] = Joints[MocapClipLibrary.Neck];
            BoneMap[HumanBodyBones.Head] = headCentre;
            BoneMap[HumanBodyBones.LeftShoulder] = Joints[MocapClipLibrary.LShoulder];
            BoneMap[HumanBodyBones.RightShoulder] = Joints[MocapClipLibrary.RShoulder];
            BoneMap[HumanBodyBones.LeftUpperArm] = Joints[MocapClipLibrary.LShoulder];
            BoneMap[HumanBodyBones.RightUpperArm] = Joints[MocapClipLibrary.RShoulder];
            BoneMap[HumanBodyBones.LeftLowerArm] = Joints[MocapClipLibrary.LElbow];
            BoneMap[HumanBodyBones.RightLowerArm] = Joints[MocapClipLibrary.RElbow];
            BoneMap[HumanBodyBones.LeftHand] = Joints[MocapClipLibrary.LWrist];
            BoneMap[HumanBodyBones.RightHand] = Joints[MocapClipLibrary.RWrist];
            BoneMap[HumanBodyBones.LeftUpperLeg] = Joints[MocapClipLibrary.LHip];
            BoneMap[HumanBodyBones.RightUpperLeg] = Joints[MocapClipLibrary.RHip];
            BoneMap[HumanBodyBones.LeftLowerLeg] = Joints[MocapClipLibrary.LKnee];
            BoneMap[HumanBodyBones.RightLowerLeg] = Joints[MocapClipLibrary.RKnee];
            BoneMap[HumanBodyBones.LeftFoot] = Joints[MocapClipLibrary.LAnkle];
            BoneMap[HumanBodyBones.RightFoot] = Joints[MocapClipLibrary.RAnkle];
            BoneMap[HumanBodyBones.LeftToes] = Joints[MocapClipLibrary.LToe];
            BoneMap[HumanBodyBones.RightToes] = Joints[MocapClipLibrary.RToe];

            pointRoot = new GameObject("Point-light figure").transform; pointRoot.SetParent(transform, false);
            for (int i = 0; i < PointJoints.Length; i++) points.Add(MakePrimitive(PrimitiveType.Sphere, pointRoot, "Point " + i));
            stickRoot = new GameObject("Stick figure").transform; stickRoot.SetParent(transform, false);
            for (int i = 0; i < Sticks.GetLength(0); i++) sticks.Add(MakePrimitive(PrimitiveType.Cylinder, stickRoot, "Stick " + i));
            pose = new Vector3[Joints.Length];
        }
        Transform headCentre;

        Transform MakePrimitive(PrimitiveType type, Transform parent, string name)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>Show exactly one representation.</summary>
        public void SetMode(Mode mode)
        {
            CurrentMode = mode;
            if (pointRoot) pointRoot.gameObject.SetActive(mode == Mode.PointLight);
            if (stickRoot) stickRoot.gameObject.SetActive(mode == Mode.StickFigure);
            if (avatar)
            {
                if (!avatar.gameObject.activeSelf) avatar.gameObject.SetActive(true);
                avatar.SetVisible(mode == Mode.Realistic);
            }
        }

        /// <summary>World-space joint positions in MocapClipLibrary.JointNames order.</summary>
        public void SetPose(Vector3[] world)
            => SetPose(world, true);

        /// <summary>Update the joint renderings, optionally preserving the path-derived figure root and heading.</summary>
        public void SetPose(Vector3[] world, bool updateRoot)
        {
            Vector3 head = (world[MocapClipLibrary.Head] + world[MocapClipLibrary.HeadTop]) * .5f;
            Vector3 neck = (world[MocapClipLibrary.LShoulder] + world[MocapClipLibrary.RShoulder]) * .5f;
            Vector3 pelvis = (world[MocapClipLibrary.LHip] + world[MocapClipLibrary.RHip]) * .5f;
            // The figure's root follows the pelvis on the ground and its walking heading (used by the research logger).
            Vector3 across = world[MocapClipLibrary.RHip] - world[MocapClipLibrary.LHip]; across.y = 0f;
            Vector3 fwd = Vector3.Cross(across, Vector3.up);
            Vector3 root = world[MocapClipLibrary.Hips];
            // Move the root first; joints and drawn parts are then placed in world space.
            if (updateRoot)
                transform.SetPositionAndRotation(new Vector3(root.x, Mathf.Min(world[MocapClipLibrary.LToe].y, world[MocapClipLibrary.RToe].y), root.z),
                    fwd.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fwd.normalized, Vector3.up) : transform.rotation);
            for (int j = 0; j < Joints.Length; j++) Joints[j].position = world[j];
            headCentre.position = head;

            // The realistic character is posed from the recorded joint rotations by MocapAvatarDriver (runner calls it).
            if (CurrentMode == Mode.PointLight)
            {
                float s = pointDiameter;
                for (int i = 0; i < PointJoints.Length; i++)
                {
                    points[i].position = PointJoints[i] < 0 ? head : world[PointJoints[i]];
                    points[i].rotation = Quaternion.identity;
                    SetWorldScale(points[i], new Vector3(s, s, s));
                }
            }
            else
            {
                for (int i = 0; i < sticks.Count; i++)
                {
                    Vector3 a = Resolve(Sticks[i, 0], world, head, neck, pelvis), b = Resolve(Sticks[i, 1], world, head, neck, pelvis);
                    Vector3 d = b - a; float len = d.magnitude;
                    sticks[i].position = (a + b) * .5f;
                    sticks[i].rotation = len > 1e-5f ? Quaternion.FromToRotation(Vector3.up, d / len) : Quaternion.identity;
                    // Unity's cylinder is 2 units tall and 1 unit wide.
                    SetWorldScale(sticks[i], new Vector3(stickWidth, len * .5f, stickWidth));
                }
            }
        }

        static Vector3 Resolve(int k, Vector3[] w, Vector3 head, Vector3 neck, Vector3 pelvis)
            => k == -1 ? head : k == -2 ? neck : k == -3 ? pelvis : w[k];

        static void SetWorldScale(Transform t, Vector3 s)
        {
            var p = t.parent ? t.parent.lossyScale : Vector3.one;
            t.localScale = new Vector3(s.x / p.x, s.y / p.y, s.z / p.z);
        }

        public Vector3 HipsPosition => Joints[MocapClipLibrary.Hips].position;

        void OnDestroy() { if (material) Destroy(material); }
    }
}
