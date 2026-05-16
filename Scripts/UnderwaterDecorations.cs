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
        private const int PopulateRadius = 1;

        private static readonly Queue<DaggerfallTerrain> workQueue = new Queue<DaggerfallTerrain>();
        private static readonly HashSet<DaggerfallTerrain> queuedTerrains = new HashSet<DaggerfallTerrain>();
        private static GameObject workerObject;
        private static bool installed;

        private class DecorationMarker : MonoBehaviour
        {
            public int MapPixelX;
            public int MapPixelY;
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

            private void ProcessWorkQueue()
            {
                while (workQueue.Count > 0)
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

                    if (IsCurrentDecoration(dfTerrain)) continue;

                    RemoveDecoration(dfTerrain);

                    PopulateTile(dfTerrain, terrain.terrainData);
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

        public static void Uninstall()
        {
            if (!installed)
                return;

            DaggerfallTerrain.OnPromoteTerrainData -= HandlePromote;
            PlayerGPS.OnMapPixelChanged -= HandleMapPixelChanged;
            DeepWaterRuntime.OnTransientReset -= ResetRuntimeState;

            if (workerObject != null)
            {
                Object.Destroy(workerObject);
                workerObject = null;
            }

            ResetRuntimeState();

            installed = false;
        }

        private static void ResetRuntimeState()
        {
            workQueue.Clear();
            queuedTerrains.Clear();
        }

        private static void HandleMapPixelChanged(DFPosition mapPixel)
        {
            var gameManager = GameManager.Instance;
            var sw = gameManager != null ? gameManager.StreamingWorld : null;
            if (sw == null) return;

            for (int dx = -PopulateRadius; dx <= PopulateRadius; dx++)
            for (int dy = -PopulateRadius; dy <= PopulateRadius; dy++)
            {
                var go = sw.GetTerrainFromPixel(mapPixel.X + dx, mapPixel.Y + dy);
                if (go == null) continue;
                var dfTerrain = go.GetComponent<DaggerfallTerrain>();
                Enqueue(dfTerrain);
            }
        }

        private static void HandlePromote(DaggerfallTerrain sender, TerrainData terrainData)
        {
            if (sender == null) return;

            var pgps = GameManager.Instance?.PlayerGPS;
            if (pgps != null)
            {
                int dx = sender.MapPixelX - pgps.CurrentMapPixel.X;
                int dy = sender.MapPixelY - pgps.CurrentMapPixel.Y;
                if (System.Math.Abs(dx) > PopulateRadius || System.Math.Abs(dy) > PopulateRadius)
                    return;
            }
            
            Enqueue(sender);
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
        }

        private static bool CanPopulate()
        {
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
                   marker.MapPixelY == dfTerrain.MapPixelY;
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
