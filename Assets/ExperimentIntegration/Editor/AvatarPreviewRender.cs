using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WalkingEyeExperiment.Editor
{
    /// <summary>
    /// Editor-only check of the realistic avatar without entering Play mode: when Temp/avatar_preview.request exists,
    /// poses the realistic avatar with MocapAvatarDriver on a recorded clip (same code path as the experiment) in an
    /// isolated preview scene and saves side / back / front views with the recorded joints drawn on top to
    /// Logs/avatar_preview/. The open scene is not changed.
    /// Request file contents (optional): clip id (default Straight_69_01).
    /// </summary>
    [InitializeOnLoad]
    static class AvatarPreviewRender
    {
        const string RequestPath = "Temp/avatar_preview.request";
        const string OutDir = "Logs/avatar_preview";
        const string AvatarPath = "Assets/RP_Character/rp_eric_rigged_001/rp_eric_rigged_001_u3d.fbx";
        const int W = 360, H = 520;
        const string FemalePrefab = "Assets/Actorcore-Unity-0818-453120/Actor/casual-f-0170/casual-f-0170 1.prefab";
        const string FemaleRig = "Assets/Actorcore-Unity-0816-465768/Actor/casual-f-0170/casual-f-0170.fbx";
        static double next;

        static AvatarPreviewRender() { EditorApplication.update += Poll; }

        [MenuItem("Experiment Integration/Render Avatar Preview Snapshots")]
        static void Menu() => Run("");

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 1;
            if (!File.Exists(RequestPath)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string arg = "";
            try { arg = File.ReadAllText(RequestPath).Trim(); File.Delete(RequestPath); } catch { }
            // Pick up script changes made outside Unity first; if that starts a compile, run again after the reload.
            AssetDatabase.Refresh();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { try { File.WriteAllText(RequestPath, arg); } catch { } return; }
            foreach (var line in arg.Split('\n')) Run(line.Trim());
        }

        /// <summary>"setavatar=female|eric": sets the experiment's realistic avatar in the open scene and saves the scene.</summary>
        static void SetSceneAvatar(string which)
        {
            string prefabPath = which == "female" ? FemalePrefab : AvatarPath, rigPath = which == "female" ? FemaleRig : AvatarPath;
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var rig = AssetDatabase.LoadAllAssetsAtPath(rigPath).OfType<Avatar>().FirstOrDefault(a => a && a.isHuman);
            var platform = UnityEngine.Object.FindFirstObjectByType<SeatedWalkingPlatform>(FindObjectsInactive.Include);
            string msg;
            if (!prefab || !rig || !platform) msg = $"setavatar FAILED prefab={(bool)prefab} rig={(bool)rig} platform={(bool)platform}";
            else
            {
                Undo.RecordObject(platform, "Set realistic avatar");
                platform.avatarPrefab = prefab; platform.avatarRigAsset = rig;
                EditorUtility.SetDirty(platform);
                EditorSceneManager.MarkSceneDirty(platform.gameObject.scene);
                EditorSceneManager.SaveScene(platform.gameObject.scene);
                msg = $"setavatar OK {prefab.name} / {rig.name} in scene {platform.gameObject.scene.path}";
            }
            Directory.CreateDirectory(OutDir);
            File.AppendAllText(Path.Combine(OutDir, "result.txt"), DateTime.Now.ToString("o") + " " + msg + "\n");
        }

        /// <summary>"ui=menu|validation": builds the experiment menu in a preview scene and saves Logs/avatar_preview/ui_*.png.</summary>
        static void RenderUi(string which)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            string msg = "ui " + which;
            try
            {
                var camGo = new GameObject("UI Camera"); SceneManager.MoveGameObjectToScene(camGo, scene);
                var cam = camGo.AddComponent<Camera>(); cam.enabled = false; cam.scene = scene;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.35f, .38f, .42f, 1); cam.fieldOfView = 60;
                var host = new GameObject("Platform"); SceneManager.MoveGameObjectToScene(host, scene);
                var platform = host.AddComponent<SeatedWalkingPlatform>();
                platform.participantCamera = cam;
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                if (which == "validation") typeof(SeatedWalkingPlatform).GetMethod("BuildValidationMenu", flags).Invoke(platform, new object[] { "Quest Guardian floor" });
                else typeof(SeatedWalkingPlatform).GetMethod("BuildStartMenu", flags).Invoke(platform, null);
                Canvas.ForceUpdateCanvases();
                foreach (var tmp in camGo.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true)) tmp.ForceMeshUpdate();
                var rt = new RenderTexture(900, 1000, 24); var tex = new Texture2D(900, 1000, TextureFormat.RGB24, false);
                cam.targetTexture = rt; cam.Render();
                var prev = RenderTexture.active; RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 900, 1000), 0, 0); tex.Apply(); RenderTexture.active = prev;
                cam.targetTexture = null; rt.Release();
                File.WriteAllBytes(Path.Combine(OutDir, "ui_" + which + ".png"), tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                msg += " OK";
            }
            catch (Exception e) { msg += " ERROR " + e; }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
            File.AppendAllText(Path.Combine(OutDir, "result.txt"), DateTime.Now.ToString("o") + " " + msg + "\n");
        }

        static void Run(string clipId)
        {
            if (clipId.StartsWith("setavatar=")) { SetSceneAvatar(clipId.Substring(10)); return; }
            if (clipId.StartsWith("ui=")) { RenderUi(clipId.Substring(3)); return; }
            var log = new System.Text.StringBuilder();
            Directory.CreateDirectory(OutDir);
            string tag = "pose";
            Scene preview = default;
            GameObject avatarGo = null, camGo = null, lightGo = null;
            try
            {
                // Arguments: "key=value;..." with clip=<id>, avatar=eric|female, match=0|1, tag=<file prefix>.
                var args = clipId.Split(';').Select(s => s.Split('=')).Where(kv => kv.Length == 2)
                    .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim());
                string Arg(string k, string d) => args.TryGetValue(k, out var v) && v.Length > 0 ? v : d;
                var lib = MocapClipLibrary.Load();
                string wantClip = Arg("clip", "Straight_69_01");
                var clip = lib.Clips.FirstOrDefault(c => c.info.id == wantClip) ?? lib.Clips[0];
                log.AppendLine("clip " + clip.info.id);
                string prefabPath = Arg("avatar", "eric") == "female" ? FemalePrefab : AvatarPath;
                string rigPath = Arg("avatar", "eric") == "female" ? FemaleRig : AvatarPath;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var rig = AssetDatabase.LoadAllAssetsAtPath(rigPath).OfType<Avatar>().FirstOrDefault(a => a && a.isHuman);
                log.AppendLine($"avatar {prefabPath} rig {(rig ? rig.name : "none")}");
                tag = Arg("tag", "pose");
                var driver = MocapAvatarDriver.Create(prefab, rig);
                if (driver) driver.matchRecordedSegments = Arg("match", "1") != "0";
                if (!driver || !driver.Valid || !driver.Prepare(clip)) throw new Exception("avatar driver not valid");
                avatarGo = driver.gameObject;

                preview = EditorSceneManager.NewPreviewScene();
                SceneManager.MoveGameObjectToScene(avatarGo, preview);
                lightGo = new GameObject("Preview Light");
                var light = lightGo.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.3f;
                lightGo.transform.rotation = Quaternion.Euler(40, 150, 0);
                SceneManager.MoveGameObjectToScene(lightGo, preview);
                camGo = new GameObject("Preview Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false; cam.scene = preview;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.55f, .58f, .62f, 1);
                cam.fieldOfView = 34; cam.nearClipPlane = .05f; cam.farClipPlane = 30;
                SceneManager.MoveGameObjectToScene(camGo, preview);
                var rt = new RenderTexture(W, H, 24);
                var tex = new Texture2D(W * 3, H, TextureFormat.RGB24, false);

                var joints = new Vector3[MocapClipLibrary.JointNames.Length];
                float[] rel = { -1.0f, -0.75f, -0.5f, -0.25f, 0f, 0.25f, 0.5f, 0.75f, 1.0f };
                for (int s = 0; s < rel.Length; s++)
                {
                    float t = clip.T0 + rel[s];
                    clip.Sample(t, joints);
                    driver.SetPose(clip, t, Vector3.zero, Quaternion.identity, joints);
                    Vector3 pelvis = (joints[MocapClipLibrary.LHip] + joints[MocapClipLibrary.RHip]) * .5f;
                    Vector3 across = joints[MocapClipLibrary.RHip] - joints[MocapClipLibrary.LHip]; across.y = 0;
                    Vector3 fwd = Vector3.Cross(across, Vector3.up).normalized, right = across.normalized;
                    Vector3 at = new Vector3(pelvis.x, pelvis.y * .95f, pelvis.z);
                    // Skinning does not run for objects in a preview scene in edit mode: bake the posed meshes.
                    var baked = Bake(avatarGo, preview);
                    Shot(cam, rt, tex, at - right * 3.4f, at, 0, joints);          // side (from the walker's left)
                    Shot(cam, rt, tex, at - fwd * 3.4f, at, W, joints);            // from behind
                    Shot(cam, rt, tex, at + fwd * 3.4f, at, 2 * W, joints);        // from the front
                    tex.Apply();
                    File.WriteAllBytes(Path.Combine(OutDir, $"{tag}_{s}_t{rel[s]:+0.00;-0.00}.png"), tex.EncodeToPNG());
                    foreach (var g in baked) { var mf = g.GetComponent<MeshFilter>(); if (mf) UnityEngine.Object.DestroyImmediate(mf.sharedMesh); UnityEngine.Object.DestroyImmediate(g); }
                }
                rt.Release();
                UnityEngine.Object.DestroyImmediate(tex);
                log.AppendLine("OK " + rel.Length + " poses");
            }
            catch (Exception e) { log.AppendLine("ERROR " + e); Debug.LogError("[AvatarPreview] " + e); }
            finally
            {
                if (avatarGo) UnityEngine.Object.DestroyImmediate(avatarGo);
                if (camGo) UnityEngine.Object.DestroyImmediate(camGo);
                if (lightGo) UnityEngine.Object.DestroyImmediate(lightGo);
                foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
                    if (t && !t.parent && t.name.StartsWith("CMU source skeleton") && !EditorUtility.IsPersistent(t)) UnityEngine.Object.DestroyImmediate(t.gameObject);
                if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
                File.AppendAllText(Path.Combine(OutDir, "result.txt"), DateTime.Now.ToString("o") + " [" + clipId + "]\n" + log);
            }
        }

        static System.Collections.Generic.List<GameObject> Bake(GameObject avatar, Scene scene)
        {
            var list = new System.Collections.Generic.List<GameObject>();
            foreach (var smr in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.enabled = false;
                var mesh = new Mesh();
                smr.BakeMesh(mesh, true);
                var g = new GameObject("baked " + smr.name);
                SceneManager.MoveGameObjectToScene(g, scene);
                g.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
                g.AddComponent<MeshFilter>().sharedMesh = mesh;
                g.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials;
                list.Add(g);
            }
            return list;
        }

        static void Shot(Camera cam, RenderTexture rt, Texture2D tex, Vector3 from, Vector3 at, int x0, Vector3[] joints)
        {
            cam.transform.position = from + Vector3.up * .1f;
            cam.transform.LookAt(at);
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), x0, 0);
            RenderTexture.active = prev;
            foreach (var j in joints)
            {
                Vector3 p = cam.WorldToScreenPoint(j);
                if (p.z <= 0) continue;
                int px = Mathf.RoundToInt(p.x * W / cam.pixelWidth), py = Mathf.RoundToInt(p.y * H / cam.pixelHeight);
                for (int dx = -2; dx <= 2; dx++) for (int dy = -2; dy <= 2; dy++)
                    if (px + dx >= 0 && px + dx < W && py + dy >= 0 && py + dy < H) tex.SetPixel(x0 + px + dx, py + dy, Color.red);
            }
            cam.targetTexture = null;
        }
    }
}
