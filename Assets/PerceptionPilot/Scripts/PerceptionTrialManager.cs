using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using PassthroughCameraSamples.MultiObjectDetection;
using WalkingEyeExperiment;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace PerceptionPilot
{
    [Serializable] public class MarkerEvent : UnityEvent<string> { }

    /// <summary>
    /// Simulated-RP perception pilot (standing observer, passthrough).
    ///
    /// Each trial: real recorded take, real root motion. The start position is computed so the avatar is
    /// exactly <see cref="cutoffDistance"/> straight ahead of the participant at the moment it disappears,
    /// whatever the cutoff. Start distance is jittered by a random extra lead-in so it cannot reveal the cutoff.
    ///
    /// Design: 4 conditions (Realistic/PointLight x Full/SimulatedRP) in a balanced Latin square by participant ID,
    /// each split into sub-blocks. Within a condition: every maneuver x cutoff x repetition, takes balanced
    /// (each take used equally often per condition), order randomised. Catch trials show the full maneuver.
    /// </summary>
    public class PerceptionTrialManager : MonoBehaviour
    {
        [Header("References")]
        public ManeuverLibrary library;
        [Tooltip("Realistic avatar (recorded motion retargeted onto the character).")]
        public AvatarStimulus avatar;
        [Tooltip("Real biological motion: dots at the recorded walker's joint positions.")]
        public RealPointLightPlayer realPointLight;
        [Tooltip("Point-light trials use only takes that have recorded joint data. If off, takes without it fall back to dots on the avatar's bones (logged as 'avatar_bones').")]
        public bool requireRealBiologicalMotion = true;
        public ResponseCollector response;
        [Tooltip("OVRCameraRig > TrackingSpace > CenterEyeAnchor")]
        public Transform head;
        public TMP_Text promptText;
        [Tooltip("Optional, practice/catch stairs-up trials only. Pivot = front edge of the first step at floor level, +Z = walking direction.")]
        public GameObject stairsUpVisual;
        [Tooltip("Optional, practice/catch stairs-down trials only. Pivot = top edge of the first step down at floor level, +Z = walking direction.")]
        public GameObject stairsDownVisual;

        [Header("Session")]
        public int participantId = 1;
        public int extraSeed = 0;
        [Tooltip("Resume after an interruption: first MAIN block to run (0 = start). The design is identical because it is generated from the participant ID.")]
        public int startAtBlock = 0;
        public bool runPractice = true;
        public bool autoStart = true;

        [Header("Geometry")]
        [Tooltip("World Y of the real floor (0 when the OVR tracking origin type is Floor Level).")]
        public float floorY = 0f;
        [Tooltip("Distance (m) from the participant's standing point to the avatar when it disappears. Same for every cutoff.")]
        public float cutoffDistance = 4f;
        [Tooltip("Seconds of walking shown before the placement time (T0 + cutoff).")]
        public float leadInSeconds = 2f;
        [Tooltip("Random extra lead-in 0..this (s): varies start distance; cutoff distance stays fixed.")]
        public float leadInJitter = 0.4f;
        [Tooltip("First part of the shown walking, used to align the avatar's straight walking direction with the participant's forward.")]
        public float baselineSeconds = 0.8f;

        [Header("Design")]
        public int[] cutoffsMs = { -500, -250, 0 };
        public int repetitions = 2;
        public int subBlocksPerCondition = 2;
        public int catchTrialsPerSubBlock = 2;
        public int practiceTrials = 9;
        public float itiSeconds = 1.0f;
        public float practiceFeedbackSeconds = 1.5f;

        [Header("Existing project integration")]
        [Tooltip("The project's low-vision tunnel. Full field / simulated RP switch its vision type.")]
        public LowVisionOverlayController rpOverlay;
        [Tooltip("Tunnel vision type for the simulated-RP condition (Glaucoma or Severe; both use the overlay's degree-accurate tunnel). Full field = None.")]
        public VisionType simulatedRpVision = VisionType.Glaucoma;
        [Tooltip("The project's per-frame research logger (Neon gaze, body-region hits). Gets trial start / response / end.")]
        public ResearchSessionLogger researchLogger;

        [Header("Extra field condition hooks (optional)")]
        public UnityEvent onFullField;
        public UnityEvent onSimulatedRPField;

        [Header("Event markers - e.g. forward to Pupil Labs as annotations")]
        public MarkerEvent onMarker;

        // Balanced (Williams) Latin square over conditions 0..3.
        static readonly int[][] Williams =
        {
            new[] { 0, 1, 3, 2 }, new[] { 1, 2, 0, 3 }, new[] { 2, 3, 1, 0 }, new[] { 3, 0, 2, 1 }
        };
        static AppearanceMode CondAvatar(int c) => c < 2 ? AppearanceMode.Realistic : AppearanceMode.PointLight;
        static FieldCondition CondField(int c) => (c % 2 == 0) ? FieldCondition.Full : FieldCondition.SimulatedRP;

        class Trial { public ManeuverEntry entry; public int cutoffMs; public bool isCatch; public bool isPractice; }
        class Block { public string label; public int condition; public bool practice; public List<Trial> trials = new List<Trial>(); }

        System.Random _rng;
        int _seed;
        TrialLogger _log;
        Vector3 _standPos, _standFwd;
        int _trialGlobal;
        bool _responded;
        ResponseCollector.Result _resp;

        void Start()
        {
            HideStairs();
            if (autoStart) StartCoroutine(RunSession());
        }

        void OnDestroy() { _log?.Dispose(); }

        [ContextMenu("Start session")]
        public void StartSession()
        {
            StopAllCoroutines();
            _log?.Dispose(); _log = null;
            StartCoroutine(RunSession());
        }

        // ------------------------------------------------------------------ session

        IEnumerator RunSession()
        {
            if (!Validate()) yield break;

            _seed = participantId * 7919 + extraSeed;
            _rng = new System.Random(_seed);
            var blocks = BuildDesign();
            if (blocks.Count == 0) { Prompt("No usable takes in the library."); yield break; }
            LogSummary(blocks);
            _log = new TrialLogger(participantId);
            if (researchLogger != null && !researchLogger.Recording)
            {
                researchLogger.participantId = "P" + participantId.ToString("D3");
                researchLogger.StartRecording();
            }

            yield return CalibrateStandPoint();

            int mainCount = 0;
            foreach (var b in blocks) if (!b.practice) mainCount++;

            int mainIndex = -1;
            foreach (var blk in blocks)
            {
                if (!blk.practice) mainIndex++;
                if (blk.practice && (!runPractice || startAtBlock > 0)) continue;
                if (!blk.practice && mainIndex < startAtBlock) continue;

                SetCondition(blk.condition);
                Prompt(blk.practice
                    ? "Practice\n\nPull the trigger to start"
                    : $"Block {mainIndex + 1} of {mainCount}\n\nRest as long as you like.\nPull the trigger to start");
                yield return WaitForTrigger();
                Prompt("");
                Marker("block_start:" + blk.label);

                for (int i = 0; i < blk.trials.Count; i++)
                    yield return RunTrial(blk, blk.practice ? -1 : mainIndex, i);

                Marker("block_end:" + blk.label);
            }

            avatar.StopAndHide();
            if (realPointLight != null) realPointLight.StopAndHide();
            Prompt("Finished - thank you!");
            Marker("session_end");
            _log.Dispose(); _log = null;
            if (researchLogger != null) researchLogger.StopRecording();
        }

        IEnumerator CalibrateStandPoint()
        {
            Prompt("Stand on the marked spot and look straight ahead.\n\nPull the trigger.");
            yield return WaitForTrigger();
            _standPos = head.position; _standPos.y = floorY;
            _standFwd = head.forward; _standFwd.y = 0f;
            if (_standFwd.sqrMagnitude < 1e-4f) _standFwd = Vector3.forward;
            _standFwd.Normalize();
            Prompt("");
            Marker("calibrated");
        }

        // ------------------------------------------------------------------ one trial

        IEnumerator RunTrial(Block blk, int blockIndex, int idx)
        {
            var tr = blk.trials[idx];
            var e = tr.entry;
            var clip = e.clip;
            int trialNo = ++_trialGlobal;
            float itiStart = Time.time;

            avatar.StopAndHide();
            if (realPointLight != null) realPointLight.StopAndHide();
            HideStairs();

            // Stimulus source: realistic avatar, real recorded joints, or (fallback) dots on avatar bones.
            bool pointLightCond = CondAvatar(blk.condition) == AppearanceMode.PointLight;
            PointLightTrajectory traj = null;
            if (pointLightCond && realPointLight != null && e.HasRealPointLight) traj = realPointLight.Load(e);
            bool usePld = traj != null;
            string source = !pointLightCond ? "avatar" : usePld ? "recorded_joints" : "avatar_bones";

            // Times (seconds in the source's own timeline)
            double t0 = usePld ? e.t0PointLight : e.t0;
            double duration = usePld ? traj.Duration : clip.length;
            double tPlace = tr.isCatch ? t0 : t0 + tr.cutoffMs / 1000.0;      // stimulus is at cutoffDistance here
            double tEnd = tr.isCatch ? duration - 0.02 : tPlace;              // last frame shown
            double jitter = _rng.NextDouble() * leadInJitter;
            double tStart = Math.Max(0.0, tPlace - leadInSeconds - jitter);
            double tBase = Math.Min(tStart + baselineSeconds, t0 - 0.15);
            if (tBase <= tStart + 0.2) tBase = Math.Min(tStart + 0.4, tPlace);

            // Pre-run to find where the recording takes the walker.
            var sim = usePld ? realPointLight.Simulate(traj, tStart, tBase, t0, tPlace)
                             : avatar.Simulate(clip, tStart, tBase, t0, tPlace);

            // Rotate so the avatar's initial straight walking points along the participant's forward,
            // then translate so it is exactly cutoffDistance straight ahead at tPlace.
            Vector3 bdir = sim.posAtBaselineEnd; bdir.y = 0f;
            float simYaw = bdir.sqrMagnitude > 0.01f ? Mathf.Atan2(bdir.x, bdir.z) * Mathf.Rad2Deg : 0f;
            float standYaw = Mathf.Atan2(_standFwd.x, _standFwd.z) * Mathf.Rad2Deg;
            Quaternion rot = Quaternion.Euler(0f, standYaw - simYaw, 0f);

            Vector3 target = _standPos + _standFwd * cutoffDistance; target.y = floorY;
            Vector3 placed = rot * sim.posAtPlacement;
            Vector3 startPos = target - new Vector3(placed.x, 0f, placed.z);
            startPos.y = floorY;

            if (tr.isCatch && ManeuverInfo.IsStairs(e.type))
            {
                var vis = e.type == ManeuverType.StairsUp ? stairsUpVisual : stairsDownVisual;
                if (vis != null)
                {
                    Vector3 p = startPos + rot * sim.posAtT0 + _standFwd * e.stairsForwardOffset;
                    p.y = floorY;
                    vis.transform.SetPositionAndRotation(p, Quaternion.LookRotation(_standFwd, Vector3.up));
                    vis.SetActive(true);
                }
            }

            float remain = itiSeconds - (Time.time - itiStart);
            if (remain > 0f) yield return new WaitForSeconds(remain);

            // Stimulus
            var trueCatForLog = ManeuverInfo.Category(e.type);
            if (researchLogger != null)
            {
                researchLogger.avatar = usePld ? realPointLight.RootProxy : avatar.transform;
                researchLogger.BeginTrial(
                    $"P{participantId:D3}_T{trialNo:D4}",
                    $"{CondAvatar(blk.condition)}|{CondField(blk.condition)}|{source}|{e.type}",
                    $"{clip.name}|{e.walkerId}|take{e.take}",
                    tr.isCatch ? -1f : tr.cutoffMs / 1000f,
                    trueCatForLog.ToString());
            }

            double stimOnRt = Time.realtimeSinceStartupAsDouble; string stimOnUtc = Utc();
            if (usePld) realPointLight.BeginPlayback(traj, startPos, rot, tStart, tEnd);
            else avatar.BeginPlayback(clip, startPos, rot, tStart, tEnd);
            Marker("stim_on:" + trialNo);
            while (usePld ? realPointLight.IsPlaying : avatar.IsPlaying) yield return null;
            double cutRt = Time.realtimeSinceStartupAsDouble; string cutUtc = Utc();
            Marker("cutoff:" + trialNo);
            HideStairs();

            Vector3 last = usePld ? realPointLight.LastShownPosition : avatar.LastShownPosition;
            Vector3 rel = last - _standPos; rel.y = 0f;
            float actualD = Vector3.Dot(rel, _standFwd);
            float actualLat = Vector3.Dot(rel, Vector3.Cross(Vector3.up, _standFwd)); // + = right

            // Response (angles measured from where the avatar was at the placement time)
            Vector3 origin = tr.isCatch ? target : last;
            _responded = false;
            response.Begin(origin, _standFwd, floorY, r => { _resp = r; _responded = true; });
            while (!_responded) yield return null;
            double respRt = Time.realtimeSinceStartupAsDouble; string respUtc = Utc();
            Marker("response:" + trialNo);
            if (researchLogger != null) researchLogger.RecordResponse(_resp.category.ToString());

            var trueCat = ManeuverInfo.Category(e.type);
            float trueAngle = ManeuverInfo.TrueAngle(e.type);
            bool correct = _resp.category == trueCat;
            float angErr = (_resp.hasAngle && !ManeuverInfo.IsStairs(e.type)) ? _resp.angleDeg - trueAngle : float.NaN;

            _log.Write(
                participantId, _seed, blockIndex, blk.label,
                CondAvatar(blk.condition).ToString(), CondField(blk.condition).ToString(),
                idx + 1, trialNo, tr.isPractice, tr.isCatch, source,
                e.type.ToString(), trueCat.ToString(), trueAngle, clip.name, e.walkerId, e.take,
                t0, e.t0IsPseudo, tr.isCatch ? (object)"full" : (object)tr.cutoffMs,
                tStart, tEnd, tPlace - tStart,
                cutoffDistance, actualD, actualLat,
                _resp.category.ToString(), _resp.hasAngle, _resp.angleDeg, correct, angErr, _resp.rtSeconds,
                stimOnRt, cutRt, respRt, stimOnUtc, cutUtc, respUtc);

            if (tr.isPractice)
            {
                Prompt(correct ? "Correct" : "It was: " + Describe(e.type));
                yield return new WaitForSeconds(practiceFeedbackSeconds);
                Prompt("");
            }
            if (researchLogger != null) researchLogger.EndTrial();
        }

        // ------------------------------------------------------------------ design

        List<Block> BuildDesign()
        {
            var blocks = new List<Block>();
            var takes = new Dictionary<ManeuverType, List<ManeuverEntry>>();
            var types = new List<ManeuverType>();
            foreach (var m in ManeuverInfo.Experimental)
            {
                var l = library.UsableOf(m);
                if (l.Count == 0) { Debug.LogWarning("[PerceptionPilot] No usable takes for " + m + " - skipped."); continue; }
                takes[m] = l; types.Add(m);
            }
            if (types.Count == 0) return blocks;

            // Practice is always generated (keeps the random design identical when resuming).
            var pb = new Block { label = "practice", condition = 0, practice = true };
            for (int i = 0; i < practiceTrials; i++)
            {
                var l = takes[types[i % types.Count]];
                pb.trials.Add(new Trial { entry = l[_rng.Next(l.Count)], isCatch = true, isPractice = true });
            }
            Shuffle(pb.trials);
            blocks.Add(pb);

            int row = ((participantId - 1) % 4 + 4) % 4;
            foreach (int cond in Williams[row])
            {
                var trials = new List<Trial>();
                bool needReal = requireRealBiologicalMotion && CondAvatar(cond) == AppearanceMode.PointLight;
                foreach (var m in types)
                {
                    var pool = new List<ManeuverEntry>();
                    foreach (var en in takes[m]) if (!needReal || en.HasRealPointLight) pool.Add(en);
                    if (pool.Count == 0)
                    {
                        Debug.LogWarning($"[PerceptionPilot] No takes with recorded joint data for {m}: skipped in {CondAvatar(cond)}_{CondField(cond)}.");
                        continue;
                    }
                    Shuffle(pool);
                    var cells = new List<int>();
                    foreach (int c in cutoffsMs) for (int r = 0; r < repetitions; r++) cells.Add(c);
                    Shuffle(cells);
                    for (int k = 0; k < cells.Count; k++)
                        trials.Add(new Trial { entry = pool[k % pool.Count], cutoffMs = cells[k] }); // takes balanced per condition
                }
                Shuffle(trials);

                int nSub = Mathf.Max(1, subBlocksPerCondition);
                for (int s = 0; s < nSub; s++)
                {
                    var blk = new Block { condition = cond, label = $"{CondAvatar(cond)}_{CondField(cond)}_{s + 1}" };
                    int from = trials.Count * s / nSub, to = trials.Count * (s + 1) / nSub;
                    for (int i = from; i < to; i++) blk.trials.Add(trials[i]);
                    var catchPool = new List<ManeuverEntry>();
                    foreach (var m in types) foreach (var en in takes[m]) if (!needReal || en.HasRealPointLight) catchPool.Add(en);
                    for (int c = 0; c < catchTrialsPerSubBlock && catchPool.Count > 0; c++)
                    {
                        blk.trials.Insert(_rng.Next(blk.trials.Count + 1),
                            new Trial { entry = catchPool[_rng.Next(catchPool.Count)], isCatch = true });
                    }
                    blocks.Add(blk);
                }
            }
            return blocks;
        }

        void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // ------------------------------------------------------------------ helpers

        bool Validate()
        {
            bool ok = true;
            if (library == null) { Debug.LogError("[PerceptionPilot] Assign a ManeuverLibrary."); ok = false; }
            if (avatar == null) { Debug.LogError("[PerceptionPilot] Assign the AvatarStimulus."); ok = false; }
            if (response == null) { Debug.LogError("[PerceptionPilot] Assign the ResponseCollector."); ok = false; }
            if (head == null) { Debug.LogError("[PerceptionPilot] Assign head (CenterEyeAnchor)."); ok = false; }
            if (!ok) Prompt("Setup error - see Console.");
            return ok;
        }

        void LogSummary(List<Block> blocks)
        {
            int total = 0, catches = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var b in blocks)
            {
                int c = 0; foreach (var t in b.trials) if (t.isCatch && !t.isPractice) c++;
                total += b.trials.Count; catches += c;
                sb.AppendLine($"  {b.label}: {b.trials.Count} trials ({c} catch)");
            }
            Debug.Log($"[PerceptionPilot] P{participantId} seed {_seed}: {blocks.Count} blocks, {total} trials incl. practice, {catches} catch\n{sb}");
        }

        void SetCondition(int c)
        {
            avatar.SetMode(CondAvatar(c));
            bool full = CondField(c) == FieldCondition.Full;
            if (rpOverlay != null && rpOverlay.overlayMaterial != null)
            {
                var vt = full ? VisionType.None : simulatedRpVision;
                rpOverlay.currentVisionType = vt;
                rpOverlay.ApplySettings(vt);
            }
            if (full) onFullField?.Invoke();
            else onSimulatedRPField?.Invoke();
            Marker($"condition:{CondAvatar(c)}_{CondField(c)}");
        }

        IEnumerator WaitForTrigger()
        {
            yield return null;
            while (true)
            {
                if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)) break;
#if ENABLE_INPUT_SYSTEM
                if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) break;
#endif
                yield return null;
            }
            yield return null;
        }

        void HideStairs()
        {
            if (stairsUpVisual != null) stairsUpVisual.SetActive(false);
            if (stairsDownVisual != null) stairsDownVisual.SetActive(false);
        }

        void Prompt(string s) { if (promptText != null) promptText.text = s; }

        void Marker(string label)
        {
            onMarker?.Invoke(label);
        }

        static string Utc() => DateTime.UtcNow.ToString("o");

        static string Describe(ManeuverType t)
        {
            switch (t)
            {
                case ManeuverType.Straight: return "straight";
                case ManeuverType.TurnLeft30: return "left 30°";
                case ManeuverType.TurnLeft60: return "left 60°";
                case ManeuverType.TurnLeft90: return "left 90°";
                case ManeuverType.TurnRight30: return "right 30°";
                case ManeuverType.TurnRight60: return "right 60°";
                case ManeuverType.TurnRight90: return "right 90°";
                case ManeuverType.StairsUp: return "stairs up";
                case ManeuverType.StairsDown: return "stairs down";
                default: return t.ToString();
            }
        }
    }
}
