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
        // World-meters per texture tile. DFU terrain dirt tile reads as one
        // tile every ~6.4m of world space, so 1/6.4 = 0.15625.
        private const float TerrainTextureWorldScale = 0.15625f;
        private const float TerrainTextureStrength = 0.45f;
		private const float DfuGroundTextureStrength = 0.25f;
		private const float SwampGroundTextureStrength = 0.90f;

        private static readonly int MainTexProperty = Shader.PropertyToID("_MainTex");
		private static readonly int SandColorProperty = Shader.PropertyToID("_SandColor");
		private static readonly int MidColorProperty = Shader.PropertyToID("_MidColor");
		private static readonly int DeepColorProperty = Shader.PropertyToID("_DeepColor");
		private static readonly int SwampColorProperty = Shader.PropertyToID("_SwampColor");
        private static readonly int TextureWorldScaleProperty = Shader.PropertyToID("_TextureWorldScale");
        private static readonly int TextureStrengthProperty = Shader.PropertyToID("_TextureStrength");
        private static readonly Dictionary<int, Material> materials = new Dictionary<int, Material>();

        public static Material GetMaterial(int worldClimate)
        {
			int textureArchive;
			int textureRecord;
			ResolveSeafloorTexture(worldClimate, out textureArchive, out textureRecord);
			int key = textureArchive * 1024 + textureRecord * 16 + worldClimate;
            Material material;
            if (materials.TryGetValue(key, out material) && material != null && material.shader != null)
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
				material.name = "DeepWaters.Seafloor." + textureArchive + "." + textureRecord + "." + worldClimate;
				bool dfuGroundTexture = UsesDfuGroundTexture(worldClimate);
				float textureStrength = worldClimate == (int)MapsFile.Climates.Swamp ? SwampGroundTextureStrength :
					(dfuGroundTexture ? DfuGroundTextureStrength : TerrainTextureStrength);
				ApplyRegionalTexture(material, textureArchive, textureRecord, textureStrength, TerrainTextureWorldScale);
				if (!dfuGroundTexture || worldClimate == (int)MapsFile.Climates.Swamp)
					ApplyBiomePalette(material, worldClimate);
                materials[key] = material;
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[DeepWaters] Failed to construct seafloor material: " + ex.Message);
                material = null;
            }

            return material;
        }

		private static void ApplyBiomePalette(Material material, int worldClimate)
		{
			switch (worldClimate)
			{
				case (int)MapsFile.Climates.Subtropical:
				case (int)MapsFile.Climates.Rainforest:
					SetPalette(material,
						new Color(0.52f, 0.67f, 0.49f, 1f),
						new Color(0.24f, 0.42f, 0.32f, 1f),
						new Color(0.08f, 0.20f, 0.20f, 1f),
						new Color(0.16f, 0.32f, 0.22f, 1f));
					break;
				case (int)MapsFile.Climates.Swamp:
					SetPalette(material,
						new Color(0.38f, 0.36f, 0.21f, 1f),
						new Color(0.23f, 0.25f, 0.15f, 1f),
						new Color(0.08f, 0.12f, 0.09f, 1f),
						new Color(0.18f, 0.17f, 0.09f, 1f));
					break;
				case (int)MapsFile.Climates.Mountain:
				case (int)MapsFile.Climates.MountainWoods:
					SetPalette(material,
						new Color(0.58f, 0.63f, 0.61f, 1f),
						new Color(0.34f, 0.43f, 0.47f, 1f),
						new Color(0.12f, 0.18f, 0.24f, 1f),
						new Color(0.24f, 0.30f, 0.32f, 1f));
					break;
				case (int)MapsFile.Climates.Desert:
				case (int)MapsFile.Climates.Desert2:
					SetPalette(material,
						new Color(0.78f, 0.65f, 0.42f, 1f),
						new Color(0.50f, 0.39f, 0.22f, 1f),
						new Color(0.23f, 0.18f, 0.13f, 1f),
						new Color(0.45f, 0.34f, 0.18f, 1f));
					break;
				case (int)MapsFile.Climates.Woodlands:
				case (int)MapsFile.Climates.HauntedWoodlands:
					SetPalette(material,
						new Color(0.58f, 0.62f, 0.45f, 1f),
						new Color(0.33f, 0.39f, 0.28f, 1f),
						new Color(0.13f, 0.18f, 0.15f, 1f),
						new Color(0.24f, 0.30f, 0.19f, 1f));
					break;
				default:
					SetPalette(material,
						new Color(0.30f, 0.42f, 0.43f, 1f),
						new Color(0.16f, 0.23f, 0.26f, 1f),
						new Color(0.06f, 0.08f, 0.11f, 1f),
						new Color(0.12f, 0.18f, 0.16f, 1f));
					break;
			}
		}

		private static void SetPalette(Material material, Color sand, Color mid, Color deep, Color swamp)
		{
			material.SetColor(SandColorProperty, sand);
			material.SetColor(MidColorProperty, mid);
			material.SetColor(DeepColorProperty, deep);
			material.SetColor(SwampColorProperty, swamp);
		}

		private static void ResolveSeafloorTexture(int worldClimate, out int archive, out int record)
		{
			if (UsesDfuGroundTexture(worldClimate))
			{
				archive = ResolveGroundArchive(worldClimate);
				record = 1;
				return;
			}

			switch (worldClimate)
			{
				case (int)MapsFile.Climates.Subtropical:
				case (int)MapsFile.Climates.Rainforest:
					archive = 402; record = 28; return;
				case (int)MapsFile.Climates.Mountain:
				case (int)MapsFile.Climates.MountainWoods:
					archive = 102; record = 3; return;
				case (int)MapsFile.Climates.Desert:
				case (int)MapsFile.Climates.Desert2:
					archive = 2; record = 10; return;
				case (int)MapsFile.Climates.HauntedWoodlands:
					archive = 302; record = 3; return;
				case (int)MapsFile.Climates.Woodlands:
					archive = 302; record = 25; return;
				default:
					DFLocation.ClimateSettings climate = MapsFile.GetWorldClimateSettings(worldClimate);
					archive = climate.GroundArchive;
					record = 1;
					return;
			}
		}

		private static bool UsesDfuGroundTexture(int worldClimate)
		{
			return worldClimate == (int)MapsFile.Climates.Ocean ||
				worldClimate == (int)MapsFile.Climates.Swamp;
		}

		private static int ResolveGroundArchive(int worldClimate)
		{
			DFLocation.ClimateSettings climate = MapsFile.GetWorldClimateSettings(worldClimate);
			int groundArchive = climate.GroundArchive;

			if (climate.ClimateType != DFLocation.ClimateBaseType.Desert &&
				DaggerfallUnity.Instance != null &&
				DaggerfallUnity.Instance.WorldTime != null &&
				DaggerfallUnity.Instance.WorldTime.Now.SeasonValue == DaggerfallDateTime.Seasons.Winter)
			{
				groundArchive++;
			}

			return groundArchive;
		}

        private static void ApplyRegionalTexture(Material material, int textureArchive, int textureRecord, float textureStrength, float textureWorldScale)
        {
            if (material == null || DaggerfallUnity.Instance == null || DaggerfallUnity.Instance.MaterialReader == null)
                return;

            try
            {
                Texture2D texture = DaggerfallUnity.Instance.MaterialReader.TextureReader.GetTexture2D(
                    textureArchive,
                    textureRecord,
                    0);

                if (texture == null)
                    return;

                texture.wrapMode = TextureWrapMode.Repeat;
                texture.filterMode = FilterMode.Point;

                material.SetTexture(MainTexProperty, texture);
                material.SetFloat(TextureWorldScaleProperty, textureWorldScale);
                material.SetFloat(TextureStrengthProperty, textureStrength);
            }
            catch (System.Exception ex)
            {
				Debug.LogWarning("[DeepWaters] Failed to load regional seafloor texture " + textureArchive + ":" + textureRecord + ": " + ex.Message);
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
