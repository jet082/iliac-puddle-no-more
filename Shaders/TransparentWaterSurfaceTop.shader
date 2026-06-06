// Project:         Deep Waters
// License:         MIT

Shader "DeepWaters/TransparentWaterSurfaceTop"
{
    Properties
    {
        _MainTex ("Wave texture (RGB)", 2D) = "white" {}
        _Color ("Surface tint and opacity", Color) = (0.34, 0.55, 0.58, 0.35)
        _SrcBlend ("Source blend", Float) = 5
        _DstBlend ("Destination blend", Float) = 10
        _ZWrite ("Depth write", Float) = 0

        _StencilRef ("Stencil reject value", Range(0, 255)) = 200
        _StencilReadMask ("Stencil read mask", Range(0, 255)) = 255

        _UnderwaterFogColor ("Deep water color", Color) = (0.055, 0.098, 0.082, 1.0)
        _WaterColumnDepth ("Nominal water column depth", Float) = 35.0
        _WaterColumnFogDepth ("Water column fog depth", Float) = 35.0
        _WaterColumnFogStrength ("Water column fog strength", Range(0, 1)) = 1.0

        _ScrollX ("Wave scroll speed X", Float) = 0.0225
        _ScrollY ("Wave scroll speed Y", Float) = 0.0375
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "TOP_SURFACE"
            Cull Back
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest LEqual

            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                Comp NotEqual
            }

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D_float _CameraDepthTexture;

            fixed4 _Color;
            fixed4 _UnderwaterFogColor;
            float _WaterColumnDepth;
            float _WaterColumnFogDepth;
            float _WaterColumnFogStrength;
            float _ScrollX;
            float _ScrollY;
            float _DeepWatersUnderwater;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex) + float2(_ScrollX, _ScrollY) * _Time.y;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            bool IsNoDepth(float rawDepth)
            {
                #if defined(UNITY_REVERSED_Z)
                    return rawDepth <= 0.0001;
                #else
                    return rawDepth >= 0.9999;
                #endif
            }

            fixed4 frag(v2f i) : SV_Target
            {
                clip(0.5 - _DeepWatersUnderwater);
                clip(_WorldSpaceCameraPos.y - i.worldPos.y + 0.02);

                float surfaceOpacity = saturate(_Color.a);
                clip(surfaceOpacity - 0.001);

                fixed4 wave = tex2D(_MainTex, i.uv);
                fixed3 waveRgb = wave.rgb * _Color.rgb;
                fixed3 surfaceRgb = lerp(_Color.rgb, waveRgb, 0.55);

                float columnDepth = _WaterColumnDepth;
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.screenPos.xy / i.screenPos.w);
                if (!IsNoDepth(rawDepth))
                {
                    float sceneDepthLinear = LinearEyeDepth(rawDepth);
                    float surfaceDepthLinear = max(i.screenPos.w, 0.0001);
                    float waterPath = max(0.0, sceneDepthLinear - surfaceDepthLinear);
                    float depthRatio = sceneDepthLinear / surfaceDepthLinear;
                    float3 sceneWorldPos = _WorldSpaceCameraPos +
                        (i.worldPos - _WorldSpaceCameraPos) * depthRatio;
                    float verticalDepth = max(0.0, i.worldPos.y - sceneWorldPos.y);
                    columnDepth = max(_WaterColumnDepth, max(verticalDepth, waterPath * 0.35));
                }

                float normalizedDepth = columnDepth / max(_WaterColumnFogDepth, 0.001);
                float fogStrength = saturate(_WaterColumnFogStrength);
                float waterTint = saturate(
                    (1.0 - exp2(-normalizedDepth * lerp(0.35, 1.6, fogStrength))) *
                    fogStrength);

                fixed4 col;
                col.rgb = lerp(surfaceRgb, _UnderwaterFogColor.rgb, waterTint * 0.28);
                col.a = surfaceOpacity;
                return col;
            }
            ENDCG
        }
    }

    FallBack Off
}
