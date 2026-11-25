using System;
using System.Collections;
using System.Collections.Generic;
using PerceptionPilot;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace WalkingEyeExperiment
{
    /// <summary>
    /// Perception session. Every trial plays one recorded human walk (CMU motion capture, Resources/Mocap): the
    /// point-light and stick figures are drawn from the recorded joint centres, and the realistic avatar is posed from
    /// the recorded joint rotations by humanoid retargeting. All three show the same recorded motion.
    /// </summary>
    public sealed class PerceptionPilotDemoRunner : MonoBehaviour
    {
        public GameObject avatarPrefab;
        public Avatar avatarRig;
        public AnimationClip kevinWalkClip;
        public Transform head;
        public Transform rightController;
        [Tooltip("Center of the corridor T junction. Turning clips are aligned so T0 occurs here.")]
        public Transform corridorTJunction;
        public LowVisionOverlayController overlay;
        public ResearchSessionLogger researchLogger;
        public int participantNumber = 1;
        public float floorY;
        public bool useConfirmedFloor;
        public float confirmedFloorY;
        public float cutoffDistance = 4f;
        [Tooltip("Straight walks stop this far before the T junction at T0.")]
        public float straightStopBeforeJunction = .75f;
        public float leadInSeconds = 2f;
        public float leadInJitter = .4f;
        public int repetitions = 2;
        [Tooltip("Practice trials; capped to one per available maneuver when maneuvers are missing.")]
        public int practiceTrials = 9;
        public int catchTrialsPerSubBlock = 2;
        public int subBlocksPerCondition = 2;
        public bool logGazeSamples;
        public bool tunnelSimulationEnabled = true;

        public bool IsRunning { get; private set; }
        public event Action Finished;

        readonly ManeuverType[] maneuvers = {
            ManeuverType.Straight,
            ManeuverType.TurnLeft30, ManeuverType.TurnLeft60, ManeuverType.TurnLeft90,
            ManeuverType.TurnRight30, ManeuverType.TurnRight60, ManeuverType.TurnRight90,
            ManeuverType.StairsUp, ManeuverType.StairsDown
        };
        readonly int[] cutoffs = { -500, -250, 0 };
        // The main experiment presents each of the three appearances under simulated RP.
        // Appearance order rotates across participants; full vision is a practice condition only.
        static readonly int[] AppearanceOrder = { 0, 1, 2 };

        sealed class Trial
        {
            public ManeuverType maneuver;
            public MocapClipLibrary.Clip clip;
            public int cutoffMs;
            public bool catchTrial, practice;
        }
        sealed class Block
        {
            public int condition;
            public string label;
            public bool practice;
            public bool simulatedRp;
            public readonly List<Trial> trials = new List<Trial>();
        }

        Camera participantCamera;
        Canvas promptCanvas;
        Image promptPanel;
        TMPro.TextMeshProUGUI promptText;
        ResponseCollector response;
        MocapClipLibrary library;
        MocapFigure figure;
        Transform avatarRoot;
        Vector3[] poseBuffer, worldBuffer;
        Coroutine session;
        TrialLogger trialLog;
        System.Random rng;
        Vector3 standPosition, standForward;
        int trialNumber, seed;
        bool waitingForResponse;
        ResponseCollector.Result responseResult;
        bool researchRecordingStarted;

        public void Configure(GameObject prefab, Avatar rigAsset, AnimationClip walkingClip,
            Camera camera, Transform headTransform, Transform controllerTransform,
            LowVisionOverlayController rpOverlay, ResearchSessionLogger logger,
            int participantId, float eyeHeight, bool hasConfirmedFloor = false, float confirmedFloor = 0f,
            Transform junction = null)
        {
            avatarPrefab = prefab; avatarRig = rigAsset; kevinWalkClip = walkingClip;
            participantCamera = camera; head = headTransform; rightController = controllerTransform;
            overlay = rpOverlay; researchLogger = logger;
            participantNumber = Mathf.Clamp(participantId, 1, 9999);
            useConfirmedFloor = hasConfirmedFloor;
            confirmedFloorY = confirmedFloor;
            corridorTJunction = junction;
            floorY = hasConfirmedFloor ? confirmedFloor : 0f;
        }

        public void StartSession()
        {
            if (session != null) StopCoroutine(session);
            if (!response) response = GetComponent<ResponseCollector>() ?? gameObject.AddComponent<ResponseCollector>();
            response.rayOrigin = rightController;
            if (!response.line)
            {
                var rayObject = new GameObject("Perception Pilot Response Ray");
                response.line = rayObject.AddComponent<LineRenderer>();
                response.line.useWorldSpace = true;
                response.line.positionCount = 2;
                response.line.startWidth = response.line.endWidth = .006f;
                response.line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                response.line.receiveShadows = false;
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                response.line.sharedMaterial = new Material(shader) { color = new Color(.2f, .9f, 1f, 1f) };
            }
            session = StartCoroutine(RunSession());
        }

        public void StopSession()
        {
            if (session != null) StopCoroutine(session);
            session = null; IsRunning = false;
            if (response != null) response.Cancel();
            HideAvatar();
            if (avatarRoot) Destroy(avatarRoot.gameObject);
            avatarRoot = null; figure = null;
            if (researchLogger && researchLogger.gaze) researchLogger.gaze.avatarRegions = null;
            if (trialLog != null) { trialLog.Dispose(); trialLog = null; }
            if (researchRecordingStarted && researchLogger) researchLogger.StopRecording();
            researchRecordingStarted = false;
            if (promptCanvas) Destroy(promptCanvas.gameObject);
            promptCanvas = null; promptText = null;
            if (overlay)
            {
                overlay.currentVisionType = VisionType.None;
                overlay.ApplySettings(VisionType.None);
            }
        }

        IEnumerator RunSession()
        {
            library = MocapClipLibrary.Load();
            if (!head || (!rightController && !response.allowKeyboard) || library.Clips.Count == 0)
            {
                MakePrompt(library.Clips.Count == 0 ? "No recorded walking clips: " + library.Error
                    : "Pilot setup is missing the participant camera or a response input.");
                yield break;
            }

            IsRunning = true;
            seed = participantNumber * 7919;
            rng = new System.Random(seed);
            var blocks = BuildDesign();
            trialNumber = 0;
            bool logOpenFailed = false;
            try { trialLog = new TrialLogger(participantNumber); }
            catch (Exception e) { MakePrompt("Could not create pilot trial log: " + e.Message); IsRunning = false; logOpenFailed = true; }
            if (logOpenFailed) yield break;

            if (logGazeSamples && researchLogger && !researchLogger.Recording)
            {
                researchLogger.participantId = "P" + participantNumber.ToString("D3");
                researchLogger.StartRecording();
                researchRecordingStarted = researchLogger.Recording;
            }

            yield return CalibrateStand();
            yield return RunIntroductionBlock();
            int mainCount = 0;
            foreach (var b in blocks) if (!b.practice) mainCount++;
            int mainIndex = -1;
            foreach (var block in blocks)
            {
                if (!block.practice) mainIndex++;
                SetCondition(block.condition, block.simulatedRp);
                MakePrompt(block.practice
                    ? block.label + "\n\nPull the right trigger or press Space to start"
                    : $"{block.label}\nBlock {mainIndex + 1} of {mainCount}\n\nPull trigger or press Space when ready");
                yield return WaitForTrigger();
                MakePrompt("");

                for (int i = 0; i < block.trials.Count; i++)
                    yield return RunTrial(block, i, mainIndex);

                if (!block.practice && mainIndex + 1 < mainCount)
                {
                    MakePrompt("Rest\n\nPull the right trigger or press Space when ready for the next block");
                    yield return WaitForTrigger();
                }
            }

            HideAvatar();
            MakePrompt("Finished — thank you.\n\nPress left Y to return to the setup menu.");
            if (trialLog != null) { trialLog.Dispose(); trialLog = null; }
            if (researchRecordingStarted && researchLogger) researchLogger.StopRecording();
            researchRecordingStarted = false;
            IsRunning = false; session = null;
            Finished?.Invoke();
        }

        IEnumerator CalibrateStand()
        {
            MakePrompt("Stand on the mark and look straight ahead.\n\nPull the right trigger.");
            yield return WaitForTrigger();
            floorY = DetectGroundHeight(head.position);
            standPosition = head.position; standPosition.y = floorY;
            standForward = head.forward; standForward.y = 0f;
            if (standForward.sqrMagnitude < 1e-4f) standForward = Vector3.forward;
            standForward.Normalize();
            SpawnAvatar();
            MakePrompt("");
        }

        IEnumerator RunIntroductionBlock()
        {
            // Unscored demonstrations show the three walking appearances with the tunnel off.
            SetField(false);
            var introManeuvers = AvailableManeuvers(library, new[] {
                ManeuverType.Straight,
                ManeuverType.TurnLeft30, ManeuverType.TurnLeft60, ManeuverType.TurnLeft90,
                ManeuverType.TurnRight30, ManeuverType.TurnRight60, ManeuverType.TurnRight90
            });
            if (introManeuvers.Count == 0)
            {
                MakePrompt("No straight or turning clips are available for the introduction.");
                yield break;
            }

            var appearances = new[] { MocapFigure.Mode.PointLight, MocapFigure.Mode.Realistic, MocapFigure.Mode.StickFigure };
            var names = new[] { "Biological motion", "Avatar", "Stick figure" };
            for (int i = 0; i < appearances.Length; i++)
            {
                // Keep the existing three-part introduction, but each appearance is its own
                // response trial. Randomize straight/left/right so nobody sees a fixed answer.
                ManeuverType maneuver = introManeuvers[rng.Next(introManeuvers.Count)];
                var clip = RandomClip(maneuver);
                if (figure.Avatar && !figure.Avatar.Prepare(clip))
                    Debug.LogError("[Pilot] Realistic avatar could not be prepared for the introduction clip " + clip.info.id);
                float startTime = Mathf.Max(-clip.T0, -1f);
                float endTime = Mathf.Min(clip.Duration - clip.T0, Mathf.Max(1.25f, startTime + 2.5f));
                if (endTime <= startTime + .25f) endTime = clip.Duration - clip.T0;
                clip.Sample(clip.T0 + startTime, poseBuffer);
                float groundOffset = floorY + clip.info.startLevel - Mathf.Min(poseBuffer[MocapClipLibrary.LToe].y, poseBuffer[MocapClipLibrary.RToe].y);
                clip.Sample(clip.T0, poseBuffer);
                Vector3 atTurn = poseBuffer[MocapClipLibrary.Hips];
                Quaternion rotation = Quaternion.LookRotation(standForward, Vector3.up);
                Vector3 target = ManeuverAnchor(maneuver, standPosition, standForward);
                Vector3 origin = target - rotation * new Vector3(atTurn.x, 0f, atTurn.z) + Vector3.up * groundOffset;

                figure.SetMode(appearances[i]);
                MakePrompt("");
                avatarRoot.gameObject.SetActive(true);
                ApplyPose(clip, startTime, origin, rotation);
                float began = Time.unscaledTime;
                while (Time.unscaledTime - began < endTime - startTime)
                {
                    float t = Mathf.Min(endTime, startTime + Time.unscaledTime - began);
                    ApplyPose(clip, t, origin, rotation);
                    yield return null;
                }
                ApplyPose(clip, endTime, origin, rotation);
                yield return new WaitForEndOfFrame();
                Vector3 lastShown = worldBuffer[MocapClipLibrary.Hips];
                HideAvatar();
                lastShown.y = floorY;
                MakePrompt($"{names[i]}\n\nWhich way was the walker heading?\n\nRIGHT STICK ← LEFT · ↑ STRAIGHT · → RIGHT");
                yield return WaitForResponse(lastShown);
                MakePrompt("");
            }
            SetField(false);
        }

        float DetectGroundHeight(Vector3 participantPosition)
        {
            if (useConfirmedFloor)
            {
                Debug.Log($"[Pilot] Using room-setup confirmed floor y={confirmedFloorY:0.000}.");
                return confirmedFloorY;
            }
            Vector3 rayStart = participantPosition + Vector3.up * .25f;
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                Debug.Log($"[Pilot] Detected floor y={hit.point.y:0.000} on '{hit.collider.name}'.");
                return hit.point.y;
            }
            Debug.LogWarning($"[Pilot] No floor collider detected below the participant; using configured floor y={floorY:0.000}.");
            return floorY;
        }

        void SpawnAvatar()
        {
            figure = MocapFigure.Create(MocapFigure.Mode.PointLight);
            figure.AttachAvatar(MocapAvatarDriver.Create(avatarPrefab, avatarRig));
            if (!figure.HasAvatar) Debug.LogError("[Pilot] Realistic avatar could not be set up; check the avatar prefab (humanoid rig). prefab=" + (avatarPrefab ? avatarPrefab.name : "null"));
            avatarRoot = figure.transform;
            avatarRoot.name = "Perception Pilot Mocap Figure";
            poseBuffer = new Vector3[MocapClipLibrary.JointNames.Length];
            worldBuffer = new Vector3[MocapClipLibrary.JointNames.Length];
            var regions = avatarRoot.gameObject.AddComponent<AvatarRegionRig>();
            regions.boneMap = figure.BoneMap;
            if (researchLogger && researchLogger.gaze) researchLogger.gaze.avatarRegions = regions;
            avatarRoot.gameObject.SetActive(false);
        }

        IEnumerator RunTrial(Block block, int indexInBlock, int blockIndex)
        {
            Trial trial = block.trials[indexInBlock];
            trialNumber++;
            var avatarMode = AppearanceForCondition(block.condition);
            bool simulatedRp = block.simulatedRp;
            figure.SetMode(avatarMode);
            SetField(simulatedRp);
            if (!(figure.Avatar && figure.Avatar.Prepare(trial.clip)))
                Debug.LogError("[Pilot] Realistic avatar cannot play " + trial.clip.info.id);

            // Times are relative to the clip's T0 (maneuver onset).
            var clip = trial.clip;
            float cutoffTime = trial.catchTrial ? 0f : trial.cutoffMs / 1000f;
            float wantedStart = cutoffTime - leadInSeconds - (float)rng.NextDouble() * leadInJitter;
            float startTime = Mathf.Max(wantedStart, -clip.T0);                       // never before the recording starts
            float endTime = trial.catchTrial ? Mathf.Min(1.5f, clip.Duration - clip.T0) : cutoffTime;
            if (startTime > wantedStart + 1e-3f) Debug.LogWarning($"[Pilot] {clip.info.id}: lead-in shortened to {cutoffTime - startTime:0.00} s");

            // Anchor turns at the physical T. For a pre-turn cutoff, its recorded root is
            // naturally still before the junction; the change of path begins at the junction.
            clip.Sample(clip.T0 + startTime, poseBuffer);
            float groundOffset = floorY + clip.info.startLevel - Mathf.Min(poseBuffer[MocapClipLibrary.LToe].y, poseBuffer[MocapClipLibrary.RToe].y);
            Quaternion baseRotation = Quaternion.LookRotation(standForward, Vector3.up);
            clip.Sample(clip.T0, poseBuffer);
            Vector3 atTurn = poseBuffer[MocapClipLibrary.Hips];
            Vector3 target = ManeuverAnchor(trial.maneuver, standPosition, standForward);
            Vector3 origin = target - baseRotation * new Vector3(atTurn.x, 0f, atTurn.z) + Vector3.up * groundOffset;

            if (trialNumber > 1) yield return new WaitForSeconds(.35f);
            if (researchLogger && researchLogger.Recording)
            {
                researchLogger.avatar = avatarRoot;
                string condition = AppearanceLabel(avatarMode) + "|" + (simulatedRp ? "SimulatedRP" : "Full") + "|mocap_cmu|" + trial.maneuver;
                researchLogger.BeginTrial($"P{participantNumber:D3}_T{trialNumber:D4}", condition, clip.info.id,
                    trial.catchTrial ? -1f : trial.cutoffMs / 1000f, ManeuverInfo.Category(trial.maneuver).ToString());
            }

            avatarRoot.gameObject.SetActive(true);
            ApplyPose(clip, startTime, origin, baseRotation);
            double stimOn = Time.realtimeSinceStartupAsDouble;
            string stimUtc = DateTime.UtcNow.ToString("o");
            float startRt = Time.unscaledTime;
            float shownTime = startTime;
            while (true)
            {
                float sourceTime = startTime + (Time.unscaledTime - startRt);
                if (sourceTime >= endTime) { shownTime = endTime; ApplyPose(clip, endTime, origin, baseRotation); break; }
                shownTime = sourceTime;
                ApplyPose(clip, sourceTime, origin, baseRotation);
                yield return null;
            }
            yield return new WaitForEndOfFrame();
            double cutoffRt = Time.realtimeSinceStartupAsDouble;
            string cutoffUtc = DateTime.UtcNow.ToString("o");
            HideAvatar();

            // Last shown position: the recorded root over the ground it was walking on.
            Vector3 lastShown = worldBuffer[MocapClipLibrary.Hips];
            lastShown.y = Mathf.Min(worldBuffer[MocapClipLibrary.LToe].y, worldBuffer[MocapClipLibrary.RToe].y);
            MakePrompt("WHERE WAS THE WALKER HEADING?\n\nRIGHT STICK ← LEFT · ↑ STRAIGHT · → RIGHT\nOr point down and pull trigger. A = stairs up · B = stairs down.");
            _respReady = false;
            response.Begin(lastShown, standForward, floorY, r => { responseResult = r; _respReady = true; });
            while (!_respReady) yield return null;
            double responseRt = Time.realtimeSinceStartupAsDouble;
            string responseUtc = DateTime.UtcNow.ToString("o");
            if (researchLogger && researchLogger.Recording) researchLogger.RecordResponse(responseResult.category.ToString());

            float trueAngle = ManeuverInfo.TrueAngle(trial.maneuver);
            bool correct = responseResult.category == ManeuverInfo.Category(trial.maneuver);
            float angleError = responseResult.hasAngle && !ManeuverInfo.IsStairs(trial.maneuver)
                ? responseResult.angleDeg - trueAngle : float.NaN;
            Vector3 rel = lastShown - standPosition; rel.y = 0f;
            float actualD = Vector3.Dot(rel, standForward);
            float actualLateral = Vector3.Dot(rel, Vector3.Cross(Vector3.up, standForward));
            Debug.Log($"[Pilot] T{trialNumber} {clip.info.id} {AppearanceLabel(avatarMode)} {(trial.catchTrial ? "catch" : trial.cutoffMs + "ms")} " +
                $"clip {clip.T0 + startTime:0.00}-{clip.T0 + endTime:0.00}s shown {(cutoffRt - stimOn):0.00}s D {actualD:0.00} lat {actualLateral:+0.00;-0.00} resp {responseResult.category}");
            if (trialLog != null)
                trialLog.Write(participantNumber, seed, blockIndex, block.label,
                    AppearanceLabel(avatarMode), simulatedRp ? "SimulatedRP" : "Full",
                    indexInBlock + 1, trialNumber, trial.practice, trial.catchTrial, "mocap_cmu",
                    trial.maneuver.ToString(), ManeuverInfo.Category(trial.maneuver).ToString(), trueAngle,
                    clip.info.id + (clip.Spliced.Length > 0 ? " [lead-in " + clip.Spliced + "]" : ""), "CMU" + clip.info.subject, clip.info.trial, clip.T0, clip.info.t0IsPseudo,
                    trial.catchTrial ? "full" : trial.cutoffMs, clip.T0 + startTime, clip.T0 + endTime, cutoffTime - startTime,
                    Vector3.Dot(target - standPosition, standForward), actualD, actualLateral, responseResult.category.ToString(), responseResult.hasAngle,
                    responseResult.angleDeg, correct, angleError, responseResult.rtSeconds,
                    stimOn, cutoffRt, responseRt, stimUtc, cutoffUtc, responseUtc);

            if (trial.practice)
            {
                MakePrompt(correct ? "Correct" : "It was: " + trial.maneuver);
                yield return new WaitForSeconds(1.2f);
                MakePrompt("");
            }
            else MakePrompt("");
            if (researchLogger && researchLogger.Recording) researchLogger.EndTrial();
        }

        bool _respReady;

        Vector3 ManeuverAnchor(ManeuverType maneuver, Vector3 start, Vector3 forward)
        {
            Vector3 junction = corridorTJunction ? corridorTJunction.position : start + forward * cutoffDistance;
            bool turning = maneuver == ManeuverType.TurnLeft30 || maneuver == ManeuverType.TurnLeft60 ||
                           maneuver == ManeuverType.TurnLeft90 || maneuver == ManeuverType.TurnRight30 ||
                           maneuver == ManeuverType.TurnRight60 || maneuver == ManeuverType.TurnRight90;
            return turning ? junction : junction - forward * straightStopBeforeJunction;
        }

        IEnumerator WaitForTrigger()
        {
            yield return null;
            while (!OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
            {
#if ENABLE_INPUT_SYSTEM
                if (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.spaceKey.wasPressedThisFrame) break;
#endif
                yield return null;
            }
            yield return null;
        }

        /// <summary>Maneuvers that have at least one verified recorded clip.</summary>
        static List<ManeuverType> AvailableManeuvers(MocapClipLibrary lib, ManeuverType[] all)
        {
            var list = new List<ManeuverType>();
            foreach (var m in all) if (lib.Has(m)) list.Add(m);
            return list;
        }

        static readonly ManeuverType[] AllManeuvers = {
            ManeuverType.Straight,
            ManeuverType.TurnLeft30, ManeuverType.TurnLeft60, ManeuverType.TurnLeft90,
            ManeuverType.TurnRight30, ManeuverType.TurnRight60, ManeuverType.TurnRight90,
            ManeuverType.StairsUp, ManeuverType.StairsDown
        };

        /// <summary>Trial counts of the design with the clips that exist (used by the setup menu).</summary>
        public static string DescribeDesign(int repetitions = 2, int cutoffCount = 3, int conditions = 3, int subBlocks = 2, int catchPerSubBlock = 2, int practice = 9)
        {
            var lib = MocapClipLibrary.Load();
            var available = AvailableManeuvers(lib, AllManeuvers);
            var missing = new List<string>();
            foreach (var m in AllManeuvers) if (!lib.Has(m)) missing.Add(ManeuverLabel(m));
            int test = available.Count * cutoffCount * repetitions * conditions, catchTrials = conditions * subBlocks * catchPerSubBlock;
            int prac = Mathf.Min(practice, available.Count) * 2;
            var have = new List<string>(); foreach (var m in available) have.Add(ManeuverLabel(m));
            return $"{test + catchTrials + prac}|{test}|{catchTrials}|{prac}|{string.Join(" · ", have)}|{string.Join(" · ", missing)}";
        }

        static string ManeuverLabel(ManeuverType m)
        {
            switch (m)
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
                default: return m.ToString();
            }
        }

        MocapClipLibrary.Clip RandomClip(ManeuverType m)
        {
            var clips = library.For(m);
            return clips[rng.Next(clips.Count)];
        }

        List<Block> BuildDesign()
        {
            var available = AvailableManeuvers(library, maneuvers);
            foreach (var m in maneuvers)
                if (!library.Has(m)) Debug.LogWarning("[Pilot] No verified recorded clip for " + m + " — left out of this session.");
            var blocks = new List<Block>();
            var practiceItems = new List<Trial>();
            int practiceCount = Mathf.Min(practiceTrials, available.Count);
            for (int i = 0; i < practiceCount; i++)
            {
                var m = available[i % available.Count];
                practiceItems.Add(new Trial { maneuver = m, clip = RandomClip(m), catchTrial = true, practice = true });
            }
            var fullPractice = new Block { label = "Practice 1 of 2 — Full view (simulation off)", condition = 0, practice = true, simulatedRp = false };
            fullPractice.trials.AddRange(practiceItems); Shuffle(fullPractice.trials); blocks.Add(fullPractice);
            var simulatedPractice = new Block { label = "Practice 2 of 2 — Simulated RP (simulation on)", condition = 0, practice = true, simulatedRp = true };
            simulatedPractice.trials.AddRange(practiceItems); Shuffle(simulatedPractice.trials); blocks.Add(simulatedPractice);

            int row = ((participantNumber - 1) % AppearanceOrder.Length + AppearanceOrder.Length) % AppearanceOrder.Length;
            for (int position = 0; position < AppearanceOrder.Length; position++)
            {
                int appearanceIndex = (AppearanceOrder[position] + row) % AppearanceOrder.Length;
                int condition = appearanceIndex * 2 + 1; // simulated RP cell for this appearance
                var trials = new List<Trial>();
                foreach (var maneuver in available)
                {
                    // Recorded exemplars rotate through the cells so each take is used about equally often.
                    var clips = library.For(maneuver);
                    int next = rng.Next(clips.Count);
                    var cells = new List<int>();
                    foreach (int cutoff in cutoffs) for (int r = 0; r < repetitions; r++) cells.Add(cutoff);
                    Shuffle(cells);
                    foreach (int cutoff in cells) trials.Add(new Trial { maneuver = maneuver, cutoffMs = cutoff, clip = clips[next++ % clips.Count] });
                }
                Shuffle(trials);
                int subBlocks = Mathf.Max(1, subBlocksPerCondition);
                for (int s = 0; s < subBlocks; s++)
                {
                    var block = new Block { condition = condition, simulatedRp = true, label = ConditionLabel(condition) + " — part " + (s + 1) };
                    int from = trials.Count * s / subBlocks, to = trials.Count * (s + 1) / subBlocks;
                    for (int i = from; i < to; i++) block.trials.Add(trials[i]);
                    for (int c = 0; c < catchTrialsPerSubBlock; c++)
                    {
                        var m = available[rng.Next(available.Count)];
                        block.trials.Insert(rng.Next(block.trials.Count + 1), new Trial { maneuver = m, clip = RandomClip(m), catchTrial = true });
                    }
                    blocks.Add(block);
                }
            }
            return blocks;
        }

        void SetCondition(int c, bool simulatedRp)
        {
            if (figure) figure.SetMode(AppearanceForCondition(c));
            SetField(simulatedRp);
        }

        void SetField(bool simulated)
        {
            if (!overlay) return;
            var vision = simulated ? VisionType.Severe : VisionType.None;
            overlay.currentVisionType = vision;
            overlay.ApplySettings(vision);
        }

        void ApplyPose(MocapClipLibrary.Clip clip, float timeRelT0, Vector3 origin, Quaternion rotation)
        {
            clip.Sample(clip.T0 + timeRelT0, poseBuffer);
            for (int k = 0; k < poseBuffer.Length; k++) worldBuffer[k] = origin + rotation * poseBuffer[k];
            figure.SetPose(worldBuffer);
            // Recorded motion only: the realistic avatar is retargeted from the recorded joint rotations of this frame.
            if (figure.CurrentMode == MocapFigure.Mode.Realistic && figure.Avatar)
                figure.Avatar.SetPose(clip, clip.T0 + timeRelT0, origin, rotation, worldBuffer);
        }

        void HideAvatar()
        {
            if (avatarRoot) avatarRoot.gameObject.SetActive(false);
        }

        void MakePrompt(string value)
        {
            if (!promptText && head)
            {
                var canvasObject = new GameObject("Perception Pilot Demo Prompt", typeof(RectTransform), typeof(Canvas));
                canvasObject.transform.SetParent(head, false);
                canvasObject.transform.localPosition = new Vector3(0f, 0f, 1.1f);
                canvasObject.transform.localScale = Vector3.one * .001f;
                promptCanvas = canvasObject.GetComponent<Canvas>();
                promptCanvas.renderMode = RenderMode.WorldSpace;
                promptCanvas.worldCamera = participantCamera;
                promptCanvas.overrideSorting = true; promptCanvas.sortingOrder = 650;
                var rect = canvasObject.GetComponent<RectTransform>(); rect.sizeDelta = new Vector2(900f, 300f);
                promptPanel = canvasObject.AddComponent<Image>();
                promptPanel.color = new Color(.025f, .045f, .075f, .94f);
                promptPanel.raycastTarget = false;
                var textObject = new GameObject("Text", typeof(RectTransform)); textObject.transform.SetParent(canvasObject.transform, false);
                promptText = textObject.AddComponent<TMPro.TextMeshProUGUI>();
                promptText.alignment = TMPro.TextAlignmentOptions.Center; promptText.fontSize = 32; promptText.color = Color.white;
                promptText.rectTransform.anchorMin = Vector2.zero; promptText.rectTransform.anchorMax = Vector2.one;
                promptText.rectTransform.offsetMin = new Vector2(35f, 24f); promptText.rectTransform.offsetMax = new Vector2(-35f, -24f);
                // Draw prompts after the tunnel overlay (queue Overlay+900), like the start menu, so they stay readable in RP blocks.
                var uiShader = Shader.Find("UI/Default");
                if (uiShader) promptPanel.material = new Material(uiShader) { renderQueue = 5000 };
                if (promptText.fontMaterial) promptText.fontMaterial.renderQueue = 5000; // URP draws transparent queues 2501-5000 only; 5001 is never rendered.
            }
            if (promptText) promptText.text = value;
            if (promptPanel) promptPanel.enabled = !string.IsNullOrEmpty(value);
        }

        IEnumerator WaitForResponse(Vector3 origin)
        {
            waitingForResponse = true;
            response.Begin(origin, standForward, floorY, r => { responseResult = r; waitingForResponse = false; });
            while (waitingForResponse) yield return null;
        }

        void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        string ConditionLabel(int condition)
        {
            string avatar = AppearanceLabel(AppearanceForCondition(condition));
            string field = condition % 2 == 0 ? "Full field" : "Simulated RP";
            return avatar + " / " + field;
        }

        // Conditions 0/1: realistic avatar (full field / simulated RP); 2/3: point-light figure; 4/5: stick figure.
        static MocapFigure.Mode AppearanceForCondition(int condition)
            => condition / 2 == 2 ? MocapFigure.Mode.StickFigure : condition / 2 == 1 ? MocapFigure.Mode.PointLight : MocapFigure.Mode.Realistic;

        static string AppearanceLabel(MocapFigure.Mode mode)
            => mode == MocapFigure.Mode.StickFigure ? "StickFigure" : mode == MocapFigure.Mode.PointLight ? "PointLight" : "Realistic";

        void OnDestroy()
        {
            if (trialLog != null) trialLog.Dispose();
            if (researchRecordingStarted && researchLogger) researchLogger.StopRecording();
        }
    }
}
