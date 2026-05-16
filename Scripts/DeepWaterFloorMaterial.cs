// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Shared seafloor material loader. Materials are cached per regional
    /// ground archive so the floor inherits the current climate/season
    /// terrain texture (DREAM-style replacements automatically picked up
    /// via DFU's TextureReader pipeline).
    /// </summary>
    internal static class DeepWaterFloorMaterial
    {
        private const string ShaderName = "DeepWaters/Seafloor";
        private const int SeafloorTextureRecord = 1;
        // World-meters per texture tile. DFU terrain dirt tile reads as one
        // tile every ~6.4m of world space, so 1/6.4 = 0.15625.
        private const float TerrainTextureWorldScale = 0.15625f;

        private static readonly int MainTexProperty = Shader.PropertyToID("_MainTex");
        private static readonly int TextureWorldScaleProperty = Shader.PropertyToID("_TextureWorldScale");
        private static readonly Dictionary<int, Material> materials = new Dictionary<int, Material>();

        public static Material GetMaterial()
        {
            return GetMaterial(MapsFile.DefaultClimate);
        }

        public static Material GetMaterial(int worldClimate)
        {
            int groundArchive = ResolveGroundArchive(worldClimate);
            Material material;
            if (materials.TryGetValue(groundArchive, out material) && material != null && material.shader != null)
                return material;

            Shader shader = ResolveShaderWithFallbacks();
            if (shader == null)
            {
                Debug.LogError("[DeepWaters] Could not resolve any shader for seafloor mesh.");
                return null;
            }

            try
            {
                material = new Material(shader);
                material.name = "DeepWaters.Seafloor." + groundArchive;
                ApplyRegionalTexture(material, groundArchive);
                materials[groundArchive] = material;
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[DeepWaters] Failed to construct seafloor material: " + ex.Message);
                material = null;
            }

            return material;
        }

        private static int ResolveGroundArchive(int worldClimate)
        {
            DFLocation.ClimateSettings climate = MapsFile.GetWorldClimateSettings(worldClimate);
            int groundArchive = climate.GroundArchive;

            // Winter variant lives at GroundArchive + 1 for every climate
            // except Desert (no snow). Match DFU's terrain-material rule so
            // our seafloor stays consistent with the surrounding land.
            if (climate.ClimateType != DFLocation.ClimateBaseType.Desert &&
                DaggerfallUnity.Instance != null &&
                DaggerfallUnity.Instance.WorldTime != null &&
                DaggerfallUnity.Instance.WorldTime.Now.SeasonValue == DaggerfallDateTime.Seasons.Winter)
            {
                groundArchive++;
            }

            return groundArchive;
        }

        private static void ApplyRegionalTexture(Material material, int groundArchive)
        {
            if (material == null || DaggerfallUnity.Instance == null || DaggerfallUnity.Instance.MaterialReader == null)
                return;

            try
            {
                Texture2D texture = DaggerfallUnity.Instance.MaterialReader.TextureReader.GetTexture2D(
                    groundArchive,
                    SeafloorTextureRecord,
                    0);

                if (texture == null)
                    return;

                texture.wrapMode = TextureWrapMode.Repeat;
                texture.filterMode = FilterMode.Point;

                material.SetTexture(MainTexProperty, texture);
                material.SetFloat(TextureWorldScaleProperty, TerrainTextureWorldScale);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[DeepWaters] Failed to load regional seafloor texture from archive " + groundArchive + ": " + ex.Message);
            }
        }

        private static Shader ResolveShaderWithFallbacks()
        {
            Shader shader = TryLoadShader();
            if (shader != null) return shader;

            string[] fallbacks =
            {
                "Legacy Shaders/Diffuse",
                "Diffuse",
                "Standard",
                "Unlit/Texture",
                "Unlit/Color",
            };
            for (int i = 0; i < fallbacks.Length; i++)
            {
                shader = Shader.Find(fallbacks[i]);
                if (shader != null)
                {
                    Debug.LogWarning("[DeepWaters] Seafloor shader unavailable; using fallback " + fallbacks[i]);
                    return shader;
                }
            }

            return null;
        }

        private static Shader TryLoadShader()
        {
            if (DeepWaters.Mod != null)
            {
                Shader shader = DeepWaters.Mod.GetAsset<Shader>("DeepWaterSeafloor");
                if (shader != null) return shader;
                shader = DeepWaters.Mod.GetAsset<Shader>("DeepWaterSeafloor.shader");
                if (shader != null) return shader;
            }

            return Shader.Find(ShaderName);
        }
    }
}
