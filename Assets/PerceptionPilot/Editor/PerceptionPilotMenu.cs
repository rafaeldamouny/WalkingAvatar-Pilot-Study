using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;

namespace PerceptionPilot.EditorTools
{
    public static class PerceptionPilotMenu
    {
        const string Root = "Assets/PerceptionPilot";
        const string LibPath = Root + "/ManeuverLibrary.asset";
        const string Menu = "Tools/Perception Pilot/";

        // Must match the trial manager defaults (used only for warnings).
        const float LeadIn = 2.0f, Jitter = 0.4f, EarliestCutoff = 0.5f;

        // ------------------------------------------------------------------ 1. import settings

        [MenuItem(Menu + "1. Set Humanoid + root-motion import on selected FBX")]
        static void ApplyImportSettings()
        {
            var paths = SelectedModelPaths();
            if (paths.Count == 0)
            {
                EditorUtility.DisplayDialog("Perception Pilot", "Select FBX files (or a folder) in the Project window first.", "OK");
                return;
            }

            int done = 0;
            foreach (var path in paths)
            {
                var imp = AssetImporter.GetAtPath(path) as ModelImporter;
                if (imp == null) continue;

                if (imp.animationType != ModelImporterAnimationType.Human)
                {
                    imp.animationType = ModelImporterAnimationType.Human;
                    imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                    imp.SaveAndReimport();
                }

                var clips = imp.clipAnimations;
                if (clips == null || clips.Length == 0) clips = imp.defaultClipAnimations;
                if (clips == null || clips.Length == 0) continue;

                foreach (var c in clips)
                {
                    c.loopTime = false;
                    // Root Transform Rotation: NOT baked, based on body orientation -> turns rotate the avatar.
                    c.lockRootRotation = false;
                    c.keepOriginalOrientation = false;
                    // Root Transform Position Y: NOT baked, based on feet -> stairs lift the avatar.
                    c.lockRootHeightY = false;
                    c.keepOriginalPositionY = false;
                    c.heightFromFeet = true;
                    // Root Transform Position XZ: NOT baked -> the recording moves the avatar.
                    c.lockRootPositionXZ = false;
                    c.keepOriginalPositionXZ = false;
                }
                imp.clipAnimations = clips;
                imp.SaveAndReimport();
                done++;
            }
            Debug.Log($"[PerceptionPilot] Import settings applied to {done} model file(s). " +
                      "Open one in the Inspector > Rig > Configure to check the bone mapping once.");
        }

        // ------------------------------------------------------------------ 2. library

        [MenuItem(Menu + "2. Build or update library from selected clips")]
        static void BuildLibrary()
        {
            var clips = SelectedClips();
            if (clips.Count == 0)
            {
                EditorUtility.DisplayDialog("Perception Pilot", "Select animation clips, FBX files or a folder in the Project window first.", "OK");
                return;
            }

            var lib = LoadOrCreateLibrary();
            Undo.RecordObject(lib, "Update maneuver library");
            int added = 0;
            var takeCounter = new Dictionary<string, int>();
            foreach (var clip in clips)
            {
                if (lib.entries.Any(e => e != null && e.clip == clip)) continue;
                var entry = new ManeuverEntry { clip = clip };
                ParseName(LabelFor(clip), entry, takeCounter);
                lib.entries.Add(entry);
                added++;
            }
            AnalyzeAll(lib);
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            Selection.activeObject = lib;
            Debug.Log($"[PerceptionPilot] Added {added} clip(s). Library now has {lib.entries.Count} entries. " +
                      "Check each entry's type, walker, take and analysis note in the Inspector.");
        }

        [MenuItem(Menu + "3. Re-analyse T0 in library")]
        static void Reanalyse()
        {
            var lib = Selection.activeObject as ManeuverLibrary;
            if (lib == null) lib = FindLibrary();
            if (lib == null) { EditorUtility.DisplayDialog("Perception Pilot", "No ManeuverLibrary found.", "OK"); return; }
            Undo.RecordObject(lib, "Re-analyse T0");
            AnalyzeAll(lib);
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            Selection.activeObject = lib;
        }

        static void AnalyzeAll(ManeuverLibrary lib)
        {
            var s = T0Analyzer.Settings.Default;
            var report = new StringBuilder("[PerceptionPilot] T0 analysis\n");

            foreach (var e in lib.entries)
            {
                if (e == null || e.clip == null) continue;
                e.clipLength = e.clip.length;
                if (e.type == ManeuverType.Unknown)
                {
                    e.analysisNote = "Set the maneuver type, then run step 3.";
                    report.AppendLine($"  {e.clip.name}: type unknown");
                    continue;
                }
                if (e.type == ManeuverType.Straight) continue; // done below

                var r = T0Analyzer.Analyze(e.clip, e.type, s);
                e.measuredTurnDeg = r.turnDeg;
                if (!e.t0Locked) { e.t0 = r.found ? r.t0 : -1f; e.t0IsPseudo = false; }
                string note = r.note;

                if (ManeuverInfo.IsTurn(e.type))
                {
                    float expected = ManeuverInfo.TrueAngle(e.type);
                    if (Mathf.Sign(r.turnDeg) != Mathf.Sign(expected) || Mathf.Abs(r.turnDeg - expected) > 20f)
                        note += $" | measured turn {r.turnDeg:0} deg vs label {expected:0} - check label/clip";
                }
                if (ManeuverInfo.IsStairs(e.type) && Mathf.Sign(r.verticalEnd) != (e.type == ManeuverType.StairsUp ? 1f : -1f))
                    note += " | vertical direction does not match the label";

                e.analysisNote = note + LeadInWarning(e);
                report.AppendLine($"  {e.clip.name}: {e.type} T0={e.t0:0.000}s turn={r.turnDeg:0}deg  {e.analysisNote}");
            }

            // Straight takes: matched pseudo-T0 = median T0 of the same walker's maneuver takes.
            foreach (var e in lib.entries)
            {
                if (e == null || e.clip == null || e.type != ManeuverType.Straight) continue;
                var r = T0Analyzer.Analyze(e.clip, ManeuverType.Straight, s);
                e.measuredTurnDeg = r.turnDeg;

                var same = lib.entries.Where(o => o != null && o.type != ManeuverType.Straight && o.type != ManeuverType.Unknown
                                                  && !o.t0IsPseudo && o.t0 > 0f && o.walkerId == e.walkerId)
                                      .Select(o => o.t0).ToList();
                if (same.Count == 0)
                    same = lib.entries.Where(o => o != null && o.type != ManeuverType.Straight && !o.t0IsPseudo && o.t0 > 0f)
                                      .Select(o => o.t0).ToList();
                float pseudo = same.Count > 0 ? Median(same) : 0.6f * e.clip.length;
                pseudo = Mathf.Clamp(pseudo, 0.5f, Mathf.Max(0.5f, e.clip.length - 0.4f));

                if (!e.t0Locked) { e.t0 = pseudo; e.t0IsPseudo = true; }
                e.analysisNote = r.note + " | pseudo-T0 = median maneuver T0" +
                                 (same.Count == 0 ? " (no maneuver takes yet: 60% of clip)" : "") +
                                 ". Assumes takes are trimmed alike; adjust by hand to match gait phase if needed." + LeadInWarning(e);
                report.AppendLine($"  {e.clip.name}: Straight pseudo-T0={e.t0:0.000}s  {e.analysisNote}");
            }

            // Real biological motion: attach each take's FreeMoCap CSV and measure T0 on the recorded joints.
            var trajs = new Dictionary<ManeuverEntry, PointLightTrajectory>();
            foreach (var e in lib.entries)
            {
                if (e == null || e.clip == null || e.type == ManeuverType.Unknown) continue;
                if (e.pointLightCsv == null) e.pointLightCsv = FindCsvFor(e.clip);
                if (e.pointLightCsv == null) continue;
                PointLightTrajectory traj;
                try { traj = PointLightTrajectory.Parse(e.pointLightCsv.text, e.pointLightFps, CaptureUpAxis.ZUp, 0f, false); }
                catch (System.Exception ex) { e.analysisNote += " | joints CSV error: " + ex.Message; continue; }
                trajs[e] = traj;
                if (e.type == ManeuverType.Straight) continue;

                var r = T0Analyzer.AnalyzeTrajectory(traj, e.type, s);
                if (!e.t0PointLightLocked) e.t0PointLight = r.found ? r.t0 : -1f;
                string cmp = "";
                if (e.t0 > 0f && e.t0PointLight > 0f)
                {
                    cmp = $" | joints T0 {e.t0PointLight:0.000}s vs clip T0 {e.t0:0.000}s";
                    if (Mathf.Abs(e.t0PointLight - e.t0) > 0.05f) cmp += " - CHECK: differ by >50 ms (clip and CSV may not start on the same frame)";
                }
                e.analysisNote += " | joints: " + traj.Note + (r.found ? "" : " (no joints T0 found)") + cmp;
                report.AppendLine($"  {e.clip.name}: joints T0={e.t0PointLight:0.000}s{cmp}");
            }
            foreach (var kv in trajs)
            {
                var e = kv.Key;
                if (e.type != ManeuverType.Straight || e.t0PointLightLocked) continue;
                var same = lib.entries.Where(o => o != null && o.type != ManeuverType.Straight && o.t0PointLight > 0f && o.walkerId == e.walkerId)
                                      .Select(o => o.t0PointLight).ToList();
                if (same.Count == 0)
                    same = lib.entries.Where(o => o != null && o.type != ManeuverType.Straight && o.t0PointLight > 0f).Select(o => o.t0PointLight).ToList();
                float pseudo = same.Count > 0 ? Median(same) : 0.6f * kv.Value.Duration;
                e.t0PointLight = Mathf.Clamp(pseudo, 0.5f, Mathf.Max(0.5f, kv.Value.Duration - 0.4f));
                e.analysisNote += $" | joints pseudo-T0={e.t0PointLight:0.000}s; " + kv.Value.Note;
            }
            Debug.Log(report.ToString());
        }

        // The take's FreeMoCap body_trajectories.csv, renamed to the FBX's name, in the same folder.
        static TextAsset FindCsvFor(AnimationClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(path)) return null;
            string dir = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);
            foreach (var cand in new[] { $"{dir}/{stem}.csv", $"{dir}/{stem}_body_trajectories.csv", $"{dir}/{stem}_body.csv" })
            {
                var ta = AssetDatabase.LoadAssetAtPath<TextAsset>(cand);
                if (ta != null) return ta;
            }
            return null;
        }

        static string LeadInWarning(ManeuverEntry e)
        {
            float needed = LeadIn + Jitter + EarliestCutoff;
            if (e.t0 > 0f && e.t0 < needed)
                return $" | short lead-in: T0 at {e.t0:0.00}s, full lead-in needs {needed:0.0}s (it will be shortened)";
            return "";
        }

        static float Median(List<float> v)
        {
            v.Sort();
            int m = v.Count / 2;
            return v.Count % 2 == 1 ? v[m] : 0.5f * (v[m - 1] + v[m]);
        }

        // Mixamo / mocap exports often name every clip "mixamo.com" or "Take 001": then use the file name.
        static string LabelFor(AnimationClip clip)
        {
            string file = System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(clip));
            string c = clip.name.ToLowerInvariant();
            bool generic = c.Contains("mixamo") || Regex.IsMatch(c, @"^take[\s_]?\d+$") || c.StartsWith("armature") || c == "default";
            if (generic || string.IsNullOrEmpty(file)) return string.IsNullOrEmpty(file) ? clip.name : file;
            return file + "_" + clip.name;
        }

        // Accepts names like  W1_TurnL60_T03,  walker2-turn-right-30-take4,  W1_StairsUp_T2,
        // Mixamo-style "Walking Turn 90 Left", "Ascending Stairs".
        static void ParseName(string name, ManeuverEntry e, Dictionary<string, int> takeCounter)
        {
            string n = name.ToLowerInvariant();

            if (Regex.IsMatch(n, @"stair|step|ascend|descend|climb"))
            {
                bool down = Regex.IsMatch(n, @"down|descend");
                e.type = down ? ManeuverType.StairsDown : ManeuverType.StairsUp;
            }
            else
            {
                var m1 = Regex.Match(n, @"(?:^|[^a-z]|turn)(l|left|r|right)[\s_\-]*(?:turn[\s_\-]*)?(30|60|90)(?!\d)");
                var m2 = Regex.Match(n, @"(30|60|90)(?:\s*deg(?:rees)?)?[\s_\-]*(left|right|l|r)(?![a-z])");
                string side = null, ang = null;
                if (m1.Success) { side = m1.Groups[1].Value; ang = m1.Groups[2].Value; }
                else if (m2.Success) { side = m2.Groups[2].Value; ang = m2.Groups[1].Value; }

                if (side != null)
                {
                    bool left = side.StartsWith("l");
                    switch (ang)
                    {
                        case "30": e.type = left ? ManeuverType.TurnLeft30 : ManeuverType.TurnRight30; break;
                        case "60": e.type = left ? ManeuverType.TurnLeft60 : ManeuverType.TurnRight60; break;
                        default: e.type = left ? ManeuverType.TurnLeft90 : ManeuverType.TurnRight90; break;
                    }
                }
                else if (Regex.IsMatch(n, @"straight|forward"))
                    e.type = ManeuverType.Straight;
                else
                    e.type = ManeuverType.Unknown;
            }

            var w = Regex.Match(n, @"(?:^|[^a-z])(?:w|walker|actor)[\s_\-]?(\d+)");
            e.walkerId = w.Success ? "W" + int.Parse(w.Groups[1].Value) : "W1";

            var t = Regex.Match(n, @"(?:^|[^a-z])(?:t|take|tk)[\s_\-]?(\d+)");
            if (t.Success) e.take = int.Parse(t.Groups[1].Value);
            else
            {
                string key = e.walkerId + "_" + e.type;
                takeCounter.TryGetValue(key, out int k);
                takeCounter[key] = k + 1;
                e.take = k + 1;
            }
        }

        // ------------------------------------------------------------------ 4. scene rig

        [MenuItem(Menu + "4. Create experiment rig in open scene (select the avatar first)")]
        static void CreateRig()
        {
            var rig = Object.FindFirstObjectByType<OVRCameraRig>();
            if (rig == null)
            {
                EditorUtility.DisplayDialog("Perception Pilot", "No OVRCameraRig in the open scene. Add the camera rig (with passthrough) first.", "OK");
                return;
            }

            var go = new GameObject("PerceptionPilot");
            Undo.RegisterCreatedObjectUndo(go, "Create Perception Pilot rig");
            var mgr = go.AddComponent<PerceptionTrialManager>();
            var resp = go.AddComponent<ResponseCollector>();

            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.widthMultiplier = 0.006f;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = GetOrCreateMaterial("PP_Ray", new Color(0.2f, 0.9f, 1f));
            lr.enabled = false;

            resp.line = lr;
            resp.rayOrigin = rig.rightControllerAnchor;
            mgr.response = resp;
            mgr.head = rig.centerEyeAnchor;
            mgr.library = FindLibrary();

            // Real biological motion player (recorded joints)
            var pldGo = new GameObject("RealPointLight");
            Undo.RegisterCreatedObjectUndo(pldGo, "Create real point-light player");
            var pld = pldGo.AddComponent<RealPointLightPlayer>();
            pld.dotMaterial = GetOrCreateMaterial("PP_Dots", Color.white);
            mgr.realPointLight = pld;

            // Existing project systems
            mgr.rpOverlay = Object.FindFirstObjectByType<PassthroughCameraSamples.MultiObjectDetection.LowVisionOverlayController>();
            mgr.researchLogger = Object.FindFirstObjectByType<WalkingEyeExperiment.ResearchSessionLogger>();

            // Head-locked prompt text
            var canvasGo = new GameObject("PP_Prompt", typeof(RectTransform), typeof(Canvas));
            Undo.RegisterCreatedObjectUndo(canvasGo, "Create prompt");
            canvasGo.transform.SetParent(rig.centerEyeAnchor, false);
            canvasGo.transform.localPosition = new Vector3(0f, 0f, 1.2f);
            canvasGo.transform.localScale = Vector3.one * 0.001f;
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            ((RectTransform)canvasGo.transform).sizeDelta = new Vector2(900f, 400f);

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(canvasGo.transform, false);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 44;
            tmp.color = Color.white;
            tmp.text = "";
            var trt = tmp.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            mgr.promptText = tmp;

            // Avatar
            var msg = new StringBuilder();
            var sel = Selection.activeGameObject;
            Animator anim = sel != null ? sel.GetComponentInChildren<Animator>() : null;
            if (anim != null)
            {
                var stim = anim.GetComponent<AvatarStimulus>();
                if (stim == null) stim = Undo.AddComponent<AvatarStimulus>(anim.gameObject);
                var pl = anim.GetComponent<PointLightRig>();
                if (pl == null) pl = Undo.AddComponent<PointLightRig>(anim.gameObject);
                pl.dotMaterial = GetOrCreateMaterial("PP_Dots", Color.white);
                stim.pointLightRig = pl;
                mgr.avatar = stim;

                var others = anim.GetComponentsInChildren<MonoBehaviour>(true)
                    .Where(c => c != null && !(c is AvatarStimulus) && !(c is PointLightRig) && c.enabled)
                    .Select(c => c.GetType().Name).Distinct().ToList();
                if (others.Count > 0)
                    msg.AppendLine("Disable these scripts on the avatar so nothing else moves it: " + string.Join(", ", others));
                if (anim.GetComponentInChildren<CharacterController>() != null)
                    msg.AppendLine("Disable the CharacterController on the avatar.");
                if (anim.transform.parent != null)
                    msg.AppendLine("Move the avatar to the scene root (no parent).");
            }
            else msg.AppendLine("No avatar selected: assign an Animator object with AvatarStimulus + PointLightRig to 'avatar'.");

            if (mgr.library == null) msg.AppendLine("No ManeuverLibrary found yet: run steps 1-2 and assign it.");
            msg.AppendLine(mgr.rpOverlay != null
                ? $"Tunnel connected: {mgr.rpOverlay.name}. Check that 'Simulated Rp Vision' ({mgr.simulatedRpVision}) is the vision type configured as your RP tunnel."
                : "No LowVisionOverlayController in this scene: open the Neon/RP scene, or wire the field hooks by hand.");
            msg.AppendLine(mgr.researchLogger != null
                ? $"Research logger connected: {mgr.researchLogger.name} (Neon gaze + trial events in one log)."
                : "No ResearchSessionLogger in this scene: gaze will not be logged with trials.");

            Selection.activeGameObject = go;
            EditorUtility.DisplayDialog("Perception Pilot rig created", msg.ToString(), "OK");
        }

        // ------------------------------------------------------------------ helpers

        static List<string> SelectedModelPaths()
        {
            var result = new List<string>();
            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;
                if (AssetDatabase.IsValidFolder(path))
                {
                    foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { path }))
                        result.Add(AssetDatabase.GUIDToAssetPath(g));
                }
                else if (AssetImporter.GetAtPath(path) is ModelImporter) result.Add(path);
            }
            return result.Distinct().ToList();
        }

        static List<AnimationClip> SelectedClips()
        {
            var result = new List<AnimationClip>();
            foreach (var obj in Selection.objects)
                if (obj is AnimationClip c && !c.name.StartsWith("__preview__")) result.Add(c);

            foreach (var path in SelectedModelPaths())
                foreach (var a in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (a is AnimationClip c && !c.name.StartsWith("__preview__")) result.Add(c);

            foreach (var obj in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (!AssetDatabase.IsValidFolder(path)) continue;
                foreach (var g in AssetDatabase.FindAssets("t:AnimationClip", new[] { path }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (!p.EndsWith(".anim")) continue;
                    var c = AssetDatabase.LoadAssetAtPath<AnimationClip>(p);
                    if (c != null) result.Add(c);
                }
            }
            return result.Distinct().ToList();
        }

        static ManeuverLibrary FindLibrary()
        {
            var lib = AssetDatabase.LoadAssetAtPath<ManeuverLibrary>(LibPath);
            if (lib != null) return lib;
            var g = AssetDatabase.FindAssets("t:ManeuverLibrary").FirstOrDefault();
            return g != null ? AssetDatabase.LoadAssetAtPath<ManeuverLibrary>(AssetDatabase.GUIDToAssetPath(g)) : null;
        }

        static ManeuverLibrary LoadOrCreateLibrary()
        {
            var lib = Selection.objects.OfType<ManeuverLibrary>().FirstOrDefault();
            if (lib == null) lib = FindLibrary();
            if (lib != null) return lib;
            if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets", "PerceptionPilot");
            lib = ScriptableObject.CreateInstance<ManeuverLibrary>();
            AssetDatabase.CreateAsset(lib, LibPath);
            return lib;
        }

        static Material GetOrCreateMaterial(string name, Color color)
        {
            string path = $"{Root}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            mat = new Material(sh);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (!AssetDatabase.IsValidFolder(Root)) AssetDatabase.CreateFolder("Assets", "PerceptionPilot");
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
