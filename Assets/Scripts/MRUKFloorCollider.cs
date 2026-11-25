using System.Collections;
using UnityEngine;
using Meta.XR.MRUtilityKit; // MRUK namespace

public class MRUKFloorCollider : MonoBehaviour
{
    [Header("Assign in Inspector")]
    public Transform realFloor; // your RealFloor GameObject

    [Header("Collider Settings")]
    public float thickness = 0.05f; // 5 cm
    public float margin = 0.20f;    // 20 cm

    [Header("Debug")]
    public bool createDebugCube = true;

    private IEnumerator Start()
    {
        // Wait until MRUK instance exists
        while (MRUK.Instance == null)
            yield return null;

        // Wait until a room is available (room scan exists + spatial data allowed)
        MRUKRoom room = null;
        while (room == null)
        {
            room = MRUK.Instance.GetCurrentRoom();
            yield return null;
        }

        // Wait until the floor anchor exists
        MRUKAnchor floor = null;
        while (floor == null)
        {
            floor = room.FloorAnchor;
            yield return null;
        }

        BuildFloorCollider(floor);
        Debug.Log("[MRUKFloorCollider] Real floor collider built successfully ✅");
    }

    private void BuildFloorCollider(MRUKAnchor floor)
    {
        if (!realFloor)
        {
            Debug.LogError("[MRUKFloorCollider] realFloor is not assigned!");
            return;
        }

        // Best-practice: use MRUK convenience methods
        Vector3 center = floor.GetAnchorCenter(); // world center
        Vector3 size = floor.GetAnchorSize();     // x,z size for plane-type anchors
        Quaternion rotation = floor.transform.rotation;

        // Clean old collider(s)
        foreach (var c in realFloor.GetComponents<Collider>())
            Destroy(c);

        // Place & rotate the floor object
        realFloor.position = center;
        realFloor.rotation = rotation;

        // Build a box collider whose top face is at floor height
        var box = realFloor.gameObject.AddComponent<BoxCollider>();
        box.size = new Vector3(size.x + margin, thickness, size.z + margin);
        box.center = new Vector3(0f, -thickness * 0.5f, 0f);

        // Optional debug cube to visualize it
        if (createDebugCube)
        {
            // Remove old debug child if exists
            var old = realFloor.Find("FloorDebugCube");
            if (old) Destroy(old.gameObject);

            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "FloorDebugCube";
            cube.transform.SetParent(realFloor, false);
            cube.transform.localPosition = box.center;
            cube.transform.localScale = box.size;

            // Remove collider on the cube (we only want the BoxCollider above)
            Destroy(cube.GetComponent<Collider>());
        }
    }
}
