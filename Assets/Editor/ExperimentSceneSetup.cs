// Assets/Editor/ExperimentSceneSetup.cs
// Menu:  Experiment ▶ Build Experiment Scene
//        Experiment ▶ Validate Resources

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ExperimentSceneSetup
{
    const float CORRIDOR_LENGTH = 48.4f;
    const float CORRIDOR_WIDTH  = 1.80f;
    const float WALL_HEIGHT     = 2.50f;
    const float WALL_THICKNESS  = 0.10f;

    // ── Material asset paths ──────────────────────────────────────
    // Saved as real .mat files so they CANNOT be shader-stripped from builds.
    const string MAT_FOLDER = "Assets/Materials/Corridor";
    const string MAT_WALL   = "Assets/Materials/Corridor/Wall.mat";
    const string MAT_FLOOR  = "Assets/Materials/Corridor/Floor.mat";
    const string MAT_GOAL   = "Assets/Materials/Corridor/Goal.mat";

    [MenuItem("Experiment/Build Experiment Scene")]
    static void BuildScene()
    {
        if (!EditorUtility.DisplayDialog("Build Experiment Scene",
            "Rebuild the experiment scene in the current open scene?", "Yes", "Cancel")) return;

        Undo.SetCurrentGroupName("Build Experiment Scene");
        int grp = Undo.GetCurrentGroup();

        // ── 1. Create material assets on disk (guaranteed in builds) ─
        EnsureMaterials();

        // ── 2. Remove old experiment objects ─────────────────────────
        Cleanup();

        // ── 3. Corridor root ──────────────────────────────────────────
        var root = new GameObject("CorridorRoot");
        Undo.RegisterCreatedObjectUndo(root, "CorridorRoot");
        root.transform.position = Vector3.zero;

        // Left wall  ─ X = 0
        MakeBox("Wall_Left", root.transform,
            new Vector3(-WALL_THICKNESS * 0.5f, WALL_HEIGHT * 0.5f, CORRIDOR_LENGTH * 0.5f),
            new Vector3(WALL_THICKNESS, WALL_HEIGHT, CORRIDOR_LENGTH),
            MAT_WALL, collider: true);

        // Right wall ─ X = 1.8
        MakeBox("Wall_Right", root.transform,
            new Vector3(CORRIDOR_WIDTH + WALL_THICKNESS * 0.5f, WALL_HEIGHT * 0.5f, CORRIDOR_LENGTH * 0.5f),
            new Vector3(WALL_THICKNESS, WALL_HEIGHT, CORRIDOR_LENGTH),
            MAT_WALL, collider: true);

        // Floor sections (no collider — avatar uses transform.position = Y of path)
        MakeBox("Floor_FreeStart", root.transform,
            new Vector3(CORRIDOR_WIDTH * 0.5f, 0.003f,  5.0f),
            new Vector3(CORRIDOR_WIDTH, 0.006f, 10.0f), MAT_FLOOR, collider: false);

        MakeBox("Floor_Obstacles", root.transform,
            new Vector3(CORRIDOR_WIDTH * 0.5f, 0.003f, 24.2f),
            new Vector3(CORRIDOR_WIDTH, 0.006f, 28.4f), MAT_FLOOR, collider: false);

        MakeBox("Floor_FreeEnd", root.transform,
            new Vector3(CORRIDOR_WIDTH * 0.5f, 0.003f, 43.4f),
            new Vector3(CORRIDOR_WIDTH, 0.006f, 10.0f), MAT_FLOOR, collider: false);

        // Goal marker
        var goal = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Undo.RegisterCreatedObjectUndo(goal, "GoalMarker");
        goal.name = "GoalMarker";
        goal.transform.SetParent(root.transform, false);
        goal.transform.localPosition = new Vector3(CORRIDOR_WIDTH * 0.5f, 0.003f, CORRIDOR_LENGTH);
        goal.transform.localScale    = new Vector3(0.4f, 0.003f, 0.4f);
        Object.DestroyImmediate(goal.GetComponent<Collider>());
        goal.GetComponent<Renderer>().sharedMaterial =
            AssetDatabase.LoadAssetAtPath<Material>(MAT_GOAL);

        // ── 4. Supporting objects ─────────────────────────────────────
        var chairContainer = new GameObject("ChairContainer");
        Undo.RegisterCreatedObjectUndo(chairContainer, "ChairContainer");
        chairContainer.transform.SetParent(root.transform, false);

        var spawn = new GameObject("AvatarSpawnPoint");
        Undo.RegisterCreatedObjectUndo(spawn, "AvatarSpawnPoint");
        spawn.transform.SetParent(root.transform, false);
        spawn.transform.position = new Vector3(CORRIDOR_WIDTH * 0.5f, 0f, 0f);

        // Directional light (avatar Lit materials need one)
        if (Object.FindObjectOfType<Light>() == null)
        {
            var lg = new GameObject("DirectionalLight");
            var lt = lg.AddComponent<Light>();
            lt.type = LightType.Directional; lt.intensity = 1.2f;
            lg.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Undo.RegisterCreatedObjectUndo(lg, "DirectionalLight");
        }

        // ── 5. ExperimentManager ──────────────────────────────────────
        var mgr = new GameObject("ExperimentManager");
        Undo.RegisterCreatedObjectUndo(mgr, "ExperimentManager");
        var tm = mgr.AddComponent<TrialManager>();
        tm.corridorRoot     = root.transform;
        tm.chairContainer   = chairContainer.transform;
        tm.avatarSpawnPoint = spawn.transform;
        mgr.AddComponent<ExperimentMenu>();

        Undo.CollapseUndoOperations(grp);
        Selection.activeGameObject = mgr;

        EditorUtility.DisplayDialog("Done",
            "Scene built.\n\n" +
            "You should see Wall_Left and Wall_Right under CorridorRoot in the Hierarchy RIGHT NOW.\n\n" +
            "Save the scene (Ctrl+S) then assign on TrialManager:\n" +
            "  • avatarPrefab\n  • chairPrefab\n  • animController\n  • avatarRigAsset", "OK");
    }

    // ── Material assets ───────────────────────────────────────────
    static void EnsureMaterials()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");
        if (!AssetDatabase.IsValidFolder(MAT_FOLDER))
            AssetDatabase.CreateFolder("Assets/Materials", "Corridor");

        // Walls: BRIGHT RED — unmistakably virtual against any real room background.
        // Change to a subtler color later once you confirm they're visible.
        EnsureMat(MAT_WALL,  new Color(1.00f, 0.05f, 0.05f, 1f), transparent: false);
        // Floor — bright blue
        EnsureMat(MAT_FLOOR, new Color(0.05f, 0.30f, 1.00f, 1f), transparent: false);
        // Goal — bright green
        EnsureMat(MAT_GOAL,  new Color(0.10f, 0.90f, 0.20f, 1f), transparent: false);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static void EnsureMat(string path, Color color, bool transparent)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit")
                  ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                  ?? Shader.Find("Standard");
            mat = new Material(sh);
            AssetDatabase.CreateAsset(mat, path);
        }

        // Set both URP and Legacy color properties
        mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
        mat.color = color;

        if (transparent)
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.renderQueue = 3000;
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "Transparent");
        }
        else
        {
            mat.SetFloat("_Surface", 0f);
            mat.SetInt("_ZWrite", 1);
            mat.renderQueue = 2000;
        }

        EditorUtility.SetDirty(mat);
    }

    // ── Box helper ────────────────────────────────────────────────
    static void MakeBox(string name, Transform parent, Vector3 pos, Vector3 scale,
                        string matPath, bool collider)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Undo.RegisterCreatedObjectUndo(go, name);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.position   = pos;
        go.transform.localScale = scale;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat) go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    // ── Cleanup ───────────────────────────────────────────────────
    static void Cleanup()
    {
        // Order matters: remove dependents before their dependencies.
        RemoveComponents<ExperimentMenu>();   // [RequireComponent(TrialManager)] — must go first
        RemoveComponents<SimpleWalkDriver>();
        RemoveComponents<CorridorRenderer>();
        RemoveComponents<TrialManager>();     // removed last

        foreach (var n in new[]
        {
            "CorridorRoot","ExperimentManager","DirectionalLight",
            "GoalMarker","SW_Walked","SW_Planned","PhysicsFloor"
        })
        {
            var found = GameObject.Find(n);
            if (found) Undo.DestroyObjectImmediate(found);
        }
    }

    static void RemoveComponents<T>() where T : Component
    {
        foreach (var c in Object.FindObjectsByType<T>(FindObjectsSortMode.None))
            if (c) Undo.DestroyObjectImmediate(c);
    }

    // ── Validate Resources ────────────────────────────────────────
    [MenuItem("Experiment/Validate Resources")]
    static void ValidateResources()
    {
        int ok = 0, bad = 0;
        for (int i = 1; i <= 12; i++)
        {
            bool lOk = Resources.Load<TextAsset>($"Layouts/P{i:D2}_chairs") != null;
            bool pOk = Resources.Load<TextAsset>($"Paths/P{i:D2}_avatar_path") != null;
            if (lOk) ok++; else { bad++; Debug.LogError($"MISSING: Resources/Layouts/P{i:D2}_chairs.json"); }
            if (pOk) ok++; else { bad++; Debug.LogError($"MISSING: Resources/Paths/P{i:D2}_avatar_path.json"); }
        }
        EditorUtility.DisplayDialog("Resources",
            bad == 0 ? $"All 24 JSON files found." : $"{bad} files missing — check Console.", "OK");
    }
}
#endif
