using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// One-click fixer: converts all Survivalist materials from Built-in Standard
/// to URP Lit, preserving textures and values.
/// Run via:  Tools → Fix Survivalist Materials (URP)
/// </summary>
public static class FixSurvivalistMaterials
{
    private const string MaterialFolder = "Assets/Survivalist";

    [MenuItem("Tools/Fix Survivalist Materials (URP)")]
    public static void FixMaterials()
    {
        Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit == null)
        {
            Debug.LogError("[FixMaterials] URP Lit shader not found. Make sure URP is installed.");
            return;
        }

        string[] guids = AssetDatabase.FindAssets("t:Material", new[] { MaterialFolder });
        int fixed_ = 0;

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (mat == null) continue;

            // Skip HDRP and already-correct URP materials
            if (path.Contains("Materials HDRP") || path.Contains("Materials URP")) continue;

            // Skip if already URP Lit
            string shaderName = mat.shader != null ? mat.shader.name : "";
            if (shaderName == "Universal Render Pipeline/Lit") continue;

            // --- Cache Built-in properties before swapping shader ---
            Texture albedo    = mat.HasProperty("_MainTex")          ? mat.GetTexture("_MainTex")          : null;
            Color   baseColor = mat.HasProperty("_Color")            ? mat.GetColor("_Color")              : Color.white;
            Texture normalMap = mat.HasProperty("_BumpMap")          ? mat.GetTexture("_BumpMap")          : null;
            float   bumpScale = mat.HasProperty("_BumpScale")        ? mat.GetFloat("_BumpScale")          : 1f;
            Texture metalGlos = mat.HasProperty("_MetallicGlossMap") ? mat.GetTexture("_MetallicGlossMap") : null;
            float   metallic  = mat.HasProperty("_Metallic")         ? mat.GetFloat("_Metallic")           : 0f;
            float   smooth    = mat.HasProperty("_Glossiness")       ? mat.GetFloat("_Glossiness")         : 0.5f;
            Texture occl      = mat.HasProperty("_OcclusionMap")     ? mat.GetTexture("_OcclusionMap")     : null;
            Texture emission  = mat.HasProperty("_EmissionMap")      ? mat.GetTexture("_EmissionMap")      : null;
            Color   emitColor = mat.HasProperty("_EmissionColor")    ? mat.GetColor("_EmissionColor")      : Color.black;

            // --- Swap to URP Lit ---
            mat.shader = urpLit;

            // --- Re-apply properties using URP property names ---
            if (albedo    != null) mat.SetTexture("_BaseMap",           albedo);
                                   mat.SetColor("_BaseColor",           baseColor);
            if (normalMap != null) mat.SetTexture("_BumpMap",           normalMap);
                                   mat.SetFloat("_BumpScale",           bumpScale);
            if (metalGlos != null) mat.SetTexture("_MetallicGlossMap",  metalGlos);
                                   mat.SetFloat("_Metallic",            metallic);
                                   mat.SetFloat("_Smoothness",          smooth);
            if (occl      != null) mat.SetTexture("_OcclusionMap",      occl);
            if (emission  != null)
            {
                mat.SetTexture("_EmissionMap", emission);
                mat.SetColor("_EmissionColor", emitColor);
                mat.EnableKeyword("_EMISSION");
            }

            EditorUtility.SetDirty(mat);
            fixed_++;
            Debug.Log($"[FixMaterials] Converted: {Path.GetFileName(path)}");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[FixMaterials] Done — {fixed_} material(s) converted to URP Lit ✅");
        EditorUtility.DisplayDialog("Fix Survivalist Materials", $"Done! {fixed_} materials converted to URP Lit.", "OK");
    }
}
