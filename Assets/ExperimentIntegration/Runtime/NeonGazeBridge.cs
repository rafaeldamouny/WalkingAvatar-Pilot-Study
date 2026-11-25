using System;
using UnityEngine;
using PupilLabs;

namespace WalkingEyeExperiment
{
    [DefaultExecutionOrder(300)]
    public class NeonGazeBridge : MonoBehaviour
    {
        public NeonGazeDataProvider provider;
        public DataStorage storage;
        public Camera participantCamera;
        public RpFieldDriver rpField;
        public AvatarRegionRig avatarRegions;
        [Tooltip("Enable ONLY after mount calibration is saved and accuracy verified in this application.")]
        public bool mountCalibrationVerified;
        public string calibrationId = "unverified";
        [Tooltip("Record the primary gaze mode selected on the Companion phone; the SDK does not identify it.")]
        public string companionGazeMode = "unverified";
        [Range(.02f, 2)] public float maximumSampleAge = .25f;
        public bool debugVisualization = true;
        [Tooltip("Show an unverified ray ONLY for calibration checking. It cannot control RP or count as valid research gaze.")]
        public bool visualizeUnverifiedCalibration = true;
        public Material debugMaterial;
        public float raycastDistance = 30f;
        public LayerMask environmentMask = ~4; // proxies queried explicitly
        [Header("Explicit synthetic test (never research gaze)")]
        public bool useSyntheticGaze;
        public Vector2 syntheticAnglesDegrees;
        public GazeFrame Current { get; private set; }
        public event Action<GazeFrame> SampleReady;
        public double SampleAge => latest.hasSample ? Time.realtimeSinceStartupAsDouble - latest.receivedRealtime : double.PositiveInfinity;
        GazeFrame latest;
        bool pending;
        long sequence;
        double nextStatusTime;
        LineRenderer line;
        GameObject pointer;

        void OnEnable()
        {
            if (provider) provider.gazeDataReady.AddListener(OnGazeDataReady);
            Application.onBeforeRender += BeforeRender;
        }
        void OnDisable()
        {
            if (provider) provider.gazeDataReady.RemoveListener(OnGazeDataReady);
            Application.onBeforeRender -= BeforeRender;
        }
        void Start()
        {
            var go = new GameObject("Calibrated Gaze Debug Ray");
            go.transform.SetParent(transform, false);
            line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = line.endWidth = .004f;
            line.sharedMaterial = debugMaterial;
            pointer = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pointer.name = "Calibrated Gaze Hit";
            pointer.layer = 2;
            Destroy(pointer.GetComponent<Collider>());
            pointer.transform.SetParent(transform, false);
            pointer.transform.localScale = Vector3.one * .025f;
            pointer.GetComponent<Renderer>().sharedMaterial = debugMaterial;
            pointer.SetActive(false);
            line.enabled = false;
        }
        void OnGazeDataReady(GazeDataProvider source)
        {
            if (useSyntheticGaze || source != provider) return;
            // The vendored extension returns the exact snapshot already processed by the SDK.
            latest = new GazeFrame { sequence = ++sequence, raw = provider.CurrentGazeData,
                hasSample = true, simulated = provider.SimulationEnabled,
                receivedRealtime = Time.realtimeSinceStartupAsDouble };
            pending = true;
        }
        void LateUpdate()
        {
            if (useSyntheticGaze)
            {
                latest = new GazeFrame { sequence=++sequence, hasSample=true, simulated=true,
                    receivedRealtime=Time.realtimeSinceStartupAsDouble,
                    raw = new GazeData { worn=true, type=EtDataType.GazeData } };
                pending = true;
            }
            var frame = Resolve();
            Current = frame;
            SetField(frame);
            if (frame.valid)
            {
                Physics.SyncTransforms(); // animated proxies must be queried at this frame's pose
                DetectHit(ref frame);
                Current = frame;
            }
            Visualize(frame);
            if (pending || Time.realtimeSinceStartupAsDouble >= nextStatusTime)
            {
                SampleReady?.Invoke(frame);
                pending = false;
                nextStatusTime = Time.realtimeSinceStartupAsDouble + 1;
            }
        }
        [BeforeRenderOrder(100)]
        void BeforeRender() { SetField(Resolve()); }
        void SetField(GazeFrame frame)
        {
            if (!rpField) return;
            rpField.gazeUsable = frame.valid;
            rpField.gazeDirectionWorld = frame.ray.direction;
        }

        GazeFrame Resolve()
        {
            GazeFrame f = latest;
            f.valid = false;
            f.calibrated = mountCalibrationVerified;
            f.invalidReason = "no_sample";
            if (!participantCamera) { f.invalidReason="no_participant_camera"; return f; }
            Transform head = participantCamera.transform;
            f.headPosition = head.position; f.headRotation = head.rotation;
            if (!f.hasSample) return f;
            if (useSyntheticGaze)
            {
                f.ray = new Ray(head.position, head.rotation * Quaternion.Euler(-syntheticAnglesDegrees.y, syntheticAnglesDegrees.x, 0) * Vector3.forward);
                f.rayKind = "synthetic_head_origin";
                f.valid = true; f.invalidReason = "synthetic_debug";
                return f;
            }
            if (f.simulated) { f.invalidReason="sdk_simulation_rejected"; return f; }
            if (!storage || !storage.Ready) { f.invalidReason="sdk_not_ready"; return f; }
            Pose offset = provider.GazeOrigin;
            Vector3 localDir = CameraUtils.ImgPointToDir(f.raw.gazePoint, storage.CameraIntrinsics.cameraMatrix, storage.CameraIntrinsics.distortionCoefficients);
            f.dualAvailable = f.raw.type == EtDataType.DualMonocularGazeData || f.raw.type == EtDataType.BinoAndDualMonoGazeData || f.raw.type == EtDataType.EyeStateEyelidDualMonoGazeData;
            Vector3 origin = head.TransformPoint(offset.position);
            if (f.dualAvailable)
            {
                Vector3 l = CameraUtils.ImgPointToDir(f.raw.gazePointDualLeft, storage.CameraIntrinsics.cameraMatrix, storage.CameraIntrinsics.distortionCoefficients);
                Vector3 r = CameraUtils.ImgPointToDir(f.raw.gazePointDualRight, storage.CameraIntrinsics.cameraMatrix, storage.CameraIntrinsics.distortionCoefficients);
                f.leftValid = FiniteDirection(l) && Finite(f.raw.gazePointDualLeft.x) && Finite(f.raw.gazePointDualLeft.y);
                f.rightValid = FiniteDirection(r) && Finite(f.raw.gazePointDualRight.x) && Finite(f.raw.gazePointDualRight.y);
                f.leftRay = new Ray(origin, head.rotation * offset.rotation * l.normalized);
                f.rightRay = new Ray(origin, head.rotation * offset.rotation * r.normalized);
                if (f.raw.type == EtDataType.DualMonocularGazeData)
                {
                    localDir = f.leftValid && f.rightValid ? (l.normalized + r.normalized).normalized : Vector3.zero;
                    f.rayKind = "computed_mean_dual_sensor_origin";
                }
            }
            if (string.IsNullOrEmpty(f.rayKind)) f.rayKind = "sdk_primary_sensor_origin";
            f.ray = new Ray(origin, head.rotation * offset.rotation * localDir.normalized);
            if (!f.calibrated) f.invalidReason="mount_calibration_unverified";
            else if (!f.raw.worn) f.invalidReason="not_worn";
            else if (SampleAge > maximumSampleAge) f.invalidReason="stale_sample";
            else if (f.raw.type == EtDataType.Unknown || !FiniteDirection(localDir)) f.invalidReason="invalid_direction";
            else if (Vector3.Dot(f.ray.direction, head.forward) <= .01f) f.invalidReason="gaze_behind_head";
            else { f.valid=true; f.invalidReason=""; }
            return f;
        }

        void DetectHit(ref GazeFrame frame)
        {
            frame.hitObject = ""; frame.bodyRegion = "None"; frame.hit=false;
            bool environmentHit = Physics.Raycast(frame.ray, out var environment, raycastDistance, environmentMask, QueryTriggerInteraction.Ignore);
            float distance = environmentHit ? environment.distance : raycastDistance;
            if (avatarRegions && avatarRegions.Raycast(frame.ray, distance + .001f, out var avatarHit, out var region))
            {
                frame.hit = true; frame.hitPoint=avatarHit.point;
                frame.hitObject=region.avatarRoot.name + "/" + region.name;
                frame.bodyRegion=region.region.ToString();
            }
            else if (environmentHit)
            { frame.hit=true; frame.hitPoint=environment.point; frame.hitObject=environment.collider.name; }
        }
        void Visualize(GazeFrame frame)
        {
            if (!line || !pointer) return;
            bool checkingCalibration=visualizeUnverifiedCalibration && frame.hasSample && !frame.simulated && frame.raw.worn &&
                SampleAge<=maximumSampleAge && frame.invalidReason=="mount_calibration_unverified" && FiniteDirection(frame.ray.direction);
            bool visible=frame.valid || checkingCalibration;
            if(checkingCalibration) { Physics.SyncTransforms(); DetectHit(ref frame); }
            line.enabled = debugVisualization && visible;
            pointer.SetActive(debugVisualization && visible && frame.hit);
            if (!visible) return;
            line.SetPosition(0, frame.ray.origin + frame.ray.direction * .05f);
            line.SetPosition(1, frame.hit ? frame.hitPoint : frame.ray.GetPoint(2));
            if (frame.hit) pointer.transform.position = frame.hitPoint;
        }
        public static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        public static bool FiniteDirection(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z) && v.sqrMagnitude > 1e-8f;
    }
}
