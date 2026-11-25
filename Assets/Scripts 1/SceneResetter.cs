using UnityEngine;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Quest 3 buttons:
///   Y (left controller)        — destroy and re-place avatar + goal only
///   X (left controller)        — full reset: walls + obstacles + avatar
///   Right grip                 — toggle clean view (hide everything except avatar)
///
/// In editor only: T = avatar reset, R = full reset, H = clean view
/// </summary>
public class SceneResetter : MonoBehaviour
{
    private WallPlacer          _wallPlacer;
    private ObstaclePlacer      _obstaclePlacer;
    private AvatarPlacer        _avatarPlacer;
    private FloorMeshValidator  _floorValidator;

    private bool _cleanView = false;

    void Start()
    {
        _wallPlacer     = FindObjectOfType<WallPlacer>();
        _obstaclePlacer = FindObjectOfType<ObstaclePlacer>();
        _avatarPlacer   = FindObjectOfType<AvatarPlacer>();
        _floorValidator = FindObjectOfType<FloorMeshValidator>();

        Debug.Log($"[SceneResetter] Awake — avatarPlacer={_avatarPlacer != null}, " +
                  $"obstaclePlacer={_obstaclePlacer != null}, wallPlacer={_wallPlacer != null}");
    }

    void Update()
    {
#if UNITY_EDITOR
        if (Keyboard.current == null) return;
        if (Keyboard.current.tKey.wasPressedThisFrame) ResetAvatarOnly();
        if (Keyboard.current.rKey.wasPressedThisFrame) FullReset();
        if (Keyboard.current.hKey.wasPressedThisFrame) ToggleCleanView();
#else
        // Debug: log any OVR button presses so you can verify input is reaching this script
        if (OVRInput.GetDown(OVRInput.Button.Four))
        {
            Debug.Log("[SceneResetter] Y pressed → ResetAvatarOnly");
            ResetAvatarOnly();
        }
        if (OVRInput.GetDown(OVRInput.Button.Three))
        {
            Debug.Log("[SceneResetter] X pressed → FullReset");
            FullReset();
        }
        if (OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
        {
            Debug.Log("[SceneResetter] Right grip → ToggleCleanView");
            ToggleCleanView();
        }
#endif
    }

    // ── Avatar-only reset ─────────────────────────────────────────
    public void ResetAvatarOnly()
    {
        _avatarPlacer?.ResetAvatarOnly();
        Debug.Log("[SceneResetter] Avatar reset.");
    }

    // ── Full reset ────────────────────────────────────────────────
    public void FullReset()
    {
        if (_cleanView) ToggleCleanView(); // restore before destroying
        _avatarPlacer?.FullReset();
        _obstaclePlacer?.FullReset();
        _wallPlacer?.FullReset();
        Debug.Log("[SceneResetter] Full reset.");
    }

    // ── Clean view ────────────────────────────────────────────────
    public void ToggleCleanView()
    {
        _cleanView = !_cleanView;
        bool show  = !_cleanView;

        _wallPlacer?.SetVisible(show);

        foreach (var go in ObstaclePlacer.Placed)
            if (go != null)
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    r.enabled = show;

        _floorValidator?.SetVisible(show);

        foreach (var name in new[] { "GoalMarker", "GoalLine", "TracNormal", "TracAvoid", "PathPreview" })
        {
            var go = GameObject.Find(name);
            if (go == null) continue;
            var lr = go.GetComponent<LineRenderer>();
            if (lr != null) { lr.enabled = show; continue; }
            foreach (var r in go.GetComponentsInChildren<Renderer>()) r.enabled = show;
        }
    }

    // ── HUD ──────────────────────────────────────────────────────
    void OnGUI()
    {
        var btn = new GUIStyle(GUI.skin.button)
        {
            fontSize = 14, fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        btn.normal.textColor = Color.white;

        var eye = new GUIStyle(btn);
        eye.normal.textColor = _cleanView ? Color.yellow : Color.white;

        float x = 10f;
#if UNITY_EDITOR
        if (GUI.Button(new Rect(x,  10f, 150f, 38f), "↺ Avatar  [T]",          btn)) ResetAvatarOnly();
        if (GUI.Button(new Rect(x,  54f, 150f, 38f), "↺ Full Reset  [R]",       btn)) FullReset();
        if (GUI.Button(new Rect(x,  98f, 150f, 38f), _cleanView ? "👁 Show All  [H]" : "👁 Clean View  [H]", eye)) ToggleCleanView();
#else
        if (GUI.Button(new Rect(x,  10f, 150f, 38f), "↺ Avatar  [Y]",           btn)) ResetAvatarOnly();
        if (GUI.Button(new Rect(x,  54f, 150f, 38f), "↺ Full Reset  [X]",       btn)) FullReset();
        if (GUI.Button(new Rect(x,  98f, 150f, 38f), _cleanView ? "👁 Show All" : "👁 Clean View", eye)) ToggleCleanView();
#endif
    }
}
