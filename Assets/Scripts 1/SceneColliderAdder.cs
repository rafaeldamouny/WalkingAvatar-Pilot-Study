using UnityEngine;
using System.Collections;

public class SceneColliderAdder : MonoBehaviour
{
    IEnumerator Start()
    {
        // Wait for scene anchors to load
        yield return new WaitForSeconds(3f);
        AddCollidersToScene();
    }

    void AddCollidersToScene()
    {
        var classifications = FindObjectsOfType<OVRSemanticClassification>();
        foreach (var c in classifications)
        {
            if (c.GetComponent<Collider>() == null)
            {
                var meshFilter = c.GetComponent<MeshFilter>();
                if (meshFilter != null && meshFilter.mesh != null)
                {
                    c.gameObject.AddComponent<MeshCollider>();
                }
                else
                {
                    // Fallback: add box collider
                    c.gameObject.AddComponent<BoxCollider>();
                }
                Debug.Log($"AVATARPLACER: Added collider to {c.gameObject.name} ({string.Join(", ", c.Labels)})");
            }
        }
    }
}