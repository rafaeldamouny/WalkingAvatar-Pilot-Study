using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace PerceptionPilot
{
    /// <summary>One CSV row per trial. On Quest the file is in
    /// /sdcard/Android/data/&lt;package&gt;/files/PerceptionPilot/</summary>
    public class TrialLogger : IDisposable
    {
        static readonly string[] Header =
        {
            "participant","seed","block_index","block_label","avatar_mode","field",
            "trial_in_block","trial_global","is_practice","is_catch","stim_source",
            "maneuver","true_category","true_angle_deg","clip","walker","take",
            "t0_s","t0_is_pseudo","cutoff_ms","t_start_s","t_end_s","lead_in_s",
            "target_D_m","actual_D_m","actual_lateral_m",
            "resp_category","resp_has_angle","resp_angle_deg","correct","angle_error_deg","rt_s",
            "stim_on_rt","cutoff_rt","response_rt","stim_on_utc","cutoff_utc","response_utc"
        };

        readonly StreamWriter _w;
        public string FilePath { get; }

        public TrialLogger(int participantId)
        {
            string dir = Path.Combine(Application.persistentDataPath, "PerceptionPilot");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, $"P{participantId:D3}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _w = new StreamWriter(FilePath, false, new UTF8Encoding(false));
            _w.WriteLine(string.Join(",", Header));
            _w.Flush();
            Debug.Log("[PerceptionPilot] Logging to " + FilePath);
        }

        public void Write(params object[] values)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Format(values[i]));
            }
            _w.WriteLine(sb.ToString());
            _w.Flush(); // survive crashes / headset removal
        }

        static string Format(object v)
        {
            switch (v)
            {
                case null: return "";
                case float f: return float.IsNaN(f) ? "" : f.ToString("0.#####", CultureInfo.InvariantCulture);
                case double d: return double.IsNaN(d) ? "" : d.ToString("0.######", CultureInfo.InvariantCulture);
                case bool b: return b ? "1" : "0";
                case string s:
                    return (s.Contains(",") || s.Contains("\"")) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        public void Dispose()
        {
            _w?.Flush();
            _w?.Dispose();
        }
    }
}
