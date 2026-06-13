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
    public static class UnderwaterDecorations
    {
        private const int MaxTilesPerWorkCycle = 1;

        private static readonly Queue<DaggerfallTerrain> workQueue = new Queue<DaggerfallTerrain>();
        private static readonly HashSet<DaggerfallTerrain> queuedTerrains = new HashSet<DaggerfallTerrain>();
        private static GameObject workerObject;
        private static bool installed;

        private class DecorationMarker : MonoBehaviour
        {
            public int MapPixelX;
            public int MapPixelY;
            public int FloorBuildVersion;
        }

        private class Worker : MonoBehaviour
        {
            void OnEnable()
            {
                StreamingWorld.OnUpdateTerrainsEnd += ProcessWorkQueue;
            }

            void OnDisable()
            {
                StreamingWorld.OnUpdateTerrainsEnd -= ProcessWorkQueue;
            }

            void Update()
            {
                ProcessWorkQueue();
            }

            private void ProcessWorkQueue()
            {
                if (!DeepWaterRuntime.CanRunHeavyRuntimeWork)
                    return;

                int processed = 0;
                while (workQueue.Count > 0 && processed < MaxTilesPerWorkCycle)
                {
                    DaggerfallTerrain dfTerrain = workQueue.Dequeue();
                    queuedTerrains.Remove(dfTerrain);
                    if (dfTerrain == null) continue;
                    
                    Terrain terrain = dfTerrain.GetComponent<Terrain>();
                    if (terrain == null || terrain.terrainData == null) continue;

                    if (dfTerrain.MapData.heightmapSamples == null) continue;

                    if (!CanPopulate())
                    {
                        RemoveDecoration(dfTerrain);
                        ClearDecorationMarker(dfTerrain);
                        continue;
                    }

                    if (ShouldPreservePlayerTileDecorations(dfTerrain))
                        continue;

                    if (IsCurrentDecoration(dfTerrain)) continue;

                    RemoveDecoration(dfTerrain);

                    PopulateTile(dfTerrain, terrain.terrainData);
                    processed++;
                }
            }
        }
        
        public static void Install()
        {
            if (installed)
                return;

            workerObject = new GameObject("DeepWaters_DecorationWorker");
            workerObject.AddComponent<Worker>();
            Object.DontDestroyOnLoad(workerObject);

            DeepWaterRuntime.OnTransientReset += ResetRuntimeState;
            DaggerfallTerrain.OnPromoteTerrainData += HandlePromote;
            PlayerGPS.OnMapPixelChanged += HandleMapPixelChanged;
            installed = true;
        }


        public static void RefreshPlayerArea()
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

            int radius = GetPopulateRadius(sw);
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
            // decorations (local positions for the old seafloor — they show
            // as kelp hanging in the sky until the rebuild queue reaches the
            // tile, or indefinitely if the tile sits outside the populate
            // radius below). Purge stale groups synchronously, BEFORE the
            // radius early-out; the throttled queue only rebuilds.
            var staleMarker = sender.GetComponent<DecorationMarker>();
            if (staleMarker != null &&
                (staleMarker.MapPixelX != sender.MapPixelX || staleMarker.MapPixelY != sender.MapPixelY))
            {
                RemoveDecoration(sender);
                ClearDecorationMarker(sender);
            }

            var pgps = GameManager.Instance?.PlayerGPS;
            if (pgps != null)
            {
                int populateRadius = GetPopulateRadius(GameManager.Instance?.StreamingWorld);
                int dx = sender.MapPixelX - pgps.CurrentMapPixel.X;
                int dy = sender.MapPixelY - pgps.CurrentMapPixel.Y;
                if (System.Math.Abs(dx) > populateRadius || System.Math.Abs(dy) > populateRadius)
                    return;
            }
            
            Enqueue(sender);
        }

        private static int GetPopulateRadius(StreamingWorld streamingWorld)
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
                    UnderwaterDecorationPlacement.BuildPositions(dfTerrain, terrainData, passes);
                if (positions.Count == 0)
                {
                    MarkCurrentTerrain(dfTerrain);
                    return;
                }

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
