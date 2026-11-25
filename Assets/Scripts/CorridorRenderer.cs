using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [ExecuteAlways] — fires OnEnable in the Editor AND at runtime.
///
/// When ExperimentSceneSetup adds this component to CorridorRoot, OnEnable fires
/// immediately in the Editor and creates the walls/floor right there in the scene.
/// The geometry is then saved with the scene (Ctrl+S) and is 100% present in builds.
///
/// Materials use _BaseColor (the real URP property) not the Legacy _Color stub
/// that URP ignores — this was the invisible-wall root cause.
/// </summary>
[ExecuteAlways]
public class CorridorRenderer : MonoBehaviour
{
    // ── Corridor constants ────────────────────────────────────────
    const float LENGTH = 48.4f;
    const float WIDTH  = 1.80f;
    const float WALL_H = 2.50f;
    const float WALL_T = 0.10f;

    [Header("Colours (change and hit Rebuild to update)")]
    public Color WallColor  = new Color(0.90f, 0.88f, 0.84f, 1.00f);  // cream white, opaque
    public Color FloorColor = new Color(0.15f, 0.45f, 1.00f, 0.45f);  // blue, translucent
    public Color GoalColor  = new Color(0.20f, 1.00f, 0.40f, 0.85f);  // green, translucent

    // ─────────────────────────────────────────────────────────────
    void OnEnable()
    {
        // Fires in Editor (when component added) AND at runtime.
        // Guard prevents duplicate geometry if OnEnable fires twice.
        if (transform.Find("Wall_Left") == null)
            Build();
    }

    /// <summary>Destroy and recreate all corridor geometry. Safe to call at any time.</summary>
    public void Rebuild()
    {
        // Remove old geometry
        var toKill = new List<GameObject>();
        foreach (Transform ch in transform)
        {
            string n = ch.name;
            if (n == "Wall_Left"  || n == "Wall_Right"  ||
                n.StartsWith("Floor") || n.StartsWith("Goal"))
                toKill.Add(ch.gameObject);
        }
        foreach (var g in toKill)
            DestroyImmediate(g);   // works in editor and runtime

        Build();
    }

    // ── Build ─────────────────────────────────────────────────────
    void Build()
    {
        // ── Side walls ────────────────────────────────────────────
        MakeBox("Wall_Left",
            new Vector3(-WALL_T * 0.5f,  WALL_H * 0.5f, LENGTH * 0.5f),
            new Vector3( WALL_T,          WALL_H,        LENGTH),
            WallColor, collider: true);

        MakeBox("Wall_Right",
            new Vector3(WIDTH + WALL_T * 0.5f, WALL_H * 0.5f, LENGTH * 0.5f),
            new Vector3(WALL_T,                WALL_H,        LENGTH),
            WallColor, collider: true);

        // ── Floor overlay ─────────────────────────────────────────
        MakeBox("Floor_FreeStart",
            new Vector3(WIDTH * 0.5f, 0.002f,  5.0f),
            new Vector3(WIDTH,        0.004f, 10.0f),
            FloorColor, collider: false);

        MakeBox("Floor_Obstacles",
            new Vector3(WIDTH * 0.5f, 0.002f, 24.2f),
            new Vector3(WIDTH,        0.004f, 28.4f),
            FloorColor, collider: false);

        MakeBox("Floor_FreeEnd",
            new Vector3(WIDTH * 0.5f, 0.002f, 43.4f),
            new Vector3(WIDTH,        0.004f, 10.0f),
            FloorColor, collider: false);

        // ── Goal disc ─────────────────────────────────────────────
        var goal = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        goal.name = "GoalMarker";
        goal.transform.SetParent(transform, false);
        goal.transform.localPosition = new Vector3(WIDTH * 0.5f, 0.002f, LENGTH);
        goal.transform.localScale    = new Vector3(0.40f, 0.002f, 0.40f);
        DestroyImmediate(goal.GetComponent<Collider>());
        Paint(goal.GetComponent<Renderer>(), GoalColor);

        Debug.Log($"[CorridorRenderer] Built corridor: 2 walls + 3 floor sections + goal.");
    }

    // ── Primitive helper ──────────────────────────────────────────
    void MakeBox(string objName, Vector3 localPos, Vector3 localScale, Color color, bool collider)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = objName;
        go.transform.SetParent(transform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale    = localScale;

        if (!collider)
            DestroyImmediate(go.GetComponent<Collider>());

        Paint(go.GetComponent<Renderer>(), color);
        Debug.Log($"[CorridorRenderer] {objName} at {localPos}  scale {localScale}  alpha={color.a:F2}");
    }

    // ── Material ──────────────────────────────────────────────────
    static void Paint(Renderer rend, Color color)
    {
        if (rend == null) return;

        // URP/Lit is ALWAYS included in Quest builds (it's the default material shader).
        // URP/Unlit gets stripped from Android builds if no saved .mat file references it.
        // Chairs use URP/Lit and are visible — walls must use the same shader.
        Shader sh = Shader.Find("Universal Render Pipeline/Lit")
                 ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                 ?? Shader.Find("Universal Render Pipeline/Unlit");

        Material mat;
        if (sh != null)
        {
            mat = new Material(sh);
        }
        else
        {
            // No shader found — modify the existing default material
            Debug.LogWarning($"[CorridorRenderer] Shader.Find returned null for {rend.gameObject.name}. Using default material.");
            mat = rend.sharedMaterial != null
                ? new Material(rend.sharedMaterial)
                : new Material(Shader.Find("Standard"));
        }

        // Set colour via BOTH properties:
        //   _BaseColor = the real URP property (URP/Lit, URP/Unlit, URP/Simple Lit)
        //   _Color     = the Legacy/Standard property (Standard, Unlit/Color)
        //   .color     = Unity shortcut (maps to _Color, ignored by pure URP shaders!)
        mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        mat.color = color;   // redundant but harmless

        // Transparency (floor and goal have alpha < 1)
        if (color.a < 1f)
        {
            mat.SetFloat("_Surface",      1f);  // URP: 0=Opaque, 1=Transparent
            mat.SetFloat("_Blend",        0f);  // URP: 0=Alpha
            mat.SetFloat("_Mode",         3f);  // Standard: 3=Transparent
            mat.SetInt("_SrcBlend",       (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend",       (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite",         0);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        }
        else
        {
            mat.SetFloat("_Surface", 0f);  // URP: Opaque
            mat.SetInt("_ZWrite",    1);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
        }

        rend.sharedMaterial = mat;

        Debug.Log($"[CorridorRenderer] {rend.gameObject.name}: shader={mat.shader?.name ?? "NULL"}  " +
                  $"_BaseColor={color}  _Color set={mat.HasProperty("_Color")}");
    }
}
