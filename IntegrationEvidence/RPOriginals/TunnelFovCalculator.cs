using UnityEngine;

public class TunnelFovCalculator : MonoBehaviour
{
    public Camera xrCamera;

    [Range(0f, 1f)]
    public float effectiveRadius = 0.05f;
    // use 0.05 because your shader has minimum soft edge width

    void Start()
    {
        Calculate();
    }

    public void Calculate()
    {
        // Use world-space Z distance to the camera (localPosition.z is distance from parent,
        // which is correct when the plane is a direct child of the camera rig at (0,0,-d)).
        float distance = Mathf.Abs(transform.localPosition.z);

        // lossyScale gives the true world-space size even when a parent has non-unit scale.
        // The shader uses dist = length(uv - 0.5), where UV 0→1 spans the full plane width,
        // so the world radius at UV-distance d is: rWorld = d * lossyScale.x.
        // (The old code used halfPlane = lossyScale.x/2, giving rWorld = d * scale/2 — half the real value.)
        float rWorld = effectiveRadius * transform.lossyScale.x;

        float fovDegrees = 2f * Mathf.Atan(rWorld / distance) * Mathf.Rad2Deg;

        Vector3 center = transform.position;
        Vector3 edge = transform.position + transform.right * rWorld;

        Vector3 centerScreen = xrCamera.WorldToScreenPoint(center);
        Vector3 edgeScreen = xrCamera.WorldToScreenPoint(edge);

        float radiusPixels = Vector2.Distance(centerScreen, edgeScreen);
        float diameterPixels = radiusPixels * 2f;

        Debug.Log($"Tunnel radius world: {rWorld:F4} m");
        Debug.Log($"Tunnel diameter pixels: {diameterPixels:F1}px");
        Debug.Log($"Tunnel FoV: {fovDegrees:F2} degrees");
    }
}