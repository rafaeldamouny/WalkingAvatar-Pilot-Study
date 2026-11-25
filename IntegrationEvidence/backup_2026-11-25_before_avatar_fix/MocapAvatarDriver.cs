using System.Collections.Generic;
using UnityEngine;

namespace WalkingEyeExperiment
{
    /// <summary>
    /// Plays a recorded CMU walk on the project's humanoid character with Unity's humanoid (Mecanim) retargeting.
    /// A hidden copy of the recorded CMU skeleton is built at runtime for each subject, given a humanoid Avatar
    /// (T-pose from the recorded bone offsets), and posed every frame with the recorded joint rotations. Its HumanPose
    /// (muscle space) is applied to the character with HumanPoseHandler, the same retargeting Unity uses for mocap
    /// clips: bone twist, roll and joint limits are handled by the character's own Avatar. The character is scaled to
    /// the recorded walker's size, so its path, speed and floor contact match the point-light and stick figures.
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
            public Transform lArm, lFore, lHand, rArm, rFore, rHand;
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
        Renderer[] avatarRenderers;
        HumanPoseHandler target;
        HumanPose pose;
        float targetHumanScale = 1f;
        Vector3 baseScale;
        Transform targetHips, tLUpper, tLLower, tLHand, tRUpper, tRLower, tRHand;
        readonly Dictionary<string, SourceRig> rigs = new Dictionary<string, SourceRig>();
        SourceRig current;
        public bool Valid { get; private set; }

        [Tooltip("Minimum outward angle (deg) of each upper arm from the trunk, avatar only. The recorded walkers' arms hang ~2-3° from " +
                 "vertical, which is right for joints/sticks but sinks the arms into a character's hips and clothing. " +
                 "Swing timing and amplitude still come from the recording. Adjustable live in Play mode.")]
        [Range(0f, 25f)] public float armClearanceDeg = 10f;
        [Tooltip("Uniform size adjustment for the realistic avatar only; does not change the recorded motion.")]
        [Range(.75f, 1f)] public float realisticSizeScale = .90f;

        /// <summary>Kept for callers that still pass an animation clip; the recorded mocap is the only motion source.</summary>
        public static MocapAvatarDriver Create(GameObject prefab, Avatar rig, AnimationClip ignoredClip) => Create(prefab, rig);

        /// <summary>Show or hide the character's renderers (the object stays active so its pose stays current).</summary>
        public void SetVisible(bool visible)
        {
            if(avatarRenderers==null) avatarRenderers=GetComponentsInChildren<Renderer>(true);
            foreach (var r in avatarRenderers) if(r) r.enabled = visible;
        }

        public static MocapAvatarDriver Create(GameObject prefab, Avatar rig)
        {
            if (!prefab) return null;
            var go = Instantiate(prefab, Vector3.zero, Quaternion.identity);
            go.name = "Mocap Realistic Avatar";
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.useGravity = false; }
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var cc in go.GetComponentsInChildren<CharacterController>(true)) cc.enabled = false;
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
            // Nothing else may move the character: the recorded pose is the only driver.
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) mb.enabled = false;
            var d = go.AddComponent<MocapAvatarDriver>();
            d.Init(rig);
            return d;
        }

        void Init(Avatar rig)
        {
            avatarRenderers=GetComponentsInChildren<Renderer>(true);
            animator = GetComponentInChildren<Animator>();
            if (!animator) animator = gameObject.AddComponent<Animator>();
            if (rig) animator.avatar = rig;
            if (!animator.avatar || !animator.avatar.isHuman) { Debug.LogError("[MocapAvatar] The avatar prefab needs a humanoid Avatar.", this); return; }
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.enabled = true; animator.Rebind();
            targetHumanScale = animator.humanScale;
            targetHips = animator.GetBoneTransform(HumanBodyBones.Hips);
            tLUpper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm); tLLower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            tLHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            tRUpper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm); tRLower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            tRHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            Transform G(HumanBodyBones b) => animator.GetBoneTransform(b);
            tSpine = G(HumanBodyBones.Spine); tChest = G(HumanBodyBones.UpperChest) ? G(HumanBodyBones.UpperChest) : G(HumanBodyBones.Chest);
            tNeck = G(HumanBodyBones.Neck); tHead = G(HumanBodyBones.Head);
            tLUL = G(HumanBodyBones.LeftUpperLeg); tLLL = G(HumanBodyBones.LeftLowerLeg); tLFoot = G(HumanBodyBones.LeftFoot); tLToes = G(HumanBodyBones.LeftToes);
            tRUL = G(HumanBodyBones.RightUpperLeg); tRLL = G(HumanBodyBones.RightLowerLeg); tRFoot = G(HumanBodyBones.RightFoot); tRToes = G(HumanBodyBones.RightToes);
            tLFinger = G(HumanBodyBones.LeftMiddleProximal) ? G(HumanBodyBones.LeftMiddleProximal) : G(HumanBodyBones.LeftIndexProximal);
            tRFinger = G(HumanBodyBones.RightMiddleProximal) ? G(HumanBodyBones.RightMiddleProximal) : G(HumanBodyBones.RightIndexProximal);
            if (tHead) headUpLocal = tHead.InverseTransformDirection(Vector3.up);
            if (!(tLUL && tRUL && tLUpper && tRUpper)) matchRecordedSegments = false;
            animator.enabled = false;
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.updateWhenOffscreen = true;
            target = new HumanPoseHandler(animator.avatar, animator.transform);
            baseScale = transform.localScale;
            targetLegLength = Leg(animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg), animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg),
                                  animator.GetBoneTransform(HumanBodyBones.LeftFoot));
            Valid = targetHumanScale > 0f && targetHips;
            Debug.Log($"[MocapAvatar] target '{animator.name}' human={animator.avatar.isHuman} humanScale={targetHumanScale:0.000} hips={(targetHips ? targetHips.name : "none")} valid={Valid}");
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
            r.lArm = B("LeftArm"); r.lFore = B("LeftForeArm"); r.lHand = B("LeftHand");
            r.rArm = B("RightArm"); r.rFore = B("RightForeArm"); r.rHand = B("RightHand");

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

        static void Swing(Transform bone, Transform child, Transform srcBone, Transform srcChild)
        {
            if (!srcBone || !srcChild) return;
            Swing(bone, child, srcBone.position, srcChild.position);
        }

        static void Swing(Transform bone, Transform child, Vector3 from, Vector3 to)
        {
            if (!bone || !child) return;
            Vector3 cur = child.position - bone.position, want = to - from;
            if (cur.sqrMagnitude < 1e-10f || want.sqrMagnitude < 1e-10f) return;
            bone.rotation = Quaternion.FromToRotation(cur, want) * bone.rotation;
        }

        /// <summary>Prepare for a clip: pick its subject's source rig and scale the character to that walker.</summary>
        public bool Prepare(MocapClipLibrary.Clip clip)
        {
            current = Valid && clip.HasRotations ? RigFor(clip.skeleton) : null;
            if (current == null) return false;
            // Same leg length as the recorded walker, so each recorded stride fits the character's legs exactly and the
            // feet plant where the recorded feet plant (no foot sliding). Human scale is only the fallback.
            float ratio = targetLegLength > 1e-4f ? current.legLength / targetLegLength : current.humanScale / Mathf.Max(1e-4f, targetHumanScale);
            if (!(ratio > .5f && ratio < 2f)) { Debug.LogError($"[MocapAvatar] Implausible size ratio {ratio:0.000}; using 1"); ratio = 1f; }
            transform.localScale = baseScale * ratio * realisticSizeScale;
            return true;
        }

        /// <summary>Pose the character at clip time t (s). origin/rotation place the clip's canonical frame in the world.</summary>
        /// <summary>
        /// Pose the character at clip time t from the recorded rotations (twist, roll) and then match every body segment
        /// to the recorded joint centres w (world, MocapClipLibrary.JointNames order) — the same joints the point-light and
        /// stick figures draw — so the character moves exactly like them.
        /// </summary>
        public void SetPose(MocapClipLibrary.Clip clip, float t, Vector3 origin, Quaternion rotation, Vector3[] w)
        {
            SetPose(clip, t, origin, rotation, w[MocapClipLibrary.Hips]);
            if (current == null || !matchRecordedSegments) return;

            // Pelvis: recorded path, and a height scaled by leg length so the shorter/longer legs still reach the floor.
            float scaleRatio = transform.localScale.x / Mathf.Max(1e-6f, baseScale.x);
            float k = current.legLength > 1e-4f ? targetLegLength * scaleRatio / current.legLength : 1f;
            Vector3 pelvis = (w[MocapClipLibrary.LHip] + w[MocapClipLibrary.RHip]) * .5f;
            Vector3 avatarPelvis = (tLUL.position + tRUL.position) * .5f;
            Vector3 wantPelvis = new Vector3(pelvis.x, origin.y + (pelvis.y - origin.y) * k, pelvis.z);
            animator.transform.position += wantPelvis - avatarPelvis;

            // Pelvis orientation: hip line and trunk direction from the recording.
            Vector3 recAcross = w[MocapClipLibrary.RHip] - w[MocapClipLibrary.LHip];
            Vector3 recUp = (w[MocapClipLibrary.LShoulder] + w[MocapClipLibrary.RShoulder]) * .5f - pelvis;
            Vector3 curAcross = tRUL.position - tLUL.position;
            Vector3 curUp = (tLUpper.position + tRUpper.position) * .5f - (tLUL.position + tRUL.position) * .5f;
            if (recAcross.sqrMagnitude > 1e-8f && curAcross.sqrMagnitude > 1e-8f && recUp.sqrMagnitude > 1e-8f && curUp.sqrMagnitude > 1e-8f)
            {
                Quaternion want = Quaternion.LookRotation(Vector3.Cross(recAcross, recUp).normalized, recUp.normalized);
                Quaternion have = Quaternion.LookRotation(Vector3.Cross(curAcross, curUp).normalized, curUp.normalized);
                Vector3 keep = (tLUL.position + tRUL.position) * .5f;
                targetHips.rotation = want * Quaternion.Inverse(have) * targetHips.rotation;
                targetHips.position += keep - (tLUL.position + tRUL.position) * .5f;
            }

            // Trunk, neck and head, then the limbs, each swung onto its recorded segment.
            Vector3 shoulderMid = (w[MocapClipLibrary.LShoulder] + w[MocapClipLibrary.RShoulder]) * .5f;
            // Trunk as one straight segment along the recorded pelvis-to-shoulders axis (the recorded neck leans forward;
            // using it for the chest hunched the back).
            Swing(tSpine, tChest, pelvis, shoulderMid);
            if (tChest && tLUpper && tRUpper)
            {
                // Chest: recorded shoulder line, trunk axis as up.
                Vector3 ra = w[MocapClipLibrary.RShoulder] - w[MocapClipLibrary.LShoulder], ru = shoulderMid - pelvis;
                Vector3 ca = tRUpper.position - tLUpper.position, cu = (tNeck ? tNeck.position : tChest.position + Vector3.up) - tChest.position;
                if (ra.sqrMagnitude > 1e-8f && ru.sqrMagnitude > 1e-8f && ca.sqrMagnitude > 1e-8f && cu.sqrMagnitude > 1e-8f)
                {
                    Quaternion want = Quaternion.LookRotation(Vector3.Cross(ra, ru).normalized, ru.normalized);
                    Quaternion have = Quaternion.LookRotation(Vector3.Cross(ca, cu).normalized, cu.normalized);
                    tChest.rotation = want * Quaternion.Inverse(have) * tChest.rotation;
                }
            }
            Swing(tNeck, tHead, w[MocapClipLibrary.Neck1], w[MocapClipLibrary.Head]);
            if (tHead)
            {
                Vector3 cur = tHead.TransformDirection(headUpLocal), want = w[MocapClipLibrary.HeadTop] - w[MocapClipLibrary.Head];
                if (want.sqrMagnitude > 1e-8f) tHead.rotation = Quaternion.FromToRotation(cur, want) * tHead.rotation;
            }
            Swing(tLUL, tLLL, w[MocapClipLibrary.LHip], w[MocapClipLibrary.LKnee]);
            Swing(tLLL, tLFoot, w[MocapClipLibrary.LKnee], w[MocapClipLibrary.LAnkle]);
            Swing(tLFoot, tLToes, w[MocapClipLibrary.LAnkle], w[MocapClipLibrary.LToe]);
            Swing(tRUL, tRLL, w[MocapClipLibrary.RHip], w[MocapClipLibrary.RKnee]);
            Swing(tRLL, tRFoot, w[MocapClipLibrary.RKnee], w[MocapClipLibrary.RAnkle]);
            Swing(tRFoot, tRToes, w[MocapClipLibrary.RAnkle], w[MocapClipLibrary.RToe]);
            Swing(tLUpper, tLLower, w[MocapClipLibrary.LShoulder], w[MocapClipLibrary.LElbow]);
            Swing(tLLower, tLHand, Vector3.zero, Forearm(w[MocapClipLibrary.LShoulder], w[MocapClipLibrary.LElbow], w[MocapClipLibrary.LWrist]));
            Swing(tRUpper, tRLower, w[MocapClipLibrary.RShoulder], w[MocapClipLibrary.RElbow]);
            Swing(tRLower, tRHand, Vector3.zero, Forearm(w[MocapClipLibrary.RShoulder], w[MocapClipLibrary.RElbow], w[MocapClipLibrary.RWrist]));
            ArmClearance();
            Swing(tLFoot, tLToes, w[MocapClipLibrary.LAnkle], w[MocapClipLibrary.LToe]);
            Swing(tRFoot, tRToes, w[MocapClipLibrary.RAnkle], w[MocapClipLibrary.RToe]);
            // Keep the avatar's lowest toe on the same detected ground plane as the point/stick figures.
            float recordedGroundToe = Mathf.Min(w[MocapClipLibrary.LToe].y, w[MocapClipLibrary.RToe].y);
            float avatarGroundToe = Mathf.Min(tLToes ? tLToes.position.y : tLFoot.position.y,
                                              tRToes ? tRToes.position.y : tRFoot.position.y);
            animator.transform.position += Vector3.up * (recordedGroundToe - avatarGroundToe);
#if UNITY_EDITOR
            if (!debugCapture) debugCapture = gameObject.AddComponent<AvatarDebugCapture>();
            debugBones ??= new[] { tLUL, tLLL, tLLL, tLFoot, tLFoot, tLToes, tRUL, tRLL, tRLL, tRFoot, tRFoot, tRToes,
                                   tLUpper, tLLower, tLLower, tLHand, tRUpper, tRLower, tRLower, tRHand,
                                   targetHips, tSpine, tSpine, tChest, tChest, tNeck, tNeck, tHead };
            if (Time.unscaledTime >= nextErrLog && errLogs < 40)
            {
                nextErrLog = Time.unscaledTime + .25f; errLogs++;
                Debug.Log($"[MocapAvatar] t={t:0.00} scale={transform.lossyScale.x:0.000} footErr L={(tLFoot.position - w[MocapClipLibrary.LAnkle]).magnitude * 100f:0.0}cm " +
                          $"R={(tRFoot.position - w[MocapClipLibrary.RAnkle]).magnitude * 100f:0.0}cm kneeErr L={(tLLL.position - w[MocapClipLibrary.LKnee]).magnitude * 100f:0.0}cm " +
                          $"hipsErr={(((tLUL.position + tRUL.position) * .5f) - (w[MocapClipLibrary.LHip] + w[MocapClipLibrary.RHip]) * .5f).magnitude * 100f:0.0}cm " +
                          $"shoulderW avatar={(tRUpper.position - tLUpper.position).magnitude:0.00} rec={(w[MocapClipLibrary.RShoulder] - w[MocapClipLibrary.LShoulder]).magnitude:0.00}");
            }
            debugCapture.Capture(w, debugBones, t);
#endif
        }
#if UNITY_EDITOR
        AvatarDebugCapture debugCapture;
        float nextErrLog; int errLogs;
        Transform[] debugBones;
#endif

        [Tooltip("Match every body segment to the recorded joint centres after retargeting (recommended).")]
        public bool matchRecordedSegments = true;
        Transform tSpine, tChest, tNeck, tHead, tLUL, tLLL, tLFoot, tLToes, tRUL, tRLL, tRFoot, tRToes, tLFinger, tRFinger;
        Vector3 headUpLocal = Vector3.up;

        [Tooltip("Extra outward angle (deg) for the avatar's thighs only. The recorded walkers place their feet close to the " +
                 "midline, which on a character with wide hips and shoes looks like crossing legs.")]
        [Range(0f, 10f)] public float legClearanceDeg = 2.5f;

        void ArmClearance()
        {
            if (legClearanceDeg > 0f && tLUL && tRUL)
            {
                Vector3 f = Vector3.Cross(tRUL.position - tLUL.position, Vector3.up);
                if (f.sqrMagnitude > 1e-8f)
                {
                    f.Normalize();
                    tRUL.rotation = Quaternion.AngleAxis(legClearanceDeg, f) * tRUL.rotation;
                    tLUL.rotation = Quaternion.AngleAxis(-legClearanceDeg, f) * tLUL.rotation;
                }
            }
            if (armClearanceDeg <= 0f || !tLUpper || !tRUpper || !tLLower || !tRLower) return;
            Vector3 right = tRUpper.position - tLUpper.position;
            Vector3 up = (tLUpper.position + tRUpper.position) * .5f - (tLUL.position + tRUL.position) * .5f;
            if (right.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f) return;
            right.Normalize(); up.Normalize();
            Vector3 fwd = Vector3.Cross(right, up).normalized;
            // Each upper arm keeps at least armClearanceDeg outward from the trunk axis (seen from the front); arms already
            // further out are left as recorded. Rotation is about the body's forward axis.
            OutwardAtLeast(tRUpper, tRLower, right, up, fwd, +1f);
            OutwardAtLeast(tLUpper, tLLower, -right, up, fwd, -1f);
            // The recorded arms hang slightly behind the trunk; bring both upper arms forward by armForwardDeg
            // (about the body's left-right axis; negative angle swings a hanging arm forward). The swing is unchanged.
            if (armForwardDeg != 0f)
            {
                tRUpper.rotation = Quaternion.AngleAxis(-armForwardDeg, right) * tRUpper.rotation;
                tLUpper.rotation = Quaternion.AngleAxis(-armForwardDeg, right) * tLUpper.rotation;
            }
        }

        [Tooltip("Forward offset (deg) of the avatar's upper arms; the recorded arms hang slightly behind the trunk.")]
        [Range(-10f, 25f)] public float armForwardDeg = 10f;

        void OutwardAtLeast(Transform upper, Transform lower, Vector3 outward, Vector3 up, Vector3 fwd, float sign)
        {
            Vector3 a = lower.position - upper.position;
            float lateral = Vector3.Dot(a, outward), down = -Vector3.Dot(a, up);
            float angle = Mathf.Atan2(lateral, Mathf.Max(1e-4f, down)) * Mathf.Rad2Deg;
            if (angle >= armClearanceDeg) return;
            upper.rotation = Quaternion.AngleAxis(sign * (armClearanceDeg - angle), fwd) * upper.rotation;
        }

        [Tooltip("Fraction (0-1) by which the recorded elbow bend is reduced on the avatar (joint-centre data bends ~32°).")]
        [Range(0f, 1f)] public float elbowStraighten = .65f;
        [Tooltip("Finger curl for the avatar's hands (the recordings have no finger data). -1 = fist, 0 = Unity neutral, 1 = flat.")]
        [Range(-1f, 1f)] public float fingerCurl = -.35f;
        int[] fingerStretch, fingerSpread;

        /// <summary>Recorded forearm direction, with the elbow bend reduced by elbowStraighten.</summary>
        Vector3 Forearm(Vector3 shoulder, Vector3 elbow, Vector3 wrist)
        {
            Vector3 upperDir = (elbow - shoulder).normalized, fore = wrist - elbow;
            return Vector3.Slerp(fore.normalized, upperDir, elbowStraighten) * fore.magnitude;
        }

        public void SetPose(MocapClipLibrary.Clip clip, float t, Vector3 origin, Quaternion rotation, Vector3 recordedHipsWorld)
        {
            if (current == null) return;
            current.root.SetPositionAndRotation(origin, rotation);
            clip.SampleRotations(t, current.local);
            for (int j = 0; j < current.bones.Length; j++) current.bones[j].localRotation = current.local[j];
            current.bones[0].position = recordedHipsWorld;
            current.handler.GetHumanPose(ref pose);
            if (fingerStretch == null)
            {
                var st = new List<int>(); var sp = new List<int>();
                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                {
                    string n = HumanTrait.MuscleName[i];
                    bool finger = n.Contains("Thumb") || n.Contains("Index") || n.Contains("Middle") || n.Contains("Ring") || n.Contains("Little");
                    if (!finger) continue;
                    if (n.Contains("Stretched")) st.Add(i); else if (n.Contains("Spread")) sp.Add(i);
                }
                fingerStretch = st.ToArray(); fingerSpread = sp.ToArray();
            }
            if (pose.muscles != null)
            {
                foreach (int i in fingerStretch) pose.muscles[i] = fingerCurl;
                foreach (int i in fingerSpread) pose.muscles[i] = 0f;
            }
            animator.transform.SetPositionAndRotation(origin, rotation);
            target.SetHumanPose(ref pose);
            // Keep the pelvis exactly on the recorded path (proportions differ slightly between bodies).
            Vector3 d = recordedHipsWorld - targetHips.position; d.y = 0f;
            animator.transform.position += d;
            // The character's own rest pose has the arms below a true T-pose, so muscle-space retargeting lowers every
            // arm pose (arms pressed to the body). Swing the upper and lower arms onto the recorded arm directions;
            // the retargeted twist is kept.
            Swing(tLUpper, tLLower, current.lArm, current.lFore);
            Swing(tLLower, tLHand, current.lFore, current.lHand);
            Swing(tRUpper, tRLower, current.rArm, current.rFore);
            Swing(tRLower, tRHand, current.rFore, current.rHand);
            if (armClearanceDeg > 0f && tLUpper && tRUpper)
            {
                Vector3 across = tRUpper.position - tLUpper.position;
                Vector3 fwd = Vector3.Cross(across, Vector3.up);
                if (fwd.sqrMagnitude > 1e-8f)
                {
                    fwd.Normalize();
                    // About the body's forward axis: + moves a hanging arm toward the body's right, - toward its left.
                    tRUpper.rotation = Quaternion.AngleAxis(armClearanceDeg, fwd) * tRUpper.rotation;
                    tLUpper.rotation = Quaternion.AngleAxis(-armClearanceDeg, fwd) * tLUpper.rotation;
                }
            }
        }
    }
}
