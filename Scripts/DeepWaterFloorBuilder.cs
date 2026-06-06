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
    ///   2. Computes a hole mask and enqueues it through
    ///      <see cref="DeepWaterHoleApplier"/>. The applier writes on a later
    ///      frame so Unity terrain holes do not race DFU's location load.
    ///   3. Spawns a <see cref="DeepWaterFloorMesh"/> child immediately. The
    ///      sub-mesh is independent of SetHoles and renders correctly even
    ///      before the hole applies; vanilla terrain may briefly occlude it
    ///      from above until the hole punches through.
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
        // Diagnostic kill-switch from the v0.40 crash bisection. When true,
        // holes still get enqueued and applied, but no seafloor sub-mesh
        // is built — leaving voids beneath the water surface. The
        // Stable post-stream hole applier made multi-tile sub-meshes safe
        // again, so this defaults to false in production.
        public static bool SkipFloorMesh = false;

        // Option B (v0.55.9) DISABLED: skipping holes near locations + the
        // per-tile location check shifted the coastal-town load timing and
        // *lost* the fragile SetHoles-vs-building-collider race that v0.55.8
        // narrowly wins — i.e. it made the on-load crash WORSE, not better.
        // Left here (off) for reference; flipping it back on reintroduces the
        // v0.55.9 load crash.
        public static bool SkipHolesNearLocations = false;

        // Per-operation trace around carving and seafloor-mesh building.
        // Useful for crash bisection, but far too noisy for normal play because
        // terrain streaming can promote dozens of tiles in a short burst.
        public static bool DiagTrace = false;

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

        public static void Uninstall()
        {
            if (!installed) return;
            DaggerfallTerrain.OnPromoteTerrainData -= HandlePromote;
            installed = false;
        }

        // Force=true bypasses HandlePromoteCore's IsCurrentBuild guard so
        // callers that genuinely need a rebuild (settings changed, save
        // load completed) actually get one even when DFU hasn't
        // re-allocated heightmap arrays.
        public static void RefreshLoadedTiles(bool force = false)
        {
            DaggerfallTerrain[] terrains = Object.FindObjectsOfType<DaggerfallTerrain>();
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain unityTerrain = terrains[i].GetComponent<Terrain>();
                if (unityTerrain != null && unityTerrain.terrainData != null)
                    HandlePromote(terrains[i], unityTerrain.terrainData, force);
            }
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

            if (DeepWaters.Instance == null || !DeepWaters.Instance.SpawnWaterSurfaces)
            {
                if (DiagnosticLogging)
                    Debug.Log("[DeepWaters.Builder] tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + ") skipped (mod disabled or surfaces off)");
                QueueSolidResetIfTouched(dfTerrain, terrainData, "surfaces disabled", fromPromoteEvent);
                RemoveFloor(dfTerrain);
                return;
            }

            // Option B: never carve holes on a location tile or its immediate
            // neighbours. DFU adds a town's building colliders during location
            // layout; a hole's SetHolesDelayLOD/SyncTexture racing that collider
            // setup native-crashes Unity 2019.4 (the "crash right after Location
            // GameObject Created" at coastal towns like Fonthope End). Leaving
            // these tiles vanilla keeps the water shallow right around a town but
            // crash-free; real depth resumes a tile or two offshore.
            if (SkipHolesNearLocations && IsNearLocation(dfTerrain))
            {
                if (DiagnosticLogging)
                    Debug.Log("[DeepWaters.Builder] tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY +
                              ") near a location — leaving vanilla (no holes).");
                QueueSolidResetIfTouched(dfTerrain, terrainData, "near location", fromPromoteEvent);
                RemoveFloor(dfTerrain);
                return;
            }

            // IsCurrentBuild guard. The mesh side and the applier side
            // can fall out of sync — DFU's promote pipeline allocates a
            // fresh float[,] heightmapSamples on every real promote, so
            // reference equality reliably tells us when the mesh needs
            // a rebuild, but it can't tell us whether the most recent
            // mask write actually reached the GPU. ClearQueue() on save
            // load / teleport drops in-flight applies; if that happens
            // between the mesh build and the SyncTexture, the mesh ends
            // up "current" while TerrainData (and the GPU) never got
            // the hole mask. The player sees vanilla terrain under the
            // water plane until DFU happens to re-promote — which is
            // exactly the "fixes itself when I leave and re-enter the
            // map pixel" recovery the user described.
            //
            // The guard now distinguishes three states:
            //   A. Mesh current AND applier confirms sync for this
            //      heightmap → fully current → skip everything.
            //   B. Mesh current but sync NOT confirmed → re-enqueue the
            //      mask (cheap) but skip the expensive mesh rebuild +
            //      OnFloorRefreshed (decorations stay put).
            //   C. Mesh stale (different heightmap ref / coords / no
            //      mesh) → full work below.
            bool meshCurrent = false;
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

                        if (DeepWaterHoleApplier.IsHeightmapSynced(
                                dfTerrain, dfTerrain.MapData.heightmapSamples))
                        {
                            // State A: fully current.
                            UpdateTerrainCapRenderer(dfTerrain);
                            return;
                        }

                        // State B: mesh current, sync pending or lost.
                        // Fall through to the cheap path (compute mask +
                        // enqueue) but flag meshCurrent so we skip the
                        // mesh rebuild block below.
                        meshCurrent = true;
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
                QueueSolidResetIfTouched(dfTerrain, terrainData, "not ocean-connected", fromPromoteEvent);
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
                QueueSolidResetIfTouched(dfTerrain, terrainData, "no holes", fromPromoteEvent);
                RemoveFloor(dfTerrain);
                return;
            }

            // Carve the vanilla terrain through the applier. Even genuine
            // promote events can overlap coastal location creation on Unity
            // 2019.4; the applier owns the narrow safe timing window.
            if (DiagTrace)
                Debug.Log("[DeepWaters.DIAG] carve>> " + DiagCtx(dfTerrain, fromPromoteEvent));
            ApplyHoleMask(dfTerrain, terrainData, holes, fromPromoteEvent);
            if (DiagTrace)
                Debug.Log("[DeepWaters.DIAG] carve<< tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + ")");

            if (meshCurrent)
            {
                // State B: nothing more to do. The mesh is already
                // correct for this heightmap; we only needed to push the
                // mask back through the applier so the GPU catches up.
                // Skip BuildOrRefreshFloor and skip the OnFloorRefreshed
                // event so decorations stay anchored.
                UpdateTerrainCapRenderer(dfTerrain);
                return;
            }

            if (!SkipFloorMesh)
            {
                if (DiagTrace)
                    Debug.Log("[DeepWaters.DIAG] mesh>> " + DiagCtx(dfTerrain, fromPromoteEvent));
                BuildOrRefreshFloor(dfTerrain, terrainData, tile, holes);
                if (DiagTrace)
                    Debug.Log("[DeepWaters.DIAG] mesh<< tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + ")");
                // Fire the OnFloorRefreshed signal so UnderwaterDecorations
                // (and any other newer subsystems) can queue per-tile work.
                UpdateTerrainCapRenderer(dfTerrain);
                RaiseFloorRefreshed(dfTerrain);
            }
            else
                RemoveFloor(dfTerrain);

            // Neighbor refresh DISABLED in this build. The working backup
            // ran a deferred refresh of each newly-promoted tile's four
            // neighbors so the per-tile BFS distance field could smooth
            // across shared edges. With the global pre-baked distance
            // field (DeepWaterDistanceBake) loaded at startup, that
            // smoothing happens at bake time — adjacent tiles already
            // agree at their shared boundary, so the refresh is
            // redundant work.
            //
            // EnqueueNeighborsForRefresh additionally called
            // FindObjectsOfType<DaggerfallTerrain> per promotion. During
            // streaming with ~70 tiles in the loaded ring that's ~70
            // FindObjectsOfType calls in rapid succession, each
            // allocating a ~70-entry array, then 4× iterating to find
            // neighbors by world-position match. The cost was a major
            // chunk of the frame stalls the user reported. Killing the
            // call entirely removes both the allocation flood and the
            // O(n²) neighbor lookup.
            // if (!suppressNeighborRefresh)
            //     EnqueueNeighborsForRefresh(dfTerrain);
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
                    // Already-loaded, already-rendered tile: treat as a
                    // refresh (no live re-carve), not a promote event.
                    HandlePromote(t, unityTerrain.terrainData, false);
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

        // True if this tile, or any of its 8 immediate neighbours, contains a
        // Daggerfall location (town/city/etc.). Carving holes anywhere in this
        // 3x3 footprint can race a town's building-collider setup and native-
        // crash Unity 2019.4, so Option B leaves the whole footprint vanilla.
        private static bool IsNearLocation(DaggerfallTerrain dfTerrain)
        {
            // The tile itself — already resolved at promote; cheapest check.
            if (dfTerrain.MapData.hasLocation)
                return true;

            DaggerfallUnity dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.ContentReader == null)
                return false;

            int mx = dfTerrain.MapPixelX;
            int my = dfTerrain.MapPixelY;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    int nx = mx + dx;
                    int ny = my + dy;
                    if (nx < 0 || ny < 0 || nx >= MapsFile.MaxMapPixelX || ny >= MapsFile.MaxMapPixelY)
                        continue;

                    if (dfu.ContentReader.HasLocation(nx, ny))
                        return true;
                }
            }

            return false;
        }

        // Compact diagnostic context for the v0.55.11 trace logs.
        private static string DiagCtx(DaggerfallTerrain dfTerrain, bool fromPromoteEvent)
        {
            int px = -1, py = -1;
            GameManager gm = GameManager.Instance;
            if (gm != null && gm.PlayerGPS != null)
            {
                px = gm.PlayerGPS.CurrentMapPixel.X;
                py = gm.PlayerGPS.CurrentMapPixel.Y;
            }
            return "tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + ")" +
                   " src=" + (fromPromoteEvent ? "promote" : "refresh") +
                   " shore=" + IsShorelineTile(dfTerrain) +
                   " locTile=" + dfTerrain.MapData.hasLocation +
                   " locNear=" + IsNearLocation(dfTerrain) +
                   " terrainUpd=" + DeepWaterRuntime.IsTerrainUpdateActive +
                   " locLoading=" + DeepWaterLocationLoadGate.IsAnyLocationLoading +
                   " player=(" + px + "," + py + ")";
        }

        // True if this tile's heightmap has BOTH land (above ocean) and water
        // (at/below ocean) cells — a coastline tile, the kind whose late carve
        // we suspect races the shore's terrain/collider setup.
        private static bool IsShorelineTile(DaggerfallTerrain dfTerrain)
        {
            float[,] h = dfTerrain.MapData.heightmapSamples;
            if (h == null)
                return false;

            DaggerfallUnity dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.TerrainSampler == null)
                return false;

            float oceanThreshold = dfu.TerrainSampler.OceanElevation / dfu.TerrainSampler.MaxTerrainHeight;
            int rows = h.GetLength(0);
            int cols = h.GetLength(1);
            bool hasLand = false;
            bool hasWater = false;
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    if (h[y, x] > oceanThreshold + 1e-5f)
                        hasLand = true;
                    else
                        hasWater = true;
                    if (hasLand && hasWater)
                        return true;
                }
            }
            return false;
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
            DeepWaterTerrainCapRenderer.Apply(
                dfTerrain,
                DeepWaterTerrainCapRenderer.ShouldHidePureOceanCap(dfTerrain));
        }

        private static void ApplyHoleMask(DaggerfallTerrain dfTerrain, TerrainData terrainData, bool[,] holes, bool fromPromoteEvent)
        {
            DeepWaterHoleApplier applier = DeepWaterHoleApplier.Instance;
            if (applier != null)
                applier.Enqueue(dfTerrain, holes);
        }

        private static void QueueSolidResetIfTouched(DaggerfallTerrain dfTerrain, TerrainData terrainData, string reason, bool fromPromoteEvent)
        {
            if (dfTerrain == null || terrainData == null)
                return;

            if (!DeepWaterHoleApplier.HasTouchedTerrainData(terrainData))
                return;

            DeepWaterHoleApplier applier = DeepWaterHoleApplier.Instance;
            if (applier != null)
                applier.EnqueueSolidReset(dfTerrain, terrainData, reason);
        }
    }
}
