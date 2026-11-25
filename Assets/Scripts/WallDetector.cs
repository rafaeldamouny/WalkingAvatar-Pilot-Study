using System.Collections;
using System.Collections.Generic;
using UnityEngine;

#if !UNITY_EDITOR
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// On device  : reads MRUK wall anchors → creates BoxColliders + optional
///              semi-transparent overlays for every detected wall.
/// In editor  : builds a configurable 4-wall room box so avoidance can be
///              tested without a headset.
///
/// Works automatically with AutoWalkDriver's SphereCast (uses ~0 layermask).
/// No extra wiring needed — just add this component to any GameObject.
/// </summary>
public class WallDetector : MonoBehaviour
{
    [Header("Visuals")]
    [Tooltip("Show a semi-transparent overlay on each wall")]
    public bool  showOverlay   = true;
    public Color wallColor     = new Color(0.4f, 0.65f, 1f, 0.18f); // light blue
    public Color edgeColor     = new Color(0.4f, 0.65f, 1f, 0.55f); // slightly brighter edge
    public float wallThickness = 0.08f; // depth of the BoxCollider / visual

    [Header("Editor Room Fallback")]
    [Tooltip("Total width (X) of the test room in metres")]
    public float roomWidth  = 8f;
    [Tooltip("Total depth (Z) of the test room in metres")]
    public float roomDepth  = 8f;
    [Tooltip("Wall height in metres")]
    public float roomHeight = 2.5f;
    [Tooltip("Room centre in world XZ (leave at 0,0 for most scenes)")]
    public Vector2 roomCenter = Vector2.zero;

    // ── runtime list of wall GameObjects ─────────────────────────
    private readonly List<GameObject> _walls = new List<GameObject>();

    public IReadOnlyList<GameObject> Walls => _walls;

    // ─────────────────────────────────────────────────────────────
    IEnumerator Start()
    {
#if UNITY_EDITOR
        BuildEditorRoom();
        Debug.Log("[WallDetector] Editor room walls ready ✅");
        yield break;
#else
        // ── On device: wait for MRUK ─────────────────────────────
        while (MRUK.Instance == null) yield return null;

        MRUKRoom room = null;
        while (room == null) { room = MRUK.Instance.GetCurrentRoom(); yield return null; }

        var walls = room.WallAnchors;
        if (walls != null && walls.Count > 0)
        {
            foreach (var anchor in walls)
                BuildWallFromAnchor(anchor);
            Debug.Log($"[WallDetector] {walls.Count} MRUK wall(s) created ✅");
        }
        else
        {
            Debug.LogWarning("[WallDetector] No WallAnchors found in MRUK room — editor room box used as fallback.");
            BuildEditorRoom();
        }
#endif
    }

    // ── MRUK wall anchor → BoxCollider ───────────────────────────
#if !UNITY_EDITOR
    void BuildWallFromAnchor(MRUKAnchor anchor)
    {
        Vector3    center = anchor.GetAnchorCenter();
        Vector3    size   = anchor.GetAnchorSize();  // (width, height, ?)
        Quaternion rot    = anchor.transform.rotation;

        float w = size.x > 0 ? size.x : 1f;
        float h = size.y > 0 ? size.y : roomHeight;

        var go = SpawnWall(anchor.name + "_Wall", center, rot,
                           new Vector3(w, h, wallThickness));
        _walls.Add(go);
    }
#endif

    // ── Editor: 4-wall room box ───────────────────────────────────
    void BuildEditorRoom()
    {
        float cx = roomCenter.x;
        float cz = roomCenter.y;   // Vector2.y maps to world Z
        float hw = roomWidth  / 2f;
        float hd = roomDepth  / 2f;
        float hy = roomHeight / 2f;
        float t  = wallThickness;

        // North (+Z), South (-Z), East (+X), West (-X)
        Add(SpawnWall("Wall_N", new Vector3(cx,      hy, cz + hd), Quaternion.identity,             new Vector3(roomWidth,  roomHeight, t)));
        Add(SpawnWall("Wall_S", new Vector3(cx,      hy, cz - hd), Quaternion.identity,             new Vector3(roomWidth,  roomHeight, t)));
        Add(SpawnWall("Wall_E", new Vector3(cx + hw, hy, cz),      Quaternion.identity,             new Vector3(t,          roomHeight, roomDepth)));
        Add(SpawnWall("Wall_W", new Vector3(cx - hw, hy, cz),      Quaternion.identity,             new Vector3(t,          roomHeight, roomDepth)));

        void Add(GameObject g) => _walls.Add(g);
    }

    // ── Shared wall builder ───────────────────────────────────────
    GameObject SpawnWall(string goName, Vector3 pos, Quaternion rot, Vector3 boxSize)
    {
        var go = new GameObject(goName);
        go.transform.SetPositionAndRotation(pos, rot);

        // Solid BoxCollider — CharacterController and SphereCast both detect this
        var bc   = go.AddComponent<BoxCollider>();
        bc.size  = boxSize;
        bc.isTrigger = false;

        if (showOverlay)
            AttachVisual(go, boxSize);

        return go;
    }

    // ── Semi-transparent wall visual ─────────────────────────────
    void AttachVisual(GameObject parent, Vector3 size)
    {
        // Fill plane
        var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
        fill.name = "WallFill";
        Destroy(fill.GetComponent<Collider>());
        fill.transform.SetParent(parent.transform, false);
        fill.transform.localPosition = Vector3.zero;
        fill.transform.localScale    = size;
        fill.GetComponent<Renderer>().material = MakeMat(wallColor, 3000);

        // Edge outline — 4 thin quads along the wall border
        float w = size.x, h = size.y, hw = w / 2f, hh = h / 2f;
        float et = 0.03f; // edge thickness (visual only)

        SpawnEdge(parent, "Edge_T", new Vector3(0,  hh, 0),  new Vector3(w,  et, et));
        SpawnEdge(parent, "Edge_B", new Vector3(0, -hh, 0),  new Vector3(w,  et, et));
        SpawnEdge(parent, "Edge_L", new Vector3(-hw, 0, 0),  new Vector3(et, h,  et));
        SpawnEdge(parent, "Edge_R", new Vector3( hw, 0, 0),  new Vector3(et, h,  et));
    }

    void SpawnEdge(GameObject parent, string name, Vector3 localPos, Vector3 localScale)
    {
        var e = GameObject.CreatePrimitive(PrimitiveType.Cube);
        e.name = name;
        Destroy(e.GetComponent<Collider>());
        e.transform.SetParent(parent.transform, false);
        e.transform.localPosition = localPos;
        e.transform.localScale    = localScale;
        e.GetComponent<Renderer>().material = MakeMat(edgeColor, 3001);
    }

    // ── Material helper ───────────────────────────────────────────
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

    // ── Helpers ───────────────────────────────────────────────────
    public void SetOverlayVisible(bool visible)
    {
        foreach (var w in _walls)
            foreach (var r in w.GetComponentsInChildren<Renderer>())
                r.enabled = visible;
    }
}
