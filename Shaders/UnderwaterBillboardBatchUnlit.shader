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
    }

    FallBack Off
}
