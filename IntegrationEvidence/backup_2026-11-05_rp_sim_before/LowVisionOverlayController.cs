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
        [Range(0f, 90f)] public float tunnelFovDegrees = 10f;

        [Header("Tunnel (Glaucoma/Severe types)")]
        [Tooltip("ON: field diameter = tunnelFovDegrees at the 50% point of a soft edge of tunnelEdgeDegrees; clear interior. OFF: legacy behaviour.")]
        public bool degreeAccurateTunnel = true;
        [Tooltip("Total width of the soft edge, in degrees.")]
        [Range(0.2f, 6f)] public float tunnelEdgeDegrees = 1.5f;
        [Tooltip("Inside the window: 0 = clear passthrough (field-only simulation). Above 0 = grey veil, which is not part of RP.")]
        [Range(0f, 1f)] public float tunnelInteriorAlpha = 0f;

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
        private static readonly int EdgeWidthID = Shader.PropertyToID("_EdgeWidth");

        private void Start()
        {
            ApplyInspectorSettings();
            LogTunnelFov();
        }

        private void OnValidate()
        {
            if (Application.isPlaying) ApplyInspectorSettings();
        }

        [ContextMenu("Apply Inspector Settings")]
        public void ApplyInspectorSettings()
        {
            // Initialize with defaults ONLY if not already set
            if (noneSettings == null) noneSettings = new VisionTypeSettings();
            if (cataractSettings == null) cataractSettings = new VisionTypeSettings();
            if (glaucomaSettings == null) glaucomaSettings = new VisionTypeSettings();
            if (severeSettings == null) severeSettings = new VisionTypeSettings();

            // The integrated experiment selects currentVisionType in the Inspector.

            // If a tunnel FoV degree is configured, override the vignetteRadius on
            // tunnel-type settings (Glaucoma, Severe) to match the requested FoV.
            // Formula: rWorld = tan(fov/2) * planeDistance, vignetteRadius = rWorld / planeWidth
            float fovDeg = tunnelFovDegrees;
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
            // The shader's minimum edge width does not clamp its island radius.
            float effectiveRadius = Mathf.Max(0f, s.vignetteRadius);
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
            overlayMaterial.SetFloat(EdgeWidthID, -1f);

            bool tunnelType = type == VisionType.Glaucoma || type == VisionType.Severe;
            if (degreeAccurateTunnel && tunnelType && tunnelFovDegrees > 0f)
            {
                // Same plane geometry as the legacy FoV formula above (overlay quad distance and width).
                float planeDistance = Mathf.Abs(transform.localPosition.z);
                float planeWidth = transform.lossyScale.x;
                if (planeDistance > 1e-4f && planeWidth > 1e-4f)
                {
                    float k = planeDistance / planeWidth;
                    float half = Mathf.Clamp(tunnelFovDegrees * 0.5f, 0.5f, 80f);
                    float e = Mathf.Clamp(tunnelEdgeDegrees * 0.5f, 0.05f, half - 0.05f);
                    float inner = Mathf.Tan((half - e) * Mathf.Deg2Rad) * k;
                    float outer = Mathf.Tan((half + e) * Mathf.Deg2Rad) * k;
                    overlayMaterial.SetColor(TintColorID, Color.black);
                    overlayMaterial.SetFloat(GlobalAlphaID, tunnelInteriorAlpha);
                    overlayMaterial.SetFloat(BlurAmountID, 0f);
                    overlayMaterial.SetFloat(VignetteAmountID, 1f);
                    overlayMaterial.SetFloat(VignetteRadiusID, inner);
                    overlayMaterial.SetFloat(EdgeWidthID, Mathf.Max(0.0001f, outer - inner));
                    Debug.Log($"[TUNNEL] {type}: diameter {tunnelFovDegrees:F1} deg at 50% of a {tunnelEdgeDegrees:F1} deg edge, interior alpha {tunnelInteriorAlpha:F2}");
                }
            }

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
