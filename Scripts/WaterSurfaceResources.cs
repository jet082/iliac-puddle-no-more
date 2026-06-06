// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using UnityEngine;
using UnityEngine.Rendering;

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
        private const string TransparentRenderType = "Transparent";

        private static Mesh sharedFlatMesh;
        private static Material sharedWaterMaterial;
        private static Texture sharedSurfaceTexture;
        private static readonly Color SurfaceTint = new Color(0.34f, 0.55f, 0.58f, 1f);
        private static readonly Color OpaqueSurfaceColor = new Color(0.045f, 0.22f, 0.30f, 1f);
        public const float SurfaceTextureTiling = 128f;
        private const float LegacyOpaqueFadeDisabledStart = 100000f;

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

        public static Texture GetSurfaceTexture()
        {
            if (sharedWaterMaterial != null && sharedWaterMaterial.mainTexture != null)
                return sharedWaterMaterial.mainTexture;

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
            if (sharedWaterMaterial == null || DeepWaters.Instance == null)
                return;

            sharedWaterMaterial.SetOverrideTag("RenderType", TransparentRenderType);
            sharedWaterMaterial.renderQueue = (int)RenderQueue.Transparent;

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
            // In the editor/project workspace, prefer the source shader so
            // visual iteration is not pinned to an older dfmod-bundled asset.
            Shader shader = Shader.Find("DeepWaters/StenciledWaterSurface");

            if (DeepWaters.Mod != null)
            {
                Shader bundled = DeepWaters.Mod.GetAsset<Shader>("StenciledWaterSurface.shader");
                if (shader == null)
                    shader = bundled;
            }

            if (shader == null)
                Debug.LogError("[DeepWaters] DeepWaters/StenciledWaterSurface shader not found. Water surfaces will not render.");

            return shader;
        }

        private static void ApplyBaseTexture(Material material)
        {
            Texture2D waterTex = LoadWaterTexture();
            if (waterTex == null)
            {
                material.color = OpaqueSurfaceColor;
                return;
            }

            ApplyWaterTextureSettings(waterTex);
            material.mainTexture = waterTex;
            material.mainTextureScale = new Vector2(SurfaceTextureTiling, SurfaceTextureTiling);
            sharedSurfaceTexture = waterTex;
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
            // Legacy bundled versions of StenciledWaterSurface used these
            // values to lerp the top surface back to full opacity with view
            // distance. Keep the properties effectively disabled so top
            // transparency is controlled by _Color.a even if that older shader
            // asset is the one Unity resolves at runtime.
            fadeStart = LegacyOpaqueFadeDisabledStart;
            fadeEnd = LegacyOpaqueFadeDisabledStart + 1f;
        }
    }
}
