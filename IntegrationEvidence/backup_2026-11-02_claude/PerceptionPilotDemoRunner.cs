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
    /// Try-it-now version of the perception pilot. It uses two appearances of the existing animated avatar and scripted
    /// paths for turns/stairs until recorded mocap takes are available. Do not treat demo kinematics
    /// as recorded biological-motion stimuli.
    /// </summary>
    public sealed class PerceptionPilotDemoRunner : MonoBehaviour
    {
        public GameObject avatarPrefab;
        public Avatar avatarRig;
        public RuntimeAnimatorController animationController;
        public Transform head;
        public Transform rightController;
        public LowVisionOverlayController overlay;
        public ResearchSessionLogger researchLogger;
        public int participantNumber = 1;
        public float floorY;
        public float walkingSpeed = 1f;
        public float cutoffDistance = 4f;
        public float leadInSeconds = 2f;
        public float leadInJitter = .4f;
        public int repetitions = 2;
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
        // Four conditions: two appearances crossed with full field / simulated RP.
        // A Williams square balances order and first-order carryover across participants.
        static readonly int[] WilliamsBase = { 0, 3, 1, 2 };

        sealed class Trial
        {
            public ManeuverType maneuver;
            public int cutoffMs;
            public bool catchTrial, practice;
        }
        sealed class Block
        {
            public int condition;
            public string label;
            public bool practice;
            public readonly List<Trial> trials = new List<Trial>();
        }
        struct MotionSample { public float time; public Vector3 position; public float yaw; }

        Camera participantCamera;
        Canvas promptCanvas;
        Image promptPanel;
        TMPro.TextMeshProUGUI promptText;
        ResponseCollector response;
        Animator animator;
        AvatarAppearanceController appearance;
        Transform avatarRoot;
        Renderer[] avatarRenderers;
        Coroutine session;
        TrialLogger trialLog;
        System.Random rng;
        Vector3 standPosition, standForward;
        int trialNumber, seed;
        bool waitingForResponse;
        ResponseCollector.Result responseResult;
        bool researchRecordingStarted;

        public void Configure(GameObject prefab, Avatar rigAsset, RuntimeAnimatorController controller,
            Camera camera, Transform headTransform, Transform controllerTransform,
            LowVisionOverlayController rpOverlay, ResearchSessionLogger logger,
            int participantId, float eyeHeight)
        {
            avatarPrefab = prefab; avatarRig = rigAsset; animationController = controller;
            participantCamera = camera; head = headTransform; rightController = controllerTransform;
            overlay = rpOverlay; researchLogger = logger;
            participantNumber = Mathf.Clamp(participantId, 1, 9999);
            floorY = 0f;
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
            avatarRoot = null; animator = null; appearance = null;
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
            if (!avatarPrefab || !head || !rightController)
            {
                MakePrompt("Pilot setup is missing the avatar or Quest camera/controller references.");
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
            int mainCount = 0;
            foreach (var b in blocks) if (!b.practice) mainCount++;
            int mainIndex = -1;
            foreach (var block in blocks)
            {
                if (!block.practice) mainIndex++;
                SetCondition(block.condition);
                MakePrompt(block.practice
                    ? "Practice block\n\nPull the right trigger to start"
                    : $"{block.label}\nBlock {mainIndex + 1} of {mainCount}\n\nPull trigger when ready");
                yield return WaitForTrigger();
                MakePrompt("");

                for (int i = 0; i < block.trials.Count; i++)
                    yield return RunTrial(block, i, mainIndex);

                if (!block.practice && mainIndex + 1 < mainCount)
                {
                    MakePrompt("Rest\n\nPull the right trigger when ready for the next block");
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
            standPosition = head.position; standPosition.y = floorY;
            standForward = head.forward; standForward.y = 0f;
            if (standForward.sqrMagnitude < 1e-4f) standForward = Vector3.forward;
            standForward.Normalize();
            SpawnAvatar();
            MakePrompt("");
        }

        void SpawnAvatar()
        {
            Vector3 p = standPosition + standForward * cutoffDistance;
            avatarRoot = Instantiate(avatarPrefab, p, Quaternion.LookRotation(standForward, Vector3.up)).transform;
            avatarRoot.name = "Perception Pilot Demo Avatar";
            foreach (var rb in avatarRoot.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.useGravity = false; }
            foreach (var c in avatarRoot.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            animator = avatarRoot.GetComponentInChildren<Animator>();
            if (!animator) animator = avatarRoot.gameObject.AddComponent<Animator>();
            if (avatarRig) animator.avatar = avatarRig;
            if (animationController) animator.runtimeAnimatorController = animationController;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            appearance = avatarRoot.gameObject.AddComponent<AvatarAppearanceController>();
            appearance.Initialize(animator, AvatarAppearanceController.Appearance.Realistic);
            var regions = avatarRoot.gameObject.AddComponent<AvatarRegionRig>();
            regions.animator = animator;
            if (researchLogger && researchLogger.gaze) researchLogger.gaze.avatarRegions = regions;
            avatarRenderers = avatarRoot.GetComponentsInChildren<Renderer>(true);
            SetWalking(false);
            avatarRoot.gameObject.SetActive(false);
        }

        IEnumerator RunTrial(Block block, int indexInBlock, int blockIndex)
        {
            Trial trial = block.trials[indexInBlock];
            trialNumber++;
            var avatarMode = AppearanceForCondition(block.condition);
            bool simulatedRp = tunnelSimulationEnabled && block.condition % 2 == 1;
            appearance.SetAppearance(avatarMode);
            SetField(simulatedRp);

            float cutoffTime = trial.catchTrial ? 0f : trial.cutoffMs / 1000f;
            float startTime = cutoffTime - leadInSeconds - (float)rng.NextDouble() * leadInJitter;
            float endTime = trial.catchTrial ? 1.5f : cutoffTime;
            var path = BuildMotion(trial.maneuver, startTime, endTime);
            Vector3 atCut = Sample(path, cutoffTime).position;
            Vector3 target = standPosition + standForward * cutoffDistance;
            Quaternion baseRotation = Quaternion.LookRotation(standForward, Vector3.up);
            Vector3 startPosition = target - baseRotation * new Vector3(atCut.x, 0f, atCut.z);
            startPosition.y = floorY - atCut.y;

            if (trialNumber > 1) yield return new WaitForSeconds(.35f);
            if (researchLogger && researchLogger.Recording)
            {
                researchLogger.avatar = avatarRoot;
                string condition = AppearanceLabel(avatarMode) + "|" + (simulatedRp ? "SimulatedRP" : "Full") + "|scripted_demo|" + trial.maneuver;
                researchLogger.BeginTrial($"P{participantNumber:D3}_T{trialNumber:D4}", condition, "existing-walk-demo",
                    trial.catchTrial ? -1f : trial.cutoffMs / 1000f, ManeuverInfo.Category(trial.maneuver).ToString());
            }

            avatarRoot.gameObject.SetActive(true);
            SetWalking(true);
            double stimOn = Time.realtimeSinceStartupAsDouble;
            string stimUtc = DateTime.UtcNow.ToString("o");
            float startRt = Time.time;
            MotionSample last = path[0];
            while (true)
            {
                float sourceTime = startTime + (Time.time - startRt);
                if (sourceTime >= endTime) { last = Sample(path, endTime); ApplyPose(startPosition, baseRotation, last); break; }
                last = Sample(path, sourceTime);
                ApplyPose(startPosition, baseRotation, last);
                yield return null;
            }
            yield return new WaitForEndOfFrame();
            double cutoffRt = Time.realtimeSinceStartupAsDouble;
            string cutoffUtc = DateTime.UtcNow.ToString("o");
            SetWalking(false);
            HideAvatar();

            Vector3 lastShown = startPosition + baseRotation * new Vector3(last.position.x, 0f, last.position.z);
            lastShown.y = floorY + last.position.y;
            MakePrompt("WHERE WAS THE AVATAR HEADING?\n\nPoint the right controller at the floor and pull the trigger.\nFor stairs: press A = up  ·  B = down.");
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
            if (trialLog != null)
                trialLog.Write(participantNumber, seed, blockIndex, block.label,
                    AppearanceLabel(avatarMode), simulatedRp ? "SimulatedRP" : "Full",
                    indexInBlock + 1, trialNumber, trial.practice, trial.catchTrial, "scripted_demo",
                    trial.maneuver.ToString(), ManeuverInfo.Category(trial.maneuver).ToString(), trueAngle,
                    "existing-walk-animation", "demo", 1, 0f, false,
                    trial.catchTrial ? "full" : trial.cutoffMs, startTime, endTime, endTime - startTime,
                    cutoffDistance, actualD, actualLateral, responseResult.category.ToString(), responseResult.hasAngle,
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

        List<Block> BuildDesign()
        {
            var blocks = new List<Block>();
            var practice = new Block { label = "Practice", condition = 0, practice = true };
            for (int i = 0; i < practiceTrials; i++)
                practice.trials.Add(new Trial { maneuver = maneuvers[i % maneuvers.Length], catchTrial = true, practice = true });
            Shuffle(practice.trials); blocks.Add(practice);

            int row = ((participantNumber - 1) % WilliamsBase.Length + WilliamsBase.Length) % WilliamsBase.Length;
            for (int position = 0; position < WilliamsBase.Length; position++)
            {
                int condition = (WilliamsBase[position] + row) % WilliamsBase.Length;
                var trials = new List<Trial>();
                foreach (var maneuver in maneuvers)
                {
                    var cells = new List<int>();
                    foreach (int cutoff in cutoffs) for (int r = 0; r < repetitions; r++) cells.Add(cutoff);
                    Shuffle(cells);
                    foreach (int cutoff in cells) trials.Add(new Trial { maneuver = maneuver, cutoffMs = cutoff });
                }
                Shuffle(trials);
                int subBlocks = Mathf.Max(1, subBlocksPerCondition);
                for (int s = 0; s < subBlocks; s++)
                {
                    var block = new Block { condition = condition, label = ConditionLabel(condition) + " — part " + (s + 1) };
                    int from = trials.Count * s / subBlocks, to = trials.Count * (s + 1) / subBlocks;
                    for (int i = from; i < to; i++) block.trials.Add(trials[i]);
                    for (int c = 0; c < catchTrialsPerSubBlock; c++)
                        block.trials.Insert(rng.Next(block.trials.Count + 1), new Trial { maneuver = maneuvers[rng.Next(maneuvers.Length)], catchTrial = true });
                    blocks.Add(block);
                }
            }
            return blocks;
        }

        void SetCondition(int c)
        {
            if (appearance) appearance.SetAppearance(AppearanceForCondition(c));
            SetField(tunnelSimulationEnabled && c % 2 == 1);
        }

        void SetField(bool simulated)
        {
            if (!overlay) return;
            var vision = simulated ? VisionType.Severe : VisionType.None;
            overlay.currentVisionType = vision;
            overlay.ApplySettings(vision);
        }

        List<MotionSample> BuildMotion(ManeuverType maneuver, float from, float to)
        {
            const float step = 1f / 90f;
            var samples = new List<MotionSample>();
            Vector3 p = Vector3.zero;
            float yaw = 0f;
            samples.Add(new MotionSample { time = from, position = p, yaw = yaw });
            for (float t = from + step; t < to; t += step)
            {
                p += Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * walkingSpeed * step;
                yaw = HeadingAt(maneuver, t);
                p.y = ElevationAt(maneuver, t);
                samples.Add(new MotionSample { time = t, position = p, yaw = yaw });
            }
            float dt = Mathf.Max(0f, to - samples[samples.Count - 1].time);
            p += Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * walkingSpeed * dt;
            p.y = ElevationAt(maneuver, to);
            samples.Add(new MotionSample { time = to, position = p, yaw = HeadingAt(maneuver, to) });
            return samples;
        }

        static float HeadingAt(ManeuverType maneuver, float t)
        {
            if (t <= 0f) return 0f;
            float progress = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / .8f));
            return ManeuverInfo.TrueAngle(maneuver) * progress;
        }

        static float ElevationAt(ManeuverType maneuver, float t)
        {
            if (maneuver != ManeuverType.StairsUp && maneuver != ManeuverType.StairsDown) return 0f;
            float sign = maneuver == ManeuverType.StairsUp ? 1f : -1f;
            int steps = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0f, t) / .35f), 0, 5);
            return sign * steps * .16f;
        }

        static MotionSample Sample(List<MotionSample> path, float t)
        {
            if (t <= path[0].time) return path[0];
            for (int i = 1; i < path.Count; i++)
            {
                if (t > path[i].time) continue;
                var a = path[i - 1]; var b = path[i];
                float u = Mathf.InverseLerp(a.time, b.time, t);
                return new MotionSample { time = t, position = Vector3.Lerp(a.position, b.position, u), yaw = Mathf.LerpAngle(a.yaw, b.yaw, u) };
            }
            return path[path.Count - 1];
        }

        void ApplyPose(Vector3 start, Quaternion rotation, MotionSample sample)
        {
            avatarRoot.SetPositionAndRotation(start + rotation * new Vector3(sample.position.x, 0f, sample.position.z) + Vector3.up * sample.position.y,
                rotation * Quaternion.Euler(0f, sample.yaw, 0f));
        }

        void SetWalking(bool walking)
        {
            if (!animator) return;
            animator.speed = walking ? 1f : 0f;
            foreach (var p in animator.parameters)
            {
                if (p.nameHash == Animator.StringToHash("Speed") && p.type == AnimatorControllerParameterType.Float)
                    animator.SetFloat(p.nameHash, walking ? 1f : 0f);
                else if (p.nameHash == Animator.StringToHash("MotionSpeed") && p.type == AnimatorControllerParameterType.Float)
                    animator.SetFloat(p.nameHash, 1f);
                else if (p.nameHash == Animator.StringToHash("Grounded") && p.type == AnimatorControllerParameterType.Bool)
                    animator.SetBool(p.nameHash, true);
            }
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
            string field = condition % 2 == 0 || !tunnelSimulationEnabled ? "Full field" : "Simulated RP";
            return avatar + " / " + field;
        }

        static AvatarAppearanceController.Appearance AppearanceForCondition(int condition)
        {
            switch (condition / 2)
            {
                case 1: return AvatarAppearanceController.Appearance.BiologicalMotion;
                default: return AvatarAppearanceController.Appearance.Realistic;
            }
        }

        static string AppearanceLabel(AvatarAppearanceController.Appearance appearance)
        {
            switch (appearance)
            {
                case AvatarAppearanceController.Appearance.BiologicalMotion: return "PointLightPreview";
                default: return "Realistic";
            }
        }

        void OnDestroy()
        {
            if (trialLog != null) trialLog.Dispose();
            if (researchRecordingStarted && researchLogger) researchLogger.StopRecording();
        }
    }
}
