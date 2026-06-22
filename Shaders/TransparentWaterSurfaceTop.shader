// Project:         Deep Waters
// License:         MIT

Shader "DeepWaters/TransparentWaterSurfaceTop"
{
    Properties
    {
        _MainTex ("Wave texture (RGB)", 2D) = "white" {}
        _Color ("Surface tint and opacity", Color) = (0.519, 0.527, 0.467, 0.35)
        _SrcBlend ("Source blend", Float) = 5
        _DstBlend ("Destination blend", Float) = 10
        _ZWrite ("Depth write", Float) = 0

        _StencilRef ("Stencil reject value", Range(0, 255)) = 200
        _StencilReadMask ("Stencil read mask", Range(0, 255)) = 255

        _UnderwaterFogColor ("Deep water color", Color) = (0.055, 0.098, 0.082, 1.0)
        _WaterColumnDepth ("Nominal water column depth", Float) = 35.0
        _WaterColumnFogDepth ("Water column fog depth", Float) = 35.0
        _WaterColumnFogStrength ("Water column fog strength", Range(0, 1)) = 1.0
        _WaterSurfaceVisionDistance ("Surface vision distance", Float) = 70.0
        _WaterSurfaceFalloff ("Surface distance falloff", Range(0, 1)) = 0.5
        _SurfaceOpaqueFadeStart ("Surface opaque fade start", Float) = 42.0
        _SurfaceOpaqueFadeEnd ("Surface opaque fade end", Float) = 160.0
        _DeepWatersPlayerPosition ("Player position", Vector) = (0, 0, 0, 1)
        _DeepWatersDepthValid ("Camera depth texture valid", Float) = 1.0

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
            float _WaterSurfaceVisionDistance;
            float _WaterSurfaceFalloff;
            float _SurfaceOpaqueFadeStart;
            float _SurfaceOpaqueFadeEnd;
            float4 _DeepWatersPlayerPosition;
            float _ScrollX;
            float _ScrollY;
            float _DeepWatersUnderwater;
            float _DeepWatersDepthValid;

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

                fixed4 wave = tex2D(_MainTex, i.uv);
                fixed3 legacyRgb = wave.rgb * _Color.rgb;
                fixed3 surfaceRgb = lerp(legacyRgb, _Color.rgb * 0.32, 0.22);

                // How much water the view ray crosses before it reaches the scene
                // behind the surface (the seafloor) — i.e. how much water you are
                // looking through. No usable depth = open void beyond the world.
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.screenPos.xy / i.screenPos.w);
                bool missingDepth = IsNoDepth(rawDepth);
                float waterPath;
                if (missingDepth)
                {
                    waterPath = 1e9;
                }
                else
                {
                    float sceneDepthLinear = LinearEyeDepth(rawDepth);
                    float surfaceDepthLinear = max(i.screenPos.w, 0.0001);
                    waterPath = max(0.0, sceneDepthLinear - surfaceDepthLinear);
                }

                // Vertical visibility looking down is set EXCLUSIVELY by the
                // underwater vision distance (Underwater Fog Distance): the
                // surface reaches full opacity once the view crosses that much
                // water, so the seafloor is visible from above out to the same
                // range it is visible from below (void pixels = fully opaque,
                // which hides the loaded-world edge). Underwater Fog Strength
                // sets how harsh the ramp to opaque is: low = gradual fade,
                // high = stays clear then closes off sharply near the limit.
                float reach = max(1.0, _WaterSurfaceVisionDistance);
                float t = saturate(waterPath / reach);
                // LINEAR ramp from the player's set surface transparency
                // (_Color.a, the "transparency from above" slider) up to fully
                // opaque at the edge of the vision distance (Underwater Fog
                // Distance). Void pixels -> waterPath huge -> t=1 -> opaque.
                float filmAlpha = saturate(_Color.a);
                float finalAlpha = lerp(filmAlpha, 1.0, t);
                clip(finalAlpha - 0.001);

                fixed4 col;
                col.rgb = surfaceRgb;
                col.a = finalAlpha;
                return col;
            }
            ENDCG
        }
    }

    FallBack Off
}
