Shader "Custom/LowVisionOverlaySimple"
{
    Properties
    {
        _VisionType ("Vision Type", Int) = 0
        _TintColor ("Tint Color", Color) = (1,1,1,1)
        _GlobalAlpha ("Global Alpha", Range(0,1)) = 0.5
        _BlurAmount ("Blur Amount", Range(0,10)) = 0
        _VignetteAmount ("Vignette Amount", Range(0,1)) = 0
        _VignetteRadius ("Vignette Radius", Range(0,1)) = 0.5
        _EdgeWidth ("Edge Width (uv, <=0 = legacy half-radius)", Float) = -1
        // Angular field model (RP simulation v2)
        _FieldModel ("Field model (0 legacy plane, 1 angular map)", Int) = 0
        _FieldMap ("Visual field map (polar, R8)", 2D) = "white" {}
        _ContrastVeil ("Contrast veil inside the field (0 = off)", Range(0,1)) = 0
        _VeilLuminance ("Veil luminance", Range(0,1)) = 0.5
        _Blackout ("Blackout (tracking loss policy)", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Overlay+900"
            "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline"
        }

        Pass
        {
            ZWrite Off
            ZTest Always
            // Alpha must obscure the native passthrough underlay as well as Unity content.
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma multi_compile_instancing

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
                float2 uv : TEXCOORD0;
            };

            int _VisionType;
            float4 _TintColor;
            float _GlobalAlpha;
            float _BlurAmount;
            float _VignetteAmount;
            float _VignetteRadius;
            float _EdgeWidth;
            float4 _HeadRightWS, _HeadUpWS, _HeadForwardWS, _GazeDirectionWS;
            float4 _GazeLeftWS, _GazeRightWS;
            float _PlaneDistance, _PlaneWidth;
            int _FieldModel;
            float _ContrastVeil, _VeilLuminance, _Blackout;
            float _MapEccStepDeg, _MapEccBins;
            TEXTURE2D(_FieldMap); SAMPLER(sampler_FieldMap);

            v2f vert(appdata v)
            {
                v2f o = (v2f)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                // Full eye viewport: prevents uncovered edges during large eye movements.
                o.pos = float4(v.vertex.xy * 2.0, 0.5, 1.0);
                o.uv = v.uv;
                return o;
            }

            // Visibility (0..1) of a view ray for this eye, from the polar field map around this eye's gaze direction.
            float FieldVisibility(float3 ray, float3 gaze, float eye)
            {
                float3 up = normalize(_HeadUpWS.xyz);
                float3 r = cross(up, gaze);
                r = dot(r, r) > 1e-8 ? normalize(r) : normalize(_HeadRightWS.xyz);
                float3 u = cross(gaze, r);
                float ecc = degrees(acos(clamp(dot(ray, gaze), -1.0, 1.0)));
                float az = degrees(atan2(dot(ray, u), dot(ray, r)));   // 0 = right, 90 = up, as the participant sees it
                if (az < 0) az += 360.0;
                float bins = max(1.0, _MapEccBins);
                float row = min(ecc / max(0.01, _MapEccStepDeg), bins - 1.0);
                if (ecc / max(0.01, _MapEccStepDeg) > bins - 0.5) return 0;   // beyond the map = not seen
                float v = (eye * bins + row) / (2.0 * bins);
                // Keep bilinear filtering inside this eye's rows.
                v = clamp(v, (eye * bins + 0.5) / (2.0 * bins), (eye * bins + bins - 0.5) / (2.0 * bins));
                return SAMPLE_TEXTURE2D_LOD(_FieldMap, sampler_FieldMap, float2(az / 360.0, v), 0).r;
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 view = mul(UNITY_MATRIX_I_P, float4(i.uv * 2.0 - 1.0, 0.5, 1.0));
                float3 worldRay = normalize(mul((float3x3)UNITY_MATRIX_I_V, view.xyz / view.w));
                if (_VisionType == 0)
                    return float4(0, 0, 0, 0);

                if (_FieldModel == 1 && _VisionType >= 2)
                {
                    // Tracking-loss policy "blackout": nothing is visible until valid gaze returns.
                    if (_Blackout > 0.5) return float4(0, 0, 0, 1);
                    #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED) || defined(UNITY_SINGLE_PASS_STEREO)
                    float eye = (float)unity_StereoEyeIndex;
                    #else
                    float eye = 0.0;
                    #endif
                    float3 gaze = normalize(eye < 0.5 ? _GazeLeftWS.xyz : _GazeRightWS.xyz);
                    float vis = FieldVisibility(worldRay, gaze, eye);
                    // Outside the field: black (also covers passthrough). Inside: optional uniform contrast veil.
                    float veil = _ContrastVeil * vis;
                    float alpha = saturate((1.0 - vis) + veil);
                    float grey = alpha > 1e-4 ? veil * _VeilLuminance / alpha : 0.0;
                    return float4(grey, grey, grey, alpha);
                }

                // ---- Legacy plane model (baseline, unchanged) ----
                float rayZ = max(0.001, dot(worldRay, _HeadForwardWS.xyz));
                float gazeZ = max(0.001, dot(_GazeDirectionWS.xyz, _HeadForwardWS.xyz));
                float2 raySlope = float2(dot(worldRay, _HeadRightWS.xyz), dot(worldRay, _HeadUpWS.xyz)) / rayZ;
                float2 gazeSlope = float2(dot(_GazeDirectionWS.xyz, _HeadRightWS.xyz), dot(_GazeDirectionWS.xyz, _HeadUpWS.xyz)) / gazeZ;
                i.uv = 0.5 + (raySlope - gazeSlope) * _PlaneDistance / max(0.001, _PlaneWidth);

                float4 color = _TintColor;

                if (_BlurAmount > 0.01)
                {
                    float blurStrength = _BlurAmount * 0.05;
                    float3 blurColor = float3(0.8, 0.8, 0.8);
                    color.rgb = lerp(color.rgb, blurColor, blurStrength);
                    color.a += blurStrength * 0.1;
                }

                float2 center = i.uv - 0.5;
                float dist = length(center);

                float edgeWidth = _EdgeWidth > 0 ? _EdgeWidth : max(0.005, _VignetteRadius * 0.5);
                float vignette = smoothstep(_VignetteRadius, _VignetteRadius + edgeWidth, dist);

                color.rgb = lerp(color.rgb, float3(0,0,0), vignette * _VignetteAmount);
                color.a = saturate(_GlobalAlpha + vignette * _VignetteAmount);

                return color;
            }
            ENDHLSL
        }
    }
}
