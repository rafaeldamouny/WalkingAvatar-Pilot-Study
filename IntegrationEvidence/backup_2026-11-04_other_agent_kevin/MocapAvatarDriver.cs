using System.Collections.Generic;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine;

namespace WalkingEyeExperiment
{
    /// <summary>
    /// Drives the trial avatar from Kevin's humanoid walk clip and exposes its sampled joint positions to the shared
    /// point-light and stick figures. The legacy CMU HumanPose retarget path remains available when no clip is assigned.
    /// </summary>
    public sealed class MocapAvatarDriver : MonoBehaviour
    {
        sealed class SourceRig
        {
            public Transform root;
            public Transform[] bones;
            public HumanPoseHandler handler;
            public float humanScale;
            public Quaternion[] local;
            public float legLength;
        }

        static float Leg(Transform hip, Transform knee, Transform ankle)
            => hip && knee && ankle ? Vector3.Distance(hip.position, knee.position) + Vector3.Distance(knee.position, ankle.position) : 0f;
        float targetLegLength;

        // Recorded CMU joint -> Unity human bone. Zero-length CMU joints (LHipJoint, LowerBack, Neck, shoulders,
        // finger joints) stay unmapped intermediates; their rotations still shape the pose.
        static readonly (string cmu, HumanBodyBones human)[] Map =
        {
            ("Hips", HumanBodyBones.Hips), ("Spine", HumanBodyBones.Spine), ("Spine1", HumanBodyBones.Chest),
            ("Neck1", HumanBodyBones.Neck), ("Head", HumanBodyBones.Head),
            ("LeftUpLeg", HumanBodyBones.LeftUpperLeg), ("LeftLeg", HumanBodyBones.LeftLowerLeg),
            ("LeftFoot", HumanBodyBones.LeftFoot), ("LeftToeBase", HumanBodyBones.LeftToes),
            ("RightUpLeg", HumanBodyBones.RightUpperLeg), ("RightLeg", HumanBodyBones.RightLowerLeg),
            ("RightFoot", HumanBodyBones.RightFoot), ("RightToeBase", HumanBodyBones.RightToes),
            ("LeftArm", HumanBodyBones.LeftUpperArm), ("LeftForeArm", HumanBodyBones.LeftLowerArm), ("LeftHand", HumanBodyBones.LeftHand),
            ("RightArm", HumanBodyBones.RightUpperArm), ("RightForeArm", HumanBodyBones.RightLowerArm), ("RightHand", HumanBodyBones.RightHand),
        };

        Animator animator;
        HumanPoseHandler target;
        HumanPose pose;
        AnimationClip animationClip;
        PlayableGraph animationGraph;
        AnimationClipPlayable animationPlayable;
        bool useAnimationClip;
        Transform[] sampledBones;
        float currentAnimationRate = .5f;
        float targetHumanScale = 1f;
        Vector3 baseScale;
        Transform targetHips;
        readonly Dictionary<string, SourceRig> rigs = new Dictionary<string, SourceRig>();
        SourceRig current;
        public bool Valid { get; private set; }

        public static MocapAvatarDriver Create(GameObject prefab, Avatar rig)
            => Create(prefab, rig, null);

        public static MocapAvatarDriver Create(GameObject prefab, Avatar rig, AnimationClip walkingClip)
        {
            if (!prefab) return null;
            var go = Instantiate(prefab, Vector3.zero, Quaternion.identity);
            go.name = "Mocap Realistic Avatar";
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.useGravity = false; }
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var cc in go.GetComponentsInChildren<CharacterController>(true)) cc.enabled = false;
            // Nothing else may move the character: the shared walk graph or recorded pose drives it.
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
            var d = go.AddComponent<MocapAvatarDriver>();
            d.Init(rig, walkingClip);
            return d;
        }

        void Init(Avatar rig, AnimationClip walkingClip)
        {
            animator = GetComponentInChildren<Animator>();
            if (!animator) animator = gameObject.AddComponent<Animator>();
            // Keep the Avatar authored with the visible character prefab. The separately
            // assigned Avatar is from a duplicate ActorCore import and was opening the arms
            // on retarget; use it only if the prefab has no humanoid Avatar of its own.
            if ((!animator.avatar || !animator.avatar.isHuman) && rig) animator.avatar = rig;
            if (!animator.avatar || !animator.avatar.isHuman) { Debug.LogError("[MocapAvatar] The avatar prefab needs a humanoid Avatar.", this); return; }
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.enabled = true; animator.Rebind();
            targetHumanScale = animator.humanScale;
            targetHips = animator.GetBoneTransform(HumanBodyBones.Hips);
            animator.enabled = false;
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.updateWhenOffscreen = true;
            target = new HumanPoseHandler(animator.avatar, animator.transform);
            baseScale = transform.localScale;
            targetLegLength = Leg(animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg), animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg),
                                  animator.GetBoneTransform(HumanBodyBones.LeftFoot));
            Valid = targetHumanScale > 0f && targetHips;
            if (Valid && walkingClip)
            {
                animationClip = walkingClip;
                useAnimationClip = true;
                sampledBones = new Transform[23];
                animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animationGraph = PlayableGraph.Create("Kevin walk on " + name);
                animationGraph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                var output = AnimationPlayableOutput.Create(animationGraph, "Kevin walk", animator);
                animationPlayable = AnimationClipPlayable.Create(animationGraph, animationClip);
                animationPlayable.SetApplyFootIK(true);
                output.SetSourcePlayable(animationPlayable);
                animationGraph.Play();
                CacheSampledBones();
                Debug.Log($"[MocapAvatar] Using shared Kevin walk clip '{walkingClip.name}' for all experiment appearances.", this);
            }
            Debug.Log($"[MocapAvatar] target '{animator.name}' human={animator.avatar.isHuman} humanScale={targetHumanScale:0.000} hips={(targetHips ? targetHips.name : "none")} valid={Valid}");
        }

        Transform Bone(HumanBodyBones bone) => animator.GetBoneTransform(bone);
        void CacheSampledBones()
        {
            sampledBones[MocapClipLibrary.Hips] = Bone(HumanBodyBones.Hips);
            sampledBones[MocapClipLibrary.Spine] = Bone(HumanBodyBones.Spine);
            sampledBones[MocapClipLibrary.Spine1] = Bone(HumanBodyBones.Chest);
            sampledBones[MocapClipLibrary.Neck] = Bone(HumanBodyBones.Neck) ?? Bone(HumanBodyBones.Chest);
            sampledBones[MocapClipLibrary.Neck1] = Bone(HumanBodyBones.Head);
            sampledBones[MocapClipLibrary.Head] = Bone(HumanBodyBones.Head);
            sampledBones[MocapClipLibrary.HeadTop] = Bone(HumanBodyBones.Head);
            sampledBones[MocapClipLibrary.LShoulder] = Bone(HumanBodyBones.LeftShoulder) ?? Bone(HumanBodyBones.LeftUpperArm);
            sampledBones[MocapClipLibrary.LElbow] = Bone(HumanBodyBones.LeftLowerArm);
            sampledBones[MocapClipLibrary.LWrist] = Bone(HumanBodyBones.LeftHand);
            sampledBones[MocapClipLibrary.LFinger] = Bone(HumanBodyBones.LeftIndexProximal) ?? Bone(HumanBodyBones.LeftHand);
            sampledBones[MocapClipLibrary.RShoulder] = Bone(HumanBodyBones.RightShoulder) ?? Bone(HumanBodyBones.RightUpperArm);
            sampledBones[MocapClipLibrary.RElbow] = Bone(HumanBodyBones.RightLowerArm);
            sampledBones[MocapClipLibrary.RWrist] = Bone(HumanBodyBones.RightHand);
            sampledBones[MocapClipLibrary.RFinger] = Bone(HumanBodyBones.RightIndexProximal) ?? Bone(HumanBodyBones.RightHand);
            sampledBones[MocapClipLibrary.LHip] = Bone(HumanBodyBones.LeftUpperLeg);
            sampledBones[MocapClipLibrary.LKnee] = Bone(HumanBodyBones.LeftLowerLeg);
            sampledBones[MocapClipLibrary.LAnkle] = Bone(HumanBodyBones.LeftFoot);
            sampledBones[MocapClipLibrary.LToe] = Bone(HumanBodyBones.LeftToes) ?? Bone(HumanBodyBones.LeftFoot);
            sampledBones[MocapClipLibrary.RHip] = Bone(HumanBodyBones.RightUpperLeg);
            sampledBones[MocapClipLibrary.RKnee] = Bone(HumanBodyBones.RightLowerLeg);
            sampledBones[MocapClipLibrary.RAnkle] = Bone(HumanBodyBones.RightFoot);
            sampledBones[MocapClipLibrary.RToe] = Bone(HumanBodyBones.RightToes) ?? Bone(HumanBodyBones.RightFoot);
        }

        public void SetVisible(bool visible)
        {
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
                renderer.enabled = visible;
        }

        public void SampleJointPositions(Vector3[] positions)
        {
            if (!useAnimationClip || sampledBones == null || positions == null || positions.Length < sampledBones.Length) return;
            for (int i = 0; i < sampledBones.Length; i++)
                positions[i] = sampledBones[i] ? sampledBones[i].position : transform.position;
            if (sampledBones[MocapClipLibrary.HeadTop])
                positions[MocapClipLibrary.HeadTop] += transform.up * .11f * transform.lossyScale.y;
        }

        SourceRig RigFor(MocapClipLibrary.Skeleton sk)
        {
            if (rigs.TryGetValue(sk.id, out var r)) return r;
            r = new SourceRig();
            var rootGo = new GameObject("CMU source skeleton " + sk.id);
            rootGo.hideFlags = HideFlags.HideInHierarchy;
            r.root = rootGo.transform;
            int n = sk.names.Length;
            r.bones = new Transform[n]; r.local = new Quaternion[n];
            for (int j = 0; j < n; j++)
            {
                var b = new GameObject(sk.names[j]).transform;
                b.SetParent(sk.parents[j] < 0 ? r.root : r.bones[sk.parents[j]], false);
                b.localPosition = new Vector3(sk.offsets[3 * j], sk.offsets[3 * j + 1], sk.offsets[3 * j + 2]);
                b.localRotation = Quaternion.identity;
                r.bones[j] = b;
            }
            Transform B(string name) { int i = System.Array.IndexOf(sk.names, name); return i >= 0 ? r.bones[i] : null; }

            // The recorded rest pose has the legs ~20° apart; give the Avatar a proper T-pose (rest only).
            void Straighten(string bone, string child, Vector3 dir)
            {
                var a = B(bone); var c = B(child);
                if (!a || !c) return;
                Vector3 cur = c.position - a.position;
                if (cur.sqrMagnitude > 1e-10f) a.rotation = Quaternion.FromToRotation(cur, dir) * a.rotation;
            }
            Straighten("Spine", "Spine1", Vector3.up); Straighten("Spine1", "Neck1", Vector3.up); Straighten("Neck1", "Head", Vector3.up);
            Straighten("LeftUpLeg", "LeftLeg", Vector3.down); Straighten("LeftLeg", "LeftFoot", Vector3.down); Straighten("LeftFoot", "LeftToeBase", Vector3.forward);
            Straighten("RightUpLeg", "RightLeg", Vector3.down); Straighten("RightLeg", "RightFoot", Vector3.down); Straighten("RightFoot", "RightToeBase", Vector3.forward);
            Straighten("LeftArm", "LeftForeArm", Vector3.left); Straighten("LeftForeArm", "LeftHand", Vector3.left); Straighten("LeftHand", "LeftHandIndex1", Vector3.left);
            Straighten("RightArm", "RightForeArm", Vector3.right); Straighten("RightForeArm", "RightHand", Vector3.right); Straighten("RightHand", "RightHandIndex1", Vector3.right);

            // Stand the T-pose on the ground: the hips must sit at their real height above the feet, otherwise Unity
            // computes a near-zero (even negative) human scale and the retargeted character collapses.
            float lowest = 0f;
            foreach (var foot in new[] { "LeftFoot", "RightFoot", "LeftToeBase", "RightToeBase" })
                if (B(foot)) lowest = Mathf.Min(lowest, B(foot).position.y - r.root.position.y);
            r.bones[0].localPosition = new Vector3(0f, -lowest, 0f);
            r.legLength = Leg(B("LeftUpLeg"), B("LeftLeg"), B("LeftFoot"));

            var desc = new HumanDescription
            {
                upperArmTwist = .5f, lowerArmTwist = .5f, upperLegTwist = .5f, lowerLegTwist = .5f,
                armStretch = .05f, legStretch = .05f, feetSpacing = 0f, hasTranslationDoF = false
            };
            var skel = new List<SkeletonBone> { new SkeletonBone { name = rootGo.name, position = Vector3.zero, rotation = Quaternion.identity, scale = Vector3.one } };
            foreach (var b in r.bones) skel.Add(new SkeletonBone { name = b.name, position = b.localPosition, rotation = b.localRotation, scale = Vector3.one });
            desc.skeleton = skel.ToArray();
            var human = new List<HumanBone>();
            foreach (var (cmu, hb) in Map)
                if (B(cmu)) human.Add(new HumanBone { boneName = cmu, humanName = HumanTrait.BoneName[(int)hb], limit = new HumanLimit { useDefaultValues = true } });
            desc.human = human.ToArray();
            var avatar = AvatarBuilder.BuildHumanAvatar(rootGo, desc);
            if (!avatar || !avatar.isValid || !avatar.isHuman) { Debug.LogError("[MocapAvatar] Could not build a humanoid Avatar for CMU subject " + sk.id); return rigs[sk.id] = null; }
            avatar.name = "CMU subject " + sk.id;
            var srcAnimator = rootGo.AddComponent<Animator>();
            srcAnimator.avatar = avatar; srcAnimator.Rebind();
            r.humanScale = srcAnimator.humanScale;
            srcAnimator.enabled = false;
            r.handler = new HumanPoseHandler(avatar, r.root);
            Debug.Log($"[MocapAvatar] CMU subject {sk.id} source avatar valid={avatar.isValid} humanScale={r.humanScale:0.000} hipsHeight={-lowest:0.000} leg={r.legLength:0.000} (target leg {targetLegLength:0.000})");
            rigs[sk.id] = r;
            return r;
        }

        /// <summary>Prepare the shared Kevin gait and pace it to this recorded trial trajectory.</summary>
        public bool Prepare(MocapClipLibrary.Clip clip)
        {
            if (useAnimationClip)
            {
                transform.localScale = baseScale;
                // Keep Kevin's authored gait cadence. Scaling its cycle by CMU root speed
                // made the limbs move in slow motion while the avatar still travelled normally.
                currentAnimationRate = 1f;
                Debug.Log($"[MocapAvatar] Kevin gait at authored rate for CMU path '{clip.info.id}'.");
                return Valid;
            }
            current = Valid && clip.HasRotations ? RigFor(clip.skeleton) : null;
            if (current == null) return false;
            // Same body size as the recorded walker. Human scales are the reference; leg lengths are the fallback.
            float ratio = current.humanScale / Mathf.Max(1e-4f, targetHumanScale);
            if (!(ratio > .5f && ratio < 2f) && targetLegLength > 1e-4f) ratio = current.legLength / targetLegLength;
            if (!(ratio > .5f && ratio < 2f)) { Debug.LogError($"[MocapAvatar] Implausible size ratio {ratio:0.000}; using 1"); ratio = 1f; }
            transform.localScale = baseScale * ratio;
            logFrames = 3;
            return true;
        }

        float EstimateWalkRate(MocapClipLibrary.Clip clip)
        {
            if (clip == null || clip.Duration <= 0f || clip.info.frames < 2) return .5f;
            var joints = new Vector3[clip.jointCount];
            int steps = Mathf.Clamp(Mathf.CeilToInt(clip.Duration * 30f), 2, 120);
            clip.Sample(0f, joints);
            Vector3 previous = joints[MocapClipLibrary.Hips];
            previous.y = 0f;
            float distance = 0f;
            for (int i = 1; i <= steps; i++)
            {
                clip.Sample(clip.Duration * i / steps, joints);
                Vector3 currentPosition = joints[MocapClipLibrary.Hips];
                currentPosition.y = 0f;
                distance += Vector3.Distance(previous, currentPosition);
                previous = currentPosition;
            }
            float pathSpeed = distance / clip.Duration;
            // The Kevin pack's forward walk is specified at 2 m/s. Scale its gait cycle to
            // the actual CMU trajectory speed so the feet cadence and root path stay aligned.
            return Mathf.Clamp(pathSpeed / 2f, .3f, 1.1f);
        }

        /// <summary>Pose the character at clip time t (s). origin/rotation place the clip's canonical frame in the world.</summary>
        public void SetPose(MocapClipLibrary.Clip clip, float t, Vector3 origin, Quaternion rotation, Vector3 recordedHipsWorld)
        {
            if (useAnimationClip)
            {
                if (!animationGraph.IsValid() || !animationClip) return;
                animator.transform.SetPositionAndRotation(origin, rotation);
                float clipTime = Mathf.Repeat(t * currentAnimationRate, Mathf.Max(.001f, animationClip.length));
                animationPlayable.SetTime(clipTime);
                animationGraph.Evaluate(0f);
                Vector3 delta = recordedHipsWorld - targetHips.position;
                delta.y = 0f;
                animator.transform.position += delta;
                return;
            }
            if (current == null) return;
            current.root.SetPositionAndRotation(origin, rotation);
            clip.SampleRotations(t, current.local);
            for (int j = 0; j < current.bones.Length; j++) current.bones[j].localRotation = current.local[j];
            current.bones[0].position = recordedHipsWorld;
            current.handler.GetHumanPose(ref pose);
            animator.transform.SetPositionAndRotation(origin, rotation);
            target.SetHumanPose(ref pose);
            // Keep the pelvis exactly on the recorded path (proportions differ slightly between bodies).
            Vector3 d = recordedHipsWorld - targetHips.position; d.y = 0f;
            animator.transform.position += d;
            if (logFrames > 0)
            {
                logFrames--;
                var rend = GetComponentInChildren<SkinnedMeshRenderer>();
                Debug.Log($"[MocapAvatar] t={t:0.00} active={gameObject.activeInHierarchy} scale={transform.lossyScale.x:0.000} " +
                          $"recordedHips={recordedHipsWorld} avatarHips={targetHips.position} root={animator.transform.position} " +
                          $"bodyPos={pose.bodyPosition} renderer={(rend ? rend.enabled + " " + rend.bounds.center : "none")}");
            }
        }
        int logFrames;

        void OnDestroy()
        {
            if (animationGraph.IsValid()) animationGraph.Destroy();
        }
    }
}
