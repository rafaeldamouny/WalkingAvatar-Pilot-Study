using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace WalkingEyeExperiment.Editor
{
    /// <summary>
    /// Editor-only helper: when the file Temp/remote_quest_build.request exists, builds the Quest APK with the project's
    /// own pipeline (IntegrationBuild.BuildQuest), installs it on the USB-connected Quest with adb, launches it, and writes
    /// a report to IntegrationEvidence/remote-quest-build.txt. Same result as the menu Experiment Integration > Build Quest APK
    /// followed by a manual install.
    /// </summary>
    [InitializeOnLoad]
    static class RemoteQuestBuild
    {
        const string RequestPath = "Temp/remote_quest_build.request";
        const string ReportPath = "IntegrationEvidence/remote-quest-build.txt";
        const string Apk = "Builds/NeonWalkingExperiment.apk";
        static double next;

        static RemoteQuestBuild() { EditorApplication.update += Poll; }

        [MenuItem("Experiment Integration/Build, Install and Run on Quest")]
        static void MenuBuild() => Run();

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 2;
            if (!File.Exists(RequestPath)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Report("Stopping Play mode before building...");
                EditorApplication.isPlaying = false;
                return;
            }
            try { File.Delete(RequestPath); } catch { }
            Run();
        }

        static void Report(string text)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)); File.WriteAllText(ReportPath, $"{DateTime.Now:o}\n{text}\n"); } catch { }
        }

        static void Run()
        {
            var log = new StringBuilder();
            Report("Building...");
            try
            {
                EditorSceneManager.SaveOpenScenes();
                IntegrationBuild.BuildQuest();
                var info = new FileInfo(Apk);
                log.AppendLine($"BUILD OK: {Apk} {info.Length / 1e6:0.0} MB {info.LastWriteTime:o}");
            }
            catch (Exception e)
            {
                log.AppendLine("BUILD FAILED: " + e.Message);
                Report(log.ToString());
                UnityEngine.Debug.LogError("[RemoteQuestBuild] " + e);
                return;
            }
            string adb = FindAdb();
            log.AppendLine("adb: " + (adb ?? "not found"));
            if (adb == null) { Report(log.ToString()); return; }
            log.AppendLine("--- adb devices -l\n" + Exec(adb, "devices -l"));
            string pkg = PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android);
            log.AppendLine("--- install\n" + Exec(adb, $"install -r -g \"{Path.GetFullPath(Apk)}\""));
            log.AppendLine("--- launch " + pkg + "\n" + Exec(adb, $"shell monkey -p {pkg} -c android.intent.category.LAUNCHER 1"));
            Report(log.ToString());
            UnityEngine.Debug.Log("[RemoteQuestBuild]\n" + log);
        }

        static string FindAdb()
        {
            try
            {
                string root = UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath;
                if (!string.IsNullOrEmpty(root))
                {
                    string p = Path.Combine(root, "platform-tools", "adb.exe");
                    if (File.Exists(p)) return p;
                    p = Path.Combine(root, "platform-tools", "adb");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        static string Exec(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(240000)) { p.Kill(); return "TIMEOUT\n" + o + e; }
                    return $"exit {p.ExitCode}\n{o}{e}";
                }
            }
            catch (Exception ex) { return "ERROR " + ex.Message; }
        }
    }
}
