// Iliac Puddle No More — Stenciled Water Surface
Shader "DeepWaters/StenciledWaterSurface"
{
    Properties
    {
        // --- Texture & Color ---
        _MainTex          ("Wave texture (RGB)",          2D)               = "white" {}
        _Color            ("Tint & Transparency",         Color)            = (0.519, 0.527, 0.467, 0.98)
        
        // --- Stencil For Boat Mods ---
        _StencilRef       ("Stencil reject value",        Range(0, 255))    = 200
        _StencilReadMask  ("Stencil read mask",           Range(0, 255))    = 255

        // --- Underside-Specific ---
        _UndersideFogTint ("Underside fog tint (0..1)", Range(0, 1)) = 0.45
        _UndersideAlpha   ("Underside transparency", Range(0, 1)) = 0.98
        _UnderwaterFogColor ("Underwater fog color", Color) = (0.055, 0.098, 0.082, 1.0)
        _WaterColumnDepth ("Nominal water column depth", Float) = 35.0
        _WaterColumnFogDepth ("Water column fog depth", Float) = 35.0
        _WaterColumnFogStrength ("Water column fog strength", Range(0, 1)) = 1.0
        _SurfaceOpaqueFadeStart ("Surface opaque fade start", Float) = 18.0
        _SurfaceOpaqueFadeEnd ("Surface opaque fade end", Float) = 85.0

        // --- Movement ---
        _ScrollX          ("Wave Scroll Speed X", Float)  = 0.0225
        _ScrollY          ("Wave Scroll Speed Y", Float)  = 0.0375
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "IgnoreProjector" = "True"
        }

        // ==========================================================================================
        // COMMON CODE BLOCK
        // Shared by both rendering passes to reduce duplication.
        // ==========================================================================================
        CGINCLUDE
        #include "UnityCG.cginc"

        // --- Structs ---
        struct appdata
        {
            float4 vertex : POSITION;
            float2 uv     : TEXCOORD0;
        };

        struct v2f
        {
            float4 pos       : SV_POSITION;
            float2 uv        : TEXCOORD0;
            float3 worldPos  : TEXCOORD1;
            float4 screenPos : TEXCOORD2; // screenPos is used by top pass, unused by bottom pass (zero overhead)
        };

        // --- Variables (from Properties) ---
        sampler2D _MainTex;
        float4    _MainTex_ST;
        fixed4    _Color;
        float     _ScrollX;
        float     _ScrollY;
        
        // --- Vertex Shader (used by both passes) ---
        v2f vert(appdata v)
        {
            v2f o;
            o.pos       = UnityObjectToClipPos(v.vertex);
            float2 baseUV = TRANSFORM_TEX(v.uv, _MainTex);
            o.uv        = baseUV + float2(_ScrollX, _ScrollY) * _Time.y;
            o.worldPos  = mul(unity_ObjectToWorld, v.vertex).xyz;
            o.screenPos = ComputeScreenPos(o.pos);
            return o;
        }

        ENDCG


        // ==========================================================================================
        // PASS 1: Top face (viewed from above)
        // Renders with transparency and a depth-based color falloff.
        // ==========================================================================================
        Pass
        {
            Name "TOP_SURFACE"
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

            Stencil
            {
                Ref       [_StencilRef]
                ReadMask  [_StencilReadMask]
                Comp      NotEqual
            }

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 3.0

            // --- Variables for this pass ---
            sampler2D_float _CameraDepthTexture;
            fixed4    _UnderwaterFogColor;
            float     _WaterColumnDepth;
            float     _WaterColumnFogDepth;
            float     _WaterColumnFogStrength;
            float     _SurfaceOpaqueFadeStart;
            float     _SurfaceOpaqueFadeEnd;

            // --- Fragment Shader ---
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 waterTexture = tex2D(_MainTex, i.uv);
                fixed3 transparentWaterRgb = waterTexture.rgb * _Color.rgb;
                fixed3 opaqueWaterRgb = transparentWaterRgb;
                float surfaceAlpha = saturate(_Color.a);
                float surfaceTransparency = saturate((1.0 - surfaceAlpha) / 0.7);

                // --- Depth Calculation ---
                float rawSceneDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.screenPos.xy / i.screenPos.w);
                bool sceneHasDepth = true;
                float sceneDepthLinear = LinearEyeDepth(rawSceneDepth);
                float surfaceDepthLinear = i.screenPos.w;
                float waterDepth = sceneDepthLinear - surfaceDepthLinear;

                // Sky pixels do not write useful depth. Treat them as no depth
                // instead of infinitely deep water, which otherwise paints a dark
                // sheet across the horizon when looking over the surface.
                #if defined(UNITY_REVERSED_Z)
                    if (rawSceneDepth <= 0.0001)
                    {
                        sceneHasDepth = false;
                        waterDepth = 0.0;
                    }
                #else
                    if (rawSceneDepth >= 0.9999)
                    {
                        sceneHasDepth = false;
                        waterDepth = 0.0;
                    }
                #endif

                float verticalDepth = 0.0;
                if (waterDepth > 0.0 && surfaceDepthLinear > 0.0001)
                {
                    float depthRatio = sceneDepthLinear / surfaceDepthLinear;
                    float3 sceneWorldPos = _WorldSpaceCameraPos + (i.worldPos - _WorldSpaceCameraPos) * depthRatio;
                    verticalDepth = max(0.0, i.worldPos.y - sceneWorldPos.y);
                }

                // Camera depth is best when it is available, because it can
                // preserve shallow water. Some exterior render paths do not
                // provide reliable depth to transparent water, so fall back to
                // the configured vertical ocean depth. Keep this vertical
                // rather than view-angle based so the above-water fog does not
                // form a circular falloff around the player.
                float fallbackColumnDepth = _WaterColumnDepth;
                float surfaceViewDistance = length(i.worldPos - _WorldSpaceCameraPos);
                // Use the scene distance behind the water when depth exists.
                // Otherwise a camera skimmed close to the surface sees an
                // artificially short plane hit and the water becomes clearer
                // than it is from a higher boat view.
                float visibilityDistance = surfaceViewDistance;
                if (sceneHasDepth && waterDepth > 0.0)
                    visibilityDistance = max(visibilityDistance, sceneDepthLinear * 0.65);
                float distanceColumnDepth = visibilityDistance * 0.35;
                float distanceFadeStart = _SurfaceOpaqueFadeStart;
                float distanceFadeEnd = max(distanceFadeStart + 1.0, _SurfaceOpaqueFadeEnd);
                float distanceOpacity = surfaceTransparency > 0.001
                    ? smoothstep(distanceFadeStart, distanceFadeEnd, visibilityDistance)
                    : 1.0;
                float transparentRange = 1.0 - distanceOpacity;

                float depthTextureColumnDepth = max(verticalDepth, waterDepth * 0.65);
                float columnDepth = max(max(depthTextureColumnDepth, fallbackColumnDepth), distanceColumnDepth);
                float normalizedDepth = columnDepth / max(_WaterColumnFogDepth, 0.001);
                // Keep mid transparency meaningfully foggy. A linear weight made
                // 0.5 transparency much clearer from above than from below.
                float topFogVisibility = surfaceTransparency > 0.001
                    ? saturate(0.35 + 0.65 * sqrt(surfaceTransparency)) * transparentRange
                    : 0.0;
                float fogStrength = saturate(_WaterColumnFogStrength);
                float depthFogCurve = max(0.001, fogStrength * lerp(2.0, 8.0, fogStrength));
                float depthFactor = saturate((1.0 - exp2(-normalizedDepth * depthFogCurve)) * topFogVisibility);

                fixed4 surfaceColor = waterTexture;
                surfaceColor.rgb = transparentWaterRgb;
                surfaceColor.a = surfaceAlpha;
                float effectiveSurfaceAlpha = lerp(1.0, surfaceAlpha, transparentRange);

                // From above, water fog follows the water path the viewer is
                // looking through. This keeps deep water from becoming a clear
                // window to the seabed when the surface transparency is high,
                // while leaving opaque water as the tinted surface texture.
                fixed4 finalColor = lerp(surfaceColor, _UnderwaterFogColor, depthFactor);
                finalColor.rgb = lerp(finalColor.rgb, opaqueWaterRgb, distanceOpacity);
                finalColor.a = effectiveSurfaceAlpha;

                // --- Fog Calculation ---
                float fogDist   = length(i.worldPos - _WorldSpaceCameraPos);
                float fogFactor = saturate(exp2(-unity_FogParams.y * fogDist));
                finalColor.rgb = lerp(finalColor.rgb, unity_FogColor.rgb, (1.0 - fogFactor) * topFogVisibility);

                return finalColor;
            }
            ENDCG
        }

        // ==========================================================================================
        // PASS 2: Bottom face (viewed from below)
        // Renders transparently to allow seeing the sky from underwater.
        // ==========================================================================================
        Pass
        {
            Name "BOTTOM_SURFACE"
            Cull Front
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off

            Stencil
            {
                Ref       [_StencilRef]
                ReadMask  [_StencilReadMask]
                Comp      NotEqual
            }

            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            // --- Variables for this pass ---
            float _UndersideFogTint;
            float _UndersideAlpha;
            fixed4 _UnderwaterFogColor;
			float _WaterColumnFogStrength;
            
            // --- Fragment Shader ---
            fixed4 frag(v2f i) : SV_Target
            {
                // Base color with alpha from the material's _Color property
                fixed4 col = tex2D(_MainTex, i.uv) * _Color;

                // The uniform tint provides the base murkiness for the underwater feel.
                col.rgb = lerp(col.rgb, _UnderwaterFogColor.rgb, _UndersideFogTint);
                col.a = _UndersideAlpha;

                if (_UndersideAlpha > 0.97)
                {
                    col.rgb = _UnderwaterFogColor.rgb;
                    col.a = 1.0;
                    return col;
                }
				
                // Hide near-parallel views into the surface. Distance fog on
                // this giant plane creates circular bands around the player,
                // but a view-angle fade only affects the true grazing case:
                // the thin horizon strip where sky or distant unloaded terrain
                // can leak through the transparent waterline.
                float3 viewToSurface = normalize(i.worldPos - _WorldSpaceCameraPos);
                float undersideTransparency = saturate((1.0 - _UndersideAlpha) / 0.7);
                float grazing = 1.0 - smoothstep(0.08, 0.28, saturate(viewToSurface.y));
                grazing *= saturate(_WaterColumnFogStrength) * undersideTransparency;
                col.rgb = lerp(col.rgb, _UnderwaterFogColor.rgb, grazing);
                col.a = lerp(col.a, max(col.a, 0.85), grazing);
                
                return col;
            }
            ENDCG
        }
    }
    FallBack "Transparent/VertexLit"
}
