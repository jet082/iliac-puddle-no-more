// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    internal static class WaterSurfaceResources
    {
        private static readonly int ColorProperty = Shader.PropertyToID("_Color");
        private static readonly int UndersideAlphaProperty = Shader.PropertyToID("_UndersideAlpha");
        private static readonly int UndersideFogTintProperty = Shader.PropertyToID("_UndersideFogTint");
        private static readonly int UnderwaterFogColorProperty = Shader.PropertyToID("_UnderwaterFogColor");
        private static readonly int WaterColumnDepthProperty = Shader.PropertyToID("_WaterColumnDepth");
        private static readonly int WaterColumnFogDepthProperty = Shader.PropertyToID("_WaterColumnFogDepth");
        private static readonly int WaterColumnFogStrengthProperty = Shader.PropertyToID("_WaterColumnFogStrength");
        private static readonly int SurfaceOpaqueFadeStartProperty = Shader.PropertyToID("_SurfaceOpaqueFadeStart");
        private static readonly int SurfaceOpaqueFadeEndProperty = Shader.PropertyToID("_SurfaceOpaqueFadeEnd");

        private static Mesh sharedFlatMesh;
        private static Material sharedWaterMaterial;
        private static readonly Color SurfaceTint = new Color(0.519f, 0.527f, 0.467f, 1f);
        private static readonly Color OpaqueSurfaceColor = new Color(0.075f, 0.24f, 0.38f, 1f);
        private const float NearOpaqueFadeStart = 12f;
        private const float FarOpaqueFadeStart = 24f;
        private const float NearOpaqueFadeEnd = 55f;
        private const float FarOpaqueFadeEnd = 115f;
        private const float SlowDistanceFalloffSpanScale = 1.85f;
        private const float FastDistanceFalloffSpanScale = 0.45f;

        public static Mesh GetFlatMesh()
        {
            if (sharedFlatMesh != null)
                return sharedFlatMesh;

            sharedFlatMesh = new Mesh { name = "DeepWaters.TopOnlyQuad" };
            sharedFlatMesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
            };
            sharedFlatMesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            sharedFlatMesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            sharedFlatMesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            sharedFlatMesh.RecalculateBounds();
            return sharedFlatMesh;
        }

        public static Material GetSharedMaterial()
        {
            ApplyMaterialSettings();
            return sharedWaterMaterial;
        }

        public static Material GetMaterial()
        {
            if (sharedWaterMaterial != null)
            {
                ApplyMaterialSettings();
                return sharedWaterMaterial;
            }

            Shader shader = LoadShader();
            if (shader == null)
                return null;

            sharedWaterMaterial = new Material(shader) { name = "DeepWaters.WaterSurface" };
            ApplyBaseTexture(sharedWaterMaterial);
            ApplyMaterialSettings();
            return sharedWaterMaterial;
        }

        public static void ApplyMaterialSettings()
        {
            if (sharedWaterMaterial == null || DeepWaters.Instance == null)
                return;

            if (sharedWaterMaterial.HasProperty(ColorProperty))
            {
                Color color = sharedWaterMaterial.GetColor(ColorProperty);
                color.r = SurfaceTint.r;
                color.g = SurfaceTint.g;
                color.b = SurfaceTint.b;
                color.a = DeepWaters.Instance.WaterSurfaceTopAlpha;
                sharedWaterMaterial.SetColor(ColorProperty, color);
            }

            if (sharedWaterMaterial.HasProperty(UndersideAlphaProperty))
                sharedWaterMaterial.SetFloat(UndersideAlphaProperty, DeepWaters.Instance.WaterSurfaceBottomAlpha);

            if (sharedWaterMaterial.HasProperty(UndersideFogTintProperty))
                sharedWaterMaterial.SetFloat(UndersideFogTintProperty, GetUndersideFogTint());

            if (sharedWaterMaterial.HasProperty(UnderwaterFogColorProperty))
                sharedWaterMaterial.SetColor(UnderwaterFogColorProperty, DeepWaters.GetUnderwaterFogColor());

            if (sharedWaterMaterial.HasProperty(WaterColumnDepthProperty))
                sharedWaterMaterial.SetFloat(WaterColumnDepthProperty, Mathf.Max(1f, DeepWaters.Instance.WaterDepth));

            if (sharedWaterMaterial.HasProperty(WaterColumnFogDepthProperty))
                sharedWaterMaterial.SetFloat(WaterColumnFogDepthProperty, GetWaterColumnFogDepth());

            if (sharedWaterMaterial.HasProperty(WaterColumnFogStrengthProperty))
                sharedWaterMaterial.SetFloat(WaterColumnFogStrengthProperty, GetWaterColumnFogStrength());

            float fadeStart;
            float fadeEnd;
            GetSurfaceOpaqueFadeRange(out fadeStart, out fadeEnd);

            if (sharedWaterMaterial.HasProperty(SurfaceOpaqueFadeStartProperty))
                sharedWaterMaterial.SetFloat(SurfaceOpaqueFadeStartProperty, fadeStart);

            if (sharedWaterMaterial.HasProperty(SurfaceOpaqueFadeEndProperty))
                sharedWaterMaterial.SetFloat(SurfaceOpaqueFadeEndProperty, fadeEnd);
        }

        private static Shader LoadShader()
        {
            Shader shader = null;
            if (DeepWaters.Mod != null)
                shader = DeepWaters.Mod.GetAsset<Shader>("StenciledWaterSurface.shader");

            if (shader == null)
                shader = Shader.Find("DeepWaters/StenciledWaterSurface");

            if (shader == null)
                Debug.LogError("[DeepWaters] DeepWaters/StenciledWaterSurface shader not found. Water surfaces will not render.");

            return shader;
        }

        private static void ApplyBaseTexture(Material material)
        {
            Texture2D waterTex = DaggerfallUnity.Instance.MaterialReader.TextureReader.GetTexture2D(302, 0, 0);
            if (waterTex == null)
            {
                material.color = OpaqueSurfaceColor;
                return;
            }

            waterTex.wrapMode = TextureWrapMode.Repeat;
            waterTex.filterMode = FilterMode.Point;
            material.mainTexture = waterTex;
            material.mainTextureScale = new Vector2(128f, 128f);
        }

        private static float GetWaterColumnFogDepth()
        {
            return Mathf.Max(2f, DeepWaters.Instance.WaterDepth * DeepWaters.Instance.UnderwaterFogDistanceMultiplier);
        }

        private static float GetWaterColumnFogStrength()
        {
            return Mathf.Clamp01(DeepWaters.Instance.UnderwaterFogStrength);
        }

        private static float GetUndersideFogTint()
        {
            return Mathf.Clamp01(DeepWaters.Instance.UnderwaterFogStrength * 0.9f);
        }

        internal static void GetSurfaceOpaqueFadeRange(out float fadeStart, out float fadeEnd)
        {
            float transparency = DeepWaters.Instance != null
                ? Mathf.Clamp01(DeepWaters.Instance.WaterSurfaceTopTransparency)
                : 0.5f;
            float falloff = DeepWaters.Instance != null
                ? Mathf.Clamp01(DeepWaters.Instance.WaterSurfaceDistanceFalloff)
                : 0.5f;

            fadeStart = Mathf.Lerp(NearOpaqueFadeStart, FarOpaqueFadeStart, transparency);
            float baseFadeEnd = Mathf.Lerp(NearOpaqueFadeEnd, FarOpaqueFadeEnd, transparency);
            float baseSpan = Mathf.Max(1f, baseFadeEnd - fadeStart);
            float spanScale = falloff <= 0.5f
                ? Mathf.Lerp(SlowDistanceFalloffSpanScale, 1f, falloff / 0.5f)
                : Mathf.Lerp(1f, FastDistanceFalloffSpanScale, (falloff - 0.5f) / 0.5f);

            fadeEnd = fadeStart + baseSpan * spanScale;

            if (fadeEnd < fadeStart + 1f)
                fadeEnd = fadeStart + 1f;
        }
    }
}
