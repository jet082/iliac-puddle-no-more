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

        private static readonly int ColorProperty = Shader.PropertyToID("_Color");
        private static readonly int UndersideAlphaProperty = Shader.PropertyToID("_UndersideAlpha");
        private static readonly int UnderwaterFogColorProperty = Shader.PropertyToID("_UnderwaterFogColor");
        private static readonly int WaterColumnDepthProperty = Shader.PropertyToID("_WaterColumnDepth");
        private static readonly int WaterColumnFogDepthProperty = Shader.PropertyToID("_WaterColumnFogDepth");
        private static readonly int WaterColumnFogStrengthProperty = Shader.PropertyToID("_WaterColumnFogStrength");
        private static readonly int WaterSurfaceVisionDistanceProperty = Shader.PropertyToID("_WaterSurfaceVisionDistance");
        private static readonly int WaterSurfaceFalloffProperty = Shader.PropertyToID("_WaterSurfaceFalloff");
        private static readonly int SurfaceOpaqueFadeStartProperty = Shader.PropertyToID("_SurfaceOpaqueFadeStart");
        private static readonly int SurfaceOpaqueFadeEndProperty = Shader.PropertyToID("_SurfaceOpaqueFadeEnd");
        private static readonly int HorizonColorProperty = Shader.PropertyToID("_HorizonColor");
        private static readonly int SrcBlendProperty = Uniforms.SrcBlend;
        private static readonly int DstBlendProperty = Uniforms.DstBlend;
        private static readonly int ZWriteProperty = Uniforms.ZWrite;

        private static Material sharedTopMaterial;
        private static Material sharedUndersideMaterial;
        private static Texture sharedSurfaceTexture;
        private static readonly Color SurfaceTint = new Color(0.519f, 0.527f, 0.467f, 1f);
        private static readonly Color FallbackSurfaceColor = new Color(0.075f, 0.24f, 0.38f, 1f);
        private const float TopSurfaceVisionMultiplier = 3.0f;
        private const float TopSurfaceFogStrengthMultiplier = 0.35f;
        private const float TopSurfaceFalloffMultiplier = 0.45f;

        public const float SurfaceTextureTiling = 128f;

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

        // Both surfaces stay enabled even at full transparency: the shaders
        // carry the opaque horizon curtain (the void fix) and clip their own
        // invisible near-field fragments, so disabling the renderer at alpha 0
        // would remove the curtain exactly when the film is most transparent.
        public static bool IsTopSurfaceVisible()
        {
            return DeepWaters.Instance != null;
        }

        public static bool IsUndersideSurfaceVisible()
        {
            return DeepWaters.Instance != null;
        }

        // Called per frame (while underwater) so the underside's opaque
        // horizon matches the fog volume's far ambient exactly — any
        // difference reads as a band where the distant surface meets the
        // fogged void.
        public static void SetHorizonColor(Color color)
        {
            if (sharedUndersideMaterial != null && sharedUndersideMaterial.HasProperty(HorizonColorProperty))
                sharedUndersideMaterial.SetColor(HorizonColorProperty, color);

            ApplyDynamicUndersideSettings(sharedUndersideMaterial);
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

        public static void RefreshDynamicMaterialSettings()
        {
            ApplyDynamicUndersideSettings(sharedUndersideMaterial);
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

            if (material.HasProperty(WaterColumnFogStrengthProperty))
                material.SetFloat(WaterColumnFogStrengthProperty, Mathf.Clamp01(GetWaterColumnFogStrength() * TopSurfaceFogStrengthMultiplier));
            if (material.HasProperty(WaterSurfaceVisionDistanceProperty))
                material.SetFloat(WaterSurfaceVisionDistanceProperty, DeepWaters.Instance.UnderwaterVisionDistance * TopSurfaceVisionMultiplier);
            if (material.HasProperty(WaterSurfaceFalloffProperty))
                material.SetFloat(WaterSurfaceFalloffProperty, Mathf.Clamp01(DeepWaters.Instance.WaterSurfaceDistanceFalloff * TopSurfaceFalloffMultiplier));

            if (material.HasProperty(SurfaceOpaqueFadeStartProperty))
                material.SetFloat(SurfaceOpaqueFadeStartProperty, DeepWaters.Instance.UnderwaterVisionDistance * 2.2f);
            if (material.HasProperty(SurfaceOpaqueFadeEndProperty))
                material.SetFloat(SurfaceOpaqueFadeEndProperty, DeepWaters.Instance.UnderwaterVisionDistance * 4.5f);
        }

        private static void ConfigureUndersideMaterial(Material material)
        {
            if (material == null || DeepWaters.Instance == null)
                return;

            ConfigureTransparentMaterial(material);

            if (material.HasProperty(ColorProperty))
                material.SetColor(ColorProperty, SurfaceTint);

            ApplySharedWaterProperties(material);
            ApplyDynamicUndersideSettings(material);
        }

        private static void ApplyDynamicUndersideSettings(Material material)
        {
            if (material == null || DeepWaters.Instance == null)
                return;

            float shallow = GetPlayerShallowWaterFactor();
            float bottomAlpha = DeepWaters.Instance.WaterSurfaceBottomAlpha;
            if (material.HasProperty(UndersideAlphaProperty))
                material.SetFloat(UndersideAlphaProperty, Mathf.Lerp(bottomAlpha, bottomAlpha * 0.35f, shallow));

            // Near shore, a close opaque curtain reads as a hard dark strip at
            // the waterline. Keep the void guard in deep water; push it out in
            // shallow columns where terrain already closes the horizon.
            float curtainVision = DeepWaters.Instance.UnderwaterVisionDistance;
            if (material.HasProperty(SurfaceOpaqueFadeStartProperty))
                material.SetFloat(SurfaceOpaqueFadeStartProperty, curtainVision * Mathf.Lerp(1.7f, 4.0f, shallow));
            if (material.HasProperty(SurfaceOpaqueFadeEndProperty))
                material.SetFloat(SurfaceOpaqueFadeEndProperty, curtainVision * Mathf.Lerp(3.8f, 8.0f, shallow));
        }

        private static float GetPlayerShallowWaterFactor()
        {
            Vector3 position;
            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetPlayerPosition(out position) ||
                !DeepWaterWorld.TryGetWaterColumn(position.x, position.z, out column))
            {
                return 0f;
            }

            return 1f - Mathf.InverseLerp(3f, 12f, column.Depth);
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

            // Above-water seabed fade. Anchored to the underwater vision distance
            // so looking down from the surface is no clearer than looking around
            // underwater, and shortened by the (previously unused) distance
            // falloff slider.
            if (material.HasProperty(WaterSurfaceVisionDistanceProperty))
                material.SetFloat(WaterSurfaceVisionDistanceProperty, DeepWaters.Instance.UnderwaterVisionDistance);

            if (material.HasProperty(WaterSurfaceFalloffProperty))
                material.SetFloat(WaterSurfaceFalloffProperty, Mathf.Clamp01(DeepWaters.Instance.WaterSurfaceDistanceFalloff));

            // Opaque horizon curtain: the surface (both sides) is fully opaque
            // past this range, hiding the loaded-world edge (the void) behind
            // an opaque sea. Anchored to the underwater vision distance so the
            // fog-distance slider scales the horizon too.
            float curtainVision = DeepWaters.Instance.UnderwaterVisionDistance;
            if (material.HasProperty(SurfaceOpaqueFadeStartProperty))
                material.SetFloat(SurfaceOpaqueFadeStartProperty, curtainVision * 0.55f);
            if (material.HasProperty(SurfaceOpaqueFadeEndProperty))
                material.SetFloat(SurfaceOpaqueFadeEndProperty, curtainVision * 1.45f);

            // Fallback horizon color; UnderwaterDistanceFog refreshes it per
            // frame while underwater so it tracks camera depth darkening.
            if (material.HasProperty(HorizonColorProperty))
                material.SetColor(HorizonColorProperty, DeepWaters.GetUnderwaterFogColor());
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

    }
}
