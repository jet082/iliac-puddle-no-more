// Project:         Deep Waters
// License:         MIT

Shader "DeepWaters/UnderwaterBillboardBatchUnlit"
{
    Properties
    {
        _Color ("Color", Color) = (1.12, 1.12, 1.12, 1)
        _Cutoff ("Alpha cutoff", Range(0.0, 1.0)) = 0.5
        _MainTex ("Albedo map", 2D) = "white" {}
        _UpVector ("Up Vector (XYZ)", Vector) = (0, 1, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "AlphaTest"
            "IgnoreProjector" = "True"
            "RenderType" = "TransparentCutout"
        }

        Pass
        {
            Name "UNDERWATER_BILLBOARD_UNLIT"
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Cutoff;
            float3 _UpVector;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 tangent : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 viewDirection = UNITY_MATRIX_V._m02_m12_m22;
                float3 rightVector = normalize(cross(viewDirection, _UpVector));

                v.vertex.xyz += rightVector * (v.tangent.z - 0.5) * v.tangent.x;
                v.vertex.xyz += _UpVector * (v.tangent.w - 0.5) * v.tangent.y;

                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * _Color;
                clip(col.a - _Cutoff);
                col.a = 1.0;
                return col;
            }
            ENDCG
        }

        // Depth pass. Unity renders _CameraDepthTexture with each shader's
        // ShadowCaster pass; without one (or with the default vertex
        // transform), the tangent-expanded billboard quads collapse to
        // degenerate points in the depth texture, so the underwater fog
        // post-effect reads the BACKGROUND's depth at the flats' pixels and
        // paints its opaque far curtain over nearby decorations (the "black
        // band on top of flats"). This pass mirrors the main pass's
        // expansion + alpha clip so flats fog by their own distance.
        // (Renderers keep shadowCastingMode = Off, so this adds no real
        // light shadows — only depth-texture presence.)
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull Off
            ZWrite On

            CGPROGRAM
            #pragma vertex vertShadow
            #pragma fragment fragShadow
            #pragma target 3.0
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Cutoff;
            float3 _UpVector;

            struct v2f_caster
            {
                V2F_SHADOW_CASTER;
                float2 uv : TEXCOORD1;
            };

            v2f_caster vertShadow(appdata_tan v)
            {
                v2f_caster o;
                float3 viewDirection = UNITY_MATRIX_V._m02_m12_m22;
                float3 rightVector = normalize(cross(viewDirection, _UpVector));

                v.vertex.xyz += rightVector * (v.tangent.z - 0.5) * v.tangent.x;
                v.vertex.xyz += _UpVector * (v.tangent.w - 0.5) * v.tangent.y;

                TRANSFER_SHADOW_CASTER_NORMALOFFSET(o)
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            float4 fragShadow(v2f_caster i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * _Color;
                clip(col.a - _Cutoff);
                SHADOW_CASTER_FRAGMENT(i)
            }
            ENDCG
        }
    }

    FallBack Off
}
