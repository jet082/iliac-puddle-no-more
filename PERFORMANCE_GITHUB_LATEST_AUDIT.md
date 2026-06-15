# Performance Study: Current Test Baseline vs GitHub Latest

Date: 2026-06-14

Current local baseline:

- `C:\S...\Games\daggerfall-unity-master\Assets\Game\Mods\deep-waters`
- `DeepWaters.Version = v0.56.1`
- `BuildStamp = 2026-06-10 painted-water-authority`
- The last "lower live content caps" patch has been undone.

GitHub latest inspected:

- `https://github.com/jet082/iliac-puddle-no-more`
- Commit `9355dc8d6c98e8a3b02d5e98cc1634ebfa5fa9aa`
- `DeepWaters.Version = v0.56.9`
- `BuildStamp = 2026-06-12 streaming-source-fix`

## Short Answer

The old `PERFORMANCE_DELTA_AUDIT.md` bisection list is effectively exhausted. The next useful work should not be another blind constant tweak. GitHub latest has a different performance shape: it removes or throttles several whole-scene scans, disables the streaming buffer path, clamps physics catch-up, makes terrain lookup cheaper, and turns several hard-coded heavy defaults into smaller settings.

Most likely first patch to try: stop installing `DeepWaterStreamingBuffer`, matching GitHub latest. Current local installs it in `DeepWaters.Bootstrap.cs`; latest does not install it and also leaves its internal expansion flags false.

## Highest-Probability Performance Differences

### 1. Streaming buffer is installed locally but not in GitHub latest

Current local:

- `DeepWaters.Bootstrap.cs:32` installs `go.AddComponent<DeepWaterStreamingBuffer>()`.
- `DeepWaterStreamingBuffer` can increase `StreamingWorld.TerrainDistance`.
- That can force DFU to keep/promote a larger terrain ring while swimming.

GitHub latest:

- `DeepWaters.Bootstrap.cs` no longer adds `DeepWaterStreamingBuffer`.
- `DeepWaterStreamingBuffer.cs` also has:
  - `EnableTerrainDistanceBuffer = false`
  - `EnableSwimWorldPositionOverride = false`

Why this is probably the best first test:

- It is a single install-path removal.
- It directly affects DFU streaming pressure, not just mod object counts.
- The user-visible symptom is large transition/streaming hitches.
- The latest working version deliberately does not run the buffer.

Suggested next experiment:

1. Remove/comment only `go.AddComponent<DeepWaterStreamingBuffer>()`.
2. Leave the class present.
3. Build and test the same swim route.

Expected functionality risk:

- If the buffer was helping far-ocean streaming, land/seafloor generation might reveal the old "only fixes when surfacing" behavior. But latest reportedly works, so the other latest changes may make the buffer unnecessary.

### 2. Terrain lookup no longer does repeated whole-scene terrain sweeps

Current local:

- `DeepWaterTerrainLookup` uses `Object.FindObjectsOfType<DaggerfallTerrain>()` to build its frame snapshot.
- `DeepWaterFloorBuilder.RefreshLoadedTiles()` and `WaterSurfaceManager.RefreshLoadedSurfaces()` also sweep all loaded terrain when called.

GitHub latest:

- `DeepWaterTerrainLookup` first enumerates `GameManager.Instance.StreamingWorld.StreamingTarget` children.
- The comments say this replaces a whole-scene object sweep with a direct streaming-target child iteration.
- It clears the lookup on terrain update end.

Why this matters:

- `DeepWaterTerrainLookup.TryGetByWorldPosition()` is used by swim state, collider gating, seafloor queries, fish/decor/loot placement, shore tests, and fog.
- If any of those paths call into lookup during a hitchy frame, a whole-scene `FindObjectsOfType` is the wrong shape of cost.
- This could explain why pausing/surfacing changes behavior: the expensive lookup and streaming state recover when frame pressure drops.

Suggested experiment after disabling the buffer:

1. Port the GitHub latest `StreamingTarget` enumeration path into `DeepWaterTerrainLookup`.
2. Do not change swim logic at the same time.
3. Test town/coast/open-ocean transitions.

Expected risk:

- Low. It should return the same active terrain set, just cheaper.

### 3. Collider gate changes are broader than the earlier radius test

Earlier local attempts tried the radius part alone. GitHub latest changes the gate as a bundle:

- Radius/padding: `250/300 -> 96/96`.
- Adds `ColliderGateRefreshIntervalSeconds = 0.15`.
- Ensures the collider gate runs at most once per rendered frame.
- Replaces several shore probes with direct vanilla-land and carved-seafloor checks.
- Caches FixedUpdate swim/fog state once per frame so catch-up physics steps do not recompute water columns repeatedly.

Why the earlier radius-only test was inconclusive:

- If the main cost is repeated lookup/gate work, smaller radius alone does not fix repeated scans.
- If Unity is in physics catch-up, FixedUpdate can run many times for one rendered frame; latest avoids recomputing the same water decision in every physics step.

Suggested experiment:

After buffer removal and terrain lookup optimization, port only:

- once-per-frame gate guard,
- 0.15s gate cadence,
- FixedUpdate state cache.

Leave shore correctness changes separate unless needed for behavior.

Expected risk:

- Medium. The gate is correctness-critical around shore entry/exit.

### 4. Physics catch-up clamp is in latest, missing locally

Current local:

- No `Time.maximumDeltaTime` clamp.

GitHub latest:

- `DeepWaters.Bootstrap.cs` clamps `Time.maximumDeltaTime` to `0.10f`.
- The comment explicitly describes the symptom: long frame -> many physics catch-up steps -> every following frame stays slow until pause clears the debt.

Why this is important:

- The user reported pausing affected/fixed spawning/performance in earlier tests.
- This clamp does not reduce the first expensive frame, but it can prevent a hitch from turning into sustained terrible performance.

Suggested experiment:

- Add only the clamp from latest.
- Test whether "massive lag spike" becomes a shorter slowdown.

Expected risk:

- Low to medium. It changes global Unity physics catch-up behavior, but latest uses it and it is a tiny patch.

### 5. Current wave/shadow fix does expensive renderer scans and renderer suppression

Current local:

- `UnderwaterWaveShadowFix` scans wave renderers every 1s.
- It calls `FindObjectsOfType<MeshRenderer>()`.
- It examines material arrays, identifies external water/wave renderers, disables them, tracks/restores their renderer state, and expands mesh bounds.
- `CutoutDepthQueueFix` also scans all mesh renderers every 1s and allocates/shared-material arrays.

GitHub latest:

- Removes almost all external wave renderer suppression.
- Keeps only the player indirect light / torch suppression.
- Rewrites cutout material scanning:
  - scan interval `1s -> 8s`,
  - slices work in chunks of 150 renderers,
  - uses `GetSharedMaterials(materialScratch)` to reduce allocations.

Why this is high impact:

- Whole-scene `MeshRenderer` scans in towns are expensive.
- Running them every second during outdoor play is a steady hitch source.
- The latest file is roughly 13 KB smaller because most of that machinery is gone.

Suggested experiment:

1. First remove external wave renderer suppression from `UnderwaterWaveShadowFix`, leaving light/torch suppression.
2. Then add the sliced `CutoutDepthQueueFix`.

Expected risk:

- Visual risk around third-party wave mods.
- Low gameplay risk.

### 6. Mesh generation defaults are much cheaper in latest

Water surface:

- Current local: `SurfaceGridResolution = 64`.
- GitHub latest: default/configurable `WaterSurfaceMeshSize = 16`.
- Latest also removes `mesh.RecalculateNormals()` from water surface generation.

Seafloor:

- Current local: `VertexGridSize = 65`.
- GitHub latest: default/configurable `SeafloorMeshSize = 33`.
- Current local skirt:
  - `SkirtSlopeTangent = 0.3`
  - max width `150m`
  - Perlin width noise per skirt vertex.
- GitHub latest skirt:
  - `SkirtSlopeTangent = 0.6`
  - max width `40m`
  - no skirt width noise.
- Latest removes `Physics.SyncTransforms()` after seafloor collider setup.

Why earlier tests were incomplete:

- Water surface was tried at 32, but latest default is 16 and also skips normals.
- Seafloor was tried at 33, but latest also simplifies the skirt and removes sync.

Suggested experiment:

After the runtime scan/gate fixes, test mesh simplification as a bundle:

1. Surface 16 + no normals.
2. Seafloor 33.
3. Skirt width/noise simplification.
4. Remove seafloor `Physics.SyncTransforms()`.

Expected risk:

- Visible shoreline/seafloor roughness.
- Medium, but latest uses this shape.

### 7. Decoration spawning in latest is less eager

Current local:

- `UnderwaterDecorations` has radius 2-3 based on terrain distance.
- It enqueues player area every 2 seconds.
- It subscribes to `DeepWaterFloorBuilder.OnFloorRefreshed`.

GitHub latest:

- Decoration radius is a setting, default `1`.
- No 2-second periodic player-area enqueue.
- No `OnFloorRefreshed` subscription.
- It still purges stale decorations synchronously when a recycled tile changes map pixel, then queues rebuilds only near the player/map-pixel path.

Why this matters:

- Earlier local decoration-radius tests reduced some values but did not fully match latest's event model.
- The periodic refresh can keep generating work while DFU is already promoting terrain.

Suggested experiment:

- Port latest's `UnderwaterDecorations` enqueue model and default radius `1`.
- Keep it isolated from mesh changes.

Expected risk:

- Decorations may appear later or closer to the player.

### 8. Content caps in latest are settings plus pruning, not just lower constants

Earlier local test only lowered hard constants. GitHub latest also:

- Adds `TransientObjectTracker.PruneToCount()`.
- Calls `PruneToCount()` for fish and loot.
- Lowers fish and loot despawn distances.
- Makes caps configurable:
  - enemies default 8,
  - fish 36,
  - fish paradise 72,
  - loot objects 32,
  - stray loot pulse 4/6.
- Lowers enemy spawn count and candidate attempts:
  - `FullSpawnCount = 4`,
  - `CandidateAttemptsPerSpawn = 4`.
- Fixes loot spawn/despawn ranges to settings instead of clear-water visibility expansion.

Why the previous cap patch did not prove much:

- It did not prune existing over-cap objects.
- It left longer despawn distances.
- It left enemy spawn attempts and spawn count higher.
- It did not move ranges to fixed settings.

Suggested experiment:

Only after heavier runtime suspects are tested, port:

1. `PruneToCount`.
2. Smaller enemy spawn count/attempts/despawn.
3. Fish/loot caps and despawn settings.
4. Fixed loot/encounter distances.

Expected risk:

- Less busy water.
- Low correctness risk.

### 9. Fish raycast throttling is real in latest

Current local:

- Passive fish can raycast for obstacle avoidance every frame.

GitHub latest:

- Adds `ObstacleProbeFrameInterval = 5`.
- Randomizes each fish's probe phase.
- Skips obstacle probes for fish farther than 60m from the player.
- Extends ray length to cover the stride interval.

Why it matters:

- This scales with live fish count.
- It matters most with high fish settings or Fish Paradise.

Suggested experiment:

- Port this only after cap/pruning behavior is settled.

Expected risk:

- Fish collision avoidance is slightly less precise.

### 10. DREAM decoration material cache is in latest, but lower priority

Current local:

- Creates owned underwater decoration material clones per renderer.

GitHub latest:

- Caches underwater materials per source material.
- Removes per-renderer material owner/destructor.

Earlier this was tried alone and did not fix the spike. It is still worth carrying eventually because it reduces allocation/destruction churn, but it is probably not the root cause of massive transition lag.

## Recommended Bisection Order From Here

1. Disable `DeepWaterStreamingBuffer` install.
2. Port `DeepWaterTerrainLookup` `StreamingTarget` enumeration.
3. Add `Time.maximumDeltaTime = 0.1f` clamp.
4. Port collider gate cadence + once-per-frame guard + FixedUpdate state cache.
5. Strip/slice `UnderwaterWaveShadowFix` renderer scans.
6. Port mesh defaults as a bundle: surface 16/no normals, seafloor 33, simpler skirt, no seafloor sync.
7. Port latest decoration enqueue model/radius.
8. Port content settings + `PruneToCount` + fixed ranges.
9. Port passive fish raycast throttling.
10. Port DREAM material cache.
11. Reconsider post-transition staged refresh only after the terrain lookup and mesh-cost changes are in place.

## Why This Ordering

The current bad symptom is a severe runtime/streaming hitch, not just "too many fish." So the first tests should hit systems that affect DFU streaming and whole-scene scans:

- streaming buffer,
- terrain lookup,
- physics catch-up,
- collider gate cadence,
- renderer scans.

The mesh/content changes are still useful, but they are second wave: they reduce how much work happens after streaming decides to do work.

## Minimal Next Patch

If applying one thing next, do this:

```csharp
// DeepWaters.Bootstrap.cs
// Remove:
go.AddComponent<DeepWaterStreamingBuffer>();
```

That exactly matches the biggest install-path difference between this baseline and the GitHub version that reportedly behaves well.

