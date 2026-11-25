using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// Rewires all Survivalist prefabs to use the pre-made URP materials
/// instead of the converted Built-in ones.
/// Run via: Tools → Rewire Survivalist Prefabs to URP Materials
/// </summary>
public static class RewireSurvivalistToURP
{
    // Maps old material GUID → URP material GUID
    private static readonly Dictionary<string, string> GuidMap = new Dictionary<string, string>
    {
        { "bb22c5d48abf85d4f98617d056e1d110", "1cddc3097f086754c9af375afced67ff" }, // Backpack1_Skin1
        { "3c18a64b693f2fc45a7f092ddf46cbce", "a4a8d1cc9aa491b47b9b5881c72e8131" }, // Backpack1_Skin2
        { "daa8d9ddafbf2c3448faa8e6f2b6ae90", "bc58aa514ab258f4893db2112f1454c6" }, // Backpack1_Skin3
        { "b15ec2cf8f326d6478fb6a1609b2ad4a", "1ddc53d882c351049bd35d969f8dc0bd" }, // Backpack1_Skin4
        { "b4968c47c65a7f04d838250af66552f0", "69a7ae62ad9e47040b84931e37b8966e" }, // Boots3_Skin1
        { "2c89187d4215b9f4c83dcefb5456897d", "0a330e6eee7c9d745b76b538da6b35c8" }, // Boots3_Skin2
        { "a210e4596a34b764387336c54096ea70", "4069970779519534094e765126f96e0d" }, // Boots3_Skin3
        { "ca362cd434882e7468146d69e8b14eda", "9e2e0aa2d3552f44ca191a0594ed99fc" }, // Cap1_Skin1
        { "177b89c6830906448881f566d8143446", "395fd9fdae985024b8e9c65ed877cfce" }, // Cap1_Skin2
        { "96a5b189e0fdcb74b970135d97057d9a", "99af33f982d11b7438d3a673d4c1c817" }, // Cap1_Skin3
        { "3ada2b513534a4a42806251244eefe3d", "ccfb5ff9a2f4e8f4c9b8355539a1358f" }, // Cap1_Skin4
        { "38b71884de7d2094587df9ab6c870d4b", "d2597d236b82606429a6b105ebaca1c7" }, // Eyes1
        { "308bfb0f350b3eb4781240efb6c21c23", "a92c3a3369b862448a6861f1c2f9c5b1" }, // FPS_Hands1_Skin1
        { "7e5e6c8eb72897145ac408f805ab3a7a", "3e4cde5eedb06e944bae6f80ebd17996" }, // Gloves1_Skin1
        { "953e54df3cf3ebd459b13ba48cac3a19", "246bdadbf19ef27498ed6bb60ce05e75" }, // Gloves1_Skin2
        { "a4f3038e85335ab4eb10f074fda8b6ad", "783219dea1a267b46b5cb22cfc843e5c" }, // Hands1_Skin1
        { "1f04324fc44f0984596062b227d2375c", "783219dea1a267b46b5cb22cfc843e5c" }, // Hands1_Skin2 → Hands1_Skin1 (no Skin2 URP)
        { "01e354ee2a262cf40a7b08040b6236fe", "e4a33c71948b6a846b08d647bb62280c" }, // Head1
        { "39c752ff4621e8f408fe3e0030d2cbce", "cc095e0331d4a0946aebfd987c33f600" }, // Pants2_Skin1
        { "91e1bfddab2e07149a6e37f1c74aa6e0", "ce27bb3f00af46c4288dbf35acdc234a" }, // Pants2_Skin2
        { "907de43488b59c54086bb78e00686023", "116fe99a763e66041a7d0596a9b72544" }, // Pants2_Skin3
        { "66281e6f4c3b1b34d8046c9fdea8fa9f", "67185e5c94b5e1a4b8b70464ea42260f" }, // Pouches1_Skin1
        { "fd4594c9614f0424da87b142d7d3801c", "a33c80a5b7a874e498ecdce08ccdd177" }, // Pouches1_Skin2
        { "c4ebab701bd8b4244a7201e9c2ee70ec", "b5740b5274315c84c894007d708bdc6c" }, // Pouches1_Skin3
        { "8f39a9a3bee8af04889e87c953c40533", "0007a2dba9c981d49bb8c07f309c8de1" }, // Shirt3_Skin1
        { "2eafbc23cd3c62a47b7b2dcbf76f1526", "57aaf53e7b9319e4189c35b2eb5d3483" }, // Shirt3_Skin2
        { "ed70616142da0fc49b90b7515061ffcf", "689396140f60e7f41a1405d5351e2f19" }, // Shirt3_Skin3
        { "85442a5678b051043a848d96f57c039d", "13b98a63cdb3b824090d7911fe076d4b" }, // Vest1_Skin1
        { "433800eadcf133e42b71c8c30a6d86e0", "ab3b0e5ba4264dd4ab18f365c6986aff" }, // Vest1_Skin2
        { "0965979b013b68345a68889b01fdc0e8", "f6e3ab4d255152c40b45cd4e6fb1c77d" }, // Vest1_Skin3
    };

    [MenuItem("Tools/Rewire Survivalist Prefabs to URP Materials")]
    public static void RewirePrefabs()
    {
        // Pre-load all URP materials keyed by GUID
        var urpMaterials = new Dictionary<string, Material>();
        foreach (var kvp in GuidMap)
        {
            string urpPath = AssetDatabase.GUIDToAssetPath(kvp.Value);
            if (!string.IsNullOrEmpty(urpPath))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(urpPath);
                if (mat != null) urpMaterials[kvp.Key] = mat;
            }
        }

        string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Survivalist" });
        int totalSwaps = 0;

        foreach (string prefabGuid in prefabGuids)
        {
            string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) continue;

            // Edit the prefab asset directly
            using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
            {
                var root = scope.prefabContentsRoot;
                int swapsInPrefab = 0;

                // Check all renderers
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = renderer.sharedMaterials;
                    bool changed = false;

                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] == null) continue;
                        string oldGuid = AssetDatabase.AssetPathToGUID(
                            AssetDatabase.GetAssetPath(mats[i]));

                        if (urpMaterials.TryGetValue(oldGuid, out Material urpMat))
                        {
                            mats[i] = urpMat;
                            changed = true;
                            swapsInPrefab++;
                        }
                    }

                    if (changed) renderer.sharedMaterials = mats;
                }

                if (swapsInPrefab > 0)
                {
                    totalSwaps += swapsInPrefab;
                    Debug.Log($"[RewireURP] {System.IO.Path.GetFileName(prefabPath)}: {swapsInPrefab} material(s) rewired");
                }
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[RewireURP] Done — {totalSwaps} total material slot(s) rewired to URP ✅");
        EditorUtility.DisplayDialog("Rewire Complete", $"Done! {totalSwaps} material slots now use URP materials.", "OK");
    }
}
