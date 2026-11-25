// RP simulation used by the walking experiment: the QuestRP "RPSimulation" island model
// (RPSimulationManager + RPIslandMask), the version that does NOT use the Passthrough Camera API.
// It draws an irregular, soft-edged island-shaped field as a black mask over the system passthrough
// (no added latency) and can make the passthrough dimmer, flatter and less colourful.
//
// Differences from the QuestRP manager, so it fits the experiment:
//  * No controller/hand input (the experiment uses the controllers). Settings are in the Inspector.
//  * On/off follows the experiment: LowVisionOverlayController.ApplySettings(Severe/Glaucoma) = on, None = off.
//  * Size follows the experiment's field setting: island radius = tunnelFovDegrees / 2 (the outline is irregular,
//    so this is the nominal radius before warp/lobes). The shape parameters come from the QuestRP stage presets.
//  * Gaze comes from RpFieldDriver (smoothed Neon gaze, hold on sample loss, head-centred fallback, optional
//    BlackOut), instead of RPGazeInput. Both eyes' islands are aimed at one point at focusDistance (RPGazeMath).
//  * The earlier tunnel overlay quad is hidden while this component is enabled (its controller and driver keep
//    running, so logging and the experiment code are unchanged). Disable this component to get the old tunnel back.

using System;
using PassthroughCameraSamples.MultiObjectDetection;
using PassthroughCameraSamples.RPSimulation;
using UnityEngine;

namespace WalkingEyeExperiment
{
    [DefaultExecutionOrder(550)]
    public sealed class RpIslandSimulation : MonoBehaviour
    {
        [Serializable]
        public class Stage
        {
            public string Name = "Stage";
            [Tooltip("Radius of the central island in degrees of visual angle.")]
            public float Radius = 10f;
            [Tooltip(">1 makes the island wider than tall.")]
            public float Aspect = 1.2f;
            [Tooltip("Island centre relative to gaze, degrees (negative y = shifted down, i.e. upper field lost first).")]
            public Vector2 CenterOffset = new(0f, -1f);
            [Tooltip("How much the outline is bent, as a fraction of the radius.")]
            public float Warp = 0.35f;
            public float Lobes = 0.3f;
            [Tooltip("Width of the soft border in degrees.")]
            public float Feather = 2f;
            [Range(0f, 1f)] public float Mottle = 0.4f;
            [Tooltip("Ring scotoma: vision returns beyond this eccentricity (degrees). 0 = off.")]
            public float OuterStart;
            [Range(0f, 1f)] public float Residual = 0.2f;
            [Tooltip("Temporal crescent of preserved far-peripheral vision starts at this eccentricity. 0 = off.")]
            public float CrescentStart;
            [Header("Passthrough colour (-1..1)")]
            public float Brightness;
            public float Contrast;
            public float Saturation;
        }

        public enum StageChoice { MatchFieldSize = -1, Early = 0, Moderate = 1, Advanced = 2, Severe = 3 }

        [Tooltip("QuestRP stage presets (same values as RPSimulationManager).")]
        public Stage[] stages =
        {
            // Warp/Lobes above ~0.25 can fold the outline and look jagged; keep them gentle.
            new() { Name = "Early (ring scotoma)", Radius = 28f, Aspect = 1.2f, Warp = 0.18f, Lobes = 0.2f, Feather = 3f, Mottle = 0.12f, OuterStart = 48f, Residual = 0.35f },
            new() { Name = "Moderate", Radius = 17f, Aspect = 1.2f, CenterOffset = new Vector2(0f, -1f), Warp = 0.16f, Lobes = 0.2f, Feather = 2.5f, Mottle = 0.12f, Residual = 0.3f, CrescentStart = 42f, Brightness = -0.1f, Contrast = -0.1f, Saturation = -0.2f },
            new() { Name = "Advanced", Radius = 8f, Aspect = 1.15f, CenterOffset = new Vector2(0.3f, -0.5f), Warp = 0.16f, Lobes = 0.18f, Feather = 1.8f, Mottle = 0.1f, Residual = 0.1f, Brightness = -0.2f, Contrast = -0.3f, Saturation = -0.5f },
            new() { Name = "Severe", Radius = 4f, Aspect = 1.1f, CenterOffset = new Vector2(0.3f, -0.3f), Warp = 0.14f, Lobes = 0.15f, Feather = 1.5f, Mottle = 0.08f, Residual = 0f, Brightness = -0.3f, Contrast = -0.5f, Saturation = -0.85f },
        };

        [Tooltip("MatchFieldSize: use the preset whose radius is closest to the experiment's field size.")]
        public StageChoice stage = StageChoice.MatchFieldSize;
        [Tooltip("ON: island radius = LowVisionOverlayController.tunnelFovDegrees / 2 (the experiment menu's field). OFF: the preset's own radius.")]
        public bool sizeFromTunnelFov = true;
        [Tooltip("ON: the island's visible area equals a circle of the menu's field size (15° = 15° across), with the same 2° soft " +
                 "edge as the earlier tunnel, and no scattered residual spots / far-peripheral islands, so field size is comparable " +
                 "between participants. OFF: QuestRP stage values (outline extends further, plus residual spots).")]
        public bool matchNominalSize = true;
        public float seed = 3.7f;
        [Tooltip("How different the two eyes' islands are. RP fields are highly symmetric, so keep this small.")]
        [Range(0f, 2f)] public float eyeDifference = 0.05f;
        [Tooltip("Dim / flatten / desaturate the passthrough while the simulation is on (stage values).")]
        public bool degradePassthroughColor = false;

        public enum ContrastLevel { FieldOnly, TypicalRP, AverageRP, Custom }
        [Tooltip("Contrast loss inside the island, drawn as a uniform grey veil over passthrough AND virtual content " +
                 "(models reduced contrast sensitivity and straylight/glare). FieldOnly = clear. TypicalRP = 0.23 log units " +
                 "(RP with acuity better than 0.3 logMAR, Xiong et al. 2020). AverageRP = 0.46 log (RP group mean). Custom = customContrastLossLog.")]
        public ContrastLevel contrastLevel = ContrastLevel.TypicalRP;
        [Range(0f, 1.5f)] public float customContrastLossLog = 0.23f;
        [Tooltip("Veil grey in linear units. 0.2 is about mid-grey; set near the scene's mean brightness so the veil lowers contrast " +
                 "without changing mean brightness much. Verify the delivered loss with a real Pelli-Robson chart seen through the headset.")]
        [Range(0f, 1f)] public float veilGrey = 0.2f;
        public float ContrastLossLog => contrastLevel switch
        {
            ContrastLevel.FieldOnly => 0f,
            ContrastLevel.TypicalRP => 0.23f,
            ContrastLevel.AverageRP => 0.46f,
            _ => Mathf.Max(0f, customContrastLossLog),
        };
        /// <summary>Veil opacity: image contrast is scaled by (1 - veil) = 10^-loss when veilGrey matches the scene mean.</summary>
        public float VeilAlpha => 1f - Mathf.Pow(10f, -ContrastLossLog);
        [Tooltip("Distance at which the two eyes' islands line up (metres). Set it to the usual viewing distance.")]
        public float focusDistance = 1.5f;
        [Tooltip("4900 = same slot as the earlier overlay, so menus and text drawn above it stay readable.")]
        public int renderQueue = 4900;

        public bool Active { get; private set; }
        public string StageName { get; private set; } = "";
        public float IslandRadiusDegrees { get; private set; }

        RpFieldDriver driver;
        LowVisionOverlayController overlay;
        Renderer tunnelQuad;
        Material material;
        MeshRenderer sphere;
        OVRCameraRig rig;
        OVRPassthroughLayer passthrough;
        bool colourApplied;
        string appliedKey;
        readonly Matrix4x4[] headToGaze = { Matrix4x4.identity, Matrix4x4.identity };

        static readonly int RadiusId = Shader.PropertyToID("_Radius");
        static readonly int AspectId = Shader.PropertyToID("_Aspect");
        static readonly int CenterOffsetId = Shader.PropertyToID("_CenterOffset");
        static readonly int WarpId = Shader.PropertyToID("_Warp");
        static readonly int LobesId = Shader.PropertyToID("_Lobes");
        static readonly int FeatherId = Shader.PropertyToID("_Feather");
        static readonly int MottleId = Shader.PropertyToID("_Mottle");
        static readonly int OuterStartId = Shader.PropertyToID("_OuterStart");
        static readonly int ResidualId = Shader.PropertyToID("_Residual");
        static readonly int CrescentStartId = Shader.PropertyToID("_CrescentStart");
        static readonly int SeedId = Shader.PropertyToID("_Seed");
        static readonly int EyeSeedOffsetId = Shader.PropertyToID("_EyeSeedOffset");
        static readonly int BlackoutId = Shader.PropertyToID("_Blackout");
        static readonly int VeilId = Shader.PropertyToID("_Veil");
        static readonly int VeilGreyId = Shader.PropertyToID("_VeilGrey");

        void Start()
        {
            driver = GetComponent<RpFieldDriver>();
            overlay = driver && driver.overlay ? driver.overlay : GetComponent<LowVisionOverlayController>();
            tunnelQuad = GetComponent<Renderer>();
            rig = FindFirstObjectByType<OVRCameraRig>();

            var shader = Resources.Load<Shader>("RPIsland/RPIslandMask");
            if (!shader) shader = Shader.Find("Meta/PCA/RPIslandMask");
            if (!shader)
            {
                Debug.LogError("[RPIsland] Shader 'Meta/PCA/RPIslandMask' not found; keeping the earlier tunnel overlay.");
                enabled = false;
                return;
            }
            material = new Material(shader) { renderQueue = renderQueue };
            CreateMaskSphere();
            Debug.Log("[RPIsland] QuestRP island simulation active (system passthrough, no camera access).");
        }

        void CreateMaskSphere()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "RP Island Mask";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            // The vertex shader keeps the sphere centred on each eye; huge bounds avoid culling.
            var filter = go.GetComponent<MeshFilter>();
            var mesh = Instantiate(filter.sharedMesh);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            filter.sharedMesh = mesh;
            sphere = go.GetComponent<MeshRenderer>();
            sphere.sharedMaterial = material;
            sphere.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            sphere.receiveShadows = false;
            sphere.enabled = false;
        }

        void OnEnable() { appliedKey = null; }

        void OnDisable()
        {
            if (tunnelQuad) tunnelQuad.enabled = true;   // give the earlier tunnel back
            if (sphere) sphere.enabled = false;
            Active = false;
            RestorePassthroughColour();
        }

        void OnDestroy()
        {
            if (material) Destroy(material);
        }

        // After RpFieldDriver.LateUpdate (order 500), so this frame's smoothed gaze is used.
        void LateUpdate()
        {
            if (!material || !overlay) return;
            if (tunnelQuad && tunnelQuad.enabled) tunnelQuad.enabled = false;

            var type = overlay.AppliedVisionType;
            bool on = type == VisionType.Severe || type == VisionType.Glaucoma;
            Active = on;
            if (sphere.enabled != on) sphere.enabled = on;
            if (!on)
            {
                RestorePassthroughColour();
                return;
            }

            var s = SelectStage(out float radius);
            ApplyStage(s, radius);

            // Gaze in head space (+Z forward); straight ahead when head-centred or without gaze.
            Vector3 gaze = driver ? driver.DisplayHeadDirection : Vector3.forward;
            if (gaze.sqrMagnitude < 1e-6f) gaze = Vector3.forward;
            RPGazeMath.ComputeEyeMatrices(gaze, focusDistance, RPGazeMath.GetEyeGeometry(rig), headToGaze);
            material.SetMatrixArray(RPGazeMath.HeadToGazeEyeId, headToGaze);
            material.SetFloat(BlackoutId, driver && driver.Blackout ? 1f : 0f);
        }

        Stage SelectStage(out float radius)
        {
            float wanted = sizeFromTunnelFov && overlay.tunnelFovDegrees > 0f ? overlay.tunnelFovDegrees * 0.5f : -1f;
            int index = (int)stage;
            if (index < 0 || index >= stages.Length)
            {
                // Closest preset radius (log scale) to the experiment's field size.
                index = 2;
                if (wanted > 0f)
                {
                    float best = float.MaxValue;
                    for (int i = 0; i < stages.Length; i++)
                    {
                        float d = Mathf.Abs(Mathf.Log(Mathf.Max(stages[i].Radius, .1f) / wanted));
                        if (d < best) { best = d; index = i; }
                    }
                }
            }
            var s = stages[index];
            radius = wanted > 0f ? wanted : s.Radius;
            return s;
        }

        void ApplyStage(Stage s, float radius)
        {
            string key = $"{contrastLevel}|{customContrastLossLog}|{veilGrey}|{s.Name}|{radius}|{seed}|{eyeDifference}|{degradePassthroughColor}|{matchNominalSize}|{overlay.tunnelEdgeDegrees}";
            if (key == appliedKey) return;
            appliedKey = key;

            float scale = radius / Mathf.Max(s.Radius, .1f);
            float feather = s.Feather * Mathf.Sqrt(scale);   // as RPSimulationManager does when resizing
            float residual = s.Residual, outer = s.OuterStart, crescent = s.CrescentStart;
            if (matchNominalSize)
            {
                // Same area as a circle of the nominal diameter (the island is stretched by Aspect), 50% point of the edge
                // on that radius, and the earlier tunnel's edge width (total fade = 2 x _Feather).
                radius /= Mathf.Sqrt(Mathf.Max(s.Aspect, .1f));
                feather = Mathf.Max(.1f, overlay.tunnelEdgeDegrees * .5f);
                residual = 0f; outer = 0f; crescent = 0f;
            }
            material.SetFloat(RadiusId, radius);
            material.SetFloat(AspectId, s.Aspect);
            material.SetVector(CenterOffsetId, s.CenterOffset);
            material.SetFloat(WarpId, s.Warp);
            material.SetFloat(LobesId, s.Lobes);
            material.SetFloat(FeatherId, feather);
            material.SetFloat(MottleId, s.Mottle);
            material.SetFloat(OuterStartId, outer);
            material.SetFloat(ResidualId, residual);
            material.SetFloat(CrescentStartId, crescent);
            material.SetFloat(SeedId, seed);
            material.SetFloat(EyeSeedOffsetId, eyeDifference);
            material.SetFloat(VeilId, VeilAlpha);
            material.SetFloat(VeilGreyId, veilGrey);
            StageName = s.Name;
            IslandRadiusDegrees = radius;

            if (degradePassthroughColor)
            {
                var layer = FindPassthrough();
                if (layer)
                {
                    layer.SetBrightnessContrastSaturation(s.Brightness, s.Contrast, s.Saturation);
                    colourApplied = true;
                }
            }
            else RestorePassthroughColour();

            Debug.Log($"[RPIsland] {s.Name} shape, radius {radius:0.#} deg, contrast loss {ContrastLossLog:0.##} log (veil {VeilAlpha:0.##}), colour degradation {(degradePassthroughColor ? "on" : "off")}");
        }

        OVRPassthroughLayer FindPassthrough()
        {
            if (passthrough && passthrough.isActiveAndEnabled) return passthrough;
            passthrough = null;
            foreach (var layer in FindObjectsByType<OVRPassthroughLayer>(FindObjectsSortMode.None))
                if (layer.isActiveAndEnabled) { passthrough = layer; break; }
            return passthrough;
        }

        void RestorePassthroughColour()
        {
            if (!colourApplied) return;
            colourApplied = false;
            appliedKey = null;
            if (passthrough) passthrough.DisableColorMap();
        }
    }
}
