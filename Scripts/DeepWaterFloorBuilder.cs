// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Listens to DaggerfallTerrain promotion. For ocean-connected tiles:
    ///   1. Attaches a <see cref="DeepWaterTileData"/> cache (climate +
    ///      distance-to-coast field).
    ///   2. Computes a hole mask and ENQUEUES it via
    ///      <see cref="DeepWaterHoleApplier"/>. The actual SetHoles call
    ///      happens on a calm post-streaming frame — synchronous SetHoles
    ///      during promotion native-crashes the player runtime.
    ///   3. Spawns a <see cref="DeepWaterFloorMesh"/> child immediately. The
    ///      sub-mesh is independent of SetHoles and renders correctly even
    ///      before the hole applies; vanilla terrain may briefly occlude it
    ///      from above until the hole punches through (~1 frame later).
    /// </summary>
    public static class DeepWaterFloorBuilder
    {
        private const string FloorChildName = "DeepWaters_Seafloor";
        // Buffer of non-hole vanilla terrain we keep around the shoreline.
        // Larger values push the hole-to-seafloor wall further offshore and
        // make the wall taller (because depth grows with distance from
        // coast). The cliff is visible from the surface as a small mini-wall
        // just out into the water. 3m keeps a safety margin against
        // floating-point hole/terrain race conditions without making the
        // shore step prominent.
        private const float HoleBufferMeters = 3f;
        private const float OceanThresholdEpsilon = 1e-5f;

        // Diagnostic kill-switch from the v0.40 crash bisection. When true,
        // holes still get enqueued and applied, but no seafloor sub-mesh
        // is built — leaving voids beneath the water surface. The
        // SetHolesDelayLOD fix made multi-tile sub-meshes safe again, so
        // this defaults to false in production.
        public static bool SkipFloorMesh = false;

        private static bool installed;

        public static void Install()
        {
            if (installed) return;
            DaggerfallTerrain.OnPromoteTerrainData += HandlePromote;
            installed = true;
        }

        public static void Uninstall()
        {
            if (!installed) return;
            DaggerfallTerrain.OnPromoteTerrainData -= HandlePromote;
            installed = false;
        }

        public static void RefreshLoadedTiles()
        {
            DaggerfallTerrain[] terrains = Object.FindObjectsOfType<DaggerfallTerrain>();
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain unityTerrain = terrains[i].GetComponent<Terrain>();
                if (unityTerrain != null && unityTerrain.terrainData != null)
                    HandlePromote(terrains[i], unityTerrain.terrainData);
            }
        }

        private static void HandlePromote(DaggerfallTerrain dfTerrain, TerrainData terrainData)
        {
            try
            {
                HandlePromoteCore(dfTerrain, terrainData);
            }
            catch (System.Exception ex)
            {
                int mx = dfTerrain != null ? dfTerrain.MapPixelX : -1;
                int my = dfTerrain != null ? dfTerrain.MapPixelY : -1;
                Debug.LogError("[DeepWaters] HandlePromote crashed for tile (" + mx + "," + my +
                               "): " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        public static bool DiagnosticLogging = true;

        private static void HandlePromoteCore(DaggerfallTerrain dfTerrain, TerrainData terrainData)
        {
            if (dfTerrain == null || terrainData == null) return;

            if (DeepWaters.Instance == null || !DeepWaters.Instance.SpawnWaterSurfaces)
            {
                if (DiagnosticLogging)
                    Debug.Log("[DeepWaters.Builder] tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + ") skipped (mod disabled or surfaces off)");
                RemoveFloor(dfTerrain);
                return;
            }

            int climateIndex = ResolveClimateIndex(dfTerrain);
            DeepWaterTileData tile = EnsureTileData(dfTerrain);
            tile.Initialize(dfTerrain, climateIndex);

            if (!tile.IsOceanConnected || !tile.HasDistanceField)
            {
                if (DiagnosticLogging)
                    Debug.Log("[DeepWaters.Builder] tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY +
                              ") NOT ocean-connected (connected=" + tile.IsOceanConnected +
                              " distField=" + tile.HasDistanceField + ") — no seafloor mesh");
                RemoveFloor(dfTerrain);
                return;
            }

            bool[,] holes;
            bool hasAnyHoles = ComputeHoleMask(dfTerrain, tile, terrainData, out holes);
            if (!hasAnyHoles)
            {
                if (DiagnosticLogging)
                    Debug.Log("[DeepWaters.Builder] tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY +
                              ") ocean-connected but no holes (entirely buffered shoreline?) — no seafloor mesh");
                RemoveFloor(dfTerrain);
                return;
            }

            // Defer SetHoles — synchronous call here crashes the player runtime.
            var applier = DeepWaterHoleApplier.Instance;
            if (applier != null)
                applier.Enqueue(dfTerrain, holes);

            if (!SkipFloorMesh)
                BuildOrRefreshFloor(dfTerrain, terrainData, tile, holes);
            else
                RemoveFloor(dfTerrain);

            // After building this tile, ENQUEUE its 4 already-loaded neighbors
            // for refresh. Deferring keeps the tile-promotion path cheap
            // (critical for save-loads where many tiles stream in at once;
            // a synchronous cascade was forcing O(n²) work on the main thread
            // and visibly stalling the player spawn). The dispatcher processes
            // a couple of refresh requests per frame, so the seam-correctness
            // gradient settles in over the first second or two of play
            // without blocking the world from coming up.
            if (!suppressNeighborRefresh)
                EnqueueNeighborsForRefresh(dfTerrain);
        }

        private static bool suppressNeighborRefresh;
        private static readonly Queue<DaggerfallTerrain> neighborRefreshQueue = new Queue<DaggerfallTerrain>();
        private static readonly HashSet<DaggerfallTerrain> neighborRefreshSet = new HashSet<DaggerfallTerrain>();
        private const int RefreshesPerFrame = 2;

        private static void EnqueueNeighborsForRefresh(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null) return;

            float tileWS = MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale;
            Vector3 origin = dfTerrain.transform.position;

            Vector3[] targets = new Vector3[]
            {
                origin + new Vector3(tileWS, 0, 0),
                origin - new Vector3(tileWS, 0, 0),
                origin + new Vector3(0, 0, tileWS),
                origin - new Vector3(0, 0, tileWS),
            };

            const float epsilon = 0.5f;
            DaggerfallTerrain[] terrains = UnityEngine.Object.FindObjectsOfType<DaggerfallTerrain>();
            for (int i = 0; i < terrains.Length; i++)
            {
                DaggerfallTerrain t = terrains[i];
                if (t == dfTerrain) continue;

                Vector3 pos = t.transform.position;
                for (int k = 0; k < targets.Length; k++)
                {
                    if (Mathf.Abs(pos.x - targets[k].x) < epsilon &&
                        Mathf.Abs(pos.z - targets[k].z) < epsilon)
                    {
                        if (neighborRefreshSet.Add(t))
                            neighborRefreshQueue.Enqueue(t);
                        break;
                    }
                }
            }
        }

        // Called from a MonoBehaviour driver each frame. Processes a bounded
        // number of queued refreshes per call so the cascade spreads over
        // many frames instead of stalling promotion. The suppress flag is
        // set during the actual rebuild so a refreshed tile doesn't immediately
        // re-enqueue all its own neighbors.
        public static void PumpDeferredRefreshes()
        {
            int budget = RefreshesPerFrame;
            while (budget > 0 && neighborRefreshQueue.Count > 0)
            {
                DaggerfallTerrain t = neighborRefreshQueue.Dequeue();
                neighborRefreshSet.Remove(t);
                if (t == null) continue;

                Terrain unityTerrain = t.GetComponent<Terrain>();
                if (unityTerrain == null || unityTerrain.terrainData == null) continue;

                suppressNeighborRefresh = true;
                try
                {
                    HandlePromote(t, unityTerrain.terrainData);
                }
                finally
                {
                    suppressNeighborRefresh = false;
                }
                budget--;
            }
        }

        private static int ResolveClimateIndex(DaggerfallTerrain dfTerrain)
        {
            DaggerfallUnity dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.ContentReader == null || dfu.ContentReader.MapFileReader == null)
                return (int)MapsFile.Climates.Ocean;

            return dfu.ContentReader.MapFileReader.GetClimateIndex(dfTerrain.MapPixelX, dfTerrain.MapPixelY);
        }

        private static DeepWaterTileData EnsureTileData(DaggerfallTerrain dfTerrain)
        {
            var existing = dfTerrain.GetComponent<DeepWaterTileData>();
            if (existing != null) return existing;
            return dfTerrain.gameObject.AddComponent<DeepWaterTileData>();
        }

        private static bool ComputeHoleMask(
            DaggerfallTerrain dfTerrain,
            DeepWaterTileData tile,
            TerrainData terrainData,
            out bool[,] holes)
        {
            holes = null;
            float[,] heights = dfTerrain.MapData.heightmapSamples;
            if (heights == null) return false;

            int holeRes = terrainData.holesResolution;
            int hRows = heights.GetLength(0);
            int hCols = heights.GetLength(1);
            if (holeRes > hRows - 1 || holeRes > hCols - 1)
                holeRes = Mathf.Min(hRows, hCols) - 1;

            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanThreshold = sampler.OceanElevation / sampler.MaxTerrainHeight;
            float tileWorldSize = MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale;
            float cellWorldSize = tileWorldSize / holeRes;
            Vector3 origin = dfTerrain.transform.position;

            holes = new bool[holeRes, holeRes];
            bool anyHole = false;

            for (int hy = 0; hy < holeRes; hy++)
            {
                for (int hx = 0; hx < holeRes; hx++)
                {
                    holes[hy, hx] = true;

                    float h00 = heights[hy,     hx];
                    float h10 = heights[hy,     hx + 1];
                    float h01 = heights[hy + 1, hx];
                    float h11 = heights[hy + 1, hx + 1];

                    bool allWater =
                        h00 <= oceanThreshold + OceanThresholdEpsilon &&
                        h10 <= oceanThreshold + OceanThresholdEpsilon &&
                        h01 <= oceanThreshold + OceanThresholdEpsilon &&
                        h11 <= oceanThreshold + OceanThresholdEpsilon;

                    if (!allWater) continue;

                    float cellWorldX = origin.x + (hx + 0.5f) * cellWorldSize;
                    float cellWorldZ = origin.z + (hy + 0.5f) * cellWorldSize;
                    float distance = tile.GetDistanceToCoastMeters(cellWorldX, cellWorldZ);
                    if (distance < HoleBufferMeters) continue;

                    holes[hy, hx] = false;
                    anyHole = true;
                }
            }

            return anyHole;
        }

        private static void BuildOrRefreshFloor(DaggerfallTerrain dfTerrain, TerrainData terrainData, DeepWaterTileData tile, bool[,] holes)
        {
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanLocalY = (sampler.OceanElevation / sampler.MaxTerrainHeight) * terrainData.size.y;

            Transform existing = dfTerrain.transform.Find(FloorChildName);
            GameObject floorGO;
            DeepWaterFloorMesh meshComp;

            if (existing == null)
            {
                floorGO = new GameObject(FloorChildName);
                floorGO.transform.SetParent(dfTerrain.transform, false);
                floorGO.transform.localPosition = Vector3.zero;
                floorGO.transform.localRotation = Quaternion.identity;
                floorGO.transform.localScale = Vector3.one;
                floorGO.AddComponent<MeshFilter>();
                var mr = floorGO.AddComponent<MeshRenderer>();
                DeepWaterRendering.DisableShadows(mr);
                meshComp = floorGO.AddComponent<DeepWaterFloorMesh>();
            }
            else
            {
                floorGO = existing.gameObject;
                floorGO.transform.localPosition = Vector3.zero;
                floorGO.transform.localRotation = Quaternion.identity;
                floorGO.transform.localScale = Vector3.one;
                meshComp = floorGO.GetComponent<DeepWaterFloorMesh>();
                if (meshComp == null)
                    meshComp = floorGO.AddComponent<DeepWaterFloorMesh>();
            }

            meshComp.Build(dfTerrain, tile, oceanLocalY, holes);
        }

        private static void RemoveFloor(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null) return;
            Transform existing = dfTerrain.transform.Find(FloorChildName);
            if (existing == null) return;

            var meshComp = existing.GetComponent<DeepWaterFloorMesh>();
            if (meshComp != null) meshComp.TearDown();

            Object.Destroy(existing.gameObject);
        }
    }
}
