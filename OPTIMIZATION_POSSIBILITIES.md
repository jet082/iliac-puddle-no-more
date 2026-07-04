# Optimization Possibilities: Map-Pixel Streaming Stutter

Date: 2026-07-03. Analysis of the current source tree (v0.56.x lineage, post-rewrite).
No code changes proposed here — this is the map for future work.

## TL;DR

The stutter while "new stuff loads" is not one expensive system — it is that the mod's
**entire per-tile pipeline runs synchronously inside DFU's terrain promote event**, on the
same frame DFU finishes that tile. Vanilla deliberately spends only a few milliseconds of
main-thread time per streamed tile (Burst jobs off-thread, one tile per frame, batched
billboards). Deep Waters adds an estimated 10–30+ ms of main-thread work to each of those
frames: hole-mask classification, a 65×65 sampled seafloor mesh with skirt, a synchronous
MeshCollider cook, a per-texel cap-clip texture rebuild, a 128×128 water-surface
classification, and (one frame later) a full decoration population pass.

The five highest-ROI changes, in order:

1. **Defer everything except the player's own tile into a time-budgeted work queue**
   (~2 ms/frame), prioritized by distance to player. Underwater fog and the opaque surface
   curtain already hide anything beyond ~36–95 m, so late builds are invisible.
2. **Stop cooking MeshColliders for far tiles.** Only the player's tile and its 8 neighbors
   ever need a walkable seafloor collider. Collider cooking is likely the single largest
   unavoidable main-thread chunk today.
3. **Move the pure-math generation (hole mask, vertex grid, surface cells, cap texels) to a
   worker thread.** All inputs are plain arrays; only the mesh/texture upload must stay on
   the main thread.
4. **Cut redundant per-vertex sampling** — cache the tile's climate neighborhood, capture
   the tile origin once per build, early-out the offshore-only noise layers, and share the
   water classification between the floor builder and the surface builder.
5. **Pool the per-tile scratch buffers** to stop generating ~0.5 MB of garbage per streamed
   tile (GC spikes stack on top of the build spikes).

---

## 1. Why vanilla doesn't stutter (the model to imitate)

Verified against the DFU source in this repo:

- **Heavy math runs off the main thread.** `StreamingWorld.UpdateTerrainDataCoroutine`
  (StreamingWorld.cs:1235) schedules the heightmap/tilemap generation as Unity Jobs via
  `dfTerrain.BeginMapPixelDataUpdate`, then `yield return new WaitUntil(() =>
  jobHandle.IsCompleted)` — the main thread renders normally while worker threads build the
  tile data over however many frames it takes.
- **One tile per frame, hard.** The `UpdateTerrains` coroutine (StreamingWorld.cs:642)
  processes one dirty tile, lays out its nature billboards, then
  `yield return new WaitForEndOfFrame()` (line 670). A full ring rebuild is *spread across
  dozens of frames by design*. (Exception: the `init` path — load/teleport — runs
  synchronously, which is why initial loads are allowed to be slow behind a load screen.)
- **Nature is one batched mesh per tile.** `DaggerfallBillboardBatch` = one draw call per
  archive per tile, built once at layout time. There is no per-tree GameObject cost.
- **Detail is graded by distance.** `TerrainNature.LayoutNature` (TerrainNature.cs:164)
  only attempts imported 3D tree replacements when `terrainDist <= 1`; everything further
  gets cheap billboards. Locations are similar (nearest first).
- **Distance masking.** Terrain fades in far away, at low visual salience. Nothing pops
  inside the player's attention radius.

Deep Waters already imitates (3) — decorations use `DaggerfallBillboardBatch` and material
batches. What it lacks are equivalents of (1), (2) and (4) for its own pipeline: everything
happens on the promote frame, at full detail, at every distance.

## 2. Where the promote-frame time goes today

`DaggerfallTerrain.OnPromoteTerrainData` fires inside DFU's per-tile coroutine step
(StreamingWorld.cs:1227). Three mod subscribers run back-to-back on that frame, for every
promoted tile — including recycled ring tiles the player will not approach for minutes:

| # | Stage | Where | Work per tile (approx.) |
|---|-------|-------|-------------------------|
| 1 | Hole-mask classification | `DeepWaterFloorBuilder.ComputeHoleMask` | 128×128 = 16,384 cells × (tilemap read + heightmap 4-corner test + fine-bake bit test) |
| 2 | Seafloor mesh build | `DeepWaterFloorMesh.Build` | 65×65 = 4,225 vertices × (2 bake bilinears + 4 climate lookups + ~8 `Mathf.PerlinNoise` calls + shore-fit with up to 5 cross-tile heightmap bilinears on coastal vertices) + skirt pass + mesh upload |
| 3 | MeshCollider cook | `DeepWaterFloorMesh.EnsureCollider` | Synchronous PhysX cook of the 4–8k-vertex mesh (already using `cookingOptions = None` + fast midphase, but still main-thread) |
| 4 | Cap-clip texture | `DeepWaterTerrainCapRenderer.ApplyTilemapTextureClip` | New `Color32[dim²]`, per-water-texel spiral search up to radius 8 (≤289 reads/texel), per-texel heightmap region scan, new `Texture2D` + `SetPixels32` + `Apply` (GPU upload) |
| 5 | Water-surface mesh | `WaterSurfaceManager.BuildSurfaceMesh` | 128×128 = 16,384 cells × (promoted-texel scan + tilemap scan + heightmap scan + up to 9 bake point-samples), then shoreline BFS, then **4 feather passes** each rescanning all cells and allocating two fresh `bool[128,128]`, then greedy rect merge + mesh create |
| 6 | Decorations (next frames) | `UnderwaterDecorations.PopulateTile` | Throttled to 1 tile/frame, but each tile is synchronous: passes × ~1,850 samples × (water-column query + floor-mesh sample + 4 slope probes each doing another column query), then billboard batch build of up to 2,304 items |

Supporting observations:

- The per-sample query stack is itself layered: every `GetDistanceToEdgeMeters` /
  `GetBlendedClimate` / `GetNoiseWorldCoords` call goes through
  `DeepWaterTileData.GetGlobalMapFractions`, which reads `transform.position` per call
  (DeepWaterTileData.cs:331). The hole-mask loop already bypasses this (the v0.53.0
  regression fix, see comment at DeepWaterFloorBuilder.cs:244) — **the floor mesh build
  does not**, so a 4,225-vertex build makes ~12,700 transform reads plus ~17,000
  `MapFileReader.GetClimateIndex` calls.
- `DeepBathymetry.SampleDepthMeters` costs ~8 `Mathf.PerlinNoise` calls per sample. Ravine,
  seamount and volcanic-cone layers can't contribute at all inside 1,500–1,800 m of the
  coast, but their noise is still sampled everywhere.
- After a map-pixel crossing, DFU recycles/promotes several tiles (one per frame), so the
  player feels a *train* of heavy frames: N promote frames each carrying stages 1–5, then
  up to 9 decoration frames (stage 6). That is exactly the reported "stutter as new stuff
  loads."
- Steady-state per-frame costs (swim driver probes, fog LateUpdate, encounter pulse) are
  already well-metered — cadenced ticks, per-tick attempt budgets, frame-cached terrain
  snapshot (`DeepWaterTerrainLookup.GetFrameSnapshot`). The streaming path is the problem,
  not the steady state.

Nothing writes real terrain holes anymore (no `SetHoles` calls exist), so terrain-data
mutation is *not* part of the cost — it is all mod-owned mesh/texture generation.

## 3. Ranked optimization proposals

### P1 — Central deferred build queue with a frame budget (the architectural fix)

**What.** Replace "do everything in the promote handler" with "record that this tile needs
a build, then drain a priority queue with a per-frame time budget." The promote handler
shrinks to bookkeeping (~free). A single driver (pumped from `DeepWaters.Update`, like the
decoration queue already is) pops the nearest-to-player pending tile and runs *one stage*
of its pipeline (hole mask → floor mesh → collider-if-near → cap clip → surface), checking
a `Stopwatch` against a ~2–3 ms budget between stages (or between row-chunks inside a
stage, see P3 note on chunking).

**Exception that keeps correctness:** the player's current tile (and any tile the player
could reach before the queue drains — in practice distance ≤ 1) still builds synchronously
on promote. The swim collider gate, water columns and swim state all query the player's
tile; those must never observe a half-built tile. Everything at distance ≥ 2 is deferrable.

**Why it's visually safe (the "gradual reveal" question).** The mod already has the two
masking mechanisms modern streamers rely on:

- Underwater, `UnderwaterVisionDistance` is 22–360 m (default ~95 m); anything beyond it is
  fully fogged. A tile is ~820 m across. A deferred tile two pixels out has *seconds* of
  swim time before its contents could possibly be seen.
- Above water, the top surface has an opaque horizon curtain
  (`SurfaceOpaqueFadeEnd`, WaterSurfaceManager) that hides the loaded-world edge — a
  seafloor that appears a second late under an already-opaque surface is undetectable. The
  one thing worth prioritizing above all else in the queue is the **surface plane** itself
  (it is visible to the horizon and its absence reads as a hole in the sea); conveniently
  it is also one of the cheaper stages once P6 lands.

So "render things gradually outside the player's viewing radius" does not need fades or
LOD morphing — the fog *is* the fade. It only needs the build order to be
nearest-and-frontmost-first and the near ring to always win.

**Ordering within the queue:** distance to player first, then stage priority
(surface > floor mesh > cap clip > collider > decorations). Optionally weight by view
direction (build tiles in the camera's forward hemisphere first) — cheap to add, matches
the "viewing radius" idea, but distance alone probably suffices underwater.

**Interactions to handle:** tile recycling (a queued tile whose `DaggerfallTerrain` got
re-promoted to a new map pixel must drop its stale queue entry — key by map pixel and
verify on pop, the same pattern `DecorationMarker` already uses); save/teleport resets
(`DeepWaterRuntime.OnTransientReset` clears the queue); and the decoration system's
`HasReadyFloor` gate already handles "floor not built yet" correctly, so decorations
naturally chain behind the deferred floor.

**Gain:** removes nearly all mod cost from promote frames; the whole ring builds over
~1–2 s of calm 2-ms slices instead of a burst of 20–30 ms frames.
**Risk:** medium — ordering bugs around recycling and the player-tile-synchronous
exception. Mitigated by the existing diagnostics harness (map_pixel_transition FPS
metrics) and by keeping the synchronous path for distance ≤ 1 initially.

### P2 — Colliders on demand (near ring only), optionally baked off-thread

**What.** `DeepWaterFloorMesh.EnsureCollider` currently cooks a MeshCollider for every
tile that gets a floor, at any distance. Nothing needs that collider unless it is near the
player: the swim driver's feet checks, `EnemyMotor` ground raycasts, fish obstacle probes
and loot placement all operate within ~60–200 m of the player, and enemies/fish only spawn
within `PopulateRadius = 200 m` (UnderwaterEncounterPulse.cs:24). Restrict collider
creation to tiles at Chebyshev distance ≤ 1 from the player's map pixel; on
`PlayerGPS.OnMapPixelChanged`, cook missing neighbors (amortized one per frame through the
P1 queue). Renderer-only tiles keep their visual mesh.

**Additionally (Unity 2019.3+, available here):** `Physics.BakeMesh(meshInstanceId,
convex:false)` can pre-cook the collider data **on a worker thread**; the later
`sharedMesh` assignment then attaches without a main-thread cook. Even for near tiles the
cook can leave the main thread entirely.

**Gain:** eliminates what is probably the largest single un-sliceable main-thread chunk
per tile (PhysX cooking cannot be "chunked" — it is all or nothing, so without this it
would dominate any P1 budget slice it lands in).
**Risk:** low-medium. Verify: enemy spawns outside the collider ring don't fall through
(they spawn from bathymetry queries, not collider raycasts, and get pruned beyond 200 m —
but `EnemyMotor` gravity needs the floor collider under any live enemy, which the ≤ 1 ring
plus 200 m populate radius covers; worth a diagnostic save to confirm the corner where an
enemy chases the player across a pixel boundary).

### P3 — Worker-thread generation for the pure-math stages

**What.** Stages 1, 2 (vertex/color/skirt arrays), 4 (texel array) and 5 (cell
classification + rect merge) are pure functions over plain data: `heightmapSamples`
(`float[,]`), `tilemapSamples` (`byte[,]`), `TileMap` (`Color32[]`), and the bake byte
arrays. None of it touches Unity objects except to *read inputs* and *upload outputs*.
Pipeline shape:

1. Main thread (promote handler / queue pop): capture inputs — tile origin, map pixel,
   array references (DFU allocates fresh arrays per promote, so the mod can safely read
   them while DFU works on other tiles), bake references, sampler constants.
2. Worker (`ThreadPool.QueueUserWorkItem` or a single dedicated builder thread): produce
   `holes[,]`, vertex/color/uv/triangle arrays, patched `Color32[]`, surface quad rects.
3. Main thread (queue pump, budgeted): `mesh.SetVertices(...)`, texture
   `SetPixels32/Apply`, collider attach (P2), decoration batch spawn.

**Platform constraints to respect:**

- Mod code is compiled at load by DFU's old Mono.CSharp (mcs) — no Burst, and job structs
  are riskier than plain threads under that compiler. Plain `System.Threading` is old .NET
  and safe. (If Burst-grade speed ever becomes necessary, the escape hatch is shipping a
  precompiled assembly in the .dfmod, but nothing here needs it.)
- `Mathf.PerlinNoise` is not documented thread-safe. `DeepBathymetry` would need a local
  Perlin implementation (~30 lines, deterministic, and it removes the engine-call overhead
  too). Everything else in the generation path is already engine-free once inputs are
  captured — with two exceptions to fix first: the floor build's shore-fit currently calls
  `DeepWaterTerrainLookup.TryGetByWorldPosition` per coastal vertex (capture the ≤ 4
  neighbor heightmaps + origins up front instead), and `IsFullWaterTile`/climate lookups
  hit `DaggerfallUnity.Instance` (resolve once during capture).

**Alternative if threading feels too invasive:** pure time-slicing on the main thread
(chunk each stage into e.g. 16-row strips processed under the P1 budget). Slower to drain
but zero thread-safety surface. A reasonable first milestone is P1 with chunked stages,
then move the chunks onto a worker later — the chunk boundaries are exactly the
capture/produce/apply seams threading needs anyway.

**Gain:** the remaining per-tile main-thread cost becomes just uploads (~1–2 ms).
**Risk:** medium — input capture discipline, plus mcs-compatibility care with closures.

### P4 — Make each sample cheaper (wins even with no architecture change)

Independent, low-risk cuts, roughly in value order:

- **Cache the climate neighborhood per tile.** `GetBlendedClimate` resolves 4×
  `MapFileReader.GetClimateIndex` per call → per vertex. A tile build touches at most a
  3×3 map-pixel neighborhood; resolve those ≤ 9 climate indices (and their
  `ClimateBaseDepth`/`ClimateBandSignal` values) once in `Initialize`, then blend from the
  cache. Removes ~17k layered calls per tile build.
- **Capture the origin once per build.** Give `DeepWaterTileData` a batch-query path that
  takes a pre-read origin (the hole-mask loop already proved this pattern at
  DeepWaterFloorBuilder.cs:244). Kills ~12.7k `transform.position` reads per floor build
  and similar counts in decoration population.
- **Early-out offshore noise layers by distance.** In `SampleDepthMeters`, skip the
  ravine/seamount/volcanic Perlin samples when `distanceToCoast` is below their minimum
  distances (1,500/1,500/1,800 m) — that's every coastal tile, where builds cluster.
  Roughly halves the noise cost where it matters.
- **Share the water classification between floor and surface.** Stages 1 and 5 classify
  the same tile against the same tilemap/heightmap/bake at 128×128. Compute once, consume
  twice (the surface's criterion is a superset — feather + neighbor BFS — but it can start
  from the hole mask instead of from scratch).
- **Rewrite the feather as one BFS.** `AddLocalShorelineFeather` runs up to 4 full-grid
  passes with fresh allocations per pass; a single BFS with a depth counter does the same
  in one pass over only the frontier cells.
- **Replace the cap spiral search with one flood pass.** `TryFindNearestSolidTexel` is
  O(waterTexels × 289) worst case; a single multi-source BFS from all solid texels
  (standard "nearest-land" dilation) is O(texels) and produces identical results.

**Gain:** plausibly 2–4× on stages 1/2/5 combined.
**Risk:** low; all mechanical, all verifiable against current output (build the same tile
both ways in the editor and diff the arrays).

### P5 — Slice decoration population

`PopulateTile` is already throttled to one tile per frame, but one tile is still
passes × ~1,850 samples plus up to four slope probes per accepted sample, then a
2,304-item batch build — synchronous. Two options, compatible:

- **Slice position generation** across frames: keep a per-tile iterator (row cursor into
  the sample grid) in the queue entry; the deterministic seeding
  (`TileDecorationSeed`) already makes resumability safe as long as the RNG state is owned
  by the iterator rather than re-seeded per slice (capture `Random.state` between slices,
  or switch the placement loop to a local `System.Random(seed)` — cleaner).
- **Batch the spawn**: positions accepted → one `batch.Apply()` at the end (already the
  shape); the slope probes are the expensive part and become cheap once P4's origin-capture
  and mesh-sample paths land (`TrySampleMeshLocalY` is already array-only and could run in
  the P3 worker).

Also worth reconsidering: `MaxDecorationsPerTile = 2304` — the iteration log shows 256 was
tried during the cap experiments. The batch renders as one draw call either way, so this
is mostly *placement* and memory cost, not draw cost; slicing matters more than lowering
the cap.

**Gain:** removes the last 5–15 ms single-frame spike after crossings.
**Risk:** low.

### P6 — Guard and pool the surface builder

- **Add the floor builder's rebuild guard to `WaterSurfaceManager`.** The floor builder
  short-circuits when `LastBuiltHeightmapSamples` is reference-equal
  (DeepWaterFloorBuilder.cs:149); the surface manager rebuilds unconditionally on every
  promote and on every settings refresh. Same guard, same pattern.
- **Pool the scratch buffers.** Every tile build allocates: `holes` bool[128,128], surface
  `cells`/`used`/`visited` + two more per feather pass, vertex/color/uv/tri lists, cap
  `Color32[dim²]`, decoration lists — roughly 0.5 MB of garbage per streamed tile, which
  turns into GC spikes layered on top of the build spikes. All of these are fixed-size per
  tile: one static scratch set (or per-queue-entry set if P3 threads overlap) removes the
  churn entirely. `EstimateWallVertexCapacity` already tries to right-size lists — pooling
  finishes the job.

**Gain:** eliminates GC-driven hitches; small CPU win.
**Risk:** trivial (single-threaded today; pool ownership needs one look when P3 lands).

### P7 — Detail knobs graded by distance (vanilla's `terrainDist` trick)

Once P1's queue exists, the tile's distance at build time is known, so detail can grade
the same way vanilla grades nature:

- **Floor vertex grid:** 65×65 near (distance ≤ 1), 33×33 far. A far tile promoted to the
  near ring re-queues for a full-res rebuild (cheap, amortized; decorations survive because
  the rebuild path already re-versions them — or gate re-spawn on the version bump the way
  `IsCurrentDecoration` already does). The comment at DeepWaterFloorMesh.cs:27 notes 33×33
  under-sampled shallow-save bathymetry — that objection applies to tiles you *swim over*,
  which are exactly the near ring; the far ring is only ever seen through ≥ 800 m of fog
  and surface curtain.
- **Surface classification:** `SurfaceGridResolution = 128` controls shoreline step size
  (~1.6 m/cell). 64 (~3.2 m) is likely indistinguishable at water level and quarters stage
  5; could also be near/far graded. (The GitHub-latest audit noted much smaller surface
  meshes shipped fine.)
- **Skirt:** near tiles keep the full skirt; far tiles could skip the
  `SampleNearbyVanillaLocalY` 4-neighbor refinement (the dominant per-perimeter-vertex
  cost) until promoted near.

**Gain:** 3–4× on far-tile build cost, multiplicative with P3/P4.
**Risk:** medium-low; the near/far rebuild transition is the only new moving part.

### P8 — Bigger swings (later, only if still needed)

- **One global surface plane + mask texture instead of per-tile surface meshes.** The
  surface is flat at ocean Y; the per-tile mesh exists purely to clip to the shoreline.
  The mod already ships clip shaders driven by tilemap textures (the cap path). Uploading
  the fine water mask (or per-tile 128×128 masks) as textures and clipping a handful of
  large camera-following quads in the fragment shader would delete stage 5 entirely.
  Replaces CPU geometry with a cheap shader discard; the main design work is the
  floating-origin/tile-space mapping and keeping the underside curtain behavior.
- **Persistent per-pixel build cache.** Builds are deterministic (bake + heightmap + seed),
  so a small LRU of recent build outputs (vertices/holes/rects keyed by map pixel +
  heightmap identity) makes re-entering a recently-left area free. Memory-for-stutter
  trade; only worth it if revisit stutter still registers after P1–P4.
- **Precompiled assembly for the generation core.** Only if profiling shows the managed
  math is still the wall after threading — unlocks Burst/jobs properly. Costs build
  complexity and loses the mcs-source distribution simplicity.

## 4. What NOT to spend time on

- **Steady-state spawner tuning.** The encounter pulse already meters attempts per tick
  per pixel with despawn radii and live caps; fish raycast throttling and prune-to-count
  are in. Lowering caps further trades content for nothing — the stutter is in the build
  path, not the population counts (the 3,312-decoration pathology was fixed long ago).
- **Terrain lookup.** `DeepWaterTerrainLookup` already snapshots `StreamingTarget` children
  once per frame with a last-hit cache; the old FindObjectsOfType-per-query shape is gone.
  The remaining `FindObjectsOfType` sites are settings-refresh/reset paths, not hot.
- **The swim collider gate and FixedUpdate caching.** Already rate-limited
  (once-per-frame guard + refresh interval) and behaviorally load-bearing — the audit's
  gate items were ported. Touching it buys milliseconds nowhere near the streaming spikes
  and risks shore-entry regressions.
- **Physics catch-up clamp.** `Time.maximumDeltaTime = 0.1f` is already in Bootstrap.
- **`Time.maximumDeltaTime`-style global tricks in general** — the remaining problem is
  the first expensive frame, not the cascade.

## 5. Suggested order and how to verify

Implementation order (each step independently shippable and testable):

1. P6 guard + P4 mechanical cuts (no behavior change, pure cost reduction) — establishes
   the measurement baseline discipline.
2. P2 collider ring (biggest single win for the least architecture).
3. P1 queue with chunked main-thread stages (player tile stays synchronous).
4. P5 decoration slicing (fold into the P1 queue).
5. P3 worker thread for the generation core (+ local Perlin).
6. P7 distance-graded detail.
7. P8 items only if the diagnostics still show transition dips.

Verification uses what's already in the repo:

- The diagnostics harness (`-deepWatersTest`, CSV under `DeepWatersDiagnostics`) already
  records FPS at `map_pixel_transition` events per save — that column is the success
  metric. Add a per-stage `Stopwatch` accumulator (hole mask / floor / collider / cap /
  surface / decorations, max-per-frame and total-per-crossing) to the CSV so each step's
  claim is checked against numbers, not vibes.
- Watch specifically: `ccc` and the transition-heavy saves; worst frame time during the
  10 s after a crossing (not average FPS — averages hide 30 ms singles).
- Correctness gates per step: the existing scenario saves (`gap*`, `ledge*`, `desert`,
  `brokencolliders*`, movement probes) — deferral bugs show up as exactly the historical
  symptom classes (holes, walls, swim-state drops), and those saves exist to catch them.
