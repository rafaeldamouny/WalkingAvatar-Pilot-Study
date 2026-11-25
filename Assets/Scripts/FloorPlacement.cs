using UnityEngine;

public class FloorPlacement : MonoBehaviour
{
    [Header("Feet (world transforms)")]
    public Transform leftFootBall;
    public Transform rightFootBall;

    [Header("Move THIS (parent rig root)")]
    public Transform avatarRoot; // assign AvatarRigRoot here

    [Header("Floor Detection")]
    public LayerMask floorMask;          // set to Floor layer
    public float raycastHeight = 0.4f;   // start a bit above foot
    public float raycastDistance = 2.0f; // downwards distance
    public float footClearance = 0.01f;  // tiny gap above floor

    [Header("Smoothing")]
    public float smooth = 12f;

    void Reset()
    {
        // if you forget to assign, it won't crash
        floorMask = LayerMask.GetMask("Floor");
    }

    void LateUpdate()
    {
        if (!leftFootBall || !rightFootBall || !avatarRoot) return;

        bool hitL = TryGetFloorY(leftFootBall.position, out float floorYL);
        bool hitR = TryGetFloorY(rightFootBall.position, out float floorYR);

        if (!hitL && !hitR) return;

        float floorY = (hitL && hitR) ? Mathf.Min(floorYL, floorYR) : (hitL ? floorYL : floorYR);
        float lowestFootY = Mathf.Min(leftFootBall.position.y, rightFootBall.position.y);

        float delta = (floorY + footClearance) - lowestFootY;

        Vector3 target = avatarRoot.position + new Vector3(0f, delta, 0f);
        avatarRoot.position = Vector3.Lerp(avatarRoot.position, target, 1f - Mathf.Exp(-smooth * Time.deltaTime));
    }

    bool TryGetFloorY(Vector3 footWorldPos, out float floorY)
    {
        Vector3 origin = footWorldPos + Vector3.up * raycastHeight;

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, raycastDistance, floorMask, QueryTriggerInteraction.Ignore))
        {
            floorY = hit.point.y;
            return true;
        }

        floorY = 0f;
        return false;
    }
}
