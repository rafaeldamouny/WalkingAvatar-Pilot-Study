using UnityEngine;

/// <summary>
/// Destroys the [BuildingBlock] EffectMesh fake room on startup.
/// Attach to any always-active GameObject in the Working scene.
/// </summary>
public class DisableEffectMesh : MonoBehaviour
{
    void Awake()
    {
        // Search all root objects including inactive ones
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (go.scene.name == null || go.scene.name == "") continue; // skip prefab assets
            if (go.name.Contains("EffectMesh") || go.name.Contains("Effect Mesh"))
            {
                Debug.Log($"[DisableEffectMesh] Destroying: {go.name}");
                Destroy(go);
            }
        }
    }
}
