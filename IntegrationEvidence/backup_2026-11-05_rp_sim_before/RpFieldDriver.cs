using UnityEngine;
using PassthroughCameraSamples.MultiObjectDetection;

namespace WalkingEyeExperiment
{
    [DefaultExecutionOrder(500)]
    public class RpFieldDriver : MonoBehaviour
    {
        public enum CenterMode { HeadCentered, GazeCentered }
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
        }
    }
}
