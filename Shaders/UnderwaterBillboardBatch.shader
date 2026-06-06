// Project:         Deep Waters
// License:         MIT

Shader "DeepWaters/UnderwaterBillboardBatch"
{
    Properties
    {
        _Color("Color", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _MainTex("Albedo Map", 2D) = "white" {}
        _Brightness("Underwater Brightness", Range(0.5, 3.0)) = 1.75
        _UpVector("Up Vector (XYZ)", Vector) = (0,1,0,0)
    }

    SubShader
    {
        Tags
        {
            "Queue" = "AlphaTest"
            "IgnoreProjector" = "True"
            "RenderType" = "TransparentCutout"
        }
        LOD 100

        Pass
        {
            Cull Off
            ZWrite On

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            float _Cutoff;
            float _Brightness;
            float3 _UpVector;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_full v)
            {
                v2f o;

                float3 viewDirection = UNITY_MATRIX_V._m02_m12_m22;
                float3 rightVector = normalize(cross(viewDirection, _UpVector));

                v.vertex.xyz += rightVector * (v.tangent.z - 0.5) * v.tangent.x;
                v.vertex.xyz += _UpVector * (v.tangent.w - 0.5) * v.tangent.y;

                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv) * _Color;
                clip(col.a - _Cutoff);
                col.rgb = saturate(col.rgb * _Brightness);
                return col;
            }
            ENDCG
        }
    }

    FallBack Off
}
