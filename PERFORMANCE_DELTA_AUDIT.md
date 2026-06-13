# Deep Waters Init-to-Current Performance Delta Audit

Baseline: `12496d19` (`init`)
Current: `d19502b` (`dream fix`)

Purpose: document source areas where the current code differs from init in ways that could reasonably affect Daggerfall Unity runtime performance. This is not a verdict on every change. It is the bisection map.

## Short List

Most suspicious:

1. Runtime terrain collider gate replacing terrain holes.
2. Expanded underwater decoration refresh/population.
3. Dynamic water surface meshes replacing one flat surface.
4. Higher seafloor mesh/collider complexity.
5. Larger active underwater content footprint from fog-distance scaling and new enemy/fish/loot behavior.

Probably not the main cause:

1. `DeepWaterStreamingBuffer`: present in source, but disabled and not installed by Bootstrap.
2. Editor bake tools: large diff, but editor-only.
3. `DeepWaterPerfProbe`: instrumentation-only, except for tiny stopwatch overhead.
4. DREAM material fixes: can add spawn-time allocation, but should not create recurring multi-second terrain spikes by itself.

## 1. Terrain Holes vs Runtime Collider Gate

Init behavior:

- Bootstrap adds `DeepWaterHoleApplier`.
- `DeepWaterFloorBuilder` computes a hole mask, enqueues it, builds the seafloor mesh, and enqueues neighbor refreshes.
- `DeepWaterHoleApplier` drains terrain hole writes with `SetHolesDelayLOD()` and `SyncTexture()` over multiple frames.
- Once holes exist, vanilla terrain colliders/rendering are physically opened where water was carved.

Current behavior:

- `DeepWaterHoleApplier` is deleted and no longer installed.
- `DeepWaterFloorBuilder` computes the mask but only builds DeepWaters mesh/collider children.
- Vanilla terrain is not modified. To let the player descend, `OutdoorSwimDriver` disables nearby `TerrainCollider`s while the player is over carved water.
- `OutdoorSwimDriver` now has a large collider gate:
  - Runs once per rendered frame from `Update()` / post-restore.
  - Uses `DeepWaterTerrainLookup.GetLoadedTerrains()`.
  - Disables all nearby water tile `TerrainCollider`s.
  - Re-enables old colliders unless `WouldEjectSubmergedPlayer()` says the player would be depenetrated upward.
  - Holds colliders disabled across transition failures.

DFU performance risk:

- High.
- This moves critical work from terrain promotion time into active gameplay.
- Enabling/disabling terrain colliders interacts with PhysX and the `CharacterController`, and that downstream physics work is not fully captured by the DeepWaters per-frame stopwatch.
- The current logs saying `modTotal < 1.3ms` while frames hit hundreds/thousands of ms are compatible with this: the measured C# gate can be cheap while Unity/DFU/PhysX/render work caused by the collider state change is expensive.
- This is the biggest behavioral difference from init.

Why grid size did not settle it:

- Reducing the seafloor mesh grid reduces mesh collider cooking/build cost.
- It does not remove the current architecture of disabling/re-enabling DFU terrain colliders at runtime.
- So if the spike source is collider state churn / terrain streamer interaction, grid size is not expected to fully fix it.

## 2. Seafloor Mesh and MeshCollider Complexity

Init behavior:

- `DeepWaterFloorMesh.VertexGridSize = 33`.
- Builds a lower-density mesh.
- Creates a `MeshCollider`, but terrain holes mean the mesh collider is more of the underwater floor, not the primary way to bypass vanilla terrain.

Current behavior:

- `VertexGridSize = 65`.
- Builds more detailed walls/skirt geometry.
- Tracks `BuildVersion`, `LastBuiltHeightmapSamples`, and collider build version.
- `EnsureRuntimeCollider()` can recreate/cook mesh colliders and calls `Physics.SyncTransforms()`.
- `DeepWaterFloorBuilder` can call `EnsureRuntimeCollider()` on current meshes during refresh.

DFU performance risk:

- Medium to high at stream/load time.
- A 65x65 grid has roughly 4x the base vertex density of 33x33, before wall/skirt additions.
- MeshCollider cooking and `Physics.SyncTransforms()` are classic Unity hitch sources.
- However, because the 33x33 experiment did not remove the spikes, this is probably not the single root cause. It can still amplify transition hitches.

## 3. Water Surface Generation

Init behavior:

- `WaterSurfaceManager` uses a shared flat quad mesh from `WaterSurfaceResources`.
- It scales one visual water object per terrain.
- It also used a `BoxCollider` surface marker/trigger path.

Current behavior:

- `WaterSurfaceManager` builds a generated per-tile water mesh.
- It creates separate top and underside renderers sharing that generated mesh.
- It calls `mesh.RecalculateBounds()` and `mesh.RecalculateNormals()` on generated meshes.
- It destroys old generated meshes during replacement/removal.
- It gates forced refreshes through `DeepWaterRuntime.CanMutateTerrainData`.

DFU performance risk:

- Medium at stream/load/settings-refresh time.
- More mesh allocation and per-tile mesh processing than init.
- Not likely to cause continuous per-frame lag by itself because this is mostly promotion/refresh work.
- More likely to contribute to "new save / map-pixel transition / water entry" stalls.

## 4. Post-Transition Refresh and Runtime Gating

Init behavior:

- `DeepWaterRuntime.OnLoad()` immediately calls:
  - `WaterSurfaceManager.RefreshLoadedSurfaces()`
  - `DeepWaterFloorBuilder.RefreshLoadedTiles()`
- `OnStartLoad()` and teleport clear the hole applier queue and transient content.

Current behavior:

- Adds:
  - `CanRunLightRuntimeWork`
  - `CanRunHeavyRuntimeWork`
  - `IsLoadGraceActive`
  - `IsTerrainUpdateActive`
  - `CanMutateTerrainData`
- Hooks `StreamingWorld.OnUpdateTerrainsStart/End`.
- Reflects `StreamingWorld.terrainUpdateRunning`.
- Adds `DeepWaterLocationLoadGate`.
- Defers post-load/post-teleport refresh until `PumpPostTransitionRefresh()`.
- `PostLoadHeavyWorkGraceSeconds` is currently `0f`, so there is no real grace period once terrain/location gates open.

DFU performance risk:

- Medium.
- Reflection itself is low cost.
- The risk is work bunching: when the gates open, `RefreshLoadedSurfaces()` and `RefreshLoadedTiles(false)` can scan all loaded `DaggerfallTerrain`s and build/update meshes in one moment.
- If that moment lands right after DFU terrain/location updates, it can stack with DFU's own expensive frame.

## 5. Location Load Gate

Init behavior:

- No explicit location-load gate.

Current behavior:

- New `DeepWaterLocationLoadGate`.
- Tracks `StreamingWorld.OnCreateLocationGameObject` / `OnUpdateLocationGameObject`.
- Keeps `IsLoadGraceActive` true while DFU is laying out location objects.
- Has a 12 second stuck-counter watchdog.

DFU performance risk:

- Low to medium.
- The event hooks and counter are cheap.
- The gate can defer work until location loading completes. That is good if it avoids contention, but it can also create a burst immediately after DFU finishes location layout.
- In the logs, the big DFU location timings appear near DeepWaters post-transition refresh, so this area is worth instrumenting, but the gate itself is unlikely to be the direct cost.

## 6. Streaming Buffer

Init behavior:

- No `DeepWaterStreamingBuffer`.

Current behavior:

- File exists.
- Both runtime flags are hard-disabled:
  - `EnableTerrainDistanceBuffer = false`
  - `EnableSwimWorldPositionOverride = false`
- Bootstrap does not add `DeepWaterStreamingBuffer`.

DFU performance risk:

- Currently none.
- It should stay out of the suspect pool unless it is installed/enabled later.

## 7. Terrain Lookup

Init behavior:

- `DeepWaterTerrainLookup` only maps map pixels through `StreamingWorld.GetTerrainFromPixel()`.
- Small cache by map-pixel key.

Current behavior:

- Adds frame-snapshotted live-terrain lookup by world position.
- Uses `StreamingWorld.StreamingTarget` children when available.
- Falls back to `FindObjectsOfType<DaggerfallTerrain>()` only when the streaming target is unavailable.
- Used heavily by:
  - swim/shore checks
  - collider gate
  - seafloor clamp
  - decorations/loot/fish/enemy placement

DFU performance risk:

- Medium, mostly because it is on hot paths.
- The current implementation is intentionally cheaper than whole-scene `FindObjectsOfType`.
- Still, it adds per-frame terrain scans over streaming children while swimming, which init did not do.
- If `StreamingTarget` is ever null during transitions, the fallback whole-scene scan can become expensive.

## 8. Outdoor Swim Driver

Init behavior:

- Smaller driver.
- Swimming decision is mostly Y-level plus a shore raycast.
- `FixedUpdate()` recomputes water state every physics step.
- No separate movement controller.
- No runtime terrain collider gate.

Current behavior:

- Much larger driver.
- Swimming decision requires:
  - not standing on shore,
  - actual carved seafloor under player,
  - sufficient water depth,
  - vanilla terrain not above waterline.
- Adds runtime terrain collider gate.
- Caches `FixedUpdate()` swim decision once per rendered frame to avoid physics catch-up spirals.
- Adds `OutdoorSwimMovementController`:
  - swim speed modifier,
  - stroke movement,
  - seafloor/shore anti-tunnel clamps,
  - `Physics.SyncTransforms()` when clamping.

DFU performance risk:

- High.
- This is the main current hot path that did not exist in init.
- The code has optimizations, but it still changes physics, terrain colliders, movement, and transform sync around the player while DFU is streaming.
- If the current problem happens when cresting/entering/leaving water, this is the first runtime subsystem to bisect.

## 9. Decorations

Init behavior:

- Populate radius is fixed at `1` tile around the player (3x3).
- Work queue drains all queued terrains when processed.
- Decorations subscribe to terrain promotion and map-pixel changes.

Current behavior:

- Populate radius is dynamic:
  - minimum `2`
  - maximum `3`
  - based on `StreamingWorld.TerrainDistance`
- Worker has an `Update()` loop.
- Every 2 seconds it enqueues the loaded player area.
- It processes only one tile per work cycle.
- It also subscribes to `DeepWaterFloorBuilder.OnFloorRefreshed`.
- It tracks floor build versions, removes stale decoration groups, and preserves player-tile decorations.

DFU performance risk:

- Medium to high.
- Radius increase is large:
  - init radius 1 = up to 9 tiles,
  - current radius 2 = up to 25 tiles,
  - current radius 3 = up to 49 tiles.
- Even at one tile per cycle, streaming transitions can keep the queue busy and cause ongoing spawn/destroy work.
- Decoration placement scans heightmaps, samples seafloor mesh, checks slope, and creates billboard batches.
- This is especially relevant with DREAM because replacement probing/material paths are active.

## 10. Decoration Rendering and DREAM Compatibility

Init behavior:

- Archive billboards use DFU `DaggerfallBillboardBatch`.
- Replacement billboards also use a DFU batch with replacement material.
- Animated archive records fall back to archive batching.

Current behavior:

- Archive batches still use DFU batch, but material is wrapped with DeepWaters underwater decoration material.
- DREAM/static replacements build custom material-batch meshes.
- DREAM/imported animated replacements spawn individual `DaggerfallBillboard` GameObjects so texture-swapped animations work.
- Materials are cloned per renderer and owned/destroyed by small owner components.
- Rubble/loot reuse this decoration rendering path.

DFU performance risk:

- Medium at spawn/despawn time, low per frame.
- More allocations than init:
  - generated mesh arrays,
  - cloned materials,
  - sometimes individual billboard GameObjects.
- This is a plausible contributor when a wreck/treasure/decor area first appears, but not the best explanation for water-entry terrain spikes unless decoration refresh is happening at the same moment.

## 11. Loot, Fish, and Enemies

Init behavior:

- Loot/enemy/fish pulse systems already exist.
- Fish cap:
  - normal 72
  - paradise 180
- Enemy cap:
  - 16
- Loot spawn/despawn distances are fixed.

Current behavior:

- Heavy runtime work is gated by `DeepWaterRuntime.CanRunHeavyRuntimeWork`.
- Fish:
  - lower caps inside locations (36 / 72),
  - species choice depends on biome/depth,
  - fish raycasts are reduced/strided compared with init.
- Enemies:
  - depth-banded enemy selection,
  - new `OutdoorAquaticEnemyPilot` per aquatic enemy,
  - `Physics.SyncTransforms()` after enemy spawn.
- Loot:
  - spawn and despawn range scale with underwater vision distance,
  - rubble uses the decoration batch path,
  - treasure cluster placement accepts dynamic ranges.

DFU performance risk:

- Medium.
- Fish is probably improved versus init for per-frame cost.
- Enemies add per-enemy `Update()`, but cap remains small.
- Loot/debris can spawn farther away and persist farther away when vision distance is high, increasing active objects.
- None of this should cause a 5 second spike alone unless spawning happens during terrain/location updates or decoration mesh/material work.

## 12. Whole-Scene Renderer Scans

Init behavior:

- `UnderwaterWaveShadowFix` scans scene renderers once per second to find external water/wave renderers.
- `CutoutDepthQueueFix` scans all `MeshRenderer`s once per second.

Current behavior:

- Wave/water renderer suppression path is removed.
- `CutoutDepthQueueFix` scans every 8 seconds and slices work across frames using `GetSharedMaterials()`.

DFU performance risk:

- Current is lower risk than init here.
- This area is unlikely to explain current-only lag spikes.

## 13. Bake, Water Classification, and Bathymetry

Init behavior:

- Per-tile `DeepWaterTileData` builds/connects local distance fields from live heightmaps and neighbor edges.
- This has many per-tile loops and neighbor lookups during promotion.

Current behavior:

- Adds global `DeepWaterDistanceBake`.
- Runtime loads distance/mask data from a baked asset.
- Adds fine water mask and edge-distance field.
- Adds `DeepWaterWaterClassification`.
- Tile data becomes mostly coordinate conversion and bake lookups.
- Bathymetry uses blended climate depth and longer shelf/deep-water curves.

DFU performance risk:

- Low to medium.
- Startup memory/load cost is higher.
- Per-tile runtime classification is generally cheaper and more consistent than init's local BFS.
- The risk is not normal per-frame performance; it is promotion-time loops over water cells and any large bake asset load.
- Re-baking affects correctness/seams/classification. It should not be expected to fix current lag spikes unless the current bake is causing excessive tiles to be treated as water/content-bearing.

## 14. Global Time.maximumDeltaTime Clamp

Init behavior:

- No global physics catch-up clamp.

Current behavior:

- Bootstrap clamps `Time.maximumDeltaTime` to `0.10s` if higher.

DFU performance risk:

- Low as a cause, medium as a behavior change.
- This should reduce physics catch-up spirals after a hitch.
- It can change how a hitch feels, but it is unlikely to create the original expensive frame.

## Suggested Bisection Order

To isolate current-only lag while preserving the init baseline, test these clusters one at a time:

1. Runtime collider gate / no-hole architecture.
2. Decoration refresh radius and 2-second player-area enqueue.
3. Water surface mesh generation.
4. Seafloor mesh density/collider cooking.
5. Loot/debris DREAM rendering path.
6. Enemy pilot / `Physics.SyncTransforms()` on enemy spawn.
7. Fish/content pulse systems.

The shortest high-signal experiment is to keep current visuals/content as-is but temporarily disable only the collider gate's `TerrainCollider` enable/disable behavior and check whether the water-entry/exit spikes disappear. That may break swimming correctness, but as a diagnostic it tests the largest init-to-current architectural change directly.

## Close-Enough Optimization Pass

This section is not another root-cause list. It is the "what can we make cheaper without losing the soul of the feature" list. Bias: fewer moving parts, fewer live objects, fewer runtime physics mutations.

### Already Cut Locally

1. Enemy pilot.
   - Current-at-audit behavior: aquatic enemies got an `OutdoorAquaticEnemyPilot` that sampled water/floor every frame and manually moved them through the water column.
   - Close-enough call: delete it. Let DFU enemy AI behave normally.
   - Functionality loss: underwater enemies may be less elegant vertically.
   - Worth it because: it avoids changing player-facing enemy AI behavior and removes a per-enemy `Update()` path.

2. Explicit `Physics.SyncTransforms()`.
   - Current-at-audit behavior: sync calls existed after seafloor collider rebuild, player anti-tunnel clamp, and enemy spawn.
   - Close-enough call: remove them.
   - Functionality loss: same-frame physics queries may see moved transforms one physics step later.
   - Worth it because: forced global physics sync is exactly the kind of "small line, giant hitch" code to avoid during terrain streaming.

### Best Optimization Candidates

1. Shrink the terrain-collider gate.
   - Area: `OutdoorSwimDriver` collider gate.
   - Current cost shape: every active swimming frame can scan loaded terrains, compute a desired collider set, disable nearby terrain colliders, and conditionally restore old ones.
   - Close-enough cut: reduce the 250m disable ring and 300m eject padding aggressively, or update the set only when the player changes tile / crosses a depth threshold.
   - Functionality loss: less tolerance for extreme swim speed, streaming stalls, or boundary-crossing edge cases.
   - Why first: this is still the largest runtime physics behavior that init did not have.

2. Cap decoration population radius.
   - Area: `UnderwaterDecorations`.
   - Current cost shape: radius 2-3 means 25-49 candidate tiles, plus a 2-second player-area refresh and floor-refresh subscription.
   - Close-enough cut: cap at radius 1 or 2, and rely mostly on terrain promotion/map-pixel changes instead of periodic area refresh.
   - Functionality loss: decorations appear less far out, or a tile may dress slightly later.
   - Why good: underwater dressing is atmosphere, not mechanics. Radius 1 already covered the core experience in init.

3. Cache DREAM underwater materials.
   - Area: `UnderwaterDecorationBatchFactory.ApplyUnderwaterDecorationMaterial()`.
   - Current cost shape: cloned material per renderer, then owner components destroy those clones later.
   - Close-enough cut: one cached underwater material per source material.
   - Functionality loss: almost none unless a source material is mutated per-instance after spawn.
   - Why good: keeps the DREAM fix while cutting allocation/destruction churn.

4. Lower generated water-surface resolution.
   - Area: `WaterSurfaceManager.SurfaceGridResolution = 64`.
   - Current cost shape: per-tile generated mesh, top and underside renderers, bounds/normals recalculation.
   - Close-enough cut: try 32, then 16. Skip normal recalculation if the shader does not need normals.
   - Functionality loss: water edge follows shoreline less precisely.
   - Why good: a slightly blockier water film is acceptable if it stops transition stalls.

5. Simplify seafloor/skirt geometry.
   - Area: `DeepWaterFloorMesh`.
   - Current cost shape: 65x65 grid plus shore skirt construction and MeshCollider cooking.
   - Close-enough cut: keep the lower grid, reduce skirt width/noise complexity, or only build expensive skirt geometry near visible/player-adjacent tiles.
   - Functionality loss: rougher seabed, less perfect shore-gap hiding.
   - Why second-order: 33x33 alone did not fix the spikes, so do this after the collider gate and decoration radius.

6. Stop scaling content range with clear-water visibility.
   - Area: loot/fish/enemy encounter distances.
   - Current cost shape: clearer water can spawn and keep content farther away.
   - Close-enough cut: fixed spawn/despawn distances regardless of fog visibility.
   - Functionality loss: clear water may look a bit emptier at long range.
   - Why good: the player mostly notices nearby life and loot; far-away persistence is expensive decoration.

7. Delay and spread post-transition refresh.
   - Area: `DeepWaterRuntime.PumpPostTransitionRefresh()`.
   - Current cost shape: once gates open, surface and floor refreshes can bunch into the same recovery frame DFU is already using after load/teleport/location updates.
   - Close-enough cut: add a real short grace period and process a few terrains per frame.
   - Functionality loss: after loading, water visuals/content may settle over a second or two.
   - Why good: delayed correctness is better than a frozen game.

8. Lower live fish/content caps.
   - Area: `UnderwaterPassiveFishSpawner`, loot tracking, enemy caps.
   - Current cost shape: fish can reach 72/180 outdoors, loot can persist far away, enemies cap at 16.
   - Close-enough cut: reduce caps before optimizing individual behaviours.
   - Functionality loss: less busy water.
   - Why good: object count is the boring lever that usually works.

### Things Not Worth Optimizing First

1. Editor bake tools.
   - Editor-only. Leave them alone for runtime lag.

2. `DeepWaterStreamingBuffer`.
   - Disabled and not installed. Do not spend time optimizing inert code.

3. `DeepWaterPerfProbe`.
   - Tiny stopwatch overhead and useful evidence. Keep it until the lag is understood.

4. Fish raycast throttling.
   - Current fish behavior already looks cheaper than init's per-frame raycast shape. Cut fish count before rewriting fish movement.

### Lazy Bisection Order

1. Collider gate radius/update cadence.
2. Decoration radius and periodic refresh.
3. DREAM material cache.
4. Water surface resolution.
5. Post-transition refresh spreading.
6. Seafloor/skirt simplification.
7. Content caps/ranges.

Do not optimize all of these at once. Cut one lever, build, test the same water-entry/exit route, then keep or revert. The fastest path is boring and measurable.
