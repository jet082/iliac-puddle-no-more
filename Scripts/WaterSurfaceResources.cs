// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepWaters
{
    internal static class WaterSurfaceResources
    {
        private const string TopSurfaceShaderName = "DeepWaters/TransparentWaterSurfaceTop";
        private const string TopSurfaceShaderAssetName = "TransparentWaterSurfaceTop.shader";
        private const string UndersideSurfaceShaderName = "DeepWaters/TransparentWaterSurfaceUnderside";
        private const string UndersideSurfaceShaderAssetName = "TransparentWaterSurfaceUnderside.shader";
        private const string TransparentRenderType = "Transparent";
        private const float LegacyOpaqueFadeDisabledStart = 100000f;

        private static readonly int ColorProperty = Shader.PropertyToID("_Color");
        private static readonly int UndersideAlphaProperty = Shader.PropertyToID("_UndersideAlpha");
        private static readonly int UnderwaterFogColorProperty = Shader.PropertyToID("_UnderwaterFogColor");
        private static readonly int WaterColumnDepthProperty = Shader.PropertyToID("_WaterColumnDepth");
        private static readonly int WaterColumnFogDepthProperty = Shader.PropertyToID("_WaterColumnFogDepth");
        private static readonly int WaterColumnFogStrengthProperty = Shader.PropertyToID("_WaterColumnFogStrength");
        private static readonly int SrcBlendProperty = Uniforms.SrcBlend;
        private static readonly int DstBlendProperty = Uniforms.DstBlend;
        private static readonly int ZWriteProperty = Uniforms.ZWrite;

        private static Mesh sharedFlatMesh;
        private static Material sharedTopMaterial;
        private static Material sharedUndersideMaterial;
        private static Texture sharedSurfaceTexture;
        private static readonly Color SurfaceTint = new Color(0.12f, 0.25f, 0.29f, 1f);
        private static readonly Color FallbackSurfaceColor = new Color(0.035f, 0.10f, 0.13f, 1f);

        public const float SurfaceTextureTiling = 128f;

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
            return GetTopMaterial();
        }

        public static Material GetMaterial()
        {
            return GetTopMaterial();
        }

        public static Material GetTopMaterial()
        {
            if (sharedTopMaterial == null)
            {
                sharedTopMaterial = CreateMaterial(
                    TopSurfaceShaderName,
                    TopSurfaceShaderAssetName,
                    "DeepWaters.WaterSurface.Top");
            }

            ApplyMaterialSettings();
            return sharedTopMaterial;
        }

        public static Material GetUndersideMaterial()
        {
            if (sharedUndersideMaterial == null)
            {
                sharedUndersideMaterial = CreateMaterial(
                    UndersideSurfaceShaderName,
                    UndersideSurfaceShaderAssetName,
                    "DeepWaters.WaterSurface.Underside");
            }

            ApplyMaterialSettings();
            return sharedUndersideMaterial;
        }

        public static bool IsTopSurfaceVisible()
        {
            return DeepWaters.Instance != null &&
                   DeepWaters.Instance.WaterSurfaceTopAlpha > 0.001f;
        }

        public static bool IsUndersideSurfaceVisible()
        {
            return DeepWaters.Instance != null &&
                   DeepWaters.Instance.WaterSurfaceBottomAlpha > 0.001f;
        }

        public static Texture GetSurfaceTexture()
        {
            if (sharedSurfaceTexture != null)
                return sharedSurfaceTexture;

            Texture2D waterTex = LoadWaterTexture();
            if (waterTex == null)
                return null;

            ApplyWaterTextureSettings(waterTex);
            sharedSurfaceTexture = waterTex;
            return sharedSurfaceTexture;
        }

        public static void ApplyMaterialSettings()
        {
            if (DeepWaters.Instance == null)
                return;

            ConfigureTopMaterial(sharedTopMaterial);
            ConfigureUndersideMaterial(sharedUndersideMaterial);
        }

        private static Material CreateMaterial(string shaderName, string shaderAssetName, string materialName)
        {
            Shader shader = LoadShader(shaderName, shaderAssetName);
            if (shader == null)
                return null;

            Material material = new Material(shader) { name = materialName };
            ConfigureTransparentMaterial(material);
            ApplyBaseTexture(material);
            return material;
        }

        private static Shader LoadShader(string shaderName, string shaderAssetName)
        {
            Shader shader = Shader.Find(shaderName);

            if (shader == null && DeepWaters.Mod != null)
                shader = DeepWaters.Mod.GetAsset<Shader>(shaderAssetName);

            if (shader == null)
            {
                Debug.LogError(
                    "[DeepWaters] " + shaderName + " shader not found. Water surfaces will not render.");
            }

            return shader;
        }

        private static void ConfigureTopMaterial(Material material)
        {
            if (material == null || DeepWaters.Instance == null)
                return;

            ConfigureTransparentMaterial(material);

            if (material.HasProperty(ColorProperty))
            {
                Color color = SurfaceTint;
                color.a = DeepWaters.Instance.WaterSurfaceTopAlpha;
                material.SetColor(ColorProperty, color);
            }

            ApplySharedWaterProperties(material);
        }

        private static void ConfigureUndersideMaterial(Material material)
        {
            if (material == null || DeepWaters.Instance == null)
                return;

            ConfigureTransparentMaterial(material);

            if (material.HasProperty(ColorProperty))
                material.SetColor(ColorProperty, SurfaceTint);

            if (material.HasProperty(UndersideAlphaProperty))
                material.SetFloat(UndersideAlphaProperty, DeepWaters.Instance.WaterSurfaceBottomAlpha);

            ApplySharedWaterProperties(material);
        }

        private static void ApplySharedWaterProperties(Material material)
        {
            if (material.HasProperty(UnderwaterFogColorProperty))
                material.SetColor(UnderwaterFogColorProperty, DeepWaters.GetUnderwaterFogColor());

            if (material.HasProperty(WaterColumnDepthProperty))
                material.SetFloat(WaterColumnDepthProperty, Mathf.Max(1f, DeepWaters.Instance.WaterDepth));

            if (material.HasProperty(WaterColumnFogDepthProperty))
                material.SetFloat(WaterColumnFogDepthProperty, GetWaterColumnFogDepth());

            if (material.HasProperty(WaterColumnFogStrengthProperty))
                material.SetFloat(WaterColumnFogStrengthProperty, GetWaterColumnFogStrength());
        }

        private static void ConfigureTransparentMaterial(Material material)
        {
            material.SetOverrideTag("RenderType", TransparentRenderType);
            material.renderQueue = (int)RenderQueue.Transparent;

            if (material.HasProperty(SrcBlendProperty))
                material.SetInt(SrcBlendProperty, (int)BlendMode.SrcAlpha);

            if (material.HasProperty(DstBlendProperty))
                material.SetInt(DstBlendProperty, (int)BlendMode.OneMinusSrcAlpha);

            if (material.HasProperty(ZWriteProperty))
                material.SetInt(ZWriteProperty, 0);

            material.DisableKeyword(KeyWords.CutOut);
            material.EnableKeyword(KeyWords.Fade);
            material.DisableKeyword(KeyWords.Transparent);
        }

        private static void ApplyBaseTexture(Material material)
        {
            Texture surfaceTexture = GetSurfaceTexture();
            if (surfaceTexture == null)
            {
                material.color = FallbackSurfaceColor;
                return;
            }

            material.mainTexture = surfaceTexture;
            material.mainTextureScale = new Vector2(SurfaceTextureTiling, SurfaceTextureTiling);
        }

        private static Texture2D LoadWaterTexture()
        {
            if (DaggerfallUnity.Instance == null ||
                DaggerfallUnity.Instance.MaterialReader == null ||
                DaggerfallUnity.Instance.MaterialReader.TextureReader == null)
            {
                return null;
            }

            return DaggerfallUnity.Instance.MaterialReader.TextureReader.GetTexture2D(302, 0, 0);
        }

        private static void ApplyWaterTextureSettings(Texture texture)
        {
            if (texture == null)
                return;

            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Point;
        }

        private static float GetWaterColumnFogDepth()
        {
            return Mathf.Max(
                2f,
                DeepWaters.Instance.WaterDepth * DeepWaters.Instance.UnderwaterFogDistanceMultiplier);
        }

        private static float GetWaterColumnFogStrength()
        {
            return Mathf.Clamp01(DeepWaters.Instance.UnderwaterFogStrength);
        }

        internal static void GetSurfaceOpaqueFadeRange(out float fadeStart, out float fadeEnd)
        {
            // Retained for older integrations that still probe this helper.
            // The split top/underside shaders do not use distance opacity.
            fadeStart = LegacyOpaqueFadeDisabledStart;
            fadeEnd = LegacyOpaqueFadeDisabledStart + 1f;
        }
    }
}
