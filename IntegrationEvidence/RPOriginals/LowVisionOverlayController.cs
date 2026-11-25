using UnityEngine;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    public enum VisionType
    {
        None = 0,
        Cataract = 1,
        Glaucoma = 2,
        Severe = 3
    }

    [System.Serializable]
    public class VisionTypeSettings
    {
        [Header("Color & Opacity")]
        public Color tintColor = Color.white;

        [Range(0f, 1f)]
        public float globalAlpha = 0.5f;

        [Header("Blur")]
        [Range(0f, 10f)]
        public float blurAmount = 0f;

        [Header("Vignette")]
        [Range(0f, 1f)]
        public float vignetteAmount = 0f;

        [Range(0f, 1f)]
        public float vignetteRadius = 0.5f;
    }

    public class LowVisionOverlayController : MonoBehaviour
    {
        [Header("Overlay Material")]
        public Material overlayMaterial;

        [Header("Per-Vision Settings")]
        public VisionTypeSettings noneSettings;
        public VisionTypeSettings cataractSettings;
        public VisionTypeSettings glaucomaSettings;
        public VisionTypeSettings severeSettings;

        [Header("Current State (read-only)")]
        public VisionType currentVisionType = VisionType.None;

        // Cached property IDs
        private static readonly int VisionTypeID = Shader.PropertyToID("_VisionType");
        private static readonly int TintColorID = Shader.PropertyToID("_TintColor");
        private static readonly int GlobalAlphaID = Shader.PropertyToID("_GlobalAlpha");
        private static readonly int BlurAmountID = Shader.PropertyToID("_BlurAmount");
        private static readonly int VignetteAmountID = Shader.PropertyToID("_VignetteAmount");
        private static readonly int VignetteRadiusID = Shader.PropertyToID("_VignetteRadius");

        private void Start()
        {
            // Initialize with defaults ONLY if not already set
            if (noneSettings == null) noneSettings = new VisionTypeSettings();
            if (cataractSettings == null) cataractSettings = new VisionTypeSettings();
            if (glaucomaSettings == null) glaucomaSettings = new VisionTypeSettings();
            if (severeSettings == null) severeSettings = new VisionTypeSettings();

            // The vision type is chosen in the Start menu before this scene loads.
            currentVisionType = DetectionSessionConfig.SelectedVisionType;

            // If a tunnel FoV degree is configured, override the vignetteRadius on
            // tunnel-type settings (Glaucoma, Severe) to match the requested FoV.
            // Formula: rWorld = tan(fov/2) * planeDistance, vignetteRadius = rWorld / planeWidth
            float fovDeg = DetectionSessionConfig.TunnelFovDegrees;
            if (fovDeg > 0f)
            {
                float planeDistance = Mathf.Abs(transform.localPosition.z);
                float planeWidth    = transform.lossyScale.x;
                if (planeDistance > 1e-4f && planeWidth > 1e-4f)
                {
                    float rWorld = Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad) * planeDistance;
                    float computedRadius = Mathf.Clamp01(rWorld / planeWidth);
                    glaucomaSettings.vignetteRadius = computedRadius;
                    severeSettings.vignetteRadius   = computedRadius;
                    Debug.Log($"[LowVisionOverlay] TunnelFoV {fovDeg}° → vignetteRadius {computedRadius:F3}");
                }
            }

            Debug.Log($"[LowVisionOverlay] START - Controller initialized (type: {currentVisionType})");

            ApplySettings(currentVisionType);

            // NEW
            LogTunnelFov();
        }

        public void LogTunnelFov()
        {
            // Plane distance from camera (localPosition.z is correct when the overlay
            // plane is a direct child of the camera rig, positioned along its Z axis).
            float distance = Mathf.Abs(transform.localPosition.z);

            // Current settings
            VisionTypeSettings s = GetSettings(currentVisionType);

            // The shader computes: dist = length(uv - 0.5), where UV 0→1 spans the
            // full plane width. World radius = vignetteRadius × lossyScale.x.
            // (The old code multiplied by halfPlane = scale/2, giving half the real radius.)
            float effectiveRadius = Mathf.Max(0.05f, s.vignetteRadius);
            float rWorld = effectiveRadius * transform.lossyScale.x;

            // Tunnel FoV calculation
            float fovDegrees =
                2f * Mathf.Atan(rWorld / distance) * Mathf.Rad2Deg;

            Debug.Log("=================================");
            Debug.Log($"[TUNNEL] Vision Type: {currentVisionType}");
            Debug.Log($"[TUNNEL] Plane Distance: {distance:F3} m");
            Debug.Log($"[TUNNEL] Plane World Width: {transform.lossyScale.x:F3} m");
            Debug.Log($"[TUNNEL] Shader Radius: {s.vignetteRadius:F3}");
            Debug.Log($"[TUNNEL] Effective Radius: {effectiveRadius:F3}");
            Debug.Log($"[TUNNEL] World Radius: {rWorld:F3} m");
            Debug.Log($"[TUNNEL] Tunnel FoV: {fovDegrees:F2} degrees");
            Debug.Log("=================================");
        }
        // Kept public so the vision type can still be cycled from a UI event or another
        // script if needed, but controller buttons no longer trigger it.
        public void CycleVisionType()
        {
            int next = ((int)currentVisionType + 1) % 4;
            currentVisionType = (VisionType)next;
            ApplySettings(currentVisionType);
            Debug.Log($"[LowVisionOverlay] ===== CYCLED TO: {currentVisionType} =====");
        }

        public void ApplySettings(VisionType type)
        {
            if (overlayMaterial == null)
            {
                Debug.LogError("[LowVisionOverlay] NO MATERIAL ASSIGNED!");
                return;
            }

            VisionTypeSettings s = GetSettings(type);

            overlayMaterial.SetInt(VisionTypeID, (int)type);
            overlayMaterial.SetColor(TintColorID, s.tintColor);
            overlayMaterial.SetFloat(GlobalAlphaID, s.globalAlpha);
            overlayMaterial.SetFloat(BlurAmountID, s.blurAmount);
            overlayMaterial.SetFloat(VignetteAmountID, s.vignetteAmount);
            overlayMaterial.SetFloat(VignetteRadiusID, s.vignetteRadius);

            Debug.Log($"[LowVisionOverlay] Applied {type}: Alpha={s.globalAlpha:F2}, Blur={s.blurAmount:F1}, Vignette={s.vignetteAmount:F2}");
        }

        private VisionTypeSettings GetSettings(VisionType type)
        {
            switch (type)
            {
                case VisionType.Cataract: return cataractSettings;
                case VisionType.Glaucoma: return glaucomaSettings;
                case VisionType.Severe: return severeSettings;
                case VisionType.None:
                default: return noneSettings;
            }
        }
    }
}