using System.Collections.Generic;
using UnityEngine;

namespace PerceptionPilot
{
    /// <summary>Point-light (biological motion) version of the same avatar: small dots attached to the
    /// joints of the SAME skeleton, so both appearances share identical kinematics and timing.</summary>
    public class PointLightRig : MonoBehaviour
    {
        [Tooltip("Unlit material for the dots. Assign one (the setup menu creates PP_Dots.mat) so the shader is included in the build.")]
        public Material dotMaterial;
        [Tooltip("Dot diameter in metres.")]
        public float dotDiameter = 0.05f;

        [Tooltip("15 classic point-light markers.")]
        public HumanBodyBones[] markerBones =
        {
            HumanBodyBones.Head, HumanBodyBones.Neck,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,   // shoulders
            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,   // elbows
            HumanBodyBones.LeftHand, HumanBodyBones.RightHand,           // wrists
            HumanBodyBones.Hips,                                         // pelvis
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,   // hips
            HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,   // knees
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot            // ankles
        };

        readonly List<Renderer> _dots = new List<Renderer>();
        bool _built;

        public IReadOnlyList<Renderer> Dots => _dots;

        public void Build(Animator animator)
        {
            if (_built || animator == null) return;
            _built = true;

            Material mat = dotMaterial;
            if (mat == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh == null) sh = Shader.Find("Unlit/Color");
                if (sh == null) { Debug.LogError("[PointLightRig] No unlit shader found. Assign dotMaterial."); return; }
                mat = new Material(sh);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            }

            foreach (var b in markerBones)
            {
                Transform bone = animator.GetBoneTransform(b);
                if (bone == null) { Debug.LogWarning("[PointLightRig] Bone not mapped: " + b); continue; }

                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "PLD_" + b;
                var col = go.GetComponent<Collider>();
                if (col != null) Destroy(col);
                go.transform.SetParent(bone, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                float s = Mathf.Max(1e-4f, bone.lossyScale.x);
                go.transform.localScale = Vector3.one * (dotDiameter / s);

                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.enabled = false;
                _dots.Add(r);
            }
        }

        public void SetVisible(bool visible)
        {
            foreach (var r in _dots) if (r != null) r.enabled = visible;
        }
    }
}
