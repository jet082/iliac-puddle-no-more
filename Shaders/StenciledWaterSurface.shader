// Project:         Deep Waters
// License:         MIT
//
// Simple visible ocean surface. The top pass renders only from above, while
// a separate underwater pass draws the underside only when the camera is below
// the plane so it cannot create an above-water screen-space band.

Shader "DeepWaters/StenciledWaterSurface"
{
    Properties
    {
        _MainTex ("Wave texture (RGB)", 2D) = "white" {}
        _Color ("Surface tint and opacity", Color) = (0.34, 0.55, 0.58, 0.35)

        _StencilRef ("Stencil reject value", Range(0, 255)) = 200
        _StencilReadMask ("Stencil read mask", Range(0, 255)) = 255

        _UndersideAlpha ("Underside transparency", Range(0, 1)) = 0.25
        _UnderwaterFogColor ("Deep water color", Color) = (0.055, 0.098, 0.082, 1.0)
        _WaterColumnDepth ("Nominal water column depth", Float) = 35.0
        _WaterColumnFogDepth ("Water column fog depth", Float) = 35.0
        _WaterColumnFogStrength ("Water column fog strength", Range(0, 1)) = 1.0
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
            Name "TOP_SURFACE_ONLY"
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

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
                // Only draw when the camera is above the water plane and the
                // player/camera presentation is not underwater. Third-person
                // cameras can be above the surface while the player is under,
                // so the explicit global flag matters more than culling here.
                clip(0.5 - _DeepWatersUnderwater);
                clip(_WorldSpaceCameraPos.y - i.worldPos.y + 0.02);

                fixed4 wave = tex2D(_MainTex, i.uv);
                fixed3 surfaceRgb = wave.rgb * _Color.rgb;
                float surfaceOpacity = saturate(_Color.a);

                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.screenPos.xy / i.screenPos.w);
                bool noDepth = IsNoDepth(rawDepth);

                float columnDepth = _WaterColumnDepth;
                if (!noDepth)
                {
                    float sceneDepthLinear = LinearEyeDepth(rawDepth);
                    float surfaceDepthLinear = max(i.screenPos.w, 0.0001);
                    float waterPath = max(0.0, sceneDepthLinear - surfaceDepthLinear);
                    float depthRatio = sceneDepthLinear / surfaceDepthLinear;
                    float3 sceneWorldPos = _WorldSpaceCameraPos + (i.worldPos - _WorldSpaceCameraPos) * depthRatio;
                    float verticalDepth = max(0.0, i.worldPos.y - sceneWorldPos.y);
                    columnDepth = max(_WaterColumnDepth, max(verticalDepth, waterPath * 0.35));
                }

                float normalizedDepth = columnDepth / max(_WaterColumnFogDepth, 0.001);
                float fogStrength = saturate(_WaterColumnFogStrength);
                float waterTint = saturate((1.0 - exp2(-normalizedDepth * lerp(0.35, 1.6, fogStrength))) * fogStrength);

                fixed4 col;
                col.rgb = lerp(surfaceRgb, _UnderwaterFogColor.rgb, waterTint * 0.35);
                col.a = surfaceOpacity;
                return col;
            }
            ENDCG
        }

        Pass
        {
            Name "UNDERWATER_SURFACE"
            Cull Front
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

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

            fixed4 _Color;
            fixed4 _UnderwaterFogColor;
            float _UndersideAlpha;
            float _WaterColumnFogStrength;
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
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex) + float2(_ScrollX, _ScrollY) * _Time.y;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Only draw the physical underside when the camera is in the
                // underwater presentation and below the water plane.
                clip(_DeepWatersUnderwater - 0.5);
                clip(i.worldPos.y - _WorldSpaceCameraPos.y + 0.02);

                fixed4 wave = tex2D(_MainTex, i.uv);
                fixed3 surfaceRgb = wave.rgb * _Color.rgb;

                float viewDistance = length(i.worldPos - _WorldSpaceCameraPos);
                float farOpacity = smoothstep(
                    _SurfaceOpaqueFadeStart * 0.35,
                    max(_SurfaceOpaqueFadeStart * 0.35 + 1.0, _SurfaceOpaqueFadeEnd),
                    viewDistance);

                float undersideAlpha = saturate(_UndersideAlpha);
                float finalOpacity = lerp(
                    undersideAlpha,
                    max(undersideAlpha, 0.72),
                    farOpacity * saturate(_WaterColumnFogStrength));

                fixed4 col;
                col.rgb = lerp(surfaceRgb, _UnderwaterFogColor.rgb, 0.35);
                col.a = finalOpacity;
                return col;
            }
            ENDCG
        }

    }

    FallBack Off
}
