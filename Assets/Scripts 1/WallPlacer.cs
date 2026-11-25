using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Lets the user manually place 4 walls before obstacle and avatar placement.
/// Wall dimensions: 2 walls × 10 m, 2 walls × 4 m, all 2.5 m high.
///
/// Editor:  WASD = slide on floor   Q/E = rotate   SPACE = confirm
/// XR:      Right thumbstick = move   Left thumbstick X = rotate   A = confirm
///
/// After all 4 walls are confirmed, AllPlaced = true and ObstaclePlacer takes over.
/// Remove or disable WallDetector from the scene when using this component.
/// </summary>
public class WallPlacer : MonoBehaviour
{
    [Header("Wall dimensions")]
    public float wallHeight    = 2.5f;
    public float wallThickness = 0.15f;
    public float floorY        = 0f;

    [Header("Placement")]
    public float moveSpeed     = 2.5f;
    public float rotationSpeed = 45f;   // slower than obstacles — walls are longer

    // 4 wall lengths (metres).  Change here to match your room.
    private static readonly float[] WallLengths = { 10f, 10f, 4f, 4f };
    private const int Total = 4;

    [Header("Colors")]
    public Color previewColor  = new Color(0.4f, 0.65f, 1f, 0.30f);
    public Color confirmedColor = new Color(0.3f, 0.50f, 0.9f, 0.55f);

    // ── runtime ──────────────────────────────────────────────────
    private int        _phase         = 0;
    private bool       _done          = false;
    private GameObject _preview;
    private float      _rotY          = 0f;
    private float      _currentLength = 0f;

    private readonly List<GameObject> _walls = new List<GameObject>();

    public bool AllPlaced => _done;
    public IReadOnlyList<GameObject> Walls => _walls;

    // ─────────────────────────────────────────────────────────────
    void Start() => SpawnPreview();

    void SpawnPreview()
    {
        if (_phase >= Total)
        {
            _done = true;
            if (_preview != null) Destroy(_preview);
            Debug.Log("[WallPlacer] All walls placed ✅");
            return;
        }

        _rotY          = 0f;
        _currentLength = WallLengths[_phase];

        _preview = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _preview.name = $"WallPreview_{_phase + 1}_{(int)_currentLength}m";
        _preview.transform.localScale = new Vector3(_currentLength, wallHeight, wallThickness);
        // Stagger default positions so they don't overlap
        _preview.transform.position = new Vector3((_phase % 2 == 0 ? -1f : 1f) * 2f,
                                                   floorY + wallHeight * 0.5f,
                                                   2f + _phase * 0.5f);
        _preview.GetComponent<Renderer>().material = MakeMat(previewColor, transparent: true);
        _preview.GetComponent<Collider>().enabled  = false;
    }

    // ─────────────────────────────────────────────────────────────
    void Update()
    {
        if (_done || _preview == null) return;
#if UNITY_EDITOR
        UpdateEditor();
#else
        UpdateXR();
#endif
    }

    void UpdateEditor()
    {
        if (Keyboard.current == null) return;
        var kb = Keyboard.current;

        float h = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f)
                + (kb.rightArrowKey.isPressed ? 1f : 0f) - (kb.leftArrowKey.isPressed ? 1f : 0f);
        float v = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f)
                + (kb.upArrowKey.isPressed ? 1f : 0f) - (kb.downArrowKey.isPressed ? 1f : 0f);

        var p = _preview.transform.position;
        p.x += h * moveSpeed * Time.deltaTime;
        p.z += v * moveSpeed * Time.deltaTime;
        p.y  = floorY + wallHeight * 0.5f;
        _preview.transform.position = p;

        if (kb.qKey.isPressed) _rotY -= rotationSpeed * Time.deltaTime;
        if (kb.eKey.isPressed) _rotY += rotationSpeed * Time.deltaTime;
        _preview.transform.rotation = Quaternion.Euler(0f, _rotY, 0f);

        if (kb.spaceKey.wasPressedThisFrame) ConfirmWall();
    }

    void UpdateXR()
    {
        Vector2 moveStick   = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick); // right — move
        Vector2 rotateStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);   // left  — rotate

        // Camera-relative movement so directions match what the user sees
        var cam   = Camera.main != null ? Camera.main.transform : null;
        var fwd   = cam != null ? Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized : Vector3.forward;
        var right = cam != null ? Vector3.ProjectOnPlane(cam.right,   Vector3.up).normalized : Vector3.right;

        var p = _preview.transform.position;
        p += right * moveStick.x * moveSpeed * Time.deltaTime;
        p += fwd   * moveStick.y * moveSpeed * Time.deltaTime;
        p.y = floorY + wallHeight * 0.5f;
        _preview.transform.position = p;

        _rotY += rotateStick.x * rotationSpeed * Time.deltaTime;
        _preview.transform.rotation = Quaternion.Euler(0f, _rotY, 0f);

        if (OVRInput.GetDown(OVRInput.Button.One)) ConfirmWall(); // A button
    }

    // ─────────────────────────────────────────────────────────────
    void ConfirmWall()
    {
        _preview.name = $"Wall_{_phase + 1}_{(int)_currentLength}m";
        _preview.GetComponent<Renderer>().material = MakeMat(confirmedColor, transparent: false);
        var bc = _preview.GetComponent<BoxCollider>();
        bc.isTrigger = false;
        _preview.GetComponent<Collider>().enabled = true;

        _walls.Add(_preview);
        _preview = null;
        _phase++;

        Debug.Log($"[WallPlacer] Wall {_phase}/{Total} confirmed ({(int)_currentLength} m).");
        SpawnPreview();
    }

    // ─────────────────────────────────────────────────────────────
    public void FullReset()
    {
        if (_preview != null) { Destroy(_preview); _preview = null; }
        foreach (var w in _walls) if (w != null) Destroy(w);
        _walls.Clear();
        _phase = 0;
        _done  = false;
        _rotY  = 0f;
        SpawnPreview();
        Debug.Log("[WallPlacer] Reset.");
    }

    /// <summary>Toggle renderer visibility on all confirmed walls.</summary>
    public void SetVisible(bool visible)
    {
        foreach (var w in _walls)
            if (w != null)
                foreach (var r in w.GetComponentsInChildren<Renderer>())
                    r.enabled = visible;
    }

    // ─────────────────────────────────────────────────────────────
    Material MakeMat(Color color, bool transparent)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var mat    = new Material(shader);
        if (transparent)
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend",   0f);
            mat.SetInt("_SrcBlend",  (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend",  (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite",    0);
            mat.renderQueue = 3000;
        }
        mat.color = color;
        return mat;
    }

    void OnGUI()
    {
        if (_done) return;
        string wallLabel = _phase < Total ? $"{(int)WallLengths[_phase]} m" : "";
        var style = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = new Color(0.6f, 0.85f, 1f);
        string controls;
#if UNITY_EDITOR
        controls = "WASD move  |  Q/E rotate  |  SPACE confirm";
#else
        controls = "Right stick = move  |  Left stick = rotate  |  A = confirm";
#endif
        GUI.Box(new Rect(Screen.width * 0.5f - 220f, 12f, 440f, 52f),
            $"Place Wall {_phase + 1}/{Total}  ({wallLabel} long)\n{controls}",
            style);
    }
}
