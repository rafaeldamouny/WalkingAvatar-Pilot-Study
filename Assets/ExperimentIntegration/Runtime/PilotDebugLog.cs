#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace WalkingEyeExperiment
{
    /// <summary>Editor only: copies pilot/mocap messages, errors and exceptions to Logs/pilot-debug.log for remote debugging.</summary>
    static class PilotDebugLog
    {
        static string path;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            path = Path.Combine(Application.dataPath, "..", "Logs", "pilot-debug.log");
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, $"--- Play {DateTime.Now:o}\n"); } catch { }
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }
        static void OnLog(string msg, string stack, LogType type)
        {
            bool ours = msg.Contains("[Mocap") || msg.Contains("[Pilot]");
            if (!ours && type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            try { lock (typeof(PilotDebugLog)) File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {type}: {msg}\n" + (type == LogType.Exception || type == LogType.Error ? stack + "\n" : "")); } catch { }
        }
    }
}
#endif
