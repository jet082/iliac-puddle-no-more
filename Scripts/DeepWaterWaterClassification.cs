// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Shared answer to "does this terrain sample visually represent water?"
    /// Prefer DFU's promoted tilemap when available because it includes
    /// marching-square shore transition tiles, then fall back to height samples.
    /// </summary>
    internal static class DeepWaterWaterClassification
    {
        private const float WaterThresholdEpsilon = 1e-5f;
        private const float CarveWaterHeadroomMeters = 0.25f;

        public static bool MapDataHasWater(MapPixelData mapData)
        {
            if (TilemapHasWater(mapData.tilemapSamples, mapData.heightmapSamples))
                return true;

            return HeightmapHasWater(mapData.heightmapSamples);
        }

        public static bool IsLocalPointWater(MapPixelData mapData, float fracX, float fracZ)
        {
            float carveWaterThreshold;
            if (!TryGetCarveWaterThreshold(out carveWaterThreshold))
                return false;

            if (TilemapPointHasWater(mapData.tilemapSamples, mapData.heightmapSamples, fracX, fracZ, carveWaterThreshold))
                return true;

            return HeightmapPointBelowThreshold(mapData.heightmapSamples, fracX, fracZ, carveWaterThreshold);
        }

        // True if any tilemap sample inside this cell is the PURE water tile
        // (record 0 in the low 6 bits; rotation variants in the high bits) —
        // the exact texel set the terrain water-texel clip shader discards.
        // The water surface mesh uses this on clipped tiles so the visible
        // film covers precisely the area whose painted vanilla water was
        // removed; anything narrower leaves a bare seabed band along shores.
        public static bool CellContainsPureWaterTile(MapPixelData mapData, int cellX, int cellY, int cellResolution)
        {
            byte[,] tilemap = mapData.tilemapSamples;
            if (tilemap == null || cellResolution <= 0)
                return false;

            int rows = tilemap.GetLength(0);
            int cols = tilemap.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return false;

            int x0 = Mathf.Clamp(cellX * cols / cellResolution, 0, cols - 1);
            int x1 = Mathf.Clamp(((cellX + 1) * cols - 1) / cellResolution, 0, cols - 1);
            int y0 = Mathf.Clamp(cellY * rows / cellResolution, 0, rows - 1);
            int y1 = Mathf.Clamp(((cellY + 1) * rows - 1) / cellResolution, 0, rows - 1);

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if ((tilemap[y, x] & 0x3F) == 0)
                        return true;
                }
            }

            return false;
        }


        // True if ANY heightmap sample in this cell is at/below ocean level —
        // i.e. part of the cell is underwater, so the water film must cover it
        // or the waterline shows a bare band where ground dips under the
        // surface (the shore gaps). Ground above the film occludes it via the
        // depth test, so partial coverage cannot paint water onto dry land.
        public static bool IsCellPartiallySubmerged(MapPixelData mapData, int cellX, int cellY, int cellResolution)
        {
            float waterThreshold;
            if (!TryGetWaterThreshold(out waterThreshold))
                return false;

            return HeightmapCellBelowThreshold(
                mapData.heightmapSamples, cellX, cellY, cellResolution, waterThreshold);
        }

        // True only if EVERY heightmap sample in this cell is at/below ocean
        // level — i.e. the whole cell is submerged, so a sea-level water plane
        // sits ABOVE the terrain everywhere in it. The stenciled water surface
        // uses this (mirroring the carve's all-corners-submerged gate) so it
        // never renders a water film over a shoreline cell whose terrain pokes
        // above the waterline — the "0-depth water above land" artifact. Uses
        // the strict ocean threshold (no shore headroom, no shore tiles).
        public static bool IsCellFullySubmerged(MapPixelData mapData, int cellX, int cellY, int cellResolution)
        {
            float waterThreshold;
            if (!TryGetWaterThreshold(out waterThreshold))
                return false;

            return HeightmapCellFullyBelowThreshold(
                mapData.heightmapSamples, cellX, cellY, cellResolution, waterThreshold);
        }

        private static bool TilemapHasWater(byte[,] tilemap, float[,] heights)
        {
            if (tilemap == null)
                return false;

            int rows = tilemap.GetLength(0);
            int cols = tilemap.GetLength(1);
            bool requireLowTerrain = heights != null;
            float visualWaterThreshold = 0f;
            if (requireLowTerrain && !TryGetVisualWaterThreshold(out visualWaterThreshold))
                requireLowTerrain = false;

            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    if (!TileValueContainsWater(tilemap[y, x]))
                        continue;

                    if (!requireLowTerrain ||
                        HeightmapCellBelowThreshold(heights, x, y, cols, visualWaterThreshold))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TilemapPointHasWater(
            byte[,] tilemap,
            float[,] heights,
            float fracX,
            float fracZ,
            float lowTerrainThreshold)
        {
            if (tilemap == null)
                return false;

            int rows = tilemap.GetLength(0);
            int cols = tilemap.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return false;

            int x = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(fracX) * cols), 0, cols - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(fracZ) * rows), 0, rows - 1);
            if (!TileValueContainsWater(tilemap[y, x]))
                return false;

            return heights == null ||
                   HeightmapPointBelowThreshold(heights, fracX, fracZ, lowTerrainThreshold);
        }

        private static bool HeightmapHasWater(float[,] heights)
        {
            if (heights == null)
                return false;

            float waterThreshold;
            if (!TryGetWaterThreshold(out waterThreshold))
                return false;

            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    if (heights[y, x] <= waterThreshold)
                        return true;
                }
            }

            return false;
        }

        private static bool HeightmapPointBelowThreshold(
            float[,] heights,
            float fracX,
            float fracZ,
            float threshold)
        {
            if (heights == null)
                return false;

            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return false;

            int x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(fracX) * (cols - 1)), 0, cols - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(fracZ) * (rows - 1)), 0, rows - 1);
            return heights[y, x] <= threshold;
        }

        private static bool HeightmapCellBelowThreshold(
            float[,] heights,
            int cellX,
            int cellY,
            int cellResolution,
            float threshold)
        {
            if (heights == null || cellResolution <= 0)
                return false;

            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return false;

            int x0 = Mathf.Clamp(Mathf.FloorToInt(cellX * (cols - 1) / (float)cellResolution), 0, cols - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((cellX + 1) * (cols - 1) / (float)cellResolution), 0, cols - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(cellY * (rows - 1) / (float)cellResolution), 0, rows - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt((cellY + 1) * (rows - 1) / (float)cellResolution), 0, rows - 1);

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (heights[y, x] <= threshold)
                        return true;
                }
            }

            return false;
        }

        // Opposite sense of HeightmapCellBelowThreshold: true only if NO sample
        // in the cell rises above the threshold (the whole cell is submerged).
        private static bool HeightmapCellFullyBelowThreshold(
            float[,] heights,
            int cellX,
            int cellY,
            int cellResolution,
            float threshold)
        {
            if (heights == null || cellResolution <= 0)
                return false;

            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return false;

            int x0 = Mathf.Clamp(Mathf.FloorToInt(cellX * (cols - 1) / (float)cellResolution), 0, cols - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((cellX + 1) * (cols - 1) / (float)cellResolution), 0, cols - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(cellY * (rows - 1) / (float)cellResolution), 0, rows - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt((cellY + 1) * (rows - 1) / (float)cellResolution), 0, rows - 1);

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (heights[y, x] > threshold)
                        return false;
                }
            }

            return true;
        }

        private static bool TryGetWaterThreshold(out float waterThreshold)
        {
            waterThreshold = 0f;
            DaggerfallUnity dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.TerrainSampler == null)
                return false;

            waterThreshold = dfu.TerrainSampler.OceanElevation /
                             dfu.TerrainSampler.MaxTerrainHeight +
                             WaterThresholdEpsilon;
            return true;
        }

        private static bool TryGetCarveWaterThreshold(out float waterThreshold)
        {
            waterThreshold = 0f;
            DaggerfallUnity dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.TerrainSampler == null)
                return false;

            waterThreshold = (dfu.TerrainSampler.OceanElevation + CarveWaterHeadroomMeters) /
                             dfu.TerrainSampler.MaxTerrainHeight;
            return true;
        }

        private static bool TryGetVisualWaterThreshold(out float waterThreshold)
        {
            waterThreshold = 0f;
            DaggerfallUnity dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.TerrainSampler == null)
                return false;

            waterThreshold = dfu.TerrainSampler.BeachElevation /
                             dfu.TerrainSampler.MaxTerrainHeight;
            return true;
        }

        private static bool TileValueContainsWater(byte tile)
        {
            int index = tile & 0x3f;
            return index == 0 ||
                   (index >= 5 && index <= 7) ||
                   index == 48;
        }
    }
}
