using System;
using System.Globalization;
using System.IO;
using System.Text;
using PassthroughCameraSamples.MultiObjectDetection;
using UnityEngine;

namespace WalkingEyeExperiment
{
    /// <summary>
    /// Per-frame RP simulation log (one CSV per run in persistentDataPath/RPSimulation/). Time base is
    /// Time.realtimeSinceStartupAsDouble, the same "unity_realtime_s" clock as ResearchSessionLogger, so rows can be joined
    /// with the gaze/trial log and walking data. Gaze directions are head-local yaw/pitch in degrees (+yaw right, +pitch up).
    /// "gaze_sample_age_s" is the age of the Neon sample when the mask was applied (network + processing delay is not
    /// included; true end-to-end latency needs a photodiode/high-speed-camera test).
    /// </summary>
    [DefaultExecutionOrder(600)]
    public sealed class RpSimulationLog : MonoBehaviour
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        RpFieldDriver driver;
        StreamWriter writer;
        string lastParams = "";
        public string FilePath { get; private set; }

        void Start()
        {
            driver = GetComponent<RpFieldDriver>();
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "RPSimulation");
                Directory.CreateDirectory(dir);
                FilePath = Path.Combine(dir, $"rp_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                writer = new StreamWriter(FilePath, false, new UTF8Encoding(false));
                writer.WriteLine("record_type,unity_realtime_s,unity_frame,vision_type,field_model,map_id,map_synthetic,map_origin," +
                                 "tunnel_fov_deg,edge_deg,contrast_veil,requested_center,effective_center,gaze_usable,holding,blackout,loss_policy," +
                                 "gaze_sample_age_s,monocular_gaze_used,cyclopean_yaw_deg,cyclopean_pitch_deg," +
                                 "left_yaw_deg,left_pitch_deg,right_yaw_deg,right_pitch_deg,head_yaw_deg,head_pitch_deg,head_roll_deg");
                Debug.Log("[RPSim] Simulation log: " + FilePath);
            }
            catch (Exception e) { Debug.LogWarning("[RPSim] Could not open simulation log: " + e.Message); enabled = false; }
        }

        void LateUpdate()
        {
            if (writer == null || !driver || !driver.overlay || !driver.participantCamera) return;
            var o = driver.overlay;
            var head = driver.participantCamera.transform;
            var island = driver.Island && driver.Island.isActiveAndEnabled ? driver.Island : null;
            string model = island ? "RPIsland_" + island.StageName.Split(' ')[0] + "_CS" + F(island.ContrastLossLog) : o.fieldModel.ToString();
            string pars = string.Join(",", o.AppliedVisionType, model, Csv(o.ActiveMapId), o.ActiveMapSynthetic ? 1 : 0, Csv(o.ActiveMapOrigin),
                F(o.tunnelFovDegrees), F(o.tunnelEdgeDegrees), F(o.contrastVeil));
            string type = pars != lastParams ? "params_changed" : "frame";
            lastParams = pars;
            Quaternion inv = Quaternion.Inverse(head.rotation);
            var sb = new StringBuilder(256);
            sb.Append(type).Append(',').Append(Time.realtimeSinceStartupAsDouble.ToString("0.######", C)).Append(',').Append(Time.frameCount).Append(',')
              .Append(pars).Append(',').Append(driver.centerMode).Append(',').Append(driver.EffectiveCenterMode).Append(',')
              .Append(driver.UsingGaze ? 1 : 0).Append(',').Append(driver.HoldingGaze ? 1 : 0).Append(',').Append(driver.Blackout ? 1 : 0).Append(',')
              .Append(driver.trackingLossPolicy).Append(',')
              .Append(double.IsInfinity(driver.GazeSampleAge) ? "" : driver.GazeSampleAge.ToString("0.#####", C)).Append(',')
              .Append(driver.MonocularGazeUsed ? 1 : 0).Append(',');
            YawPitch(sb, inv * (head.rotation * driver.DisplayHeadDirection)); sb.Append(',');
            YawPitch(sb, inv * driver.LeftEyeGazeWorld); sb.Append(',');
            YawPitch(sb, inv * driver.RightEyeGazeWorld); sb.Append(',');
            Vector3 e = head.eulerAngles;
            sb.Append(F(Mathf.DeltaAngle(0, e.y))).Append(',').Append(F(-Mathf.DeltaAngle(0, e.x))).Append(',').Append(F(Mathf.DeltaAngle(0, e.z)));
            writer.WriteLine(sb.ToString());
            if (Time.frameCount % 90 == 0) writer.Flush();
        }

        static void YawPitch(StringBuilder sb, Vector3 local)
        {
            local.Normalize();
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;
            sb.Append(F(yaw)).Append(',').Append(F(pitch));
        }
        static string F(float v) => v.ToString("0.###", C);
        static string Csv(string s) => string.IsNullOrEmpty(s) ? "" : (s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s);

        void OnApplicationPause(bool paused) { if (paused) writer?.Flush(); }
        void OnDestroy() { try { writer?.Flush(); writer?.Dispose(); } catch { } writer = null; }
    }
}
