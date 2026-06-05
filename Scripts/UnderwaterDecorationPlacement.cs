// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Generates seafloor decoration positions. Routes seafloor Y queries
    /// through the per-tile <see cref="DeepWaterFloorMesh"/> so decorations
    /// land on the EXACT rendered surface — the mesh's linear interpolation
    /// between vertices differs from the raw bathymetry function over a
    /// vertex cell, and using the function value made decorations float a
    /// noticeable amount above visible undulations.
    /// </summary>
    internal static class UnderwaterDecorationPlacement
    {
        private const int SampleStride = 3;
        private const float MinimumDecorationSpacing = 5f;
        private const float MinimumSeafloorDepth = 8f;
        private const float SeafloorClearance = 0.25f;
        private const float SurfaceDecorationClearance = 0.5f;
        // Slope filter: probe seafloor Y a few meters in each cardinal
        // direction around the candidate and reject if the surface tilts
        // steeper than the threshold. Keeps billboards from spawning
        // partway up the shelf wall or in a trench wall.
        private const float SlopeProbeDistance = 4f;
        private const float MaxDecorationSlopeDegrees = 35f;
        private const float BubbleFallbackVisualHeightNativeUnits = 72f;

        private static readonly Dictionary<UnderwaterDecorationRecord, float> authoredVisualHeightCache =
            new Dictionary<UnderwaterDecorationRecord, float>();
        private static readonly Dictionary<UnderwaterDecorationRecord, float> floorAnchorOffsetCache =
            new Dictionary<UnderwaterDecorationRecord, float>();

        public static List<UnderwaterDecorationPlacementInfo> BuildPositions(
            DaggerfallTerrain dfTerrain,
            TerrainData terrainData,
            int passes)
        {
            var positions = new List<UnderwaterDecorationPlacementInfo>();
            var spacingGrid = new Dictionary<int, List<Vector2>>();

            // Resolve the per-tile floor mesh once so every candidate uses
            // the same interpolation source. Falls back to the bathymetry
            // function (via TryGetWaterColumn) if the mesh isn't ready.
            DeepWaterFloorMesh floorMesh = dfTerrain.GetComponentInChildren<DeepWaterFloorMesh>();

            for (int i = 0; i < passes; i++)
                GenerateBillboardPositions(dfTerrain, terrainData, floorMesh, positions, spacingGrid);

            return positions;
        }

        private static void GenerateBillboardPositions(
            DaggerfallTerrain dfTerrain,
            TerrainData terrainData,
            DeepWaterFloorMesh floorMesh,
            List<UnderwaterDecorationPlacementInfo> positions,
            Dictionary<int, List<Vector2>> spacingGrid)
        {
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanLocalY = (sampler.OceanElevation / sampler.MaxTerrainHeight) * terrainData.size.y;
            float[,] heights = dfTerrain.MapData.heightmapSamples;
            int hDim0 = heights.GetLength(0);
            int hDim1 = heights.GetLength(1);
            float oceanThresholdNormalised = sampler.OceanElevation / sampler.MaxTerrainHeight;
            Vector3 origin = dfTerrain.transform.position;

            for (int gy = 0; gy < hDim0 - 1; gy += SampleStride)
            for (int gx = 0; gx < hDim1 - 1; gx += SampleStride)
            {
                int sy = gy + Random.Range(0, SampleStride);
                int sx = gx + Random.Range(0, SampleStride);
                if (sy >= hDim0 || sx >= hDim1) continue;

                // Skip cells that are clearly land in the vanilla heightmap.
                if (heights[sy, sx] > oceanThresholdNormalised + 1e-5f) continue;

                float localX = ((float)sx / (hDim1 - 1)) * terrainData.size.x;
                float localZ = ((float)sy / (hDim0 - 1)) * terrainData.size.z;
                float worldX = origin.x + localX;
                float worldZ = origin.z + localZ;

                DeepWaterColumn column;
                if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column)) continue;
                if (column.Depth < MinimumSeafloorDepth) continue;

                float seafloorLocalY;
                if (!TryResolveSeafloorY(floorMesh, column, worldX, worldZ, out seafloorLocalY))
                    continue;

                if (!IsGentleEnoughForDecoration(floorMesh, worldX, worldZ, oceanLocalY)) continue;

                UnderwaterDecorationRecord record = UnderwaterDecorationCatalog.PickRecord();
                float visualHeight;
                if (!TryGetDecorationVisualHeight(record, out visualHeight)) continue;

                float billboardBaseLocalY = seafloorLocalY + SeafloorClearance -
                    GetDecorationFloorAnchorOffset(record, visualHeight);
                if (billboardBaseLocalY >= oceanLocalY) continue;
                if (!HasDecorationSurfaceClearance(billboardBaseLocalY, visualHeight, oceanLocalY)) continue;

                Vector3 localPos = new Vector3(localX, billboardBaseLocalY, localZ);

                if (!CanPlaceDecoration(localPos, spacingGrid)) continue;

                positions.Add(new UnderwaterDecorationPlacementInfo(
                    record,
                    localPos));
            }
        }

        private static bool HasDecorationSurfaceClearance(
            float baseLocalY,
            float visualHeight,
            float oceanLocalY)
        {
            return baseLocalY + visualHeight <= oceanLocalY - SurfaceDecorationClearance;
        }

        internal static float ResolveBillboardBaseWorldY(int archive, int record, float visibleBottomWorldY)
        {
            return ResolveBillboardBaseWorldY(archive, record, visibleBottomWorldY, 1f);
        }

        internal static float ResolveBillboardBaseWorldY(
            int archive,
            int record,
            float visibleBottomWorldY,
            float scaleFactor)
        {
            UnderwaterDecorationRecord decorationRecord = new UnderwaterDecorationRecord(archive, record);
            float visualHeight;
            if (!TryGetDecorationVisualHeight(decorationRecord, scaleFactor, out visualHeight))
                return visibleBottomWorldY;

            return visibleBottomWorldY - GetDecorationFloorAnchorOffset(decorationRecord, visualHeight);
        }

        private static float GetDecorationFloorAnchorOffset(UnderwaterDecorationRecord record, float visualHeight)
        {
            float authoredHeight;
            if (!TryGetAuthoredDecorationVisualHeight(record, out authoredHeight) || authoredHeight <= 0f)
                return 0f;

            float authoredOffset = GetAuthoredDecorationFloorAnchorOffset(record, authoredHeight);
            return Mathf.Clamp(authoredOffset * (visualHeight / authoredHeight), 0f, visualHeight);
        }

        private static float GetAuthoredDecorationFloorAnchorOffset(
            UnderwaterDecorationRecord record,
            float authoredHeight)
        {
            float offset;
            if (floorAnchorOffsetCache.TryGetValue(record, out offset))
                return offset;

            offset = Mathf.Max(
                TryGetVisibleBottomPadding(record, authoredHeight),
                TryGetMetadataYOffset(record));

            if (offset <= 0f && IsBubbleDecorationRecord(record))
                offset = authoredHeight * 0.75f;

            offset = Mathf.Clamp(offset, 0f, authoredHeight);
            floorAnchorOffsetCache[record] = offset;
            return offset;
        }

        private static bool IsBubbleDecorationRecord(UnderwaterDecorationRecord record)
        {
            return record.Archive == 106;
        }

        private static float TryGetVisibleBottomPadding(UnderwaterDecorationRecord record, float visualHeight)
        {
            CachedMaterial cachedMaterial;
            if (!TryGetArchiveCachedMaterial(record.Archive, out cachedMaterial) ||
                cachedMaterial.material == null ||
                cachedMaterial.material.mainTexture == null ||
                cachedMaterial.atlasRects == null ||
                cachedMaterial.atlasIndices == null ||
                record.Record < 0 ||
                record.Record >= cachedMaterial.atlasIndices.Length)
            {
                return 0f;
            }

            Texture2D texture = cachedMaterial.material.mainTexture as Texture2D;
            if (texture == null)
                return 0f;

            RecordIndex index = cachedMaterial.atlasIndices[record.Record];
            if (index.startIndex < 0 || index.startIndex >= cachedMaterial.atlasRects.Length)
                return 0f;

            Rect rect = cachedMaterial.atlasRects[index.startIndex];
            int xMin = Mathf.Clamp(Mathf.FloorToInt(rect.xMin * texture.width), 0, texture.width - 1);
            int xMax = Mathf.Clamp(Mathf.CeilToInt(rect.xMax * texture.width) - 1, 0, texture.width - 1);
            int yMin = Mathf.Clamp(Mathf.FloorToInt(rect.yMin * texture.height), 0, texture.height - 1);
            int yMax = Mathf.Clamp(Mathf.CeilToInt(rect.yMax * texture.height) - 1, 0, texture.height - 1);
            if (xMax < xMin || yMax < yMin)
                return 0f;

            try
            {
                for (int y = yMin; y <= yMax; y++)
                {
                    for (int x = xMin; x <= xMax; x++)
                    {
                        if (texture.GetPixel(x, y).a > 0.1f)
                        {
                            float rectHeight = Mathf.Max(1f, yMax - yMin + 1);
                            return ((y - yMin) / rectHeight) * visualHeight;
                        }
                    }
                }
            }
            catch (UnityException)
            {
                return 0f;
            }

            return 0f;
        }

        private static float TryGetMetadataYOffset(UnderwaterDecorationRecord record)
        {
            CachedMaterial cachedMaterial;
            if (!TryGetArchiveCachedMaterial(record.Archive, out cachedMaterial) ||
                cachedMaterial.recordOffsets == null ||
                record.Record < 0 ||
                record.Record >= cachedMaterial.recordOffsets.Length)
            {
                return 0f;
            }

            return Mathf.Abs(cachedMaterial.recordOffsets[record.Record].y) * MeshReader.GlobalScale;
        }

        private static bool TryGetArchiveCachedMaterial(int archive, out CachedMaterial cachedMaterial)
        {
            cachedMaterial = new CachedMaterial();
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
            if (dfUnity == null || dfUnity.MaterialReader == null)
                return false;

            if (dfUnity.MaterialReader.GetCachedMaterialAtlas(archive, out cachedMaterial))
                return true;

            Rect[] rects;
            RecordIndex[] indices;
            int atlasSize = DaggerfallUnity.Settings.AssetInjection ? 4096 : 2048;
            dfUnity.MaterialReader.GetMaterialAtlas(
                archive,
                0,
                4,
                atlasSize,
                out rects,
                out indices,
                4,
                true,
                0,
                false,
                true);

            return dfUnity.MaterialReader.GetCachedMaterialAtlas(archive, out cachedMaterial);
        }

        private static bool TryGetDecorationVisualHeight(UnderwaterDecorationRecord record, out float visualHeight)
        {
            return TryGetDecorationVisualHeight(
                record,
                UnderwaterDecorationBatchFactory.DecorationScaleMax,
                out visualHeight);
        }

        private static bool TryGetDecorationVisualHeight(
            UnderwaterDecorationRecord record,
            float scaleFactor,
            out float visualHeight)
        {
            float authoredHeight;
            if (!TryGetAuthoredDecorationVisualHeight(record, out authoredHeight))
            {
                visualHeight = 0f;
                return false;
            }

            visualHeight = authoredHeight * Mathf.Max(0.01f, scaleFactor);
            return visualHeight > 0f;
        }

        private static bool TryGetAuthoredDecorationVisualHeight(UnderwaterDecorationRecord record, out float visualHeight)
        {
            if (authoredVisualHeightCache.TryGetValue(record, out visualHeight))
                return visualHeight > 0f;

            visualHeight = 0f;
            DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
            if (dfUnity == null || dfUnity.MeshReader == null)
                return false;

            Vector2 archiveSize = dfUnity.MeshReader.GetScaledBillboardSize(record.Archive, record.Record);
            if (archiveSize.y > 0f)
                visualHeight = archiveSize.y * MeshReader.GlobalScale;
            else if (IsBubbleDecorationRecord(record))
                visualHeight = BubbleFallbackVisualHeightNativeUnits * MeshReader.GlobalScale;

            if (DaggerfallUnity.Settings.AssetInjection &&
                !UnderwaterDecorationCatalog.UsesArchiveAnimation(record))
            {
                UnderwaterDecorationReplacementInfo replacementInfo;
                if (UnderwaterDecorationReplacementCache.TryGet(record, out replacementInfo) &&
                    replacementInfo.BatchSize.y > 0f)
                {
                    visualHeight = Mathf.Max(
                        visualHeight,
                        replacementInfo.BatchSize.y * MeshReader.GlobalScale);
                }
            }

            authoredVisualHeightCache[record] = visualHeight;
            return visualHeight > 0f;
        }

        private static bool TryResolveSeafloorY(
            DeepWaterFloorMesh floorMesh,
            DeepWaterColumn column,
            float worldX,
            float worldZ,
            out float seafloorLocalY)
        {
            if (DeepWaterWorld.TryGetRenderedSeafloorLocalY(column, worldX, worldZ, out seafloorLocalY))
                return true;

            seafloorLocalY = 0f;
            return false;
        }

        private static bool IsGentleEnoughForDecoration(
            DeepWaterFloorMesh floorMesh,
            float worldX,
            float worldZ,
            float oceanLocalY)
        {
            float leftY, rightY, backY, forwardY;
            if (!TryProbeSeafloorY(floorMesh, worldX - SlopeProbeDistance, worldZ, oceanLocalY, out leftY) ||
                !TryProbeSeafloorY(floorMesh, worldX + SlopeProbeDistance, worldZ, oceanLocalY, out rightY) ||
                !TryProbeSeafloorY(floorMesh, worldX, worldZ - SlopeProbeDistance, oceanLocalY, out backY) ||
                !TryProbeSeafloorY(floorMesh, worldX, worldZ + SlopeProbeDistance, oceanLocalY, out forwardY))
            {
                return false;
            }

            float dhdx = (rightY - leftY) / (SlopeProbeDistance * 2f);
            float dhdz = (forwardY - backY) / (SlopeProbeDistance * 2f);
            float slopeDegrees = Mathf.Atan(Mathf.Sqrt(dhdx * dhdx + dhdz * dhdz)) * Mathf.Rad2Deg;
            return slopeDegrees <= MaxDecorationSlopeDegrees;
        }

        private static bool TryProbeSeafloorY(
            DeepWaterFloorMesh floorMesh,
            float worldX,
            float worldZ,
            float oceanLocalY,
            out float seafloorLocalY)
        {
            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
            {
                seafloorLocalY = 0f;
                return false;
            }

            if (column.Depth < MinimumSeafloorDepth)
            {
                seafloorLocalY = 0f;
                return false;
            }

            if (!DeepWaterWorld.TryGetRenderedSeafloorLocalY(column, worldX, worldZ, out seafloorLocalY))
            {
                seafloorLocalY = 0f;
                return false;
            }

            return seafloorLocalY < oceanLocalY - MinimumSeafloorDepth;
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
