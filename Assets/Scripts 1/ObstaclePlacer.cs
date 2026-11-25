using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Lets the user place 5 box obstacles one at a time.
/// Waits for WallPlacer to finish first (if one exists in the scene).
/// Editor:  WASD = move   Q/E = rotate   SPACE = confirm
/// XR:      Right thumbstick = move   B button = confirm
/// After all 5 are placed, AllPlaced = true and AvatarPlacer takes over.
/// </summary>
public class ObstaclePlacer : MonoBehaviour
{
    [Header("Obstacle settings")]
    public float moveSpeed     = 2.5f;
    public float rotationSpeed = 90f;
    public float floorY        = 0f;

    // Five obstacles with different shapes to create varied corridors
    private static readonly Vector3[] ObstacleSizes = new Vector3[]
    {
        new Vector3(0.55f, 1.40f, 0.55f),  // 1 — standard pillar
        new Vector3(1.30f, 0.80f, 0.45f),  // 2 — wide low bar
        new Vector3(0.35f, 1.90f, 0.35f),  // 3 — thin tall spike
        new Vector3(1.00f, 1.10f, 0.80f),  // 4 — chunky block
        new Vector3(0.50f, 0.65f, 1.20f),  // 5 — deep flat slab
    };

    [Header("Colors")]
    public Color previewColor = new Color(1f, 0.45f, 0f, 0.50f);   // orange ghost
    public Color placedColor  = new Color(0.9f, 0.15f, 0.05f, 1f); // solid red

    // ── state ─────────────────────────────────────────────────────
    private int        _phase  = 0;
    private const int  Total   = 5;
    private bool       _done   = false;
    private GameObject _preview;
    private float      _rotY   = 0f;
    private Vector3    _currentSize;

    // Wall-placer dependency
    private WallPlacer _wallPlacer;
    private bool       _wallsDone = false;

    private readonly List<GameObject> _obstacles = new List<GameObject>();

    // Static list for cross-script access (AutoWalkDriver, etc.)
    public static readonly List<GameObject> Placed = new List<GameObject>();

    public bool AllPlaced => _done;

    // ─────────────────────────────────────────────────────────────
    void Start()
    {
        Placed.Clear();
        _wallPlacer = FindObjectOfType<WallPlacer>();
        if (_wallPlacer == null)
        {
            _wallsDone = true;   // no wall placer — start immediately
            SpawnPreview();
        }
        // else: Update() polls WallPlacer.AllPlaced
    }

    void SpawnPreview()
    {
        if (_phase >= Total)
        {
            _done = true;
            if (_preview) Destroy(_preview);
            Debug.Log("[ObstaclePlacer] All obstacles placed ✅");
            return;
        }

        _rotY        = 0f;
        _currentSize = ObstacleSizes[_phase];
        _preview     = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _preview.name = $"ObstaclePreview_{_phase + 1}";
        _preview.transform.localScale = _currentSize;
        _preview.transform.position   = new Vector3((_phase - 2) * 1.4f,
                                                     floorY + _currentSize.y * 0.5f,
                                                     3f);
        _preview.GetComponent<Renderer>().material = MakeMat(previewColor, transparent: true);
        _preview.GetComponent<Collider>().enabled  = false;
    }

    // ─────────────────────────────────────────────────────────────
    void Update()
    {
        // Wait for walls first
        if (!_wallsDone)
        {
            if (_wallPlacer != null && _wallPlacer.AllPlaced)
            {
                _wallsDone = true;
                SpawnPreview();
            }
            return;
        }

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
        p.y  = floorY + _currentSize.y * 0.5f;
        _preview.transform.position = p;

        if (kb.qKey.isPressed) _rotY -= rotationSpeed * Time.deltaTime;
        if (kb.eKey.isPressed) _rotY += rotationSpeed * Time.deltaTime;
        _preview.transform.rotation = Quaternion.Euler(0f, _rotY, 0f);

        if (kb.spaceKey.wasPressedThisFrame) ConfirmObstacle();
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
        p.y = floorY + _currentSize.y * 0.5f;
        _preview.transform.position = p;

        // Left stick X = rotate (separate from move)
        _rotY += rotateStick.x * rotationSpeed * Time.deltaTime;
        _preview.transform.rotation = Quaternion.Euler(0f, _rotY, 0f);

        if (OVRInput.GetDown(OVRInput.Button.Two)) ConfirmObstacle(); // B button
    }

    // ─────────────────────────────────────────────────────────────
    void ConfirmObstacle()
    {
        _preview.name = $"Obstacle_{_phase + 1}";
        _preview.GetComponent<Renderer>().material = MakeMat(placedColor, transparent: false);
        _preview.GetComponent<Collider>().enabled  = true;

        _obstacles.Add(_preview);
        Placed.Add(_preview);
        _preview = null;

        _phase++;
        Debug.Log($"[ObstaclePlacer] Obstacle {_phase}/{Total} confirmed.");
        SpawnPreview();
    }

    // ─────────────────────────────────────────────────────────────
    public void FullReset()
    {
        if (_preview != null) { Destroy(_preview); _preview = null; }
        foreach (var go in _obstacles) if (go != null) Destroy(go);
        _obstacles.Clear();
        Placed.Clear();

        _phase = 0;
        _done  = false;
        _rotY  = 0f;

        // If walls exist, wait for them to be re-placed after a full reset
        _wallsDone = (_wallPlacer == null);
        if (_wallsDone) SpawnPreview();
        Debug.Log("[ObstaclePlacer] Reset.");
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
        if (!_wallsDone)
        {
            var s = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            s.normal.textColor = Color.cyan;
            GUI.Box(new Rect(Screen.width * 0.5f - 200f, 12f, 400f, 30f), "Waiting for walls to be placed…", s);
            return;
        }
        if (_done) return;

        var style = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = Color.white;
        string controls;
#if UNITY_EDITOR
        controls = "WASD move  |  Q/E rotate  |  SPACE confirm";
#else
        controls = "Right stick = move  |  Left stick = rotate  |  B = confirm";
#endif
        GUI.Box(new Rect(Screen.width * 0.5f - 200f, 12f, 400f, 52f),
            $"Place Obstacle {_phase + 1} / {Total}\n{controls}",
            style);
    }
}
