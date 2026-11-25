using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using PupilLabs;

namespace WalkingEyeExperiment
{
    [DefaultExecutionOrder(600)]
    public class ResearchSessionLogger : MonoBehaviour
    {
        public NeonGazeBridge gaze;
        public Transform avatar;
        public string participantId = "pilot";
        public string trialId = "platform_validation";
        public string condition = "RP_10deg";
        public string avatarExemplar = "casual-f-0170";
        public float cutoffTime = -1;
        public bool autoStart = false;
        public bool allowSyntheticRecording;
        public bool Recording { get; private set; }
        public string LogPath { get; private set; }
        public string Error { get; private set; }
        public long RowsWritten { get; private set; }
        ResearchCsvWriter writer;
        double trialStart, nextFlush;
        long previousSequence = -1;
        bool samplePending;
        GazeFrame pendingSample;
        string participantResponse = "", correctResponse = "", responseCorrect = "", responseTime = "";
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        public static string[] Columns
        {
            get
            {
                var cols = new List<string> { "schema_version", "record_type", "timestamp", "unity_realtime_s", "sdk_timestamp_ms", "sdk_rtcp_synchronized", "sample_sequence", "sample_age_s", "unity_frame", "participant_id", "trial_id", "condition", "avatar_exemplar", "cutoff_time", "trial_elapsed_s", "avatar_heading_deg", "gaze_has_sample", "gaze_validity", "gaze_confidence", "gaze_confidence_available", "gaze_invalid_reason", "gaze_simulated", "gaze_worn", "sdk_payload_type", "gaze_ray_kind", "gaze_origin_kind", "companion_gaze_mode", "calibration_id", "mount_calibration_verified", "rp_requested_center", "rp_effective_center", "rp_fallback", "rp_vision_type", "rp_tunnel_fov_deg", "rp_global_alpha", "rp_blur_amount", "rp_vignette_amount", "rp_vignette_radius", "gaze_hit_object", "gaze_body_region", "gaze_hit", "participant_response", "correct_response", "response_correct", "response_time", "dual_monocular_available", "left_ray_valid", "right_ray_valid", "eye_state_available", "eyelid_available", "pupil_left_mm", "pupil_right_mm", "eyelid_aperture_left_mm", "eyelid_aperture_right_mm", "eyelid_top_left_rad", "eyelid_bottom_left_rad", "eyelid_top_right_rad", "eyelid_bottom_right_rad", "sdk_gaze_x_px", "sdk_gaze_y_px", "sdk_left_gaze_x_px", "sdk_left_gaze_y_px", "sdk_right_gaze_x_px", "sdk_right_gaze_y_px" };
                cols.AddRange(new[] { "rp_smoothing_enabled", "rp_smoothing_minimum_hz", "rp_smoothing_speed_coefficient", "rp_smoothing_speed_filter_hz", "rp_invalid_hold_seconds", "rp_display_applied_unity_frame" });
                foreach (var prefix in new[] { "avatar_position", "head_position", "gaze_origin", "gaze_direction", "gaze_hit_point", "left_gaze_origin", "left_gaze_direction", "right_gaze_origin", "right_gaze_direction", "sensor_left_eyeball_center_m", "sensor_right_eyeball_center_m", "sensor_left_optical_axis", "sensor_right_optical_axis", "rp_display_head_direction" })
                    foreach (var axis in new[] { "x", "y", "z" }) cols.Add(prefix + "_" + axis);
                foreach (var axis in new[] { "x", "y", "z", "w" }) cols.Add("head_rotation_" + axis);
                return cols.ToArray();
            }
        }
        void OnEnable() { if (gaze) gaze.SampleReady += OnSample; }
        void Start() { if (autoStart) StartRecording(); }
        void OnDisable() { if (gaze) gaze.SampleReady -= OnSample; StopRecording(); }
        void OnApplicationPause(bool paused) { if (paused) Flush(); }
        void OnApplicationQuit() { StopRecording(); }
        void Update()
        {
            if (Recording && Time.realtimeSinceStartupAsDouble >= nextFlush)
            { Flush(); nextFlush=Time.realtimeSinceStartupAsDouble+1; }
        }
        void LateUpdate() { WritePendingSample(); } // after the window's display filter
        [ContextMenu("Start Recording")]
        public void StartRecording()
        {
            if (Recording) return;
            try
            {
                string directory=Path.Combine(Application.persistentDataPath,"ResearchLogs");
                Directory.CreateDirectory(directory);
                LogPath=Path.Combine(directory,SafeFilePart(participantId)+"_session_"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff",Culture)+"_"+Guid.NewGuid().ToString("N")+".csv");
                writer=new ResearchCsvWriter(LogPath,Columns);
                Recording=true; Error=""; RowsWritten=0;
                trialStart=Time.realtimeSinceStartupAsDouble;
                Write("session_start",default);
                Flush();
                Debug.Log("[ResearchLogs] CSV: "+LogPath,this);
            }
            catch (Exception e) { Fail(e); }
        }
        [ContextMenu("Stop Recording")]
        public void StopRecording()
        {
            WritePendingSample();
            if (writer==null) return;
            if (Recording) Write("session_end",gaze ? gaze.Current : default);
            try { writer.Dispose(); } catch(Exception e) { Fail(e); }
            writer=null; Recording=false;
        }
        public void BeginTrial(string id, string newCondition, string exemplar, float cutoff = -1, string expectedResponse = "")
        {
            WritePendingSample();
            trialId=id; condition=newCondition; avatarExemplar=exemplar; cutoffTime=cutoff;
            trialStart=Time.realtimeSinceStartupAsDouble;
            participantResponse=""; correctResponse=expectedResponse; responseCorrect=""; responseTime="";
            Write("trial_start",gaze ? gaze.Current : default); Flush();
        }
        public void RecordResponse(string response)
        {
            WritePendingSample();
            participantResponse=response;
            responseTime=Number(Time.realtimeSinceStartupAsDouble-trialStart);
            responseCorrect=string.IsNullOrEmpty(correctResponse) ? "" : Bool(string.Equals(response,correctResponse,StringComparison.OrdinalIgnoreCase));
            Write("response",gaze ? gaze.Current : default); Flush();
        }
        public void EndTrial() { WritePendingSample(); Write("trial_end",gaze ? gaze.Current : default); Flush(); }
        void OnSample(GazeFrame sample)
        {
            if (sample.simulated && !allowSyntheticRecording) return;
            pendingSample=sample; samplePending=true;
        }
        void WritePendingSample()
        {
            if(!samplePending) return;
            var sample=pendingSample;
            samplePending=false;
            Write(sample.hasSample && sample.sequence != previousSequence ? "gaze_sample" : "tracking_status",sample);
            previousSequence=sample.sequence;
        }
        void Fail(Exception e)
        {
            Recording=false; Error=e.Message;
            Debug.LogError("[ResearchLogs] Recording stopped: "+e.Message,this);
        }
        void Flush()
        { if(writer!=null) try { writer.Flush(); } catch(Exception e) { Fail(e); } }
        static string Bool(bool v) => v ? "1" : "0";
        static string SafeFilePart(string value)
        {
            if(string.IsNullOrWhiteSpace(value)) return "participant";
            var chars=value.Trim().ToCharArray();
            for(int i=0;i<chars.Length;i++) if(!char.IsLetterOrDigit(chars[i]) && chars[i]!='-' && chars[i]!='_') chars[i]='_';
            return new string(chars);
        }
        static string Number(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString("R",Culture);
        static void Vector(Dictionary<string,string> row, string prefix, Vector3 value)
        { row[prefix+"_x"]=Number(value.x); row[prefix+"_y"]=Number(value.y); row[prefix+"_z"]=Number(value.z); }

        void Write(string recordType, GazeFrame f)
        {
            if (!Recording || writer==null) return;
            var row=new Dictionary<string,string> {
                ["schema_version"]="3", ["record_type"]=recordType, ["timestamp"]=DateTime.UtcNow.ToString("O",Culture),
                ["unity_realtime_s"]=Number(Time.realtimeSinceStartupAsDouble), ["unity_frame"]=Time.frameCount.ToString(Culture),
                ["participant_id"]=participantId, ["trial_id"]=trialId, ["condition"]=condition, ["avatar_exemplar"]=avatarExemplar,
                ["cutoff_time"]=cutoffTime<0 ? "" : Number(cutoffTime), ["trial_elapsed_s"]=Number(Time.realtimeSinceStartupAsDouble-trialStart),
                ["gaze_has_sample"]=Bool(f.hasSample), ["gaze_validity"]=Bool(f.hasSample && f.valid && !f.simulated), ["gaze_confidence_available"]="0",
                ["gaze_invalid_reason"]=f.invalidReason, ["gaze_simulated"]=Bool(f.simulated), ["gaze_hit"]=Bool(f.hit),
                ["gaze_hit_object"]=f.hitObject, ["gaze_body_region"]=f.valid ? f.bodyRegion : "None",
                ["participant_response"]=participantResponse, ["correct_response"]=correctResponse, ["response_correct"]=responseCorrect, ["response_time"]=responseTime,
                ["mount_calibration_verified"]=Bool(f.calibrated), ["gaze_origin_kind"]=f.simulated ? "head_debug_origin" : "calibrated_neon_scene_camera_proxy",
                ["gaze_ray_kind"]=f.rayKind
            };
            if (avatar) { Vector(row,"avatar_position",avatar.position); row["avatar_heading_deg"]=Number(avatar.eulerAngles.y); }
            if (gaze && gaze.participantCamera)
            {
                row["calibration_id"]=gaze.calibrationId; row["companion_gaze_mode"]=gaze.companionGazeMode;
                var head=gaze.participantCamera.transform;
                Vector(row,"head_position",f.hasSample ? f.headPosition : head.position);
                Quaternion rotation=f.hasSample ? f.headRotation : head.rotation;
                row["head_rotation_x"]=Number(rotation.x); row["head_rotation_y"]=Number(rotation.y);
                row["head_rotation_z"]=Number(rotation.z); row["head_rotation_w"]=Number(rotation.w);
                if (gaze.rpField)
                {
                    var rp=gaze.rpField;
                    bool useGaze=rp.centerMode==RpFieldDriver.CenterMode.GazeCentered && f.valid;
                    row["rp_requested_center"]=rp.centerMode.ToString(); row["rp_effective_center"]=rp.EffectiveCenterMode;
                    row["rp_fallback"]=Bool(rp.centerMode==RpFieldDriver.CenterMode.GazeCentered && !useGaze);
                    row["rp_smoothing_enabled"]=Bool(rp.smoothGazeWindow);
                    row["rp_smoothing_minimum_hz"]=Number(rp.smoothingMinimumHz);
                    row["rp_smoothing_speed_coefficient"]=Number(rp.smoothingSpeedCoefficient);
                    row["rp_smoothing_speed_filter_hz"]=Number(rp.smoothingSpeedFilterHz);
                    row["rp_invalid_hold_seconds"]=Number(rp.maximumInvalidHoldSeconds);
                    row["rp_display_applied_unity_frame"]=rp.DisplayAppliedFrame.ToString(Culture);
                    Vector(row,"rp_display_head_direction",rp.DisplayHeadDirection);
                    var overlay=rp.overlay;
                    row["rp_vision_type"]=overlay.currentVisionType.ToString(); row["rp_tunnel_fov_deg"]=Number(overlay.tunnelFovDegrees);
                    var mat=overlay.overlayMaterial;
                    if(mat) { row["rp_global_alpha"]=Number(mat.GetFloat("_GlobalAlpha")); row["rp_blur_amount"]=Number(mat.GetFloat("_BlurAmount")); row["rp_vignette_amount"]=Number(mat.GetFloat("_VignetteAmount")); row["rp_vignette_radius"]=Number(mat.GetFloat("_VignetteRadius")); }
                }
            }
            if (f.hasSample)
            {
                row["sample_sequence"]=f.sequence.ToString(Culture);
                row["sample_age_s"]=Number(Time.realtimeSinceStartupAsDouble-f.receivedRealtime);
                if(!f.simulated) { row["sdk_timestamp_ms"]=f.raw.timestampMs.ToString(Culture); row["sdk_rtcp_synchronized"]=Bool(f.raw.rtcpSynchronized); }
                row["gaze_worn"]=Bool(f.raw.worn); row["sdk_payload_type"]=f.raw.type.ToString();
                row["sdk_gaze_x_px"]=Number(f.raw.gazePoint.x); row["sdk_gaze_y_px"]=Number(f.raw.gazePoint.y);
                Vector(row,"gaze_origin",f.ray.origin); Vector(row,"gaze_direction",f.ray.direction);
                row["dual_monocular_available"]=Bool(f.dualAvailable); row["left_ray_valid"]=Bool(f.dualAvailable && f.leftValid && f.valid); row["right_ray_valid"]=Bool(f.dualAvailable && f.rightValid && f.valid);
                if(f.hit) Vector(row,"gaze_hit_point",f.hitPoint);
                if(f.dualAvailable)
                {
                    row["sdk_left_gaze_x_px"]=Number(f.raw.gazePointDualLeft.x); row["sdk_left_gaze_y_px"]=Number(f.raw.gazePointDualLeft.y);
                    row["sdk_right_gaze_x_px"]=Number(f.raw.gazePointDualRight.x); row["sdk_right_gaze_y_px"]=Number(f.raw.gazePointDualRight.y);
                    Vector(row,"left_gaze_origin",f.leftRay.origin); Vector(row,"left_gaze_direction",f.leftRay.direction);
                    Vector(row,"right_gaze_origin",f.rightRay.origin); Vector(row,"right_gaze_direction",f.rightRay.direction);
                }
                bool eyeState=f.raw.type>=EtDataType.EyeStateGazeData;
                bool eyelid=f.raw.type>=EtDataType.EyeStateEyelidGazeData;
                row["eye_state_available"]=Bool(eyeState); row["eyelid_available"]=Bool(eyelid);
                if (eyeState)
                {
                    row["pupil_left_mm"]=Number(f.raw.eyeState.pupilDiameterLeft * 1000.0); row["pupil_right_mm"]=Number(f.raw.eyeState.pupilDiameterRight * 1000.0);
                    Vector(row,"sensor_left_eyeball_center_m",f.raw.eyeState.eyeballCenterLeft); Vector(row,"sensor_right_eyeball_center_m",f.raw.eyeState.eyeballCenterRight);
                    Vector(row,"sensor_left_optical_axis",f.raw.eyeState.opticalAxisLeft); Vector(row,"sensor_right_optical_axis",f.raw.eyeState.opticalAxisRight);
                }
                if(eyelid)
                {
                    row["eyelid_aperture_left_mm"]=Number(f.raw.eyelid.eyelidApertureLeft); row["eyelid_aperture_right_mm"]=Number(f.raw.eyelid.eyelidApertureRight);
                    row["eyelid_top_left_rad"]=Number(f.raw.eyelid.eyelidAngleTopLeft * Mathf.Deg2Rad); row["eyelid_bottom_left_rad"]=Number(f.raw.eyelid.eyelidAngleBottomLeft * Mathf.Deg2Rad);
                    row["eyelid_top_right_rad"]=Number(f.raw.eyelid.eyelidAngleTopRight * Mathf.Deg2Rad); row["eyelid_bottom_right_rad"]=Number(f.raw.eyelid.eyelidAngleBottomRight * Mathf.Deg2Rad);
                }
            }
            try { writer.Write(row); RowsWritten++; } catch(Exception e) { Fail(e); }
        }
    }
}

