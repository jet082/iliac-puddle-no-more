// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    internal static class UnderwaterDecorationPlacement
    {
        private const int SampleStride = 3;
        private const float MinimumDecorationSpacing = 5f;
        private const float SeafloorYClearance = 3f;

        public static List<UnderwaterDecorationPlacementInfo> BuildPositions(
            DaggerfallTerrain dfTerrain,
            TerrainData terrainData,
            int passes)
        {
            var positions = new List<UnderwaterDecorationPlacementInfo>();
            var spacingGrid = new Dictionary<int, List<Vector2>>();

            for (int i = 0; i < passes; i++)
                GenerateBillboardPositions(dfTerrain, terrainData, positions, spacingGrid);

            return positions;
        }

        private static void GenerateBillboardPositions(
            DaggerfallTerrain dfTerrain,
            TerrainData terrainData,
            List<UnderwaterDecorationPlacementInfo> positions,
            Dictionary<int, List<Vector2>> spacingGrid)
        {
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float sampleOcean = sampler.OceanElevation / sampler.MaxTerrainHeight;
            float[,] heights = dfTerrain.MapData.heightmapSamples;
            int hDim0 = heights.GetLength(0);
            int hDim1 = heights.GetLength(1);
            float maxSeafloorLocalY = sampleOcean * terrainData.size.y - SeafloorYClearance;

            for (int gy = 0; gy < hDim0 - 1; gy += SampleStride)
            for (int gx = 0; gx < hDim1 - 1; gx += SampleStride)
            {
                int sy = gy + Random.Range(0, SampleStride);
                int sx = gx + Random.Range(0, SampleStride);
                if (sy >= hDim0 || sx >= hDim1)
                    continue;

                float seafloorLocalY = heights[sy, sx] * terrainData.size.y;
                if (seafloorLocalY > maxSeafloorLocalY)
                    continue;

                Vector3 localPos = new Vector3(
                    ((float)sx / (hDim1 - 1)) * terrainData.size.x,
                    seafloorLocalY,
                    ((float)sy / (hDim0 - 1)) * terrainData.size.z);

                if (!CanPlaceDecoration(localPos, spacingGrid))
                    continue;

                positions.Add(new UnderwaterDecorationPlacementInfo(
                    UnderwaterDecorationCatalog.PickRecord(),
                    localPos));
            }
        }

        private static bool CanPlaceDecoration(Vector3 localPos, Dictionary<int, List<Vector2>> spacingGrid)
        {
            int cellX = Mathf.FloorToInt(localPos.x / MinimumDecorationSpacing);
            int cellZ = Mathf.FloorToInt(localPos.z / MinimumDecorationSpacing);
            float minDistanceSqr = MinimumDecorationSpacing * MinimumDecorationSpacing;
            Vector2 candidate = new Vector2(localPos.x, localPos.z);

            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                List<Vector2> cellPositions;
                if (!spacingGrid.TryGetValue(GetSpacingCellKey(cellX + dx, cellZ + dz), out cellPositions))
                    continue;

                for (int i = 0; i < cellPositions.Count; i++)
                {
                    if ((cellPositions[i] - candidate).sqrMagnitude < minDistanceSqr)
                        return false;
                }
            }

            int key = GetSpacingCellKey(cellX, cellZ);
            List<Vector2> positions;
            if (!spacingGrid.TryGetValue(key, out positions))
            {
                positions = new List<Vector2>();
                spacingGrid.Add(key, positions);
            }

            positions.Add(candidate);
            return true;
        }

        private static int GetSpacingCellKey(int x, int z)
        {
            return (x * 73856093) ^ (z * 19349663);
        }
    }
}
