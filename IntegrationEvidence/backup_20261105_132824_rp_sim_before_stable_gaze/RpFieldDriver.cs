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
        [Range(.5f, 20f)] public float smoothingMinimumHz = 2f;
        [Range(0f, .2f)] public float smoothingSpeedCoefficient = .03f;
        [Range(.5f, 30f)] public float smoothingSpeedFilterHz = 8f;
        [Range(0f, .25f)] public float maximumInvalidHoldSeconds = .12f;
        [Header("RP simulation v2: per-eye fixation, tracking loss, diagnostics")]
        [Tooltip("Each eye's field is centred on that eye's line of sight to the fixation point (vergence), not one shared direction.")]
        public bool perEyeFixation = true;
        [Tooltip("Fixation distance used when no vergence estimate is available (m).")]
        [Range(.3f, 20f)] public float defaultFixationDistance = 3f;
        [Tooltip("Estimate fixation distance from Neon's per-eye eyeball centres and optical axes (eye-state data), when streamed.")]
        public bool vergenceFromDualRays = true;
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
        public float FixationDistance { get; private set; } = 3f;
        public Vector3 LeftEyeGazeWorld { get; private set; } = Vector3.forward;
        public Vector3 RightEyeGazeWorld { get; private set; } = Vector3.forward;
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
        int lastSmoothedFrame = -1;
        double lastSmoothedTime;
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
        void ResetSmoothing() { smoother.Reset(); lastSmoothedFrame = -1; lastSmoothedTime = 0; hasWindowGaze=false; HoldingGaze=false; wasUsingGaze=false; recoverySmoothingUntil=0; }
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

        float smoothedVergence = -1f;
        double lastVergenceTime;

        /// <summary>Per-eye gaze directions toward the fixation point, and the tracking-loss blackout flag.</summary>
        void ApplyPerEye(Transform head, Vector3 direction, Material material, double now)
        {
            GazeSampleAge = gazeSource ? gazeSource.SampleAge : double.PositiveInfinity;
            float distance = defaultFixationDistance;
            if (vergenceFromDualRays && UsingGaze && gazeSource)
            {
                var f = gazeSource.Current;
                if (f.valid && f.raw.type >= PupilLabs.EtDataType.EyeStateGazeData && TryVergence(f.raw.eyeState, out float d))
                {
                    float dt = (float)(now - lastVergenceTime);
                    float a = smoothedVergence < 0f || dt > .5f ? 1f : 1f - Mathf.Exp(-dt * 2f * Mathf.PI * 3f); // ~3 Hz low-pass
                    smoothedVergence = smoothedVergence < 0f ? d : Mathf.Lerp(smoothedVergence, d, a);
                    lastVergenceTime = now;
                }
                if (smoothedVergence > 0f) distance = smoothedVergence;
            }
            FixationDistance = distance;
            Vector3 dir = direction.normalized;
            if (perEyeFixation && participantCamera)
            {
                float halfIpd = Mathf.Clamp(participantCamera.stereoSeparation > 1e-4f ? participantCamera.stereoSeparation : .063f, .045f, .08f) * .5f;
                Vector3 fixation = head.position + dir * distance;
                LeftEyeGazeWorld = (fixation - (head.position - head.right * halfIpd)).normalized;
                RightEyeGazeWorld = (fixation - (head.position + head.right * halfIpd)).normalized;
            }
            else LeftEyeGazeWorld = RightEyeGazeWorld = dir;
            Blackout = trackingLossPolicy == TrackingLossPolicy.BlackOut && centerMode == CenterMode.GazeCentered && !UsingGaze && !HoldingGaze;
            material.SetVector("_GazeLeftWS", LeftEyeGazeWorld);
            material.SetVector("_GazeRightWS", RightEyeGazeWorld);
            material.SetFloat("_Blackout", Blackout ? 1f : 0f);
        }

        /// <summary>Fixation distance from the two eyes' optical axes (Neon eye state, scene-camera coordinates):
        /// distance from the midpoint between the eyeball centres to the axes' closest approach.</summary>
        static bool TryVergence(PupilLabs.EyeState es, out float distance)
        {
            distance = 0f;
            Vector3 cl = es.eyeballCenterLeft, cr = es.eyeballCenterRight, dl = es.opticalAxisLeft, dr = es.opticalAxisRight;
            float sep = Vector3.Distance(cl, cr);
            if (!(sep > 1e-6f) || dl.sqrMagnitude < 1e-8f || dr.sqrMagnitude < 1e-8f) return false;
            float unit = sep > 1f ? .001f : 1f;            // Neon reports millimetres; accept metres too
            dl.Normalize(); dr.Normalize();
            Vector3 w0 = cl - cr;
            float b = Vector3.Dot(dl, dr), d = Vector3.Dot(dl, w0), e = Vector3.Dot(dr, w0), den = 1f - b * b;
            if (den < 1e-6f) return false;                // parallel axes: far fixation, keep the default
            float s = (b * e - d) / den, t = (e - b * d) / den;
            if (s <= 0f || t <= 0f) return false;         // diverging axes
            Vector3 p = (cl + dl * s + cr + dr * t) * .5f;
            distance = Mathf.Clamp(Vector3.Distance((cl + cr) * .5f, p) * unit, .3f, 20f);
            return true;
        }
    }
}
