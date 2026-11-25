using System.Collections.Generic;
using UnityEngine;

namespace WalkingEyeExperiment
{
    // Anatomical proxies follow animated humanoid bones. Sizes can be adjusted per exemplar.
    [DefaultExecutionOrder(100)]
    public class AvatarRegionRig : MonoBehaviour
    {
        public Animator animator;
        [Range(.02f, .3f)] public float headRadius = .11f;
        [Range(.02f, .3f)] public float torsoRadius = .18f;
        [Range(.02f, .3f)] public float pelvisRadius = .15f;
        [Range(.02f, .2f)] public float armRadius = .055f;
        [Range(.02f, .2f)] public float legRadius = .075f;
        [Range(.02f, .2f)] public float footRadius = .065f;
        public int proxyLayer = 2; // Ignore Raycast; queried explicitly with Collider.Raycast.
        public bool showProxies;
        public readonly List<Collider> Colliders = new List<Collider>();
        readonly List<Segment> segments = new List<Segment>();
        class Segment { public Transform a, b, proxy; public CapsuleCollider collider; public float radius; }

        void Start() { Build(); }
        public void Build()
        {
            if (Colliders.Count > 0) return;
            if (!animator) animator = GetComponentInChildren<Animator>();
            if (!animator || !animator.isHuman || !animator.avatar || !animator.avatar.isValid)
            { Debug.LogError("[AvatarRegions] A valid existing humanoid Avatar is required.", this); return; }
            Sphere(HumanBodyBones.Head, BodyRegion.Head, headRadius);
            Link(HumanBodyBones.Hips, HumanBodyBones.Spine, BodyRegion.Pelvis, pelvisRadius);
            HumanBodyBones upper = animator.GetBoneTransform(HumanBodyBones.UpperChest) ? HumanBodyBones.UpperChest : HumanBodyBones.Chest;
            Link(HumanBodyBones.Spine, upper, BodyRegion.UpperTorsoShoulders, torsoRadius);
            Link(HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder, BodyRegion.UpperTorsoShoulders, .08f);
            Limb(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, BodyRegion.LeftArm, armRadius);
            Limb(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, BodyRegion.RightArm, armRadius);
            Limb(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, BodyRegion.LeftLeg, legRadius);
            Limb(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, BodyRegion.RightLeg, legRadius);
            Foot(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, BodyRegion.LeftFoot);
            Foot(HumanBodyBones.RightFoot, HumanBodyBones.RightToes, BodyRegion.RightFoot);
            UpdateSegments();
        }

        GameObject Proxy(BodyRegion region)
        {
            var go = new GameObject("GazeRegion_" + region);
            go.layer = proxyLayer;
            go.transform.SetParent(transform, false);
            var tag = go.AddComponent<AvatarBodyRegion>();
            tag.region = region;
            tag.avatarRoot = transform;
            return go;
        }
        void Sphere(HumanBodyBones bone, BodyRegion region, float radius)
        {
            Transform anchor = animator.GetBoneTransform(bone);
            if (!anchor) { Debug.LogWarning("[AvatarRegions] Missing " + bone, this); return; }
            var go = Proxy(region);
            go.transform.SetParent(anchor, false);
            var collider = go.AddComponent<SphereCollider>();
            collider.radius = radius / Mathf.Max(.001f, anchor.lossyScale.x);
            collider.isTrigger = true;
            Colliders.Add(collider);
        }
        void Limb(HumanBodyBones a, HumanBodyBones b, HumanBodyBones c, BodyRegion region, float radius)
        { Link(a,b,region,radius); Link(b,c,region,radius); }
        void Foot(HumanBodyBones a, HumanBodyBones b, BodyRegion region)
        {
            if (animator.GetBoneTransform(b)) Link(a,b,region,footRadius);
            else Sphere(a,region,footRadius);
        }
        void Link(HumanBodyBones a, HumanBodyBones b, BodyRegion region, float radius)
        {
            var start = animator.GetBoneTransform(a);
            var end = animator.GetBoneTransform(b);
            if (!start || !end) { Debug.LogWarning("[AvatarRegions] Missing segment " + a + " -> " + b, this); return; }
            var go = Proxy(region);
            var collider = go.AddComponent<CapsuleCollider>();
            collider.direction = 1;
            collider.isTrigger = true;
            Colliders.Add(collider);
            segments.Add(new Segment { a=start, b=end, proxy=go.transform, collider=collider, radius=radius });
        }
        void LateUpdate() { UpdateSegments(); }
        void UpdateSegments()
        {
            foreach (var s in segments)
            {
                if (!s.a || !s.b) continue;
                Vector3 offset = s.b.position - s.a.position;
                s.proxy.position = (s.a.position + s.b.position) * .5f;
                s.proxy.rotation = Quaternion.FromToRotation(Vector3.up, offset.sqrMagnitude > 1e-8f ? offset : Vector3.up);
                // Neutralize the model's import/root scale: proxy sizes are in meters.
                Vector3 scale = transform.lossyScale;
                s.proxy.localScale = new Vector3(1/Mathf.Max(.001f,Mathf.Abs(scale.x)), 1/Mathf.Max(.001f,Mathf.Abs(scale.y)), 1/Mathf.Max(.001f,Mathf.Abs(scale.z)));
                s.collider.radius = s.radius;
                s.collider.height = Mathf.Max(2*s.radius, offset.magnitude + 2*s.radius);
            }
        }

        public bool Raycast(Ray ray, float distance, out RaycastHit closest, out AvatarBodyRegion region)
        {
            closest = default; region = null;
            float nearest = distance;
            foreach (var collider in Colliders)
                if (collider && collider.enabled && collider.Raycast(ray, out var hit, nearest))
                { nearest = hit.distance; closest = hit; region = collider.GetComponent<AvatarBodyRegion>(); }
            return region;
        }

        void OnDrawGizmos()
        {
            if (!showProxies) return;
            Gizmos.color = Color.cyan;
            foreach (var collider in Colliders)
                if (collider) Gizmos.DrawWireCube(collider.bounds.center, collider.bounds.size);
        }
    }
}
