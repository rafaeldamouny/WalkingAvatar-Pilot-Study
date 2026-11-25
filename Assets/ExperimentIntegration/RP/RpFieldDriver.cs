using UnityEngine;
using PassthroughCameraSamples.MultiObjectDetection;

namespace WalkingEyeExperiment
{
    [DefaultExecutionOrder(500)]
    public class RpFieldDriver : MonoBehaviour
    {
        public enum CenterMode { HeadCentered, GazeCentered }
        public enum TrackingLossPolicy { HeadCentered, BlackOut }
        public Camera participantCamera;
        public LowVisionOverlayController overlay;
        public CenterMode centerMode = CenterMode.HeadCentered;
        public float planeDistance = 0.17f;
        public float planeWidth = 2f;
        public Vector3 gazeDirectionWorld;
        public bool gazeUsable;
        [Header("Window smoothing (raw gaze remains unchanged)")]
        public bool smoothGazeWindow = true;
        [Tooltip("Low-end cutoff for gaze-window smoothing. Keep this responsive; heavy smoothing creates visible mask lag.")]
        [Range(.5f, 30f)] public float smoothingMinimumHz = 12f;
        [Range(0f, .2f)] public float smoothingSpeedCoefficient = .03f;
        [Range(.5f, 30f)] public float smoothingSpeedFilterHz = 12f;
        [Range(0f, .25f)] public float maximumInvalidHoldSeconds = .12f;
        [Header("RP simulation: binocular gaze")]
        [Tooltip("Use Neon’s separate left/right gaze estimates when available. Eye-state optical axes are not gaze rays and are not used to position the field.")]
        public bool usePerEyeMonocularGaze = true;
        [Tooltip("What participants see when gaze is lost for longer than the hold time (gaze-centred mode only). " +
                 "HeadCentered (original behaviour) re-centres the field on the head; BlackOut shows nothing until gaze returns, " +
                 "so tracking loss can never reveal the scene outside the simulated field.")]
        public TrackingLossPolicy trackingLossPolicy = TrackingLossPolicy.HeadCentered;
        [Tooltip("Optional: Neon gaze bridge (found automatically) for per-eye rays and sample age.")]
        public NeonGazeBridge gazeSource;
        [Tooltip("Write RPSimulation/rp_*.csv with per-frame mask state, gaze age and parameters.")]
        public bool writeSimulationLog = true;
        [Tooltip("Show head-locked test dots at 2.5-20° eccentricity on 8 meridians, for checking the field size on the headset.")]
        public bool showAngleProbe;
        [Header("RP simulation model")]
        [Tooltip("ON: QuestRP island simulation (irregular RP island over the system passthrough, no camera access) replaces " +
                 "the circular tunnel overlay. OFF: the earlier tunnel overlay.")]
        public bool useIslandSimulation = true;
        public RpIslandSimulation Island { get; private set; }
        public Vector3 LeftEyeGazeWorld { get; private set; } = Vector3.forward;
        public Vector3 RightEyeGazeWorld { get; private set; } = Vector3.forward;
        public bool MonocularGazeUsed { get; private set; }
        public bool Blackout { get; private set; }
        public double GazeSampleAge { get; private set; } = double.PositiveInfinity;
        RpSimulationLog simLog;
        RpAngleProbe probe;
        public bool UsingGaze { get; private set; }
        public bool HoldingGaze { get; private set; }
        public string EffectiveCenterMode { get; private set; } = "HeadCentered";
        public Vector3 DisplayHeadDirection { get; private set; } = Vector3.forward;
        public int DisplayAppliedFrame { get; private set; } = -1;
        Material instance;
        readonly GazeWindowSmoother smoother = new GazeWindowSmoother();
        readonly GazeWindowSmoother leftEyeSmoother = new GazeWindowSmoother();
        readonly GazeWindowSmoother rightEyeSmoother = new GazeWindowSmoother();
        int lastSmoothedFrame = -1;
        int lastEyeSmoothedFrame = -1;
        double lastSmoothedTime;
        double lastEyeSmoothedTime;
        Vector3 filteredLeftHeadLocal = Vector3.forward;
        Vector3 filteredRightHeadLocal = Vector3.forward;
        double lastUsableGazeTime;
        bool hasWindowGaze;
        bool wasUsingGaze;
        double recoverySmoothingUntil;
        const double RecoverySmoothingSeconds=.15;

        void Start()
        {
            if (!overlay || !overlay.overlayMaterial) return;
            instance = new Material(overlay.overlayMaterial);
            overlay.overlayMaterial = instance;
            GetComponent<Renderer>().sharedMaterial = instance;
            if (!gazeSource) gazeSource = FindFirstObjectByType<NeonGazeBridge>();
            if (writeSimulationLog) simLog = gameObject.AddComponent<RpSimulationLog>();
            if (useIslandSimulation)
            {
                Island = GetComponent<RpIslandSimulation>();
                if (!Island) Island = gameObject.AddComponent<RpIslandSimulation>();
            }
        }
        void Update()
        {
            if (showAngleProbe && !probe && participantCamera) probe = RpAngleProbe.Create(participantCamera.transform);
            if (probe) probe.gameObject.SetActive(showAngleProbe);
        }
        void OnDestroy() { if (instance) Destroy(instance); }

        void OnEnable() { ResetSmoothing(); Application.onBeforeRender += Apply; }
        void OnDisable() { Application.onBeforeRender -= Apply; ResetSmoothing(); }
        void OnApplicationPause(bool paused) { ResetSmoothing(); }
        void ResetSmoothing()
        {
            smoother.Reset(); leftEyeSmoother.Reset(); rightEyeSmoother.Reset();
            lastSmoothedFrame = lastEyeSmoothedFrame = -1;
            lastSmoothedTime = lastEyeSmoothedTime = 0;
            filteredLeftHeadLocal = filteredRightHeadLocal = Vector3.forward;
            hasWindowGaze=false; HoldingGaze=false; wasUsingGaze=false; recoverySmoothingUntil=0;
        }
        void LateUpdate() { Apply(); }
        [BeforeRenderOrder(200)]
        void Apply()
        {
            if (!participantCamera || !overlay || !overlay.overlayMaterial) return;
            Transform head = participantCamera.transform;
            UsingGaze = centerMode == CenterMode.GazeCentered && gazeUsable;
            double now = Time.realtimeSinceStartupAsDouble;
            if(UsingGaze && !wasUsingGaze && hasWindowGaze)
                recoverySmoothingUntil=now+RecoverySmoothingSeconds;
            wasUsingGaze=UsingGaze;
            if(UsingGaze) { lastUsableGazeTime=now; hasWindowGaze=true; }
            HoldingGaze = centerMode == CenterMode.GazeCentered && !UsingGaze && smoothGazeWindow &&
                hasWindowGaze && now-lastUsableGazeTime<=maximumInvalidHoldSeconds;
            if (centerMode == CenterMode.GazeCentered && smoothGazeWindow)
            {
                // LateUpdate and before-render both apply the material, but filter
                // only once per frame. Reproject with the latest head pose each time.
                if (lastSmoothedFrame != Time.frameCount)
                {
                    Vector3 local = UsingGaze ? Quaternion.Inverse(head.rotation) * gazeDirectionWorld.normalized : Vector3.forward;
                    if(!HoldingGaze)
                        DisplayHeadDirection = smoother.Step(local, (float)(now - lastSmoothedTime),
                            smoothingMinimumHz, UsingGaze && now>=recoverySmoothingUntil ? smoothingSpeedCoefficient : 0, smoothingSpeedFilterHz);
                    lastSmoothedFrame = Time.frameCount;
                    lastSmoothedTime = now;
                }
            }
            else
            {
                ResetSmoothing();
                DisplayHeadDirection = UsingGaze ? Quaternion.Inverse(head.rotation) * gazeDirectionWorld.normalized : Vector3.forward;
            }
            EffectiveCenterMode = UsingGaze ? "GazeCentered" : HoldingGaze ? "HeldGaze" :
                centerMode==CenterMode.GazeCentered && Vector3.Angle(DisplayHeadDirection,Vector3.forward)>.1f ? "HeadCenteredTransition" : "HeadCentered";
            Vector3 direction = head.rotation * DisplayHeadDirection;
            DisplayAppliedFrame = Time.frameCount;
            Material material = overlay.overlayMaterial;
            material.SetVector("_HeadRightWS", head.right);
            material.SetVector("_HeadUpWS", head.up);
            material.SetVector("_HeadForwardWS", head.forward);
            material.SetVector("_GazeDirectionWS", direction);
            material.SetFloat("_PlaneDistance", planeDistance);
            material.SetFloat("_PlaneWidth", planeWidth);
            ApplyPerEye(head, direction, material, now);
        }

        /// <summary>Use true monocular gaze directions when supplied; otherwise use the binocular gaze for both eyes.</summary>
        void ApplyPerEye(Transform head, Vector3 direction, Material material, double now)
        {
            GazeSampleAge = gazeSource ? gazeSource.SampleAge : double.PositiveInfinity;
            Vector3 left = direction.normalized;
            Vector3 right = direction.normalized;
            MonocularGazeUsed = false;

            // Neon’s dual-monocular gaze points provide separate gaze directions.
            // Its eye-state optical axes point toward the pupils; Pupil Labs documents
            // that they are not gaze rays, so do not infer fixation depth from them.
            if (usePerEyeMonocularGaze && UsingGaze && gazeSource)
            {
                var f = gazeSource.Current;
                if (f.valid && f.dualAvailable)
                {
                    if (f.leftValid) left = f.leftRay.direction.normalized;
                    if (f.rightValid) right = f.rightRay.direction.normalized;
                    MonocularGazeUsed = f.leftValid || f.rightValid;
                }
            }

            // Smooth in head-local coordinates so headset rotation is applied at render time.
            // Most importantly, these are angles only: fixation distance never scales the field.
            if (centerMode == CenterMode.GazeCentered && smoothGazeWindow)
            {
                if (lastEyeSmoothedFrame != Time.frameCount)
                {
                    Quaternion worldToHead = Quaternion.Inverse(head.rotation);
                    float dt = lastEyeSmoothedTime <= 0 ? 0 : (float)(now - lastEyeSmoothedTime);
                    float speedCoefficient = UsingGaze && now >= recoverySmoothingUntil ? smoothingSpeedCoefficient : 0;
                    filteredLeftHeadLocal = leftEyeSmoother.Step(worldToHead * left, dt, smoothingMinimumHz, speedCoefficient, smoothingSpeedFilterHz);
                    filteredRightHeadLocal = rightEyeSmoother.Step(worldToHead * right, dt, smoothingMinimumHz, speedCoefficient, smoothingSpeedFilterHz);
                    lastEyeSmoothedFrame = Time.frameCount;
                    lastEyeSmoothedTime = now;
                }
                // Reproject using the freshest head pose on both LateUpdate and before-render.
                LeftEyeGazeWorld = head.rotation * filteredLeftHeadLocal;
                RightEyeGazeWorld = head.rotation * filteredRightHeadLocal;
            }
            else
            {
                LeftEyeGazeWorld = left;
                RightEyeGazeWorld = right;
            }
            Blackout = trackingLossPolicy == TrackingLossPolicy.BlackOut && centerMode == CenterMode.GazeCentered && !UsingGaze && !HoldingGaze;
            material.SetVector("_GazeLeftWS", LeftEyeGazeWorld);
            material.SetVector("_GazeRightWS", RightEyeGazeWorld);
            material.SetFloat("_Blackout", Blackout ? 1f : 0f);
        }

    }
}
