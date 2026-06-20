// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Spawns and manages underwater decoration billboards.
    /// Uses a worker queue processed at a safe point in the engine's update cycle.
    /// </summary>
    internal static class UnderwaterDecorations
    {
        private const int MaxTilesPerWorkCycle = 1;
        private const int MaxDecorationsPerTile = 2304;
        private const int SampleStride = 3;
        private const float MinimumDecorationSpacing = 5f;
        private const float MinimumSeafloorDepth = 8f;
        private const float SeafloorClearance = 0.25f;
        private const float AnimatedSeafloorClearance = 0.75f;
        private const float SurfaceDecorationClearance = 0.5f;
        private const float SlopeProbeDistance = 4f;
        private const float MaxDecorationSlopeDegrees = 35f;
        private const float BubbleFallbackVisualHeightNativeUnits = 72f;

        private static readonly Queue<DaggerfallTerrain> workQueue = new Queue<DaggerfallTerrain>();
        private static readonly HashSet<DaggerfallTerrain> queuedTerrains = new HashSet<DaggerfallTerrain>();
        private static readonly Dictionary<UnderwaterDecorationRecord, float> authoredVisualHeightCache =
            new Dictionary<UnderwaterDecorationRecord, float>();
        private static bool installed;

        internal static int PendingWorkCount
        {
            get { return workQueue.Count; }
        }

        internal static int QueuedTerrainCount
        {
            get { return queuedTerrains.Count; }
        }

        private class DecorationMarker : MonoBehaviour
        {
            internal int MapPixelX;
            internal int MapPixelY;
            internal int FloorBuildVersion;
        }

        internal static void Install()
        {
            if (installed)
                return;

			StreamingWorld.OnUpdateTerrainsEnd += ProcessWorkQueue;
            DeepWaterRuntime.OnTransientReset += ResetRuntimeState;
            DaggerfallTerrain.OnPromoteTerrainData += HandlePromote;
            DeepWaterFloorBuilder.OnFloorRefreshed += HandleFloorRefreshed;
            PlayerGPS.OnMapPixelChanged += HandleMapPixelChanged;
            installed = true;
        }

		internal static void ProcessWorkQueue()
		{
			if (!DeepWaterRuntime.CanRunHeavyRuntimeWork)
				return;

			int processed = 0;
			while (workQueue.Count > 0 && processed < MaxTilesPerWorkCycle)
			{
				DaggerfallTerrain dfTerrain = workQueue.Dequeue();
				queuedTerrains.Remove(dfTerrain);
				if (dfTerrain == null)
					continue;

				Terrain terrain = dfTerrain.GetComponent<Terrain>();
				if (terrain == null || terrain.terrainData == null)
					continue;

				if (dfTerrain.MapData.heightmapSamples == null)
					continue;

				if (!CanPopulate())
				{
					RemoveDecoration(dfTerrain);
					ClearDecorationMarker(dfTerrain);
					continue;
				}

				if (!HasReadyFloor(dfTerrain))
					continue;

				if (ShouldPreservePlayerTileDecorations(dfTerrain))
					continue;

				if (IsCurrentDecoration(dfTerrain))
					continue;

				RemoveDecoration(dfTerrain);

				PopulateTile(dfTerrain, terrain.terrainData);
				processed++;
			}
		}


        internal static void RefreshPlayerArea()
        {
            if (!DeepWaterRuntime.CanRunLightRuntimeWork)
                return;

            EnqueueLoadedPlayerArea();
        }

        private static void ResetRuntimeState()
        {
            workQueue.Clear();
            queuedTerrains.Clear();

            DecorationMarker[] markers = Object.FindObjectsOfType<DecorationMarker>();
            for (int i = 0; i < markers.Length; i++)
            {
                DecorationMarker marker = markers[i];
                if (marker == null)
                    continue;

                DaggerfallTerrain dfTerrain = marker.GetComponent<DaggerfallTerrain>();
                if (dfTerrain != null)
                    RemoveDecoration(dfTerrain);

                Object.Destroy(marker);
            }
        }

        private static void HandleMapPixelChanged(DFPosition mapPixel)
        {
            if (!DeepWaterRuntime.CanRunLightRuntimeWork)
                return;

            DeepWaterTerrainLookup.Clear();
            EnqueueAroundMapPixel(mapPixel);
        }

        private static void EnqueueLoadedPlayerArea()
        {
            var gameManager = GameManager.Instance;
            var playerGPS = gameManager != null ? gameManager.PlayerGPS : null;
            if (playerGPS == null) return;

            EnqueueAroundMapPixel(playerGPS.CurrentMapPixel);
        }

        private static void EnqueueAroundMapPixel(DFPosition mapPixel)
        {
            var gameManager = GameManager.Instance;
            var sw = gameManager != null ? gameManager.StreamingWorld : null;
            if (sw == null) return;

            int radius = GetPopulateRadius();
            for (int ring = 0; ring <= radius; ring++)
            for (int dx = -ring; dx <= ring; dx++)
            for (int dy = -ring; dy <= ring; dy++)
            {
                if (Mathf.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != ring)
                    continue;

                var go = sw.GetTerrainFromPixel(mapPixel.X + dx, mapPixel.Y + dy);
                if (go == null) continue;
                var dfTerrain = go.GetComponent<DaggerfallTerrain>();
                Enqueue(dfTerrain);
            }
        }

        private static void HandlePromote(DaggerfallTerrain sender, TerrainData terrainData)
        {
            if (sender == null) return;
            if (!DeepWaterRuntime.CanRunLightRuntimeWork)
                return;

            // A recycled tile still carries the PREVIOUS map pixel's
            // decorations until the rebuild queue reaches it. Purge stale
            // groups synchronously; the throttled queue only rebuilds.
            var staleMarker = sender.GetComponent<DecorationMarker>();
            if (staleMarker != null &&
                (staleMarker.MapPixelX != sender.MapPixelX || staleMarker.MapPixelY != sender.MapPixelY))
            {
                RemoveDecoration(sender);
                ClearDecorationMarker(sender);
            }

            if (!IsWithinPopulateRadius(sender))
                return;
            
            Enqueue(sender);
        }

        private static void HandleFloorRefreshed(DaggerfallTerrain sender)
        {
            if (sender == null) return;
            if (!DeepWaterRuntime.CanRunLightRuntimeWork)
                return;

			if (!IsWithinPopulateRadius(sender))
				return;

			Enqueue(sender);
		}

        private static bool IsWithinPopulateRadius(DaggerfallTerrain dfTerrain)
        {
            var pgps = GameManager.Instance?.PlayerGPS;
            if (pgps == null || dfTerrain == null)
                return true;

            int populateRadius = GetPopulateRadius();
            int dx = dfTerrain.MapPixelX - pgps.CurrentMapPixel.X;
            int dy = dfTerrain.MapPixelY - pgps.CurrentMapPixel.Y;
            return System.Math.Abs(dx) <= populateRadius &&
                   System.Math.Abs(dy) <= populateRadius;
        }

		private static int GetPopulateRadius()
		{
			int radius = DeepWaters.Instance != null ? DeepWaters.Instance.DecorationPopulateRadius : 1;
            return Mathf.Clamp(radius, 1, 3);
        }
        
        private static void PopulateTile(DaggerfallTerrain dfTerrain, TerrainData terrainData)
        {
            // Save Unity's global Random state and seed it from this tile's
            // map coordinates so the SAME tile always generates the SAME
            // decoration set across save/load. Without this, every visit to
            // a coastline rolled different pass counts, sample jitter, picks,
            // and (for issue 8) random scales — making players see the
            // seafloor reshuffle on every reload.
            UnityEngine.Random.State previousState = UnityEngine.Random.state;
            UnityEngine.Random.InitState(TileDecorationSeed(dfTerrain.MapPixelX, dfTerrain.MapPixelY));
            try
            {
                int passes = RollDecorationPasses();
                if (passes <= 0)
                {
                    MarkCurrentTerrain(dfTerrain);
                    return;
                }

                List<UnderwaterDecorationPlacementInfo> positions =
                    BuildDecorationPositions(dfTerrain, terrainData, passes);
                if (positions.Count == 0)
                    return;

                TrimDecorationPositions(positions);
                UnderwaterDecorationBatchFactory.Spawn(dfTerrain.transform, positions);
                MarkCurrentTerrain(dfTerrain);
            }
            finally
            {
                UnityEngine.Random.state = previousState;
            }
        }

        private static int TileDecorationSeed(int mapPixelX, int mapPixelY)
        {
            // Two-prime hash, identical pattern used elsewhere in this mod
            // for spatial cell keys. Stable across runs/saves.
            unchecked
            {
                return (mapPixelX * 73856093) ^ (mapPixelY * 19349663);
            }
        }

        private static List<UnderwaterDecorationPlacementInfo> BuildDecorationPositions(
            DaggerfallTerrain dfTerrain,
            TerrainData terrainData,
            int passes)
        {
            var positions = new List<UnderwaterDecorationPlacementInfo>();
            var spacingGrid = new Dictionary<int, List<Vector2>>();
            DeepWaterFloorMesh floorMesh = dfTerrain.GetComponentInChildren<DeepWaterFloorMesh>();
            DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();
            int climateIndex = tile != null ? tile.BiomeClimateIndex : 0;
            int totalPasses = DecorationPassesForBiome(passes, climateIndex);

            for (int i = 0; i < totalPasses; i++)
                GenerateBillboardPositions(dfTerrain, terrainData, floorMesh, climateIndex, positions, spacingGrid);

            return positions;
        }

        private static int DecorationPassesForBiome(int passes, int climateIndex)
        {
            if (passes <= 0)
                return 0;

            WaterBiome biome = PassiveFishSpeciesCatalog.ClimateToBiome(climateIndex);
            if (biome == WaterBiome.OpenOcean)
                return Mathf.CeilToInt(passes * 1.35f);
            if (biome == WaterBiome.Desert)
                return Mathf.CeilToInt(passes * 0.55f);

            return passes;
        }

        private static void GenerateBillboardPositions(
            DaggerfallTerrain dfTerrain,
            TerrainData terrainData,
            DeepWaterFloorMesh floorMesh,
            int climateIndex,
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

                UnderwaterDecorationRecord record = UnderwaterDecorationCatalog.PickRecord(climateIndex);
                float visualHeight;
                if (!TryGetDecorationVisualHeight(record, UnderwaterDecorationBatchFactory.DecorationScaleMax, out visualHeight)) continue;

                float billboardBaseLocalY = seafloorLocalY + GetSeafloorClearance(record);
                if (billboardBaseLocalY >= oceanLocalY) continue;
                if (billboardBaseLocalY + visualHeight > oceanLocalY - SurfaceDecorationClearance) continue;

                Vector3 localPos = new Vector3(localX, billboardBaseLocalY, localZ);
                if (!CanPlaceDecoration(localPos, spacingGrid)) continue;

                positions.Add(new UnderwaterDecorationPlacementInfo(record, localPos));
            }
        }

        private static float GetSeafloorClearance(UnderwaterDecorationRecord record)
        {
            return UnderwaterDecorationCatalog.UsesArchiveAnimation(record)
                ? AnimatedSeafloorClearance
                : SeafloorClearance;
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
            else if (record.Archive == 106)
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
            if (floorMesh != null && floorMesh.TrySampleMeshLocalY(worldX, worldZ, out seafloorLocalY))
                return true;

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
            if (floorMesh != null && floorMesh.TrySampleMeshLocalY(worldX, worldZ, out seafloorLocalY))
                return seafloorLocalY < oceanLocalY - MinimumSeafloorDepth;

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
                    if ((cellPositions[i] - candidate).sqrMagnitude < minDistanceSqr)
                        return false;
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

        private static void TrimDecorationPositions(List<UnderwaterDecorationPlacementInfo> positions)
        {
            if (positions == null || positions.Count <= MaxDecorationsPerTile)
                return;

            for (int i = positions.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                UnderwaterDecorationPlacementInfo swap = positions[i];
                positions[i] = positions[j];
                positions[j] = swap;
            }

            positions.RemoveRange(MaxDecorationsPerTile, positions.Count - MaxDecorationsPerTile);
        }

        private static void MarkCurrentTerrain(DaggerfallTerrain dfTerrain)
        {
            var marker = dfTerrain.GetComponent<DecorationMarker>() ?? dfTerrain.gameObject.AddComponent<DecorationMarker>();
            marker.MapPixelX = dfTerrain.MapPixelX;
            marker.MapPixelY = dfTerrain.MapPixelY;
            marker.FloorBuildVersion = CurrentFloorBuildVersion(dfTerrain);
        }

        private static bool CanPopulate()
        {
            // Decorations belong to the underwater world, not the visible water
            // plane, so they are gated only by SpawnUnderwaterDecorations — not
            // SpawnWaterSurfaces. (Turning the surface off must not strip the
            // seabed and its dressing.)
            return DeepWaters.Instance != null &&
                   DeepWaters.Instance.SpawnUnderwaterDecorations;
        }

        private static void Enqueue(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain != null && queuedTerrains.Add(dfTerrain))
                workQueue.Enqueue(dfTerrain);
        }

        private static bool IsCurrentDecoration(DaggerfallTerrain dfTerrain)
        {
            var marker = dfTerrain.GetComponent<DecorationMarker>();
            return marker != null &&
                   marker.MapPixelX == dfTerrain.MapPixelX &&
                   marker.MapPixelY == dfTerrain.MapPixelY &&
                   marker.FloorBuildVersion == CurrentFloorBuildVersion(dfTerrain);
        }

        private static bool HasReadyFloor(DaggerfallTerrain dfTerrain)
        {
            DeepWaterFloorMesh floorMesh = dfTerrain != null ? dfTerrain.GetComponentInChildren<DeepWaterFloorMesh>() : null;
            return floorMesh != null &&
                   floorMesh.BuildVersion > 0 &&
                   floorMesh.BuiltMapPixelX == dfTerrain.MapPixelX &&
                   floorMesh.BuiltMapPixelY == dfTerrain.MapPixelY;
        }

        private static bool ShouldPreservePlayerTileDecorations(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null)
                return false;

            var playerGPS = GameManager.Instance != null ? GameManager.Instance.PlayerGPS : null;
            if (playerGPS == null)
                return false;

            if (dfTerrain.MapPixelX != playerGPS.CurrentMapPixel.X ||
                dfTerrain.MapPixelY != playerGPS.CurrentMapPixel.Y)
            {
                return false;
            }

            return dfTerrain.transform.Find(UnderwaterDecorationBatchFactory.GroupName) != null;
        }

        private static int CurrentFloorBuildVersion(DaggerfallTerrain dfTerrain)
        {
            DeepWaterFloorMesh floorMesh = dfTerrain != null ? dfTerrain.GetComponentInChildren<DeepWaterFloorMesh>() : null;
            return floorMesh != null ? floorMesh.BuildVersion : 0;
        }

        private static void RemoveDecoration(DaggerfallTerrain dfTerrain)
        {
            Transform existing = dfTerrain.transform.Find(UnderwaterDecorationBatchFactory.GroupName);
            if (existing != null)
            {
                existing.gameObject.SetActive(false);
                Object.Destroy(existing.gameObject);
            }
        }

        private static void ClearDecorationMarker(DaggerfallTerrain dfTerrain)
        {
            var marker = dfTerrain.GetComponent<DecorationMarker>();
            if (marker != null)
                Object.Destroy(marker);
        }

        private static int RollDecorationPasses()
        {
            float frequency = Mathf.Max(0f, DeepWaters.Instance.DecorationFrequency);
            int passes = Mathf.FloorToInt(frequency);
            if (Random.value < frequency - passes)
                passes++;

            return passes;
        }
    }
}
