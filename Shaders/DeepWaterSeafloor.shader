// Project:         Iliac Puddle No More
// License:         MIT
//
// Seafloor shader. Vertex color packs (depthBand, climateBand, distanceBand)
// driving a sand → silt → rock palette blend. A regional terrain texture
// (matching the surrounding climate's ground archive) supplies local grain
// and modulates the palette so the seafloor inherits the visual identity of
// its climate. No lighting macros that pull in variant compilation — keeps
// the .dfmod bundle's shader variant set tiny.

Shader "DeepWaters/Seafloor"
{
    Properties
    {
        _SandColor   ("Shallow Sand Color", Color) = (0.74, 0.66, 0.49, 1)
        _MidColor    ("Mid-depth Silt Color", Color) = (0.41, 0.38, 0.31, 1)
        _DeepColor   ("Deep Rock Color", Color) = (0.18, 0.18, 0.20, 1)
        _SwampColor  ("Inland Mud Color", Color) = (0.30, 0.27, 0.18, 1)
        _MainTex     ("Regional Seafloor Texture", 2D) = "white" {}
        _TextureWorldScale("Texture World Scale", Float) = 0.15625
        _TextureStrength("Texture Strength", Range(0, 1)) = 0.75
        _ShelfMix    ("Shelf Sand Boost", Range(0, 1)) = 0.55
        _DepthGamma  ("Depth Curve", Range(0.25, 4.0)) = 1.4
        _AmbientBoost("Ambient Boost", Range(0, 3)) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            fixed4 _SandColor;
            fixed4 _MidColor;
            fixed4 _DeepColor;
            fixed4 _SwampColor;
            sampler2D _MainTex;
            float _TextureWorldScale;
            float _TextureStrength;
            float _ShelfMix;
            float _DepthGamma;
            float _AmbientBoost;
            // Water tint applied to the seafloor when viewed from above the
            // surface (rgb = the water surface tint, a = tint strength / gate).
            // a == 0 (incl. unset) = no tint, so the underwater view is untouched.
            float4 _DeepWatersSceneTint;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                fixed4 vColor : COLOR;
                float2 uv     : TEXCOORD0;
                UNITY_FOG_COORDS(1)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.vColor = v.color;
                o.uv = v.uv;
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float depthBand    = i.vColor.r;
                float climateBand  = i.vColor.g;
                float distanceBand = i.vColor.b;

                float dCurve = pow(saturate(depthBand), _DepthGamma);

                float shelfBoost = (1.0 - distanceBand) * _ShelfMix;
                float sandWeight = saturate(1.0 - dCurve + shelfBoost);
                float deepWeight = saturate(dCurve - shelfBoost * 0.5);
                float midWeight  = saturate(1.0 - sandWeight - deepWeight);

                fixed3 baseColor = sandWeight * _SandColor.rgb
                                 + midWeight  * _MidColor.rgb
                                 + deepWeight * _DeepColor.rgb;

                float inlandMix = saturate((0.7 - climateBand) / 0.7);
                baseColor = lerp(baseColor, _SwampColor.rgb, inlandMix * 0.5);

                // Regional terrain texture as grain + tint. UVs come in as
                // world-meter local positions; multiply by _TextureWorldScale
                // (1 / meters-per-tile) so the texture tiles every ~6.4m of
                // world distance, matching DFU's normal terrain look. Vertex
                // alpha locally attenuates this on the hidden shore-wall lip so
                // winter terrain textures do not draw a bright waterline.
                float textureStrength = _TextureStrength * saturate(i.vColor.a);
                fixed3 terrainTex = tex2D(_MainTex, i.uv * _TextureWorldScale).rgb;
                float terrainLum = dot(terrainTex, fixed3(0.299, 0.587, 0.114));
                fixed3 textureTint = lerp(baseColor, terrainTex, textureStrength * 0.35);
                float textureShade = lerp(1.0, lerp(0.55, 1.45, terrainLum), textureStrength * 0.75);
                baseColor = textureTint * textureShade;

                // Tint the seafloor by the water color when seen from above the
                // surface, so day water reads clear and night water genuinely
                // dims (preserving contrast); untouched underwater.
                fixed3 sceneTint = lerp(fixed3(1, 1, 1), _DeepWatersSceneTint.rgb, _DeepWatersSceneTint.a);

                fixed4 col = fixed4(baseColor * _AmbientBoost * sceneTint, 1.0);
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }

            ENDCG
        }
    }

    FallBack "Diffuse"
}
