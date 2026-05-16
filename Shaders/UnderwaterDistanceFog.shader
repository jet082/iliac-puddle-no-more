Shader "DeepWaters/UnderwaterDistanceFog"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _UnderwaterFogColor ("Underwater fog color", Color) = (0.055, 0.098, 0.082, 1.0)
        _FogDensity ("Fog density", Float) = 0.0875
        _GeometryFogDensity ("Above-surface fog density", Float) = 0.065
        _GeometryFogStart ("Above-surface fog start", Float) = 25.0
        _FogStart ("Fog start", Float) = 1.0
        _FogStrength ("Fog strength", Range(0, 1)) = 1.0
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

            fixed4 _UnderwaterFogColor;
            float _FogDensity;
            float _GeometryFogDensity;
            float _GeometryFogStart;
            float _FogStart;
            float _FogStrength;
            float _WaterSurfaceY;
            float _CameraUnderwater;
            float4x4 _DeepWatersInvProjection;
            float4x4 _DeepWatersCameraToWorld;

            float3 WorldRay(float2 uv)
            {
                float4 clip = float4(uv * 2.0 - 1.0, 1.0, 1.0);
                float4 view = mul(_DeepWatersInvProjection, clip);
                view.xyz /= view.w;
                return normalize(mul((float3x3)_DeepWatersCameraToWorld, view.xyz));
            }

            bool IsNoDepth(float rawDepth)
            {
                #if defined(UNITY_REVERSED_Z)
                    return rawDepth <= 0.0001;
                #else
                    return rawDepth >= 0.9999;
                #endif
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed4 source = tex2D(_MainTex, i.uv);

                float2 depthUV = i.uv;
                #if UNITY_UV_STARTS_AT_TOP
                    if (_MainTex_TexelSize.y < 0.0)
                        depthUV.y = 1.0 - depthUV.y;
                #endif

                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, depthUV);
                bool noDepth = IsNoDepth(rawDepth);
                float3 rayWorld = WorldRay(depthUV);

                if (_CameraUnderwater < 0.5)
                    return source;

                float waterExitDistance = 1e6;
                if (rayWorld.y > 0.0001)
                    waterExitDistance = max(0.0, (_WaterSurfaceY - _WorldSpaceCameraPos.y) / rayWorld.y);

                // Missing depth while looking steeply downward is often just a
                // transparent/cutout renderer or depth-texture gap. Near the
                // horizon, however, no-depth pixels are usually sky/terrain
                // leaking through the transparent waterline, so keep those in
                // this fog pass even when the ray is very slightly downward.
                if (noDepth && rayWorld.y < -0.18)
                    return source;

                float fogDistance = waterExitDistance;
                float fogDensity = _FogDensity;
                float fogStart = _FogStart;
                if (noDepth)
                {
                    float horizonFactor = 1.0 - smoothstep(0.03, 0.18, abs(rayWorld.y));
                    fogDistance = max(fogDistance, horizonFactor * 300.0);
                }
                else
                {
                    float sceneEyeDepth = LinearEyeDepth(rawDepth);
                    float3 cameraForward = normalize(mul((float3x3)_DeepWatersCameraToWorld, float3(0.0, 0.0, 1.0)));
                    float rayForward = max(0.0001, dot(rayWorld, cameraForward));
                    float sceneDistance = sceneEyeDepth / rayForward;
                    float3 sceneWorldPos = _WorldSpaceCameraPos.xyz + rayWorld * sceneDistance;

                    // Do not add a second screen-space fog layer to underwater
                    // geometry. DFU's RenderSettings fog already handles the
                    // seafloor; doing it here makes the local view collapse.
                    // Only fade above-surface renderers, e.g. distant flats and
                    // boats visible through the underside of the water plane.
                    if (sceneWorldPos.y <= _WaterSurfaceY)
                        return source;

                    fogDistance = sceneDistance;
                    fogDensity = _GeometryFogDensity;
                    fogStart = _GeometryFogStart;
                }

                float fogAmount = 1.0 - exp2(-max(0.0, fogDistance - fogStart) * fogDensity);
                fogAmount = saturate(fogAmount * _FogStrength);

                source.rgb = lerp(source.rgb, _UnderwaterFogColor.rgb, fogAmount);
                return source;
            }
            ENDCG
        }
    }
    FallBack Off
}
