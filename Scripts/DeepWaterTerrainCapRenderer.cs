// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// The vanilla terrain is never carved (real Unity terrain holes
    /// native-crash Unity 2019.4), so its sea-level heightmap renderer would
    /// sit as an opaque cap over fish, decorations, water surfaces, and the
    /// generated seafloor. On pure ocean map pixels that renderer can safely
    /// be hidden so the generated Deep Waters meshes own the visual scene.
    /// </summary>
    internal static class DeepWaterTerrainCapRenderer
    {
        private sealed class HiddenCapMarker : MonoBehaviour
        {
            public bool HasOriginalDrawHeightmap;
            public bool OriginalDrawHeightmap;
            public bool Hidden;
            public Shader OriginalShader;
            public Texture OriginalTilemapTexture;
            public Texture2D PatchedTilemapTexture;
            public bool WaterTexelsClipped;
        }

        private static readonly int TilemapTexProperty = Shader.PropertyToID("_TilemapTex");
        private static Shader tilemapTextureArrayClipShader;
        private static Shader tilemapClipShader;
        private static bool clipShadersResolved;
        private static bool loggedUnknownTerrainShader;

        public static void Apply(DaggerfallTerrain dfTerrain, bool hide)
        {
            if (dfTerrain == null)
                return;

            Terrain terrain = dfTerrain.GetComponent<Terrain>();
            if (terrain == null)
                return;

            if (!hide)
            {
                Restore(dfTerrain);
                return;
            }

            HiddenCapMarker marker = dfTerrain.GetComponent<HiddenCapMarker>();
            if (marker == null)
                marker = dfTerrain.gameObject.AddComponent<HiddenCapMarker>();

            if (!marker.HasOriginalDrawHeightmap)
            {
                marker.OriginalDrawHeightmap = terrain.drawHeightmap;
                marker.HasOriginalDrawHeightmap = true;
            }

            terrain.drawHeightmap = false;
            marker.Hidden = true;
        }

        public static void Restore(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null)
                return;

            ApplyWaterTexelClip(dfTerrain, false);

            HiddenCapMarker marker = dfTerrain.GetComponent<HiddenCapMarker>();
            if (marker == null || !marker.Hidden)
                return;

            Terrain terrain = dfTerrain.GetComponent<Terrain>();
            if (terrain != null && marker.HasOriginalDrawHeightmap)
                terrain.drawHeightmap = marker.OriginalDrawHeightmap;

            marker.Hidden = false;
        }

        /// <summary>
        /// Render-side water hole punching for MIXED land/water tiles, where the
        /// whole-renderer cap hide is not an option (the land must render).
        /// Swaps the tile's terrain material to a copy of DFU's terrain shader
        /// that clips pure-water texels, so the sea-level painted water
        /// disappears and the carved seafloor below shows through the
        /// transparent water surface. The material instance is per-tile (DFU's
        /// TerrainMaterialProvider creates one per terrain), so only the shader
        /// reference is swapped and all textures/properties are preserved.
        /// </summary>
        public static void ApplyWaterTexelClip(DaggerfallTerrain dfTerrain, bool clip)
        {
            if (dfTerrain == null)
                return;

            Terrain terrain = dfTerrain.GetComponent<Terrain>();
            Material material = terrain != null ? terrain.materialTemplate : null;
            if (material == null || material.shader == null)
                return;

            HiddenCapMarker marker = dfTerrain.GetComponent<HiddenCapMarker>();

            if (!clip)
            {
                if (marker != null && marker.WaterTexelsClipped)
                {
                    if (marker.OriginalShader != null)
                        material.shader = marker.OriginalShader;
                    RestoreTilemapTexture(material, marker);
                    marker.WaterTexelsClipped = false;
                }
                return;
            }

            if (marker != null && marker.WaterTexelsClipped)
            {
                ApplyTilemapTextureClip(dfTerrain, material, marker);
                return;
            }

            string originalShaderName = material.shader.name;
            Shader clipShader = ResolveClipShader(originalShaderName);
            if (clipShader == null)
                return;

            if (marker == null)
                marker = dfTerrain.gameObject.AddComponent<HiddenCapMarker>();

            marker.OriginalShader = material.shader;
            material.shader = clipShader;
            marker.WaterTexelsClipped = true;
            ApplyTilemapTextureClip(dfTerrain, material, marker);
        }

        private static void ApplyTilemapTextureClip(DaggerfallTerrain dfTerrain, Material material, HiddenCapMarker marker)
        {
            if (dfTerrain == null || material == null || marker == null)
                return;

            Color32[] source = dfTerrain.TileMap;
            if (source == null || source.Length == 0)
                return;

            int dim = Mathf.RoundToInt(Mathf.Sqrt(source.Length));
            if (dim <= 0 || dim * dim != source.Length)
                return;

            bool textureArray =
                (marker.OriginalShader != null && marker.OriginalShader.name == "Daggerfall/TilemapTextureArray") ||
                (material.shader != null && material.shader.name == "DeepWaters/TilemapTextureArrayClipWater");

            var pixels = new Color32[source.Length];
            bool changed = false;
            for (int z = 0; z < dim; z++)
            {
                for (int x = 0; x < dim; x++)
                {
                    int i = z * dim + x;
                    Color32 color = source[i];
					bool waterTexel = IsClippedWaterTileData(color.a, textureArray);
					if (waterTexel && ShouldClipPromotedWaterTexel(dfTerrain, color.a, textureArray, x, z, dim))
					{
						color.r = 255;
						color.g = 0;
						color.b = 255;
						color.a = 0;
						changed = true;
					}
					else if (waterTexel && TryFindNearestSolidTexel(source, textureArray, x, z, dim, out color))
					{
						changed = true;
					}
					pixels[i] = color;
				}
			}

            if (!changed)
                return;

            if (marker.OriginalTilemapTexture == null)
                marker.OriginalTilemapTexture = material.GetTexture(TilemapTexProperty);

            if (marker.PatchedTilemapTexture != null)
                Object.Destroy(marker.PatchedTilemapTexture);

            Texture2D texture = new Texture2D(dim, dim, TextureFormat.RGBA32, false);
            texture.name = "DeepWaters_ClippedTilemap_" + dfTerrain.MapPixelX + "_" + dfTerrain.MapPixelY;
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.SetPixels32(pixels);
            texture.Apply(false, true);

			marker.PatchedTilemapTexture = texture;
			material.SetTexture(TilemapTexProperty, texture);
		}

		private static bool TryFindNearestSolidTexel(
			Color32[] source,
			bool textureArray,
			int texelX,
			int texelZ,
			int dim,
			out Color32 replacement)
		{
			replacement = default(Color32);
			if (source == null || dim <= 0)
				return false;

			const int maxRadius = 8;
			for (int radius = 1; radius <= maxRadius; radius++)
			{
				for (int dz = -radius; dz <= radius; dz++)
				{
					for (int dx = -radius; dx <= radius; dx++)
					{
						if (Mathf.Abs(dx) != radius && Mathf.Abs(dz) != radius)
							continue;

						int x = texelX + dx;
						int z = texelZ + dz;
						if (x < 0 || z < 0 || x >= dim || z >= dim)
							continue;

						Color32 candidate = source[z * dim + x];
						if (!IsClippedWaterTileData(candidate.a, textureArray))
						{
							replacement = candidate;
							return true;
						}
					}
				}
			}

			return false;
		}

		private static void RestoreTilemapTexture(Material material, HiddenCapMarker marker)
        {
            if (material != null && marker.OriginalTilemapTexture != null)
                material.SetTexture(TilemapTexProperty, marker.OriginalTilemapTexture);

            if (marker.PatchedTilemapTexture != null)
            {
                Object.Destroy(marker.PatchedTilemapTexture);
                marker.PatchedTilemapTexture = null;
            }

            marker.OriginalTilemapTexture = null;
        }

        internal static bool IsClippedWaterTileData(byte tileData, bool textureArray)
        {
            int tileIndex = textureArray ? tileData >> 2 : tileData & 0x3f;
            return tileIndex == 0 ||
                   (tileIndex >= 5 && tileIndex <= 7) ||
                   tileIndex == 48;
        }

        internal static bool ShouldClipPromotedWaterTexel(
            DaggerfallTerrain terrain,
            byte tileData,
            bool textureArray,
            int texelX,
            int texelZ,
            int texelDim)
        {
            return IsClippedWaterTileData(tileData, textureArray) &&
                   IsPromotedTerrainTexelSafeToClip(terrain, texelX, texelZ, texelDim);
        }

        private static bool IsPromotedTerrainTexelSafeToClip(
            DaggerfallTerrain terrain,
            int texelX,
            int texelZ,
            int texelDim)
        {
            if (terrain == null || terrain.MapData.heightmapSamples == null || texelDim <= 0)
                return true;

            DaggerfallUnity dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.TerrainSampler == null)
                return true;

            float threshold = dfu.TerrainSampler.OceanElevation /
                              dfu.TerrainSampler.MaxTerrainHeight + 1e-5f;
            float[,] heights = terrain.MapData.heightmapSamples;
            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return true;

            int x0 = Mathf.Clamp(Mathf.FloorToInt(texelX * (cols - 1) / (float)texelDim), 0, cols - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((texelX + 1) * (cols - 1) / (float)texelDim), 0, cols - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(texelZ * (rows - 1) / (float)texelDim), 0, rows - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((texelZ + 1) * (rows - 1) / (float)texelDim), 0, rows - 1);

            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (heights[z, x] > threshold)
                        return false;
                }
            }

            return true;
        }

        private static Shader ResolveClipShader(string currentShaderName)
        {
            if (!clipShadersResolved)
            {
                clipShadersResolved = true;
                tilemapTextureArrayClipShader = LoadModShader(
                    "DeepWaters/TilemapTextureArrayClipWater",
                    "DeepWaterTilemapTextureArrayClipWater.shader");
                tilemapClipShader = LoadModShader(
                    "DeepWaters/TilemapClipWater",
                    "DeepWaterTilemapClipWater.shader");
            }

            // Already swapped (tile re-promoted while clipped).
            if (currentShaderName == "DeepWaters/TilemapTextureArrayClipWater")
                return tilemapTextureArrayClipShader;
            if (currentShaderName == "DeepWaters/TilemapClipWater")
                return tilemapClipShader;

            if (currentShaderName == "Daggerfall/TilemapTextureArray")
                return tilemapTextureArrayClipShader;
            if (currentShaderName == "Daggerfall/Tilemap")
                return tilemapClipShader;

            // Unknown terrain shader (another terrain rendering mod owns it):
            // leave it alone — the painted water cap stays on mixed tiles there.
            if (!loggedUnknownTerrainShader)
            {
                loggedUnknownTerrainShader = true;
                Debug.Log("[DeepWaters.Cap] Terrain uses shader '" + currentShaderName +
                          "' — no water-texel clip variant available, so the sea-level " +
                          "water cap stays visible on mixed land/water map pixels.");
            }

            return null;
        }

        private static Shader LoadModShader(string shaderName, string assetName)
        {
            Shader shader = Shader.Find(shaderName);

            if (shader == null && DeepWaters.Mod != null)
                shader = DeepWaters.Mod.GetAsset<Shader>(assetName);

            if (shader == null)
                Debug.LogWarning("[DeepWaters.Cap] " + shaderName + " shader not found; " +
                                 "water-texel clipping unavailable for this terrain mode.");

            return shader;
        }

        public static bool ShouldHidePureOceanCap(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null)
                return false;

            if (!DeepWaterDistanceBake.IsLoaded ||
                !DeepWaterDistanceBake.MapPixelHasWaterCells(dfTerrain.MapPixelX, dfTerrain.MapPixelY))
            {
                return false;
            }

            if (DeepWaterDistanceBake.MapPixelHasLandCells(dfTerrain.MapPixelX, dfTerrain.MapPixelY))
                return false;

            return DeepWaterWaterClassification.MapDataHasWater(dfTerrain.MapData) &&
                   DeepWaterWaterClassification.MapDataFullySubmerged(dfTerrain.MapData);
        }
    }
}
