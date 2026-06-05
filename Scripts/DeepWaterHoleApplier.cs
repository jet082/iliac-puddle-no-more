// Project:         Iliac Puddle No More
// License:         MIT

using System;
using System.Collections;
using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Defers all Terrain hole writes out of the busy promotion frame.
    /// Calling the high-level <c>TerrainData.SetHoles</c> while DFU is
    /// streaming terrain native-crashes the player runtime because it
    /// synchronously invalidates LOD/vegetation/collision while Unity 2019.4
    /// is still rebuilding adjacent terrain. The stable path waits for DFU's
    /// terrain and location work to go quiet, then applies one direct
    /// <c>SetHoles</c> write per rendered frame. A delayed
    /// <c>SetHolesDelayLOD</c> + <c>SyncTexture</c> path remains as a
    /// diagnostic fallback only.
    /// </summary>
    public class DeepWaterHoleApplier : MonoBehaviour
    {
        // Delay before applying the first queued hole. The known-good build
        // drains immediately (0s): the drain only ever runs on calm frames
        // (WaitForTerrainMutationWindow gates every write on
        // !terrainUpdateRunning) and uses SetHolesDelayLOD, which never
        // rebuilds the LOD quadtree, so the old multi-second "let the LOD
        // settle" cooldown isn't needed and just delayed the bay deepening.
        public static float PostStreamCooldownSeconds = 0f;

        // Drain pacing. These remain public diagnostic knobs, but the stable
        // path below deliberately handles one tile per rendered frame.
        // Leaving a terrain in the delayed-holes state across a camera cull
        // can native-crash TerrainRenderer::ForceSplitParent on Unity 2019.4.
        public static int ApplyBatchSize = 1;
        public static int FramesBetweenPhases = 2;
        public static int SyncBatchSize = 1;

        // Cap total applies per drain session. 0 = unlimited. The known-good
        // build drains the whole queue each cycle (0): SetHolesDelayLOD does
        // not touch the LOD quadtree, so carving every loaded water tile in a
        // ring is safe and avoids the void tiles a 1-per-cycle cap produced
        // when the player outran the carve.
        public static int MaxAppliesPerDrain = 0;

        // -1 = no radius limit (carve every queued water tile). The old
        // "carve only the player's tile" clamp was a defensive measure for the
        // immediate-SetHoles path; with SetHolesDelayLOD on calm frames the
        // whole loaded ring carves cleanly, so distant tiles stop being voids.
        public static int ActiveCarveRadiusInMapPixels = -1;

        // Production path after the direct SetHoles probe: full 128x128
        // direct writes can stall Unity 2019.4 for several terrains in a
        // row. Use delayed holes, but SyncTexture immediately in the same
        // coroutine slice so no rendered frame sees an unsynced delayed mask.
        public static bool UseDelayedHolesSyncFallback = true;

        // Diagnostic logging.
        public static bool Verbose = false;
        // v0.55: real terrain holes re-enabled. The mask is computed at the
        // promote event but the actual write is deferred (Enqueue -> drain
        // coroutine) so it never runs inside DFU's promotion call stack, and
        // it uses SetHolesDelayLOD + SyncTexture so the terrain LOD quadtree
        // never becomes hole-aware. Set back to true to fall back to the
        // outdoor swim collider gate if holes ever destabilize a GPU/driver.
        public static bool DisableRuntimeTerrainHoles = false;
        private static bool disabledNoticeLogged;
        private static bool firstInactiveCarveLogged;
        private static bool firstDrainCarveLogged;

        public static DeepWaterHoleApplier Instance { get; private set; }

        private const int TerrainMutationSettleFrames = 12;
        private static int lastTerrainMutationFrame = -100000;

        internal static bool HasPendingHoleMutationWork
        {
            get
            {
                DeepWaterHoleApplier applier = Instance;
                return applier != null &&
                       !DisableRuntimeTerrainHoles &&
                       (applier.drainCoroutine != null || applier.queue.Count > 0);
            }
        }

        internal static bool IsTerrainHoleMutationSettling
        {
            // Always false now. The deferred drain runs continuously during
            // exploration, so gating swim engagement / fog on "is a hole write
            // pending" (a leftover from the immediate-SetHoles era) would make
            // swimming flaky. SetHolesDelayLOD never disrupts collision or LOD
            // mid-frame, so there is nothing to wait out.
            get { return false; }
        }

        private class PendingApply
        {
            public DaggerfallTerrain DfTerrain;
            public bool[,] Holes;
            public int MapPixelX;
            public int MapPixelY;
            public bool IsReset;
            public string Reason;
            // Snapshot of the heightmap array reference this mask was
            // computed from. Used by the post-sync confirmation tracker
            // so HandlePromoteCore can tell whether the mask currently
            // sitting in TerrainData (and the GPU) matches the heightmap
            // the tile is presently wearing.
            public float[,] HeightmapRef;
        }

        private readonly List<PendingApply> queue = new List<PendingApply>();
        private readonly Dictionary<DaggerfallTerrain, PendingApply> latestByTerrain =
            new Dictionary<DaggerfallTerrain, PendingApply>();

        // Per-terrain "this heightmap ref was successfully synced to the
        // GPU" tracker. The DeepWaterFloorBuilder.IsCurrentBuild guard
        // consults this to decide whether a tile's existing mesh + mask
        // are FULLY current or just visually-current-but-mask-pending.
        // Without this:
        //   - A first promote can race with applier.ClearQueue() (fires
        //     on save load / teleport / location enter), losing the
        //     mask write before it ever reaches the GPU.
        //   - My IsCurrentBuild guard then locks the broken state in:
        //     mesh exists, heightmap ref matches, return early — but
        //     the GPU never received the mask, so the hole was never
        //     punched. Vanilla terrain stays collidable and the player
        //     walks on it under the water plane.
        //   - The user's "fixes itself on revisit" observation is
        //     exactly this: DFU eventually re-promotes the tile with a
        //     fresh heightmap array, the guard releases, the second
        //     build completes, and the mask reaches the GPU.
        // Tracking sync confirmation lets the guard distinguish those
        // two cases and re-enqueue when the mask never landed, without
        // re-running ComputeHoleMask + Enqueue every cycle.
        private static readonly Dictionary<DaggerfallTerrain, float[,]> syncedHeightmapRefByTerrain =
            new Dictionary<DaggerfallTerrain, float[,]>();
        private static readonly HashSet<int> initializedTerrainDataIds = new HashSet<int>();
        private static readonly HashSet<int> touchedTerrainDataIds = new HashSet<int>();

        internal static string ApplyModeName
        {
            get { return UseDelayedHolesSyncFallback ? "SetHolesDelayLOD+SyncTexture" : "SetHoles"; }
        }

        public static bool IsHeightmapSynced(DaggerfallTerrain dfTerrain, float[,] heightmapRef)
        {
            if (dfTerrain == null || heightmapRef == null) return false;
            float[,] synced;
            return syncedHeightmapRefByTerrain.TryGetValue(dfTerrain, out synced) &&
                   object.ReferenceEquals(synced, heightmapRef);
        }

        internal static bool HasTouchedTerrainData(TerrainData terrainData)
        {
            if (DisableRuntimeTerrainHoles)
                return false;

            int id = GetTerrainDataId(terrainData);
            return id != 0 && touchedTerrainDataIds.Contains(id);
        }

        private Coroutine drainCoroutine;
        private bool installed;
        private int totalApplied;
        private int totalSynced;

        void Awake()
        {
            Instance = this;
        }

        void OnEnable()
        {
            if (installed) return;
            StreamingWorld.OnUpdateTerrainsEnd += OnStreamEnd;
            installed = true;
            Debug.Log("[DeepWaters.Applier] Installed terrain hole writer mode=" +
                      ApplyModeName + " cooldown=" +
                      PostStreamCooldownSeconds.ToString("F1") + "s build=" +
                      DeepWaters.BuildStamp);
            if (Verbose) Debug.Log("[DeepWaters.Applier] Subscribed to OnUpdateTerrainsEnd");
        }

        void OnDisable()
        {
            if (!installed) return;
            StreamingWorld.OnUpdateTerrainsEnd -= OnStreamEnd;
            installed = false;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Enqueue(DaggerfallTerrain dfTerrain, bool[,] holes)
        {
            if (dfTerrain == null || holes == null) return;

            if (DisableRuntimeTerrainHoles)
            {
                MarkHeightmapSynced(dfTerrain);
                LogDisabledNotice();
                return;
            }

            // Dedup: if this tile already has a pending queue entry, update its
            // mask in place rather than adding another. Without this, the
            // refresh path re-enqueuing every stream cycle while the drain waits
            // out a location load grows the queue without bound (the v0.55.3
            // "remaining=168" explosion). The drain's IsLatestEntry check also
            // guards against duplicates, but this keeps the queue itself small.
            PendingApply existing;
            if (latestByTerrain.TryGetValue(dfTerrain, out existing) && queue.Contains(existing))
            {
                existing.Holes = holes;
                existing.HeightmapRef = dfTerrain.MapData.heightmapSamples;
                existing.IsReset = false;
                existing.Reason = "carve";
                BeginDrain("enqueue-update");
                return;
            }

            var entry = new PendingApply
            {
                DfTerrain = dfTerrain,
                Holes = holes,
                MapPixelX = dfTerrain.MapPixelX,
                MapPixelY = dfTerrain.MapPixelY,
                IsReset = false,
                Reason = "carve",
                HeightmapRef = dfTerrain.MapData.heightmapSamples,
            };
            latestByTerrain[dfTerrain] = entry;
            queue.Add(entry);
            if (Verbose) Debug.Log("[DeepWaters.Applier] Enqueued tile (" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + "); queue size=" + queue.Count);
            BeginDrain("enqueue");
        }

        // === Simple deferred carve (v0.55.4) ===
        // In-game ASYNC promotes (terrainUpdateRunning=true) run the promote
        // event inside DFU's streaming coroutine, alongside concurrent terrain
        // rebuilds; carving holes inline there native-crashes Unity 2019.4. The
        // builder routes those here and we apply them one frame later from
        // Update(), which is outside DFU's promotion call stack. There is NO
        // streaming gate (SetHolesDelayLOD outside the call stack is safe during
        // streaming, proven by the known-good build), no texture "seed", and no
        // player-distance dropping — the heavyweight drain above is what stalled
        // save loads (WaitForTerrainMutationWindow blocked the whole load) and
        // exploded the queue. Synchronous init promotes (the player's own tile
        // on a save load) are carved immediately by the builder instead, so the
        // hole exists before the player is dropped in.
        private struct DeferredCarve
        {
            public DaggerfallTerrain DfTerrain;
            public bool[,] Holes;
            public int MapPixelX;
            public int MapPixelY;
            public bool IsReset;
        }

        private readonly List<DeferredCarve> deferredCarves = new List<DeferredCarve>();
        private const int DeferredCarvesPerFrame = 8;

        public void EnqueueDeferredCarve(DaggerfallTerrain dfTerrain, bool[,] holes, bool isReset)
        {
            if (DisableRuntimeTerrainHoles || dfTerrain == null)
                return;

            deferredCarves.Add(new DeferredCarve
            {
                DfTerrain = dfTerrain,
                Holes = holes,
                MapPixelX = dfTerrain.MapPixelX,
                MapPixelY = dfTerrain.MapPixelY,
                IsReset = isReset,
            });
        }

        void Update()
        {
            if (DisableRuntimeTerrainHoles || deferredCarves.Count == 0)
                return;

            int budget = DeferredCarvesPerFrame;
            while (budget-- > 0 && deferredCarves.Count > 0)
            {
                DeferredCarve c = deferredCarves[0];
                deferredCarves.RemoveAt(0);

                // Drop stale entries: the tile may have been recycled to a
                // different map pixel before we reached it.
                if (c.DfTerrain == null ||
                    c.DfTerrain.MapPixelX != c.MapPixelX ||
                    c.DfTerrain.MapPixelY != c.MapPixelY)
                    continue;

                Terrain terrain = c.DfTerrain.GetComponent<Terrain>();
                TerrainData terrainData = terrain != null ? terrain.terrainData : null;
                if (terrainData == null)
                    continue;

                if (c.IsReset)
                    ResetTerrainNow(c.DfTerrain, terrainData);
                else
                    CarveTerrainNow(c.DfTerrain, terrainData, c.Holes);
            }
        }

        /// <summary>
        /// Carve a tile's holes from inside the DFU promote event using the
        /// LOD-safe two-phase flow (SetHolesDelayLOD + SyncTexture). The mask
        /// is written for both rendering and collision, but the terrain LOD
        /// quadtree is never made hole-aware — so later patch subdivision
        /// (e.g. when the camera rises through the surface) does not hit the
        /// Unity 2019.4 TerrainRenderer::ForceSplitParent native crash. Doing
        /// it at the promote event (before the tile's first render) also means
        /// the hole is present the moment the tile becomes visible, with no
        /// pop-in. Returns true if the write was issued.
        /// </summary>
        public static bool CarveTerrainNow(DaggerfallTerrain dfTerrain, TerrainData terrainData, bool[,] holes)
        {
            if (DisableRuntimeTerrainHoles || dfTerrain == null || terrainData == null || holes == null)
                return false;

            try
            {
                terrainData.enableHolesTextureCompression = false;

                int id = GetTerrainDataId(terrainData);
                if (id != 0)
                    initializedTerrainDataIds.Add(id);

                // Use Unity's two-phase hole flow, NOT the high-level SetHoles.
                // SetHoles rebuilds the terrain's LOD data with hole awareness;
                // once a patch is flagged as holed, a later subdivision (e.g.
                // when the camera rises through the water surface) hits the
                // Unity 2019.4 TerrainRenderer::ForceSplitParent native crash.
                // SetHolesDelayLOD writes the holes mask for BOTH rendering and
                // collision without touching the LOD quadtree, and SyncTexture
                // pushes just the holes texture to the GPU. The patch tree stays
                // "hole-unaware" and subdivides normally — exactly how the
                // known-good build crosses the waterline without crashing.
                if (DeepWaterFloorBuilder.DiagTrace)
                    Debug.Log("[DeepWaters.DIAG] SetHolesDelayLOD>> tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + ")");
                terrainData.SetHolesDelayLOD(0, 0, holes);
                if (DeepWaterFloorBuilder.DiagTrace)
                    Debug.Log("[DeepWaters.DIAG] SyncTexture>> tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + ")");
                terrainData.SyncTexture(TerrainData.HolesTextureName);
                if (DeepWaterFloorBuilder.DiagTrace)
                    Debug.Log("[DeepWaters.DIAG] holewrite<< tile=(" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + ")");

                MarkTerrainDataTouched(terrainData);
                MarkHeightmapSynced(dfTerrain);
                if (!firstInactiveCarveLogged)
                {
                    Debug.Log("[DeepWaters.Applier] Promote-time terrain hole carving is ACTIVE " +
                              "(first tile (" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY +
                              ") carved at promote event via SetHolesDelayLOD). build=" + DeepWaters.BuildStamp);
                    firstInactiveCarveLogged = true;
                }
                // NOTE: deliberately NOT calling MarkTerrainDataMutated().
                // The mutation-settle window exists to keep the swim driver
                // off live terrain whose collision is changing under it. An
                // inactive carve is baked in before the tile renders, so its
                // collision is stable the moment it activates — flagging it
                // as "settling" would needlessly suppress swim engagement
                // for ~12 frames after every tile streams in.
                if (Verbose)
                    Debug.Log("[DeepWaters.Applier] Carved tile (" +
                              dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY +
                              ") at promote event via SetHolesDelayLOD + SyncTexture.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DeepWaters.Applier] Inactive promote-time hole carve failed for tile (" +
                                 dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + "): " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Reset a tile's holes to all-solid from inside the promote event
        /// (the safe pre-first-render window). Used when a previously-carved
        /// TerrainData is recycled for a non-ocean / no-hole map pixel; DFU's
        /// PromoteTerrainData never touches the holes texture, so a recycled
        /// tile would keep stale holes without this. No-op for tiles this
        /// applier never carved.
        /// </summary>
        public static bool ResetTerrainNow(DaggerfallTerrain dfTerrain, TerrainData terrainData)
        {
            if (DisableRuntimeTerrainHoles || dfTerrain == null || terrainData == null)
                return false;

            int id = GetTerrainDataId(terrainData);
            if (id == 0 || !touchedTerrainDataIds.Contains(id))
                return false;

            int holeRes = terrainData.holesResolution;
            if (holeRes <= 0)
                return false;

            try
            {
                terrainData.enableHolesTextureCompression = false;
                terrainData.SetHolesDelayLOD(0, 0, CreateSolidHoles(holeRes));
                terrainData.SyncTexture(TerrainData.HolesTextureName);
                syncedHeightmapRefByTerrain.Remove(dfTerrain);
                if (Verbose)
                    Debug.Log("[DeepWaters.Applier] Reset recycled tile (" +
                              dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + ") to all-solid.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DeepWaters.Applier] Inactive solid-reset failed for tile (" +
                                 dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + "): " + ex.Message);
                return false;
            }
        }

        public void EnqueueSolidReset(DaggerfallTerrain dfTerrain, TerrainData terrainData, string reason)
        {
            if (dfTerrain == null || terrainData == null)
                return;

            if (DisableRuntimeTerrainHoles)
                return;

            int holeRes = terrainData.holesResolution;
            if (holeRes <= 0)
                return;

            var entry = new PendingApply
            {
                DfTerrain = dfTerrain,
                Holes = CreateSolidHoles(holeRes),
                MapPixelX = dfTerrain.MapPixelX,
                MapPixelY = dfTerrain.MapPixelY,
                IsReset = true,
                Reason = string.IsNullOrEmpty(reason) ? "reset" : reason,
                HeightmapRef = dfTerrain.MapData.heightmapSamples,
            };

            latestByTerrain[dfTerrain] = entry;
            queue.Add(entry);
            Debug.Log("[DeepWaters.Applier] Enqueued all-solid terrain hole reset for tile (" +
                      entry.MapPixelX + "," + entry.MapPixelY + ") reason=" +
                      entry.Reason + "; queue size=" + queue.Count);
            BeginDrain("solid reset");
        }

        public void ClearQueue()
        {
            queue.Clear();
            latestByTerrain.Clear();
            deferredCarves.Clear();
            // NOTE: syncedHeightmapRefByTerrain is intentionally NOT
            // cleared here. ClearQueue is fired by save load / teleport
            // to abandon pending applies — but masks that were already
            // synced before the clear are still on the GPU and don't
            // need re-syncing. Clearing the synced-tracker would force
            // every loaded tile through a redundant re-enqueue on the
            // next OnUpdateTerrainsEnd, defeating the v0.52.0 perf win.
            // Stale entries (terrain destroyed / recycled) are cleaned
            // lazily by the IsHeightmapSynced reference-equality check.
        }

        public int PendingCount
        {
            get { return queue.Count; }
        }

        private void OnStreamEnd()
        {
            if (Verbose) Debug.Log("[DeepWaters.Applier] OnUpdateTerrainsEnd received; queue size=" + queue.Count + " drainActive=" + (drainCoroutine != null));
            BeginDrain("stream end");
        }

        private void BeginDrain(string reason)
        {
            if (DisableRuntimeTerrainHoles)
            {
                queue.Clear();
                latestByTerrain.Clear();
                LogDisabledNotice();
                return;
            }

            if (drainCoroutine != null) return;
            if (queue.Count == 0) return;

            if (Verbose) Debug.Log("[DeepWaters.Applier] Starting drain from " + reason + "; queue size=" + queue.Count);
            drainCoroutine = StartCoroutine(DrainQueue());
        }

        private IEnumerator DrainQueue()
        {
            // Leave DFU's promotion call stack (one frame) before touching any
            // holes; then drain FIFO with SetHolesDelayLOD, a short gap, then
            // SyncTexture. We do NOT block on the whole terrain stream / save
            // load (that broad blocking stalled the load and exploded the queue
            // in v0.55.3) — SetHolesDelayLOD outside the call stack is safe
            // during streaming. We DO wait out the narrow window while DFU is
            // laying out a location's RMB blocks: the v0.55.12 load crash dump
            // truncated right after "Location GameObject Created: Fonthope End /
            // Grimton", proving a concurrent SetHolesDelayLOD/SyncTexture races
            // CreateRMBBlockGameObject and native-crashes Unity 2019.4.
            yield return null;

            if (PostStreamCooldownSeconds > 0f)
                yield return new WaitForSeconds(PostStreamCooldownSeconds);

            if (Verbose)
                Debug.Log("[DeepWaters.Applier] Draining " + queue.Count + " queued hole masks.");

            int appliesThisDrain = 0;
            while (queue.Count > 0)
            {
                if (MaxAppliesPerDrain > 0 && appliesThisDrain >= MaxAppliesPerDrain)
                    break;

                // FIFO, in promote order. Stale duplicates for the same tile
                // are skipped cheaply by IsLatestEntry inside ApplyOne.
                PendingApply entry = queue[0];
                queue.RemoveAt(0);

                // NO calm-gate (v0.55.22): carve during streaming, BEFORE DFU
                // stitches neighbour LODs — exactly like the minimal working
                // build. Gating the carve until the world settled (v0.55.15+)
                // made it land on an already-stitched, fully-subdivided tile,
                // which is when the holed-patch render crash fires. Testing
                // whether the working build's early-carve timing avoids it.
                if (ApplyOne(entry))
                {
                    appliesThisDrain++;
                    totalApplied++;

                    if (!firstDrainCarveLogged)
                    {
                        Debug.Log("[DeepWaters.Applier] Deferred terrain hole carving is ACTIVE " +
                                  "(first tile (" + entry.MapPixelX + "," + entry.MapPixelY +
                                  ") via SetHolesDelayLOD). build=" + DeepWaters.BuildStamp);
                        firstDrainCarveLogged = true;
                    }

                    // Space the GPU texture sync a couple of frames after the
                    // mask write — the known-good pacing, so a full streaming
                    // ring never does every SetHolesDelayLOD AND every
                    // SyncTexture in one frame (that burst crashed v0.55.4).
                    for (int j = 0; j < FramesBetweenPhases; j++)
                        yield return null;

                    if (SyncOne(entry))
                        totalSynced++;

                    // Record sync confirmation for the builder's IsCurrentBuild
                    // guard. Resets clear it so a later ocean rebuild re-carves.
                    if (entry.IsReset)
                    {
                        if (entry.DfTerrain != null)
                            syncedHeightmapRefByTerrain.Remove(entry.DfTerrain);
                    }
                    else if (entry.DfTerrain != null && entry.HeightmapRef != null)
                    {
                        syncedHeightmapRefByTerrain[entry.DfTerrain] = entry.HeightmapRef;
                    }

                    RemoveLatest(entry);
                }

                // One frame between tiles.
                yield return null;
            }

            drainCoroutine = null;

            if (queue.Count > 0)
                BeginDrain("remaining queue");
        }

        // Wait for a fully calm frame before touching terrain holes: no active
        // DFU terrain update AND no location laying out its RMB blocks. The
        // deferred drain's SetHolesDelayLOD/SyncTexture race both — a concurrent
        // terrain-update render and a CreateRMBBlockGameObject collider setup
        // each native-crash Unity 2019.4. v0.55.12/13/14 all crashed on load
        // when the drain's first carve+sync landed during the 2nd terrain update
        // while a coastal town (GENRAS04) loaded, slipping past the
        // location-only gate. Ruling out the spawners (v0.55.14's heavy-work
        // gate left the crash byte-identical) localized it to the drain itself.
        //
        // This is bounded, NOT a whole-save-load block (which stalled v0.55.3):
        // terrain updates always end, and the location gate self-heals via a 12s
        // watchdog, so the drain can't deadlock. The hole just lands a moment
        // after the world settles (a brief above-water blip on an underwater
        // load) instead of mid-stream.
        private IEnumerator WaitForCalmTerrainFrame()
        {
            while (DeepWaterRuntime.IsTerrainUpdateActive ||
                   DeepWaterLocationLoadGate.IsAnyLocationLoading)
                yield return null;
        }

        private bool TryTakeBestEntryForPlayer(out PendingApply entry)
        {
            entry = null;

            int playerX;
            int playerY;
            if (!TryGetPlayerMapPixel(out playerX, out playerY))
            {
                Debug.Log("[DeepWaters.Applier] Dropping " + queue.Count +
                          " queued terrain hole masks because PlayerGPS is unavailable.");
                queue.Clear();
                latestByTerrain.Clear();
                return false;
            }

            int bestIndex = -1;
            int bestDistanceSq = int.MaxValue;
            for (int i = 0; i < queue.Count; i++)
            {
                PendingApply candidate = queue[i];
                int dx = candidate.MapPixelX - playerX;
                int dy = candidate.MapPixelY - playerY;
                int distanceSq = dx * dx + dy * dy;
                if (!IsInsideActiveCarveRadius(dx, dy))
                    continue;

                if (distanceSq < bestDistanceSq)
                {
                    bestDistanceSq = distanceSq;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                Debug.Log("[DeepWaters.Applier] Dropping " + queue.Count +
                          " distant queued terrain hole masks; player tile=(" +
                          playerX + "," + playerY + ") activeRadius=" +
                          ActiveCarveRadiusInMapPixels + ".");
                queue.Clear();
                latestByTerrain.Clear();
                return false;
            }

            entry = queue[bestIndex];
            queue.RemoveAt(bestIndex);

            int dropped = DropEntriesOutsideActiveRadius(playerX, playerY);
            if (dropped > 0)
            {
                Debug.Log("[DeepWaters.Applier] Dropped " + dropped +
                          " distant queued terrain hole masks after selecting tile (" +
                          entry.MapPixelX + "," + entry.MapPixelY + "); player tile=(" +
                          playerX + "," + playerY + ") activeRadius=" +
                          ActiveCarveRadiusInMapPixels + ".");
            }

            return true;
        }

        private int DropEntriesOutsideActiveRadius(int playerX, int playerY)
        {
            int dropped = 0;
            for (int i = queue.Count - 1; i >= 0; i--)
            {
                PendingApply candidate = queue[i];
                int dx = candidate.MapPixelX - playerX;
                int dy = candidate.MapPixelY - playerY;
                if (IsInsideActiveCarveRadius(dx, dy))
                    continue;

                RemoveLatest(candidate);
                queue.RemoveAt(i);
                dropped++;
            }

            return dropped;
        }

        private static bool IsInsideActiveCarveRadius(int dx, int dy)
        {
            if (ActiveCarveRadiusInMapPixels < 0)
                return true;

            return Mathf.Abs(dx) <= ActiveCarveRadiusInMapPixels &&
                   Mathf.Abs(dy) <= ActiveCarveRadiusInMapPixels;
        }

        private static bool TryGetPlayerMapPixel(out int x, out int y)
        {
            x = 0;
            y = 0;

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerGPS == null)
                return false;

            var pos = gameManager.PlayerGPS.CurrentMapPixel;
            x = pos.X;
            y = pos.Y;
            return true;
        }

        private bool ApplyOne(PendingApply entry)
        {
            if (!IsEntryCurrent(entry, "apply"))
                return false;

            if (!IsLatestEntry(entry, "apply"))
                return false;

            var unityTerrain = entry.DfTerrain.GetComponent<Terrain>();
            if (unityTerrain == null || unityTerrain.terrainData == null)
            {
                if (Verbose) Debug.Log("[DeepWaters.Applier] Tile missing Terrain/TerrainData");
                return false;
            }

            try
            {
                TerrainData terrainData = unityTerrain.terrainData;
                // Match the known-good build exactly: a plain SetHolesDelayLOD.
                // No texture "seed" first (that extra write crashed v0.55.3 on a
                // location tile during load), and never the high-level SetHoles
                // (it makes the LOD quadtree hole-aware and crashes on a later
                // subdivision when the camera surfaces).
                if (DeepWaterFloorBuilder.DiagTrace)
                    Debug.Log("[DeepWaters.DIAG] drainCarve>> tile=(" + entry.MapPixelX + "," + entry.MapPixelY + ") reset=" + entry.IsReset);
                terrainData.SetHolesDelayLOD(0, 0, entry.Holes);
                MarkTerrainDataTouched(terrainData);
                if (DeepWaterFloorBuilder.DiagTrace)
                    Debug.Log("[DeepWaters.DIAG] drainCarve<< tile=(" + entry.MapPixelX + "," + entry.MapPixelY + ")");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DeepWaters.Applier] SetHolesDelayLOD failed for tile (" +
                                 entry.MapPixelX + "," + entry.MapPixelY + "): " + ex.Message);
                return false;
            }
        }

        private bool SyncOne(PendingApply entry)
        {
            if (!UseDelayedHolesSyncFallback)
                return true;

            if (!IsEntryCurrent(entry, "sync"))
                return false;

            // INTENTIONAL: no IsLatestEntry check here.
            //
            // SyncTexture pushes whatever the TerrainData currently holds
            // to the GPU; it does not depend on which PendingApply object
            // wrote the data. Dropping IsLatestEntry from sync is safe
            // because the sync is idempotent and content-agnostic: if a
            // newer mask somehow landed between ApplyOne and this call,
            // syncing pushes that newer state, which is still correct.

            var unityTerrain = entry.DfTerrain.GetComponent<Terrain>();
            if (unityTerrain == null || unityTerrain.terrainData == null)
            {
                if (Verbose) Debug.Log("[DeepWaters.Applier] Tile missing Terrain/TerrainData before sync");
                return false;
            }

            try
            {
                TerrainData terrainData = unityTerrain.terrainData;
                // v0.55.24 TEST: do NOT set enableHolesTextureCompression here.
                // The minimal working build never touches it. Changing the holes
                // texture's compression AFTER SetHolesDelayLOD but before
                // SyncTexture (the deferred order) is the suspected render-thread
                // crash — the immediate build set it BEFORE the mask write and
                // loaded fine, so the ORDER, not the value, is the likely cause.
                if (DeepWaterFloorBuilder.DiagTrace)
                    Debug.Log("[DeepWaters.DIAG] drainSync>> tile=(" + entry.MapPixelX + "," + entry.MapPixelY + ")");
                terrainData.SyncTexture(TerrainData.HolesTextureName);
                if (DeepWaterFloorBuilder.DiagTrace)
                    Debug.Log("[DeepWaters.DIAG] drainSync<< tile=(" + entry.MapPixelX + "," + entry.MapPixelY + ")");
                MarkTerrainDataMutated();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DeepWaters.Applier] Holes texture sync failed for tile (" +
                                 entry.MapPixelX + "," + entry.MapPixelY + "): " + ex.Message);
                return false;
            }
        }

        private IEnumerator WaitForTerrainMutationWindow(string context)
        {
            string lastReason = null;
            while (!DeepWaterRuntime.CanMutateTerrainData)
            {
                string reason = DeepWaterRuntime.TerrainMutationBlockReason;
                if (string.IsNullOrEmpty(reason))
                    reason = "unknown terrain mutation block";

                if (reason != lastReason)
                {
                    Debug.Log("[DeepWaters.Applier] Waiting to " + context +
                              ": " + reason + " (queue=" + queue.Count + ")");
                    lastReason = reason;
                }

                yield return null;
            }
        }

        private static bool PrepareTerrainDataForHoleWrite(TerrainData terrainData, PendingApply entry, string phase)
        {
            if (terrainData == null)
                return false;

            try
            {
                terrainData.enableHolesTextureCompression = false;

                int id = GetTerrainDataId(terrainData);
                if (id != 0 && !initializedTerrainDataIds.Contains(id))
                {
                    bool[,] seed = terrainData.GetHoles(0, 0, 1, 1);
                    // Seed via the LOD-safe path too: a plain SetHoles here can
                    // run on an already-rendered terrain (rare active solid
                    // reset) and would make the LOD hole-aware, risking the
                    // ForceSplitParent crash on later subdivision.
                    terrainData.SetHolesDelayLOD(0, 0, seed);
                    terrainData.SyncTexture(TerrainData.HolesTextureName);
                    initializedTerrainDataIds.Add(id);
                    MarkTerrainDataMutated();
                    Debug.Log("[DeepWaters.Applier] Initialized holes texture for tile (" +
                              entry.MapPixelX + "," + entry.MapPixelY + ") before " +
                              phase + ".");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DeepWaters.Applier] Failed to prepare TerrainData holes for tile (" +
                                 entry.MapPixelX + "," + entry.MapPixelY + ") before " +
                                 phase + ": " + ex.Message);
                return false;
            }
        }

        private bool IsEntryCurrent(PendingApply entry, string phase)
        {
            if (entry.DfTerrain == null)
            {
                if (Verbose) Debug.Log("[DeepWaters.Applier] Tile destroyed before " + phase);
                return false;
            }

            if (entry.DfTerrain.MapPixelX != entry.MapPixelX ||
                entry.DfTerrain.MapPixelY != entry.MapPixelY)
            {
                if (Verbose) Debug.Log("[DeepWaters.Applier] Tile recycled before " + phase + " (was " + entry.MapPixelX + "," + entry.MapPixelY +
                                       "; now " + entry.DfTerrain.MapPixelX + "," + entry.DfTerrain.MapPixelY + ")");
                return false;
            }

            return true;
        }

        private bool IsLatestEntry(PendingApply entry, string phase)
        {
            PendingApply latest;
            if (!latestByTerrain.TryGetValue(entry.DfTerrain, out latest) ||
                !object.ReferenceEquals(latest, entry))
            {
                if (Verbose) Debug.Log("[DeepWaters.Applier] Skipping stale " + phase + " for tile (" +
                                       entry.MapPixelX + "," + entry.MapPixelY + ")");
                return false;
            }

            return true;
        }

        private void RemoveLatest(PendingApply entry)
        {
            PendingApply latest;
            if (entry.DfTerrain != null &&
                latestByTerrain.TryGetValue(entry.DfTerrain, out latest) &&
                object.ReferenceEquals(latest, entry))
                latestByTerrain.Remove(entry.DfTerrain);
        }

        private static bool[,] CreateSolidHoles(int holeRes)
        {
            bool[,] holes = new bool[holeRes, holeRes];
            for (int y = 0; y < holeRes; y++)
            {
                for (int x = 0; x < holeRes; x++)
                    holes[y, x] = true;
            }
            return holes;
        }

        private static int GetTerrainDataId(TerrainData terrainData)
        {
            return terrainData != null ? terrainData.GetInstanceID() : 0;
        }

        private static void MarkTerrainDataTouched(TerrainData terrainData)
        {
            int id = GetTerrainDataId(terrainData);
            if (id != 0)
                touchedTerrainDataIds.Add(id);
        }

        private static void MarkHeightmapSynced(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null || dfTerrain.MapData.heightmapSamples == null)
                return;

            syncedHeightmapRefByTerrain[dfTerrain] = dfTerrain.MapData.heightmapSamples;
        }

        private static void LogDisabledNotice()
        {
            if (disabledNoticeLogged)
                return;

            Debug.Log("[DeepWaters.Applier] Runtime Terrain holes are disabled; using outdoor swim collider gate instead.");
            disabledNoticeLogged = true;
        }

        private static void MarkTerrainDataMutated()
        {
            lastTerrainMutationFrame = Time.frameCount;
        }
    }
}
