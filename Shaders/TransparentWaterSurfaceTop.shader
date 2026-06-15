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

                fixed4 wave = tex2D(_MainTex, i.uv);
                fixed3 legacyRgb = wave.rgb * _Color.rgb;
                fixed3 surfaceRgb = lerp(legacyRgb, _Color.rgb * 0.32, 0.22);

                // Distance the downward view travels through the water column
                // before it reaches the seabed. Prefer the camera depth texture;
                // when nothing is rendered behind the surface (sky / culled
                // distant seabed) treat the column as deep so it fogs out rather
                // than reading as a thin clear film.
                float fogStrength = saturate(_WaterColumnFogStrength);
                float falloff = saturate(_WaterSurfaceFalloff);
                // Reference distance the seabed fades over, anchored to the
                // underwater vision distance so the bay is no clearer from above
                // than from below. Falloff shortens it: 0 = gradual, 1 = swift.
                float visionRef = max(1.0, _WaterSurfaceVisionDistance * lerp(1.6, 0.35, falloff));

                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.screenPos.xy / i.screenPos.w);
                float waterPath;
                if (IsNoDepth(rawDepth))
                {
                    waterPath = visionRef * 4.0;
                }
                else
                {
                    float sceneDepthLinear = LinearEyeDepth(rawDepth);
                    float surfaceDepthLinear = max(i.screenPos.w, 0.0001);
                    waterPath = max(0.0, sceneDepthLinear - surfaceDepthLinear);
                }

                // Beer-Lambert-style occlusion of the seabed by the water column.
                // Reaches near-opaque well before the deepest water, so the floor
                // is hidden at depth while shallows by the shore stay clear.
                float occlusionRate = lerp(1.2, 3.0, fogStrength);
                float bodyOpacity = saturate(1.0 - exp2(-(waterPath / visionRef) * occlusionRate));

                // Opaque horizon curtain. The surface goes FULLY opaque at
                // distance, independent of the transparency setting, so open
                // water forms a wall in front of the loaded-world edge — the
                // outdoor equivalent of the dungeon walls that hide DFU's
                // void. Without it the void is visible across open sea
                // whenever the film is transparent.
                float viewDist = distance(i.worldPos, _WorldSpaceCameraPos);
                float horizonFade = smoothstep(_SurfaceOpaqueFadeStart, max(_SurfaceOpaqueFadeStart + 1.0, _SurfaceOpaqueFadeEnd), viewDist);

                // The surface film (configured top transparency) is the minimum
                // opacity; the water column adds opacity on top as it deepens, so
                // a clear film still hides a deep seabed.
                float finalAlpha = saturate(max(max(surfaceOpacity, bodyOpacity), horizonFade));
                clip(finalAlpha - 0.001);

                fixed4 col;
                // Keep a little surface sheen even over deep water so the wave
                // texture still reads instead of flattening to solid fog color.
                col.rgb = lerp(surfaceRgb, _UnderwaterFogColor.rgb, max(bodyOpacity * 0.88, horizonFade * 0.92));
                col.a = finalAlpha;
                return col;
            }
            ENDCG
        }
    }

    FallBack Off
}
