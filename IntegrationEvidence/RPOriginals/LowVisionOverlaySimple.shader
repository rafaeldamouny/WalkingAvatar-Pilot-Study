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
    }

    SubShader
    {
        Tags
        {
            "Queue"="Overlay+1000"
            "RenderType"="Transparent"
        }

        Pass
        {
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            int _VisionType;
            float4 _TintColor;
            float _GlobalAlpha;
            float _BlurAmount;
            float _VignetteAmount;
            float _VignetteRadius;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                if (_VisionType == 0)
                    return float4(0, 0, 0, 0);

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

                float edgeWidth = max(0.005, _VignetteRadius * 0.5);
                float vignette = smoothstep(_VignetteRadius, _VignetteRadius + edgeWidth, dist);

                color.rgb = lerp(color.rgb, float3(0,0,0), vignette * _VignetteAmount);
                color.a = saturate(_GlobalAlpha + vignette * _VignetteAmount);

                return color;
            }
            ENDCG
        }
    }
}
