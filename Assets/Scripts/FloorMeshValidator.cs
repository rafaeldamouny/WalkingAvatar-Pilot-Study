using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if !UNITY_EDITOR
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Adaptive floor validator:
/// - Creates an invisible BoxCollider floor so CharacterController avatars can stand
/// - Overlays a semi-transparent green grid that tiles under the avatar in real-time
/// - Grid snaps to world-space increments — appears infinite as the avatar walks
/// </summary>
public class FloorMeshValidator : MonoBehaviour
{
    [Header("Visual")]
    public Color meshColor   = new Color(0f, 1f, 0f, 0.25f);
    public Color gridColor   = new Color(0f, 1f, 0f, 0.85f);
    public float gridSpacing = 0.5f;     // metres per cell
    public float gridRadius  = 4f;       // how far the grid extends from the avatar
    public float yOffset     = 0.004f;   // just above floor to avoid z-fight

    [Header("Editor Fallback")]
    public float editorFloorY = 0f;

    [Header("Runtime")]
    [Tooltip("Assign the spawned avatar Transform here so the grid follows it.")]
    public Transform avatarTransform;

    // ── internals ─────────────────────────────────────────────────
    private float   _floorY;
    private GameObject _colliderFloor;
    private GameObject _gridParent;
    private GameObject _fillQuad;

    private readonly List<LineRenderer> _linesX = new List<LineRenderer>();
    private readonly List<LineRenderer> _linesZ = new List<LineRenderer>();

    private Vector3 _lastSnap = new Vector3(float.MaxValue, 0, float.MaxValue);

    // ─────────────────────────────────────────────────────────────
    IEnumerator Start()
    {
#if UNITY_EDITOR
        _floorY = editorFloorY;
        Build();
        Debug.Log("[FloorMeshValidator] Editor floor ready ✅");
        yield break;
#else
        while (MRUK.Instance == null) yield return null;
        MRUKRoom room = null;
        while (room == null) { room = MRUK.Instance.GetCurrentRoom(); yield return null; }
        MRUKAnchor floor = null;
        while (floor == null) { floor = room.FloorAnchor; yield return null; }

        _floorY = floor.GetAnchorCenter().y;
        Build();
        Debug.Log("[FloorMeshValidator] MRUK floor ready ✅");
#endif
    }

    // ─────────────────────────────────────────────────────────────
    void Build()
    {
        BuildColliderFloor();
        BuildFillQuad();
        BuildGridLines();
    }

    /// <summary>Large invisible BoxCollider — CharacterController stands on this.</summary>
    void BuildColliderFloor()
    {
        _colliderFloor = new GameObject("FloorCollider");
        _colliderFloor.transform.position = new Vector3(0f, _floorY - 0.05f, 0f);

        var bc = _colliderFloor.AddComponent<BoxCollider>();
        bc.size   = new Vector3(60f, 0.1f, 60f); // 60×60 m, 10 cm thick
        bc.center = Vector3.zero;

        // No renderer — purely physical
    }

    /// <summary>Semi-transparent green fill quad under the grid — gives depth.</summary>
    void BuildFillQuad()
    {
        _fillQuad = GameObject.CreatePrimitive(PrimitiveType.Plane);
        _fillQuad.name = "FloorFill";
        Destroy(_fillQuad.GetComponent<Collider>());

        float size = gridRadius * 2f;
        _fillQuad.transform.localScale = new Vector3(size / 10f, 1f, size / 10f);
        _fillQuad.GetComponent<Renderer>().material = MakeMat(meshColor, 3000);
    }

    /// <summary>Grid lines — rebuilt in world space when avatar moves a full cell.</summary>
    void BuildGridLines()
    {
        _gridParent = new GameObject("FloorGrid");
        var mat = MakeMat(gridColor, 3001);

        int count = Mathf.CeilToInt(gridRadius / gridSpacing) * 2 + 2;

        for (int i = 0; i < count; i++)
        {
            _linesX.Add(MakeLine(_gridParent, mat));
            _linesZ.Add(MakeLine(_gridParent, mat));
        }

        // Force first update
        UpdateGridToAvatar(forceRebuild: true);
    }

    // ─────────────────────────────────────────────────────────────
    void Update()
    {
        if (avatarTransform == null) return;
        UpdateGridToAvatar();
    }

    void UpdateGridToAvatar(bool forceRebuild = false)
    {
        Vector3 avatarPos = avatarTransform != null
            ? avatarTransform.position
            : Vector3.zero;

        // Snap center to grid spacing so lines appear world-aligned
        float snapX = Mathf.Round(avatarPos.x / gridSpacing) * gridSpacing;
        float snapZ = Mathf.Round(avatarPos.z / gridSpacing) * gridSpacing;
        Vector3 snap = new Vector3(snapX, 0f, snapZ);

        // Only rebuild when snapped position changes by one cell
        if (!forceRebuild &&
            Mathf.Abs(snap.x - _lastSnap.x) < 0.001f &&
            Mathf.Abs(snap.z - _lastSnap.z) < 0.001f)
        {
            return;
        }

        _lastSnap = snap;
        float y = _floorY + yOffset;

        // Move fill quad
        if (_fillQuad != null)
            _fillQuad.transform.position = new Vector3(snapX, y, snapZ);

        // Rebuild X lines (run along X axis, vary Z)
        int idx = 0;
        float z = snapZ - gridRadius;
        float step = gridRadius * 2f / (_linesX.Count - 1);
        foreach (var lr in _linesX)
        {
            float wz = snapZ - gridRadius + idx * step;
            // Snap wz to grid
            wz = Mathf.Round(wz / gridSpacing) * gridSpacing;
            lr.SetPosition(0, new Vector3(snapX - gridRadius, y, wz));
            lr.SetPosition(1, new Vector3(snapX + gridRadius, y, wz));
            idx++;
        }

        // Rebuild Z lines (run along Z axis, vary X)
        idx = 0;
        foreach (var lr in _linesZ)
        {
            float wx = snapX - gridRadius + idx * step;
            wx = Mathf.Round(wx / gridSpacing) * gridSpacing;
            lr.SetPosition(0, new Vector3(wx, y, snapZ - gridRadius));
            lr.SetPosition(1, new Vector3(wx, y, snapZ + gridRadius));
            idx++;
        }
    }

    // ─────────────────────────────────────────────────────────────
    LineRenderer MakeLine(GameObject parent, Material mat)
    {
        var go = new GameObject("L");
        go.transform.SetParent(parent.transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.startWidth = lr.endWidth = 0.006f;
        lr.material  = mat;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        return lr;
    }

    Material MakeMat(Color color, int queue)
    {
        var s = Shader.Find("Universal Render Pipeline/Unlit");
        if (s == null) s = Shader.Find("Sprites/Default");
        var m = new Material(s);
        m.color = color;
        m.SetFloat("_Surface", 1);
        m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetInt("_ZWrite", 0);
        m.renderQueue = queue;
        return m;
    }

    public void SetVisible(bool v)
    {
        if (_fillQuad)   _fillQuad.SetActive(v);
        if (_gridParent) _gridParent.SetActive(v);
    }
}
