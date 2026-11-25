#if UNITY_EDITOR
using System.IO;
using UnityEngine;

namespace WalkingEyeExperiment
{
    /// <summary>
    /// Editor-only diagnostics: while the realistic avatar plays, saves snapshots (side view | view from behind) to
    /// Logs/avatar_shots with the recorded joint centres drawn as red dots and the bones as green lines, so the
    /// retargeted body can be compared with the recording without seeing the editor.
    /// </summary>
    public sealed class AvatarDebugCapture : MonoBehaviour
    {
        public int maxShots = 40;
        public float everySeconds = .25f;
        int shots;
        float nextTime;
        Camera cam;
        RenderTexture rt;
        Texture2D tex;
        string dir;
        const int W = 512, H = 640;

        void Awake()
        {
            dir = Path.Combine(Application.dataPath, "..", "Logs", "avatar_shots");
            try { if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir, "*.png")) File.Delete(f); Directory.CreateDirectory(dir); } catch { }
            var go = new GameObject("Avatar Debug Camera") { hideFlags = HideFlags.HideAndDontSave };
            cam = go.AddComponent<Camera>();
            cam.enabled = false;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.35f, .38f, .42f, 1f);
            cam.fieldOfView = 40f; cam.nearClipPlane = .05f; cam.farClipPlane = 30f;
            rt = new RenderTexture(W, H, 24);
            tex = new Texture2D(W * 2, H, TextureFormat.RGB24, false);
        }

        public void Capture(Vector3[] joints, Transform[] avatarBones, float clipTime)
        {
            if (shots >= maxShots || Time.unscaledTime < nextTime || joints == null) return;
            nextTime = Time.unscaledTime + everySeconds;
            Vector3 pelvis = (joints[MocapClipLibrary.LHip] + joints[MocapClipLibrary.RHip]) * .5f;
            Vector3 across = joints[MocapClipLibrary.RHip] - joints[MocapClipLibrary.LHip]; across.y = 0;
            if (across.sqrMagnitude < 1e-6f) return;
            Vector3 fwd = Vector3.Cross(across, Vector3.up).normalized; Vector3 right = across.normalized;
            Vector3 target = new Vector3(pelvis.x, pelvis.y - .05f, pelvis.z);
            // Left half: side view from the walker's left. Right half: from behind (as the participant sees it).
            Render(target - right * 3.2f, target, 0, joints, avatarBones);
            Render(target - fwd * 3.2f, target, W, joints, avatarBones);
            tex.Apply();
            try { File.WriteAllBytes(Path.Combine(dir, $"shot_{shots:D3}_t{clipTime:0.00}.png"), tex.EncodeToPNG()); } catch { }
            shots++;
        }

        void Render(Vector3 from, Vector3 at, int x0, Vector3[] joints, Transform[] bones)
        {
            cam.transform.position = from + Vector3.up * .05f;
            cam.transform.LookAt(at);
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), x0, 0);
            RenderTexture.active = prev;
            // Avatar bones (green lines between consecutive humanoid bones of each chain).
            if (bones != null)
                for (int i = 0; i + 1 < bones.Length; i += 2)
                    if (bones[i] && bones[i + 1]) Line(bones[i].position, bones[i + 1].position, x0, Color.green);
            // Recorded joint centres (red dots).
            foreach (var j in joints) Dot(j, x0, Color.red);
            cam.targetTexture = null;
        }

        void Dot(Vector3 world, int x0, Color c)
        {
            Vector3 p = cam.WorldToScreenPoint(world);
            if (p.z <= 0) return;
            int px = Mathf.RoundToInt(p.x * W / cam.pixelWidth), py = Mathf.RoundToInt(p.y * H / cam.pixelHeight);
            for (int dx = -3; dx <= 3; dx++) for (int dy = -3; dy <= 3; dy++)
                if (dx * dx + dy * dy <= 9 && px + dx >= 0 && px + dx < W && py + dy >= 0 && py + dy < H) tex.SetPixel(x0 + px + dx, py + dy, c);
        }

        void Line(Vector3 a, Vector3 b, int x0, Color c)
        {
            for (int s = 0; s <= 30; s++)
            {
                Vector3 p = cam.WorldToScreenPoint(Vector3.Lerp(a, b, s / 30f));
                if (p.z <= 0) continue;
                int px = Mathf.RoundToInt(p.x * W / cam.pixelWidth), py = Mathf.RoundToInt(p.y * H / cam.pixelHeight);
                if (px >= 0 && px < W && py >= 0 && py < H) tex.SetPixel(x0 + px, py, c);
            }
        }

        void OnDestroy()
        {
            if (cam) Destroy(cam.gameObject);
            if (rt) rt.Release();
        }
    }
}
#endif
