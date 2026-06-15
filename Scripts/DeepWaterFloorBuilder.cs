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
    ///   2. Computes the per-cell carve mask (which cells are deep water).
    ///   3. Builds a <see cref="DeepWaterFloorMesh"/> child from that mask.
    ///
    /// The vanilla terrain is never modified: real Unity terrain holes
    /// native-crash Unity 2019.4 (TerrainRenderer::ForceSplitParent when a
    /// holed patch subdivides), so the heightfield stays intact and the
    /// outdoor swim collider gate disables its collider locally to let the
    /// player descend to the seafloor mesh.
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
        // Internal so DeepWaterFloorMesh can read the same constant
        // when sampling the bake's distance field (it's used as the
        // shore-buffer cutoff in two places).
        //
        // Reduced from 3 m to 0.5 m to push carved holes much closer to
        // the actual visible shore. The 3 m value was a paranoid safety
        // margin against per-tile BFS errors at tile boundaries; with
        // the global bake's deterministic distance field, adjacent
        // tiles agree exactly at their shared edge, so the safety
        // margin doesn't need to be that wide. 0.5 m leaves just
        // enough buffer to avoid the heightmap-interpolation seam where
        // IT's beach sand starts to fade up from sea level.
        internal const float HoleBufferMeters = 0.5f;

        // Event raised after a tile's seafloor mesh has been built or
        // refreshed. UnderwaterDecorations subscribes to this so it can
        // queue per-tile decoration placement without polling. Added
        // here because the working backup's UnderwaterDecorations
        // hooked OnPromoteTerrainData directly, but the current version
        // expects OnFloorRefreshed as a signal that the tile is fully
        // set up (including the seafloor mesh, which decorations sample
        // for spawn heights).
        public delegate void FloorRefreshedHandler(DaggerfallTerrain dfTerrain);
        public static event FloorRefreshedHandler OnFloorRefreshed;

        private static bool installed;

        public static void Install()
        {
            if (installed) return;
            DaggerfallTerrain.OnPromoteTerrainData += HandlePromote;
            installed = true;
        }

        // Force=true bypasses HandlePromoteCore's IsCurrentBuild guard so
        // callers that genuinely need a rebuild (settings changed, save
        // load completed) actually get one even when DFU hasn't
        // re-allocated heightmap arrays.
        public static void RefreshLoadedTiles(bool force = false)
        {
            DaggerfallTerrain[] terrains = Object.FindObjectsOfType<DaggerfallTerrain>();
            for (int i = 0; i < terrains.Length; i++)
                RefreshLoadedTile(terrains[i], force);
        }

        public static void RefreshLoadedTile(DaggerfallTerrain dfTerrain, bool force = false)
        {
            if (dfTerrain == null)
                return;

            Terrain unityTerrain = dfTerrain.GetComponent<Terrain>();
            if (unityTerrain != null && unityTerrain.terrainData != null)
                HandlePromote(dfTerrain, unityTerrain.terrainData, force);
        }

        // API surface used by DeepWaterStreamingBuffer to warm the
        // expanded streaming ring while the player is in exterior
        // water. Forwards to RefreshLoadedTiles WITHOUT force, so the
        // IsCurrentBuild guard short-circuits the per-stream-cycle
        // re-promotion of every loaded tile. Without that guard, every
        // OnUpdateTerrainsEnd bumped each tile's BuildVersion, which
        // invalidated UnderwaterDecorations markers and caused the
        // entire decoration ring to tear down and respawn on every
        // streaming pulse.
        public static void RefreshPlayerArea()
        {
            RefreshLoadedTiles(false);
        }

        // Fire OnFloorRefreshed after a floor mesh is built. Called from
        // BuildOrRefreshFloor's success path (see additions below).
        internal static void RaiseFloorRefreshed(DaggerfallTerrain dfTerrain)
        {
            if (OnFloorRefreshed != null)
            {
                try { OnFloorRefreshed(dfTerrain); }
                catch (System.Exception ex)
                {
                    Debug.LogWarning("[DeepWaters.Builder] OnFloorRefreshed subscriber threw for tile (" +
                                     (dfTerrain != null ? dfTerrain.MapPixelX : -1) + "," +
                                     (dfTerrain != null ? dfTerrain.MapPixelY : -1) + "): " + ex.Message);
                }
            }
        }

        private static void HandlePromote(DaggerfallTerrain dfTerrain, TerrainData terrainData)
        {
            // Genuine DFU promote event. It fires from inside
            // StreamingWorld's terrain update — BEFORE this tile renders its
            // first frame — so the TerrainRenderer LOD quadtree does not yet
            // exist and carving holes here cannot hit the ForceSplitParent
            // crash, whether the GameObject is active (brand-new tiles are
            // created active) or inactive (recycled tiles). This, not the
            // active flag, is the safe carve window.
            HandlePromote(dfTerrain, terrainData, force: false, fromPromoteEvent: true);
        }

        private static void HandlePromote(DaggerfallTerrain dfTerrain, TerrainData terrainData, bool force)
        {
            // Forced refresh (settings change / post-load). The tile has
            // already rendered, so an immediate SetHoles here is the unstable
            // live-terrain write path; the carve from the original promote
            // event still stands, so this path never re-carves.
            HandlePromote(dfTerrain, terrainData, force, fromPromoteEvent: false);
        }

        private static void HandlePromote(DaggerfallTerrain dfTerrain, TerrainData terrainData, bool force, bool fromPromoteEvent)
        {
            System.Diagnostics.Stopwatch profile = DeepWaterRuntime.StartProfile();
            try
            {
                HandlePromoteCore(dfTerrain, terrainData, force, fromPromoteEvent);
            }
            catch (System.Exception ex)
            {
                int mx = dfTerrain != null ? dfTerrain.MapPixelX : -1;
                int my = dfTerrain != null ? dfTerrain.MapPixelY : -1;
                Debug.LogError("[DeepWaters] HandlePromote crashed for tile (" + mx + "," + my +
                               "): " + ex.Message + "\n" + ex.StackTrace);
            }
            finally
            {
                DeepWaterRuntime.LogProfile(profile, "floor-promote", dfTerrain);
            }
        }

        public static bool DiagnosticLogging = false;

        private static void HandlePromoteCore(DaggerfallTerrain dfTerrain, TerrainData terrainData, bool force, bool fromPromoteEvent)
        {
            if (dfTerrain == null || terrainData == null) return;

            // The genuine promote event is the safe pre-first-render window to
            // carve holes and build the seafloor child, so let it through even
            // though CanMutateTerrainData reports terrain-streaming-active.
            // Only the forced refresh path (already-rendered tiles) is gated,
            // because mutating a live terrain is the unstable write path.
            if (!fromPromoteEvent && !DeepWaterRuntime.CanMutateTerrainData)
                return;

            // NOTE: do NOT gate seafloor generation on SpawnWaterSurfaces.
            // SpawnWaterSurfaces controls only the visible water-plane mesh
            // (WaterSurfaceManager); the carved seabed, swim depth, and
            // underwater world must still generate when the surface is hidden,
            // otherwise turning surfaces off breaks water generation entirely.
            if (DeepWaters.Instance == null)
            {
                if (DiagnosticLogging)
                    Debug.Log("[DeepWaters.Builder] tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + ") skipped (mod not ready)");
                RemoveFloor(dfTerrain);
                return;
            }

            // IsCurrentBuild guard. DFU's promote pipeline allocates a fresh
            // float[,] heightmapSamples on every real promote, so reference
            // equality reliably tells us whether the existing mesh was built
            // from the heightmap the tile is presently wearing. Skipping
            // current tiles matters: RefreshPlayerArea re-promotes the whole
            // loaded ring every streaming pulse, and a spurious rebuild bumps
            // BuildVersion, which tears down and respawns the tile's
            // decorations.
            if (!force)
            {
                Transform existingFloorChild = dfTerrain.transform.Find(FloorChildName);
                if (existingFloorChild != null)
                {
                    DeepWaterFloorMesh existingMesh = existingFloorChild.GetComponent<DeepWaterFloorMesh>();
                    if (existingMesh != null &&
                        existingMesh.BuildVersion > 0 &&
                        existingMesh.BuiltMapPixelX == dfTerrain.MapPixelX &&
                        existingMesh.BuiltMapPixelY == dfTerrain.MapPixelY &&
                        object.ReferenceEquals(
                            existingMesh.LastBuiltHeightmapSamples,
                            dfTerrain.MapData.heightmapSamples))
                    {
                        existingMesh.EnsureRuntimeCollider();
                        UpdateTerrainCapRenderer(dfTerrain);
                        return;
                    }
                }
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
                              ") ocean-connected but no carved cells (entirely buffered shoreline?) — no seafloor mesh");
                RemoveFloor(dfTerrain);
                return;
            }

            BuildOrRefreshFloor(dfTerrain, terrainData, tile, holes);
            UpdateTerrainCapRenderer(dfTerrain);
            // Fire the OnFloorRefreshed signal so UnderwaterDecorations
            // (and any other subscribers) can queue per-tile work.
            RaiseFloorRefreshed(dfTerrain);
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

            holes = new bool[holeRes, holeRes];
            bool anyHole = false;

            // Phase B path (v4 bake with fine mask): per-cell carve query
            // against the global fine mask. Adjacent tiles read the SAME
            // bake value at their shared boundary world positions, so
            // they agree on every boundary cell's carve decision —
            // eliminating cross-pixel seams and the 1-pixel walls at the
            // shore that came from per-tile heightmap reclassification
            // mismatches.
            //
            // Hot-loop optimization: we used to call tile.IsCarvedWater
            // (worldX, worldZ) which routed through DeepWaterTileData's
            // GetGlobalMapFractions, which reads transform.position once
            // per cell. With ~16k cells × ~30 tiles per stream cycle
            // that's ~500k Unity transform.position calls per pulse,
            // which directly produced the v0.53.0 perf regression. We
            // can compute fracX/fracZ from (hx, hy) and the hole grid
            // size alone (independent of world XZ + tile origin), then
            // call DeepWaterDistanceBake.IsCarvedWater directly. No
            // transform access, no world-position math, no distance
            // lookup (the carve decision doesn't need it — distance is
            // only used for the seafloor mesh depth profile, sampled at
            // vertex time).
            //
            // Pre-v4 fallback: the original heightmap any-corner check.
            // Matches WaterSurfaceManager.HasWaterTile's water plane
            // criterion exactly. Boundary cells where 3 of 4 corners sit
            // on the beach gradient still get carved if 1 corner reaches
            // the clamp.
            bool useBakeMask = DeepWaterDistanceBake.HasFineWaterMask;
            int mapPixelX = dfTerrain.MapPixelX;
            int mapPixelY = dfTerrain.MapPixelY;
            float invHoleRes = 1f / holeRes;

            // CRASH-FIX gate: only carve a cell whose ALL FOUR heightmap corners
            // sit at/below ocean level (fully-flat ocean floor). Carving a cell
            // that has shore RELIEF puts a hole in a terrain patch that
            // subdivides, and Unity 2019.4 native-crashes its render thread when
            // it splits a holed patch — that is BOTH the load crash (the player's
            // coastal tile) and the near-shore surface crash. The minimal working
            // build carves with exactly this all-corners test and never crashes;
            // the bake below still decides which flat cells to carve so cross-
            // tile seams stay fixed.
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanThreshold = sampler.OceanElevation / sampler.MaxTerrainHeight;
            const float oceanThresholdEps = 1e-5f;

            for (int hy = 0; hy < holeRes; hy++)
            {
                float fracZ = (hy + 0.5f) * invHoleRes;
                for (int hx = 0; hx < holeRes; hx++)
                {
                    holes[hy, hx] = true;

                    // Reject cells with any shore relief (see crash-fix note):
                    // a holed patch with relief subdivides and crashes the render
                    // thread. Flat ocean cells never subdivide.
                    if (heights[hy, hx]         > oceanThreshold + oceanThresholdEps ||
                        heights[hy, hx + 1]     > oceanThreshold + oceanThresholdEps ||
                        heights[hy + 1, hx]     > oceanThreshold + oceanThresholdEps ||
                        heights[hy + 1, hx + 1] > oceanThreshold + oceanThresholdEps)
                        continue;

                    float fracX = (hx + 0.5f) * invHoleRes;
                    bool isWater = true;
                    if (isWater && useBakeMask)
                    {
                        isWater = DeepWaterDistanceBake.IsCarvedWater(
                            mapPixelX, mapPixelY, fracX, fracZ);
                    }

                    if (!isWater) continue;

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
            DeepWaterTerrainCapRenderer.Restore(dfTerrain);
            Transform existing = dfTerrain.transform.Find(FloorChildName);
            if (existing == null) return;

            var meshComp = existing.GetComponent<DeepWaterFloorMesh>();
            if (meshComp != null) meshComp.TearDown();

            Object.Destroy(existing.gameObject);
        }

        private static void UpdateTerrainCapRenderer(DaggerfallTerrain dfTerrain)
        {
            bool hidePureOceanCap = DeepWaterTerrainCapRenderer.ShouldHidePureOceanCap(dfTerrain);
            DeepWaterTerrainCapRenderer.Apply(dfTerrain, hidePureOceanCap);
            // Mixed land/water tiles can't hide the whole heightmap renderer,
            // so clip just the painted water texels instead. Only reached for
            // tiles with a carved seafloor (callers gate on the floor build),
            // so there is real underwater world to reveal beneath.
            DeepWaterTerrainCapRenderer.ApplyWaterTexelClip(dfTerrain, !hidePureOceanCap);
        }
    }
}
