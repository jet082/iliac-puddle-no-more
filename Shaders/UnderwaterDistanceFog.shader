// Project:         Deep Waters
// License:         MIT
//
// Underwater camera image effect — simple clean version. Implements
// per-channel Beer-Lambert absorption for the natural red-fading look,
// plus distance-based in-scattering and a vertical depth-darkening curve.
// Earlier experimental features (caustics, god rays, dust motes, screen
// refraction, surface-from-below sampling) are removed pending the user's
// approval of the base look. Keeping the base solid first.

Shader "DeepWaters/UnderwaterDistanceFog"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}

        // Per-channel Beer-Lambert absorption (R fades fastest).
        _AbsorptionColor ("Per-channel absorption (R fastest)", Color) = (1.80, 1.25, 1.05, 1.0)
        _ScatterColor ("In-scattered ambient water color", Color) = (0.090, 0.165, 0.155, 1.0)
        _ScatterStrength ("In-scatter strength", Range(0, 5)) = 1.20
        _DeepWaterColor ("Deep abyssal floor color", Color) = (0.012, 0.022, 0.026, 1.0)

        // Legacy / settings-driven params.
        _UnderwaterFogColor ("Legacy fog color (back-compat)", Color) = (0.055, 0.098, 0.082, 1.0)
        _FogStrength ("Fog strength", Range(0, 1)) = 0.5
        _VisionDistance ("Underwater vision distance", Float) = 70.0
        _DepthDarkeningStart ("Depth darkening start", Float) = 6.0
        _DepthDarkeningEnd ("Depth darkening end", Float) = 110.0
        _WaterSurfaceY ("Water surface Y", Float) = 0.0
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            sampler2D_float _CameraDepthTexture;

            fixed4 _AbsorptionColor;
            fixed4 _ScatterColor;
            float _ScatterStrength;
            fixed4 _DeepWaterColor;

            fixed4 _UnderwaterFogColor;
            float _FogStrength;
            float _VisionDistance;
            float _DepthDarkeningStart;
            float _DepthDarkeningEnd;
            float _WaterSurfaceY;
            float _CameraUnderwater;

            float4 _DeepWatersRayBL;
            float4 _DeepWatersRayBR;
            float4 _DeepWatersRayTL;
            float4 _DeepWatersRayTR;

            float3 InterpolateRay(float2 uv)
            {
                float3 bottom = lerp(_DeepWatersRayBL.xyz, _DeepWatersRayBR.xyz, uv.x);
                float3 top = lerp(_DeepWatersRayTL.xyz, _DeepWatersRayTR.xyz, uv.x);
                return lerp(bottom, top, uv.y);
            }

            bool IsNoDepth(float rawDepth)
            {
                #if defined(UNITY_REVERSED_Z)
                    return rawDepth <= 0.0001;
                #else
                    return rawDepth >= 0.9999;
                #endif
            }

            float EffectiveVisionDistance(float depthDarkening)
            {
                // Vision dims with depth — bottoms out at half the surface
                // value at the end of the ramp.
                return max(1.0, _VisionDistance * lerp(1.0, 0.55, depthDarkening));
            }

            fixed3 ApplyDistanceLimit(fixed3 lit, float dist, float depthDarkening)
            {
                float fogStrength = saturate(_FogStrength);
                float effectiveVision = EffectiveVisionDistance(depthDarkening);

                // Beer-Lambert color loss looks natural up close, but it does
                // not fully hide sky/transparent/no-depth pixels. Add a soft
                // visibility curtain that reaches full strength only at high
                // fog settings.
                float fadeStart = effectiveVision * lerp(1.40, 0.55, fogStrength);
                float fadeEnd = effectiveVision * lerp(3.20, 1.05, fogStrength);
                float curtain = smoothstep(
                    fadeStart,
                    max(fadeStart + 0.10, fadeEnd),
                    dist) * smoothstep(0.15, 1.0, fogStrength);

                // Absolute far limit, independent of fog strength: past this
                // distance the curtain is FULLY opaque, so the loaded-world
                // edge (the void) and far terrain silhouettes can never bleed
                // through. The strength-scaled curtain above only softens what
                // happens before this hard horizon.
                float hardCurtain = smoothstep(
                    effectiveVision * 2.4,
                    effectiveVision * 3.2,
                    dist);
                curtain = max(curtain, hardCurtain);

                fixed3 ambientWater = lerp(
                    _ScatterColor.rgb,
                    _DeepWaterColor.rgb,
                    saturate(depthDarkening * 0.85 + fogStrength * 0.18));

                return lerp(lit, ambientWater, curtain);
            }

            fixed3 ApplyWaterVolume(fixed3 sceneColor, float dist, float depthDarkening)
            {
                float effectiveVision = EffectiveVisionDistance(depthDarkening);
                float fogStrength = saturate(_FogStrength);
                float strengthScale = lerp(0.55, 1.45, fogStrength);

                float3 absorption = _AbsorptionColor.rgb * strengthScale / effectiveVision;
                float3 transmission = exp(-absorption * dist);

                float scatterCoef = _ScatterStrength * strengthScale / effectiveVision;
                float scatterAmount = 1.0 - exp(-scatterCoef * dist);

                fixed3 ambientWater = lerp(_ScatterColor.rgb, _DeepWaterColor.rgb, saturate(depthDarkening * 0.65));
                fixed3 inScatter = ambientWater * scatterAmount;

                fixed3 lit = sceneColor * transmission + inScatter;

                // Vertical column dimming. Stays at ~0.55 at full depth so the
                // scene remains readable rather than going to silhouette-mode.
                lit *= lerp(1.0, 0.55, depthDarkening);

                return ApplyDistanceLimit(lit, dist, depthDarkening);
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 source = tex2D(_MainTex, i.uv);
                if (_CameraUnderwater < 0.5)
                    return source;

                float2 depthUV = i.uv;
                #if UNITY_UV_STARTS_AT_TOP
                    if (_MainTex_TexelSize.y < 0.0)
                        depthUV.y = 1.0 - depthUV.y;
                #endif
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, depthUV);
                bool noDepth = IsNoDepth(rawDepth);

                float3 rayWorldUnnorm = InterpolateRay(i.uv);

                float cameraDepthBelow = max(0.0, _WaterSurfaceY - _WorldSpaceCameraPos.y);
                float depthDarkening = smoothstep(
                    _DepthDarkeningStart,
                    max(_DepthDarkeningStart + 0.001, _DepthDarkeningEnd),
                    cameraDepthBelow);

                float effectiveVision = EffectiveVisionDistance(depthDarkening);

                float viewDistance;
                if (noDepth)
                {
                    // Sky / no-depth pixels: use a far value so the volume
                    // pushes them to ambient water color. Must sit PAST the
                    // hard far curtain (3.2x vision) so the void fully fogs
                    // out at every fog strength.
                    float farFog = effectiveVision * lerp(3.6, 1.40, saturate(_FogStrength));

                    // When looking UP from below the surface, stop the fog at the
                    // water surface instead of treating the whole ray as infinite
                    // water. Otherwise the rendered underside of the surface (and
                    // the bright world above it) is buried under full ambient fog
                    // and becomes invisible. Horizon-ward rays (rayDir.y ~ 0) keep
                    // the far value so missing-terrain/skybox leaks still fog out.
                    // (issue 9: can't see the underside of the water surface)
                    float3 rayDir = normalize(rayWorldUnnorm);
                    float belowSurface = _WaterSurfaceY - _WorldSpaceCameraPos.y;
                    if (rayDir.y > 0.0001 && belowSurface > 0.0)
                        viewDistance = min(farFog, belowSurface / rayDir.y);
                    else
                        viewDistance = farFog;
                }
                else
                {
                    float linearEyeDepth = LinearEyeDepth(rawDepth);
                    viewDistance = length(rayWorldUnnorm) * linearEyeDepth;
                }

                source.rgb = ApplyWaterVolume(source.rgb, viewDistance, depthDarkening);

                return source;
            }
            ENDCG
        }
    }

    FallBack Off
}
