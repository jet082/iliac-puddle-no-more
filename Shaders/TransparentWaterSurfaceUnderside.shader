// Project:         Deep Waters
// License:         MIT

Shader "DeepWaters/TransparentWaterSurfaceUnderside"
{
    Properties
    {
        _MainTex ("Wave texture (RGB)", 2D) = "white" {}
        _Color ("Surface tint", Color) = (0.519, 0.527, 0.467, 1.0)
        _SrcBlend ("Source blend", Float) = 5
        _DstBlend ("Destination blend", Float) = 10
        _ZWrite ("Depth write", Float) = 0

        _StencilRef ("Stencil reject value", Range(0, 255)) = 200
        _StencilReadMask ("Stencil read mask", Range(0, 255)) = 255

        _UndersideAlpha ("Underside transparency", Range(0, 1)) = 0.25
        _UnderwaterFogColor ("Deep water color", Color) = (0.055, 0.098, 0.082, 1.0)
        _HorizonColor ("Horizon curtain color", Color) = (0.055, 0.098, 0.082, 1.0)
        _SurfaceOpaqueFadeStart ("Surface opaque fade start", Float) = 42.0
        _SurfaceOpaqueFadeEnd ("Surface opaque fade end", Float) = 160.0
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
            Name "UNDERSIDE_SURFACE"
            Cull Front
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

            fixed4 _Color;
            fixed4 _UnderwaterFogColor;
            float _UndersideAlpha;
            fixed4 _HorizonColor;
            float _SurfaceOpaqueFadeStart;
            float _SurfaceOpaqueFadeEnd;
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
                clip(_DeepWatersUnderwater - 0.5);
                clip(i.worldPos.y - _WorldSpaceCameraPos.y + 0.02);

                float undersideOpacity = saturate(_UndersideAlpha);

                // Distance wins exactly like the top sheet's old good behavior:
                // local transparency is honored nearby, and the horizon becomes
                // a real opaque curtain instead of a soft tint wash.
                float viewDist = distance(i.worldPos, _WorldSpaceCameraPos);
                float horizonFade = smoothstep(_SurfaceOpaqueFadeStart, max(_SurfaceOpaqueFadeStart + 1.0, _SurfaceOpaqueFadeEnd), viewDist);
                float fogStrength = saturate(_WaterColumnFogStrength);
                undersideOpacity = saturate(max(undersideOpacity, horizonFade));
                clip(undersideOpacity - 0.001);

                fixed4 wave = tex2D(_MainTex, i.uv);
                // Converge to the SAME ambient the fog volume saturates to
                // (_HorizonColor, pushed per frame), so the distant underside
                // and the fogged void are indistinguishable at range.
                fixed3 surfaceRgb = lerp(wave.rgb * _Color.rgb, _HorizonColor.rgb, horizonFade);

                fixed4 col;
                col.rgb = lerp(
                    surfaceRgb,
                    _UnderwaterFogColor.rgb,
                    fogStrength * 0.35);
                col.a = undersideOpacity;
                return col;
            }
            ENDCG
        }
    }

    FallBack Off
}
