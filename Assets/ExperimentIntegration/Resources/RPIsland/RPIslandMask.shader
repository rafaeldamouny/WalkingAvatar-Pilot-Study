// Retinitis pigmentosa field-loss mask drawn over the system passthrough.
// The island shape comes from RPIsland.cginc (irregular, soft-edged, per-eye).
//
// Output is black with alpha = blindness, so alpha 0 shows the passthrough underlay untouched and
// partial alpha dims it (= reduced sensitivity).
//
// From QuestRP / RPSimulation. Ported from the built-in CG include (UnityCG.cginc) to the URP shader library
// so the per-eye matrices are correct under URP multiview, as in this project's earlier overlay shader.
// Added: _Blackout (1 = nothing visible, used for the BlackOut tracking-loss policy). The field model itself
// (RPIsland.cginc) is unchanged.
Shader "Meta/PCA/RPIslandMask"
{
    Properties
    {
        _Radius ("Island radius (deg)", Float) = 10
        _Aspect ("Horizontal stretch", Float) = 1.2
        _CenterOffset ("Island centre offset (deg)", Vector) = (0, -1, 0, 0)
        _Warp ("Outline warp (fraction of radius)", Float) = 0.35
        _Lobes ("Lobe strength", Float) = 0.3
        _Feather ("Edge softness (deg)", Float) = 2
        _Mottle ("Patchy loss near edge", Float) = 0.4
        _OuterStart ("Ring scotoma outer edge (deg, 0 = off)", Float) = 0
        _Residual ("Residual spots", Float) = 0.2
        _CrescentStart ("Temporal crescent start (deg, 0 = off)", Float) = 0
        _Seed ("Seed", Float) = 3.7
        _EyeSeedOffset ("Per-eye seed offset", Float) = 0.35
        _Blackout ("Blackout (tracking loss)", Float) = 0
        _Veil ("Contrast veil opacity inside the island", Range(0,1)) = 0
        _VeilGrey ("Veil grey (linear)", Range(0,1)) = 0.2
    }

    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" "RenderPipeline"="UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always
        // premultiplied: colour = src + dst * (1 - a)   alpha: a + dstA * (1 - a)
        // Blind: src 0, a 1. Seeing: a grey veil of opacity _Veil over passthrough and virtual content.
        Blend One OneMinusSrcAlpha, One OneMinusSrcAlpha

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "RPIsland.cginc"

            float _Blackout;
            float _Veil, _VeilGrey;

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 ray : TEXCOORD0;               // view ray in gaze space
                nointerpolation float eye : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o = (v2f)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 dir = normalize(v.vertex.xyz);
                float3 world = _WorldSpaceCameraPos + dir * 5.0;   // sphere glued to the eye
                o.pos = TransformWorldToHClip(world);

                // World direction -> head space (Unity view space looks down -Z, so flip it).
                float3 viewDir = mul((float3x3)UNITY_MATRIX_V, dir);
                float3 headDir = float3(viewDir.x, viewDir.y, -viewDir.z);

                int eyeIndex = 0;
            #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED) || defined(UNITY_SINGLE_PASS_STEREO)
                eyeIndex = (int)unity_StereoEyeIndex;
            #endif
                o.eye = eyeIndex;
                o.ray = mul((float3x3)_HeadToGazeEye[eyeIndex], headDir);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float vis = RPIslandVisibility(normalize(i.ray), (int)(i.eye + 0.5));
                vis *= 1.0 - saturate(_Blackout);
                // Contrast loss / straylight: uniform veil inside the seeing field.
                float v = saturate(_Veil);
                float keep = vis * (1.0 - v);              // share of the scene that stays visible
                return float4(_VeilGrey.xxx * vis * v, 1.0 - keep);
            }
            ENDHLSL
        }
    }
}
