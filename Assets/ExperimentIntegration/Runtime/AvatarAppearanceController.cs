using System.Collections.Generic;
using UnityEngine;

namespace WalkingEyeExperiment
{
    /// <summary>Switches the animated humanoid between its body and a clean point-light display.</summary>
    public sealed class AvatarAppearanceController : MonoBehaviour
    {
        public enum Appearance { Realistic, BiologicalMotion }

        // Nineteen unlit landmarks make the animated human motion legible as a 3D point-light figure.
        static readonly HumanBodyBones[] TrackedBones = {
            HumanBodyBones.Head,
            HumanBodyBones.Chest, HumanBodyBones.Hips,
            HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
            HumanBodyBones.LeftToes, HumanBodyBones.RightToes
        };

        Animator animator;
        Renderer[] bodyRenderers;
        readonly Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
        readonly Dictionary<HumanBodyBones, Transform> markers = new Dictionary<HumanBodyBones, Transform>();
        Material markerMaterial;

        public void Initialize(Animator sourceAnimator, Appearance appearance)
        {
            animator = sourceAnimator;
            bodyRenderers = GetComponentsInChildren<Renderer>(true);
            foreach (var bone in TrackedBones)
            {
                var joint = animator ? animator.GetBoneTransform(bone) : null;
                if (joint) bones[bone] = joint;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (!shader) shader = Shader.Find("Sprites/Default");
            var pointColor = new Color(0.96f, 0.98f, 1f, 1f);
            markerMaterial = new Material(shader);
            markerMaterial.color = pointColor;
            if (markerMaterial.HasProperty("_BaseColor")) markerMaterial.SetColor("_BaseColor", pointColor);
            markerMaterial.renderQueue = 3000;

            foreach (var pair in bones)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = "Point Light - " + pair.Key;
                marker.transform.SetParent(transform, false);
                marker.transform.localScale = Vector3.one * 0.018f;
                var renderer = marker.GetComponent<Renderer>();
                renderer.sharedMaterial = markerMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                var collider = marker.GetComponent<Collider>();
                if (collider) Destroy(collider);
                markers[pair.Key] = marker.transform;
            }

            SetAppearance(appearance);
        }

        public void SetAppearance(Appearance appearance)
        {
            bool realistic = appearance == Appearance.Realistic;
            if (bodyRenderers != null)
                foreach (var renderer in bodyRenderers)
                    if (renderer) renderer.enabled = realistic;
            foreach (var marker in markers.Values)
                if (marker) marker.gameObject.SetActive(!realistic);
        }

        void LateUpdate()
        {
            foreach (var pair in markers)
                if (pair.Value && bones.TryGetValue(pair.Key, out var joint) && joint)
                    pair.Value.position = joint.position;
        }

        void OnDestroy()
        {
            if (markerMaterial) Destroy(markerMaterial);
        }
    }
}
