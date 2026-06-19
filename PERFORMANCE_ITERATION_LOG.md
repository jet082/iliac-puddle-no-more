# Deep Waters Performance Iteration Log

Date: 2026-06-14

Working source:

- `C:\S...\Games\daggerfall-unity-master\Assets\Game\Mods\deep-waters`

Playable bundles patched after each source change:

- `C:\S...\Games\Daggerfall Unity - Modding\staging\StandaloneWindows\iliac puddle no more.dfmod`
- `C:\S...\Games\Daggerfall Unity - Modding\DaggerfallUnity_Data\StreamingAssets\Mods\iliac puddle no more.dfmod`

Diagnostics command shape:

- Hidden standalone launch with `-deepWatersTest -deepWatersTestCharacter Miranda -deepWatersTestSaves bbb,ccc -deepWatersTestDuration 120 -deepWatersTestQuit`.
- `ShowOptionsAtStart` is temporarily set to `False` before each run and restored to `True` afterward.
- CSV output is under `AppData\LocalLow\Daggerfall Workshop\Daggerfall Unity\DeepWatersDiagnostics`.

## Goal

Keep the known-good functional behavior from the restored working lineage while removing the severe load, transition, and underwater runtime hitches. The current automated test suite measures `bbb` and `ccc` saves across first load, underwater movement, above-water movement, underwater return, boat/surface movement, and map-pixel transitions.

## Pre-Iteration Baseline

Before the current sequence, `ccc` showed a pathological decoration count and very poor FPS:

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 41.79 | 38.70 | 0.00 | 0.00 | 1.00 | 16.00 |
| bbb | underwater_outbound / phase_start | 54.89 | 43.00 | 0.00 | 0.00 | 0.10 | 16.00 |
| bbb | above_water_levitation / phase_start | 56.01 | 43.00 | 0.00 | 0.00 | 0.00 | 16.00 |
| bbb | underwater_return / phase_start | 53.79 | 43.00 | 0.00 | 0.00 | 0.00 | 16.00 |
| bbb | surface_boat_like / phase_start | 55.15 | 43.00 | 0.00 | 0.00 | 0.00 | 16.00 |
| ccc | initial_load / first_10s | 6.89 | 0.00 | 1.00 | 15.00 | 8.00 | 24.00 |
| ccc | underwater_outbound / phase_start | 34.34 | 3312.00 | 1.10 | 19.50 | 8.00 | 24.00 |
| ccc | above_water_levitation / phase_start | 39.34 | 3312.00 | 4.60 | 38.70 | 18.90 | 37.40 |
| ccc | underwater_return / phase_start | 42.49 | 3312.00 | 2.20 | 30.10 | 12.00 | 5.00 |
| ccc | surface_boat_like / phase_start | 5.69 | 3312.00 | 0.00 | 0.00 | 24.00 | 4.00 |
| ccc | surface_boat_like / map_pixel_transition | 5.67 | 343.00 | 0.00 | 0.00 | 0.00 | 0.00 |

Interpretation:

- The `3312` decoration count in `ccc` was the first clear mod-owned performance bug.
- The low transition FPS persisted even when steady-state counts became sane, suggesting a separate synchronous terrain/location/build cost.

## Iteration 1: Cap Decorations Per Tile

Change:

- Added `MaxDecorationsPerTile = 256`.
- Trimmed generated decoration positions deterministically before spawning.

Result CSV:

- `deep-waters-diagnostics-20260614-180905.csv`

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 52.23 | 39.09 | 0.00 | 0.27 | 0.00 | 0.00 |
| bbb | underwater_outbound / phase_start | 59.83 | 43.00 | 0.00 | 0.10 | 0.00 | 0.00 |
| bbb | above_water_levitation / phase_start | 66.01 | 43.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | underwater_return / phase_start | 67.98 | 43.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | surface_boat_like / phase_start | 67.58 | 43.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| ccc | initial_load / first_10s | 2.99 | 0.00 | 6.00 | 15.00 | 13.00 | 51.00 |
| ccc | underwater_outbound / phase_start | 44.14 | 204.80 | 6.10 | 19.50 | 13.00 | 51.00 |
| ccc | above_water_levitation / phase_start | 47.44 | 256.00 | 2.30 | 39.40 | 9.10 | 6.20 |
| ccc | underwater_return / phase_start | 57.53 | 256.00 | 2.40 | 41.50 | 18.00 | 4.00 |
| ccc | surface_boat_like / phase_start | 51.23 | 256.00 | 9.20 | 32.50 | 33.50 | 53.40 |
| ccc | surface_boat_like / map_pixel_transition | 3.86 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 |

Finding:

- Keep. The cap fixed the pathological steady-state `ccc` decoration count and moved active water FPS from the 30s/40s into the 40s/50s.
- It did not fix first-load or transition cliffs. Those are not caused only by live decoration count.

## Iteration 2: Disable DeepWaterStreamingBuffer Install

Change:

- Removed `go.AddComponent<DeepWaterStreamingBuffer>()` from `DeepWaters.Bootstrap.cs`.
- Left the class present for easy rollback.

Result CSV:

- `deep-waters-diagnostics-20260614-181740.csv`

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 44.07 | 39.09 | 0.00 | 3.00 | 1.00 | 2.00 |
| bbb | underwater_outbound / phase_start | 63.26 | 43.00 | 0.00 | 0.30 | 0.10 | 2.00 |
| bbb | above_water_levitation / phase_start | 67.15 | 43.00 | 0.00 | 0.00 | 0.00 | 2.00 |
| bbb | underwater_return / phase_start | 67.49 | 43.00 | 0.00 | 0.00 | 0.00 | 2.00 |
| bbb | surface_boat_like / phase_start | 72.18 | 43.00 | 0.00 | 0.00 | 0.00 | 2.00 |
| ccc | initial_load / first_10s | 4.55 | 0.00 | 6.00 | 15.00 | 16.00 | 58.00 |
| ccc | underwater_outbound / phase_start | 61.19 | 230.40 | 6.00 | 21.60 | 16.20 | 58.00 |
| ccc | above_water_levitation / phase_start | 52.57 | 256.00 | 1.70 | 26.70 | 10.20 | 4.60 |
| ccc | underwater_return / phase_start | 59.54 | 256.00 | 5.80 | 25.50 | 17.00 | 49.00 |
| ccc | surface_boat_like / phase_start | 53.43 | 256.00 | 9.80 | 37.00 | 31.80 | 56.20 |
| ccc | surface_boat_like / map_pixel_transition | 4.03 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 |

Finding:

- Keep. Disabling the buffer substantially improved steady underwater `ccc` FPS, especially underwater outbound (`44.14 -> 61.19`).
- Transition remained bad (`3.86 -> 4.03`), so the buffer was a steady streaming pressure multiplier but not the core transition cliff.

## Iteration 3: Replace Whole-Scene Terrain Lookup With StreamingTarget Enumeration

Change:

- `DeepWaterTerrainLookup.GetFrameSnapshot()` now enumerates `GameManager.Instance.StreamingWorld.StreamingTarget` children first.
- `Object.FindObjectsOfType<DaggerfallTerrain>()` remains only as fallback.

Result CSV:

- `deep-waters-diagnostics-20260614-182550.csv`

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 64.00 | 38.70 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | underwater_outbound / phase_start | 82.77 | 43.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | above_water_levitation / phase_start | 77.82 | 43.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | underwater_return / phase_start | 84.19 | 43.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | surface_boat_like / phase_start | 89.37 | 43.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| ccc | initial_load / first_10s | 3.20 | 0.00 | 6.00 | 15.00 | 12.00 | 57.00 |
| ccc | underwater_outbound / phase_start | 70.44 | 230.40 | 6.20 | 20.40 | 12.00 | 57.00 |
| ccc | above_water_levitation / phase_start | 61.13 | 256.00 | 4.70 | 20.10 | 18.00 | 22.20 |
| ccc | underwater_return / phase_start | 77.55 | 256.00 | 2.70 | 30.90 | 10.00 | 17.00 |
| ccc | surface_boat_like / phase_start | 77.86 | 256.00 | 9.30 | 30.20 | 28.00 | 53.00 |
| ccc | surface_boat_like / map_pixel_transition | 4.07 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 |

Finding:

- Keep. This is the biggest active-play improvement so far.
- It strongly confirms terrain lookup was on the swim/collider/spawn hot path.
- It still does not fix first-load or transition cliffs, which are probably synchronous DFU terrain/location rebuild plus mod mesh/content build work.

## Renderer Sweep Check

Finding:

- `UnderwaterWaveShadowFix.cs` was already mostly in the latest shape:
  - external wave renderer suppression is absent,
  - `CutoutDepthQueueFix` scans every 8 seconds,
  - scans are sliced in chunks of 150 renderers,
  - `GetSharedMaterials(materialScratch)` is used.
- No renderer-sweep patch was applied because the code already matched the intended optimization closely enough.

## Iteration 4: Mesh Build Cost Bundle

Change:

- `WaterSurfaceManager.SurfaceGridResolution`: `64 -> 16`.
- Removed `mesh.RecalculateNormals()` from water surface generation.
- `DeepWaterFloorMesh.VertexGridSize`: `65 -> 33`.
- Shore skirt simplified:
  - `SkirtSlopeTangent`: `0.3 -> 0.6`.
  - `SkirtMaxWidthMeters`: `150 -> 40`.
  - removed Perlin width noise from skirt width.
- Removed `Physics.SyncTransforms()` after seafloor collider assignment.

Result CSV:

- `deep-waters-diagnostics-20260614-183414.csv`

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 70.94 | 38.18 | 0.00 | 7.73 | 0.00 | 0.00 |
| bbb | underwater_outbound / phase_start | 80.18 | 42.00 | 0.00 | 3.20 | 2.00 | 7.20 |
| bbb | above_water_levitation / phase_start | 68.95 | 42.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | above_water_levitation / map_pixel_transition | 69.79 | 256.00 | 2.30 | 17.00 | 16.90 | 9.00 |
| bbb | underwater_return / phase_start | 79.82 | 256.00 | 4.50 | 32.20 | 18.00 | 22.80 |
| bbb | surface_boat_like / phase_start | 76.63 | 256.00 | 7.40 | 33.10 | 26.70 | 56.90 |
| ccc | initial_load / first_10s | 3.03 | 0.00 | 6.00 | 16.00 | 13.00 | 54.00 |
| ccc | underwater_outbound / phase_start | 74.94 | 230.40 | 3.00 | 6.40 | 13.00 | 54.00 |
| ccc | above_water_levitation / map_pixel_transition | 3.85 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| ccc | above_water_levitation / phase_start | 3.85 | 256.00 | 0.00 | 0.00 | 13.00 | 54.00 |
| ccc | underwater_return / phase_start | 49.80 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| ccc | surface_boat_like / phase_start | 49.86 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| ccc | surface_boat_like / map_pixel_transition | 43.05 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |

Finding:

- Active `bbb` and `ccc` water FPS stayed strong.
- `bbb` transition became healthy in this route (`69.79 FPS`), which is a good sign.
- `ccc` still has a severe above-water transition cliff (`3.85 FPS`). The log around this showed a DFU location update of about `13.4s`, so that spike is likely dominated by DFU/location/mod object rebuild rather than seafloor mesh density.
- After the bad `ccc` transition, the same run recovered to `49.80-49.86 FPS`, then recorded another map-pixel transition at `43.05 FPS`.
- Tentatively keep, pending visual verification. The mesh bundle improves or preserves active-play FPS and appears to reduce build pressure on ordinary terrain transitions, but it cannot solve a location rebuild spike by itself.

## Current Best Kept Set

These changes are currently worth keeping unless later tests expose a functional/visual regression:

- Decoration cap at 256 per tile.
- Do not install `DeepWaterStreamingBuffer`.
- Enumerate `StreamingTarget` children for terrain lookup.
- Keep `Time.maximumDeltaTime = 0.1f` clamp already present.
- Collider gate cadence/radius reduction:
  - `ColliderGateTileProximityMeters`: `250 -> 96`.
  - `ColliderGateEjectGuardPaddingMeters`: `300 -> 96`.
  - gate refresh throttled to once per frame and at most every `0.15s`.
- Post-transition runtime refresh now queues decorations only; it no longer scans all terrains or rebuilds water surface meshes after load/teleport.

The mesh bundle is tentatively kept, pending visual verification.

## Iteration 5: Collider Gate Cadence And Radius

Change:

- `ColliderGateTileProximityMeters`: `250 -> 96`.
- `ColliderGateEjectGuardPaddingMeters`: `300 -> 96`.
- Added `ColliderGateRefreshIntervalSeconds = 0.15`.
- Added a once-per-frame guard so `Update()` and `PostPhaseRestore()` do not rebuild the same collider set in one rendered frame.
- Above-surface collider restoration remains immediate.

Result CSV:

- `deep-waters-diagnostics-20260614-184417.csv`

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 69.36 | 42.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | underwater_outbound / phase_start | 77.26 | 42.00 | 0.00 | 0.00 | 2.00 | 2.50 |
| bbb | above_water_levitation / phase_start | 72.38 | 42.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | above_water_levitation / map_pixel_transition | 73.25 | 256.00 | 3.70 | 20.60 | 20.60 | 56.00 |
| bbb | underwater_return / phase_start | 79.29 | 256.00 | 4.40 | 32.80 | 17.10 | 21.50 |
| bbb | surface_boat_like / phase_start | 83.34 | 256.00 | 5.30 | 22.00 | 39.80 | 56.10 |
| ccc | initial_load / first_10s | 3.17 | 0.00 | 1.00 | 15.00 | 12.00 | 46.00 |
| ccc | underwater_outbound / phase_start | 68.99 | 230.40 | 1.20 | 21.00 | 12.00 | 46.00 |
| ccc | above_water_levitation / phase_start | 78.26 | 256.00 | 1.90 | 40.30 | 15.50 | 4.00 |
| ccc | underwater_return / phase_start | 76.19 | 256.00 | 2.20 | 8.50 | 14.00 | 6.00 |
| ccc | surface_boat_like / phase_start | 70.76 | 256.00 | 10.70 | 45.10 | 32.30 | 55.20 |
| ccc | surface_boat_like / map_pixel_transition | 4.08 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 |

Finding:

- Keep for now. Active-play FPS remained high and `bbb` transition stayed healthy.
- The collider cadence helped the normal phases stay smooth but did not solve the `ccc` surface transition cliff.
- The persistent bad cases are now tightly clustered around `ccc` first load and one surface map-pixel transition, not general underwater play.

## Iteration 6: Remove Redundant Post-Transition Terrain/Surface Refresh

Change:

- `DeepWaterRuntime.PumpPostTransitionRefresh()` no longer:
  - calls `Object.FindObjectsOfType<DaggerfallTerrain>()`,
  - runs `DeepWaterFloorBuilder.RefreshLoadedTile()` over every loaded terrain,
  - runs `WaterSurfaceManager.RefreshLoadedSurface()` over every loaded terrain.
- It now waits until terrain data can be safely touched, then queues `UnderwaterDecorations.RefreshPlayerArea()`.
- The intent is to trust genuine `DaggerfallTerrain.OnPromoteTerrainData` for seafloor/surface creation and avoid a redundant post-load sweep.

Result CSV:

- `deep-waters-diagnostics-20260614-185307.csv`

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 77.06 | 42.00 | 0.00 | 8.00 | 1.00 | 2.00 |
| bbb | underwater_outbound / phase_start | 75.49 | 42.00 | 0.00 | 3.20 | 1.80 | 3.20 |
| bbb | above_water_levitation / phase_start | 60.14 | 42.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | above_water_levitation / map_pixel_transition | 60.85 | 256.00 | 4.50 | 25.00 | 18.20 | 43.00 |
| bbb | underwater_return / phase_start | 74.60 | 256.00 | 2.40 | 41.60 | 21.80 | 23.00 |
| bbb | surface_boat_like / phase_start | 74.66 | 256.00 | 7.00 | 37.50 | 27.80 | 51.30 |
| ccc | initial_load / first_10s | 7.80 | 56.89 | 6.00 | 16.00 | 13.00 | 54.00 |
| ccc | underwater_outbound / phase_start | 85.16 | 256.00 | 5.90 | 24.40 | 13.30 | 54.00 |
| ccc | above_water_levitation / phase_start | 85.45 | 256.00 | 4.60 | 24.60 | 16.50 | 5.70 |
| ccc | above_water_levitation / map_pixel_transition | 4.10 | 256.00 | 0.00 | 16.00 | 1.00 | 2.00 |
| ccc | underwater_return / phase_start | 10.80 | 256.00 | 0.00 | 18.00 | 1.00 | 2.00 |
| ccc | surface_boat_like / phase_start | 75.00 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| ccc | surface_boat_like / map_pixel_transition | 76.36 | 256.00 | 2.50 | 28.40 | 11.00 | 5.00 |

Finding:

- Keep. This is the first change to improve the stubborn `ccc` first-load window (`~3 FPS -> 7.80 FPS`) while preserving load-in-water content. `ccc` now has decorations, enemies, fish, loot, and rubble in the first 10 seconds.
- Active `ccc` phases are excellent (`85.16`, `85.45`, `75.00`, `76.36 FPS`) outside the location-heavy transition window.
- The remaining bad window coincides with `[DeepWaters.LoadGate] activeLoads=1 stuck for >12s` and `DFTFU ... Time to update location 95: 12800ms`.
- Interpretation: the remaining cliff is probably a location/DFU/mod-load event. Deep Waters can still avoid piling on extra work around it, but the log now shows a large non-Deep-Waters contributor.

## Iteration 7: Include Location Loading In CanRunHeavyRuntimeWork

Change:

- Temporarily changed `DeepWaterRuntime.CanRunHeavyRuntimeWork` to return false while `DeepWaterLocationLoadGate.IsAnyLocationLoading` is true.
- The idea was to make all encounter/loot/fish/decor heavy drivers pause during DFU location rebuilds, not only systems that check `IsLoadGraceActive`.

Result CSV:

- `deep-waters-diagnostics-20260614-190232.csv`

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 75.98 | 42.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | underwater_outbound / phase_start | 82.56 | 42.00 | 0.00 | 0.00 | 1.00 | 1.50 |
| bbb | above_water_levitation / phase_start | 76.84 | 42.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| bbb | above_water_levitation / map_pixel_transition | 77.59 | 256.00 | 5.80 | 17.80 | 18.00 | 55.00 |
| bbb | underwater_return / phase_start | 85.27 | 256.00 | 2.00 | 32.80 | 17.90 | 7.30 |
| bbb | surface_boat_like / phase_start | 96.88 | 256.00 | 2.60 | 7.50 | 16.60 | 40.40 |
| ccc | initial_load / first_10s | 3.96 | 56.89 | 0.22 | 1.56 | 2.00 | 6.89 |
| ccc | underwater_outbound / phase_start | 86.68 | 256.00 | 1.00 | 12.50 | 9.00 | 31.00 |
| ccc | above_water_levitation / phase_start | 75.60 | 256.00 | 4.00 | 46.50 | 17.40 | 6.30 |
| ccc | above_water_levitation / map_pixel_transition | 4.07 | 256.00 | 0.00 | 1.00 | 10.00 | 0.00 |
| ccc | underwater_return / phase_start | 12.68 | 256.00 | 0.33 | 4.33 | 11.00 | 1.00 |
| ccc | surface_boat_like / phase_start | 73.60 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| ccc | surface_boat_like / map_pixel_transition | 74.97 | 256.00 | 8.60 | 27.00 | 23.00 | 56.00 |

Finding:

- Rejected and reverted.
- It did not improve the bad `ccc` location-heavy transition (`4.07 FPS`, then `12.68 FPS`).
- It made first-load content worse by delaying enemies/fish/loot/rubble in `ccc`, and FPS fell back near the old bad load window.
- Conclusion: heavy spawners should not be globally blocked on the location gate. The better base is iteration 6.

## Next Ideas

Likely next cuts, in rough order:

1. Add lightweight transition diagnostics that logs whether a low-FPS sample overlaps location loading, terrain update, decoration queue work, enemy/fish/loot pulses, and current post-transition pending state.
2. Reduce or stage only specific synchronous content generation after pixel transitions, instead of globally blocking heavy work on location loading.
3. If the remaining `ccc` cliff is mostly DFU location rebuild (`Time to update location ... 13s`), add diagnostics that separates DFU terrain/location time from mod seafloor/decor/content time before changing gameplay logic.
4. Visually verify the mesh bundle, especially shore skirt quality and surface coverage, because the numerical win is only acceptable if it does not bring back holes, walls, or bare-water bands.
5. Consider a targeted avoidance/defer strategy for expensive work when the destination pixel is a location-heavy coast, because the bad samples line up with location rebuilds rather than open-water movement.

## Transition Instrumentation

Change:

- Added CSV columns for:
  - `loadGateActive`, `loadGateCount`, `loadGateAge`
  - `terrainUpdateActive`, `loadGraceActive`, `heavyWorkBlocked`, `heavyWorkResumeIn`
  - `postRefreshPending`
  - `decorQueue`, `decorQueuedTerrains`
  - later, `locationSkippedLast`, `locationDeferred`
- Exposed side-effect-free load-gate and decoration queue counters.

Baseline instrumentation CSV:

- `deep-waters-diagnostics-20260614-192109.csv`

Key rows:

| Save | Event | FPS | LoadGateActive | LoadGateCount | TerrainUpdateActive | DecorQueue |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| bbb | surface_boat_like / map_pixel_transition | 5.30 | 0.88 | 2.50 | 0.13 | 0.88 |
| ccc | initial_load / first_10s | 3.13 | 0.78 | 0.89 | 0.00 | 1.22 |
| ccc | above_water_levitation / map_pixel_transition | 4.18 | 0.78 | 0.78 | 0.22 | 0.22 |
| ccc | underwater_return / phase_start | 18.57 | 0.67 | 0.67 | 0.00 | 0.00 |

Finding:

- The bad windows line up with DFU location-object layout (`loadGateActive`) rather than Deep Waters decoration queue depth.
- Decoration queue was tiny during the bad transition windows, so continuing to tune decoration spawning was the wrong target.

## Iteration 8: Defer Peripheral Location Layouts Offshore

Change:

- Added `DeepWaterLocationUpdateSkipper`.
- It reflects `StreamingWorld.terrainArray` and clears `updateLocation` for non-current, non-owned-ship location pixels while the player is in an ocean/deep-water context.
- Deferred location keys are remembered; if a deferred key later becomes the current map pixel, it is re-enabled so DFU can build it then.
- This is intentionally not a Harmony patch and does not require changing standalone managed DLLs.

Intermediate CSV:

- `deep-waters-diagnostics-20260614-193509.csv`

Key result:

| Save | Event | FPS Before | FPS After | LoadGateActive After | LocationDeferred |
| --- | --- | ---: | ---: | ---: | ---: |
| ccc | above_water_levitation / map_pixel_transition | 4.18 | 63.89 | 0.00 | 1.60 |
| ccc | underwater_return / phase_start | 18.57 | 75.70 | 0.00 | 2.00 |

Finding:

- Keep. This fixed the worst reproducible `ccc` transition by preventing multiple peripheral coastal locations from laying out while the player is offshore.
- It did not yet fix first load because the hook returned early while `GameManager.IsPlayingGame()` was still false during save-load terrain promotion.

## Iteration 9: Treat Ocean-Connected Current Pixels As Offshore Context

Change:

- Broadened the skipper predicate:
  - exact player deep-water/underwater context, or
  - current map pixel has an ocean-connected Deep Waters tile.

Result CSV:

- `deep-waters-diagnostics-20260614-195008.csv`

Key result:

| Save | Event | FPS | LoadGateActive | LocationDeferred |
| --- | --- | ---: | ---: | ---: |
| bbb | above_water_levitation / map_pixel_transition | 68.69 | 0.00 | 0.00 |
| ccc | initial_load / first_10s | 6.93 | 0.78 | 0.00 |
| ccc | above_water_levitation / map_pixel_transition | 65.70 | 0.00 | 1.60 |
| ccc | surface_boat_like / map_pixel_transition | 76.75 | 0.00 | 2.00 |

Finding:

- Partial keep. Transition behavior stayed good and `ccc` initial improved a little, but first-load still hit the location gate because the save-load hook was running before `IsPlayingGame()`.

## Iteration 10: Let Location Skipper Run During Save Load

Change:

- Removed the `GameManager.IsPlayingGame()` guard from `DeepWaterLocationUpdateSkipper.OnUpdateTerrainsEnd`.
- The hook now requires only `GameManager`, `StreamingWorld`, and `PlayerGPS`; this lets it defer peripheral locations during save-load terrain promotion, before DFU starts location layout.
- Added a lightweight deferred-location restore pump. If deferred locations exist and the player is no longer in deep water, it re-enables those location updates and flips DFU's `updateLocations` flag so land-side functionality can recover without requiring another map-pixel transition.

Result CSV:

- `deep-waters-diagnostics-20260614-195822.csv`

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble | LoadGateActive | LocationDeferred |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 97.16 | 42.00 | 0.00 | 6.09 | 1.00 | 1.00 | 0.00 | 4.00 |
| bbb | above_water_levitation / map_pixel_transition | 103.00 | 256.00 | 2.00 | 27.40 | 11.00 | 5.00 | 0.00 | 4.00 |
| bbb | surface_boat_like / phase_start | 121.06 | 256.00 | 5.60 | 32.00 | 19.60 | 51.30 | 0.00 | 4.00 |
| ccc | initial_load / first_10s | 415.82 | 256.00 | 2.00 | 10.55 | 14.00 | 50.00 | 0.00 | 5.00 |
| ccc | above_water_levitation / map_pixel_transition | 239.84 | 256.00 | 0.00 | 9.70 | 2.00 | 1.00 | 0.00 | 6.60 |
| ccc | surface_boat_like / map_pixel_transition | 298.28 | 256.00 | 7.00 | 30.50 | 22.00 | 53.00 | 0.00 | 7.00 |

Finding:

- Keep, pending manual visual/function check.
- This removes the load-gate overlap from the automated run and keeps decorations/enemies/fish/loot/rubble present.
- Trade-off: peripheral coastal/town exterior locations can remain deferred while the player stays offshore. The current pixel and owned ship are preserved; a deferred location is re-enabled when it becomes the current map pixel. This is the right performance trade for ocean traversal, but it should be checked manually near visible coasts/towns.

## Iteration 11: Track Missing Swim World Position

Problem found by longer transition run:

- CSV `deep-waters-diagnostics-20260614-201757.csv` was stopped after it exposed the issue.
- During a long underwater leg, `PlayerGPS.CurrentMapPixel` stayed at `207:223`.
- On the next above-water phase, it jumped to `209:222`, overlapping DFU location layout and producing the old transition cliff:

| Save | Event | FPS | LoadGateActive | LocationSkippedLast | Decorations |
| --- | --- | ---: | ---: | ---: | ---: |
| bbb | above_water_levitation / phase_start | 5.14 | 0.67 | 1.33 | 42.00 |
| bbb | above_water_levitation / map_pixel_transition 207:223 -> 209:222 | 5.07 | 0.67 | 1.33 | 0.00 |

Change:

- Added `DeepWaterSwimWorldTracker`.
- It keeps only the useful part of the old `DeepWaterStreamingBuffer`: while the player is in the exterior deep-water swim band, it compares transform delta against actual `PlayerGPS.WorldX/Z` delta and applies only the missing world-coordinate movement.
- It does not widen `TerrainDistance`, force terrain updates, or install the old buffer ring.

Result CSV:

- `deep-waters-diagnostics-20260614-202711.csv`

Key rows:

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble | LoadGateActive | LocationDeferred |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 95.47 | 42.00 | 0.00 | 0.00 | 1.00 | 1.00 | 0.00 | 4.00 |
| bbb | underwater_outbound / map_pixel_transition 207:223 -> 207:222 | 134.74 | 256.00 | 0.00 | 2.50 | 4.00 | 4.00 | 0.00 | 4.00 |
| bbb | underwater_outbound / map_pixel_transition 207:222 -> 208:222 | 123.98 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 5.60 |
| bbb | above_water_levitation / phase_start | 102.93 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 6.00 |
| bbb | above_water_levitation / map_pixel_transition 208:222 -> 207:223 | 103.57 | 42.00 | 0.00 | 0.00 | 3.00 | 6.00 | 0.00 | 6.00 |
| ccc | initial_load / first_10s | 358.49 | 256.00 | 6.00 | 32.00 | 17.00 | 65.00 | 0.00 | 5.00 |
| ccc | underwater_outbound / map_pixel_transition 207:222 -> 208:222 | 269.11 | 256.00 | 0.00 | 1.60 | 1.00 | 1.00 | 0.00 | 6.20 |
| ccc | surface_boat_like / phase_start | 258.87 | 256.00 | 0.40 | 21.40 | 4.90 | 16.00 | 0.00 | 7.00 |

Finding:

- Keep.
- The delayed above-water catch-up transition is gone in the 300-second run.
- Underwater map-pixel transitions now record incrementally, with the worst sampled row at `95.41 FPS` in the hidden harness.

## Iteration 12: Skip Peripheral Locations While Any Loaded Ocean Tile Is Nearby

Problem found by the longer transition run after iteration 11:

- The underwater position tracker fixed delayed pixel transitions.
- A later above-water coastal crossing still triggered a batch of DFU location layouts:

| Log Event | Time |
| --- | ---: |
| Time to update location 8 | 312ms |
| Time to update location 75 | 1572ms |
| Time to update location 65 | 2344ms |
| Time to update location 51 | 3045ms |
| Time to update location 25 | 3771ms |
| Time to update location 78 | 12602ms |

Change:

- Broadened `DeepWaterLocationUpdateSkipper`.
- Instead of skipping peripheral locations only when the current pixel is ocean-connected, it now skips peripheral locations while any active loaded terrain tile is ocean-connected.
- The current pixel is still preserved; this only defers surrounding coastal/town locations while ocean traversal is nearby.

Result CSV:

- `deep-waters-diagnostics-20260614-205120.csv`

Key rows:

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble | LoadGateActive | LocationDeferred |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 107.04 | 42.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 4.00 |
| bbb | underwater_outbound / map_pixel_transition 207:223 -> 207:222 | 147.49 | 256.00 | 0.70 | 2.20 | 3.00 | 6.00 | 0.00 | 4.00 |
| bbb | above_water_levitation / map_pixel_transition 209:222 -> 208:223 | 129.66 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 7.00 |
| bbb | surface_boat_like / phase_start | 97.06 | 25.60 | 0.00 | 0.00 | 2.00 | 3.00 | 0.00 | 7.00 |
| bbb | surface_boat_like / map_pixel_transition 207:222 -> 204:227 | 98.14 | 230.40 | 0.80 | 16.80 | 5.60 | 34.40 | 0.00 | 6.60 |
| ccc | initial_load / first_10s | 380.86 | 256.00 | 0.00 | 15.00 | 6.00 | 11.00 | 0.00 | 5.00 |
| ccc | underwater_outbound / map_pixel_transition 207:222 -> 208:222 | 266.82 | 256.00 | 0.00 | 8.00 | 6.00 | 11.00 | 0.00 | 6.20 |
| ccc | underwater_return / map_pixel_transition 209:221 -> 208:221 | 203.40 | 256.00 | 5.10 | 18.70 | 10.00 | 22.00 | 0.00 | 7.00 |
| ccc | surface_boat_like / map_pixel_transition 208:221 -> 208:222 | 152.55 | 256.00 | 1.30 | 6.60 | 6.00 | 11.00 | 0.00 | 7.00 |

Log check:

- After the initial save-load location update, the long run had many map-pixel crossings and no repeated `Time to update location ...` batch.
- Largest remaining transition cost in the log was terrain update work, with the biggest sampled line around `2432ms`.

Finding:

- Keep, pending manual visual pass.
- The old `12.6s` location-layout cliff was removed.
- The worst sampled 600-second FPS row was `97.06` in the hidden harness.
- Caveat: pixel `209:222` reports zero decorations/fish/loot/rubble in both saves. This appears deterministic for that coastal/edge pixel, not a random spawn pulse failure, and needs visual inspection before forcing content into it.

## Iteration 13: Fine-Mask Self-Gate for Ocean-Connected Tiles

Problem found after iteration 12:

- The deterministic zero-content pixel `209:222` was not random spawn loss.
- Offline bake inspection showed `209:222` was a coarse-mask false positive:
  - coarse water cells: `64/64`
  - fine water cells: `0/4096`
  - distance field: `0` throughout
- `DeepWaterTileData.ComputeOceanConnectivity()` still checked `MapPixelHasWaterCellsNear(mx, my, 2)` before the fine mask, so a dry fine-mask coastal pixel could be treated as ocean-connected.

Change:

- For v4+/v5 bakes with a fine water mask, ocean connectivity now uses the exact pixel's fine-mask water cells.
- The old coarse-neighbor fallback remains only for legacy bakes without a fine mask.
- Added `contentEligibleCurrent` and `contentEligibleFormer` diagnostics columns so zero-content rows can be separated from spawn failures.

Validation note:

- First full run after the source edit was discarded: the bundle patcher had written UnityPy `TextAsset.script`, but the serialized field is `TextAsset.m_Script`, so the game still loaded the old script.
- Fixed the packer and confirmed both playable bundles contain the changed `DeepWaters.Bootstrap.cs` and `DeepWaterTileData.cs`.
- Smoke CSV `deep-waters-diagnostics-20260614-220451.csv` confirmed the new eligibility columns were loaded.

Result CSV:

- `deep-waters-diagnostics-20260614-220737.csv`

Key rows:

| Save | Event | FPS | Decorations | Enemies | Fish | Loot | Rubble | Eligible | LocationDeferred |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| bbb | initial_load / first_10s | 99.71 | 42.00 | 0.00 | 0.00 | 0.00 | 0.00 | 1 | 4.00 |
| bbb | underwater_outbound / map_pixel_transition 208:222 -> 209:222 | 136.10 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0 | 6.00 |
| bbb | above_water_levitation / phase_start 209:222 | 131.48 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0 | 6.00 |
| bbb | above_water_levitation / map_pixel_transition 209:222 -> 208:223 | 132.46 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0 | 6.00 |
| bbb | surface_boat_like / phase_start | 89.95 | 25.60 | 0.00 | 0.00 | 0.00 | 0.00 | 1 | 6.00 |
| bbb | surface_boat_like / map_pixel_transition 207:222 -> 204:227 | 90.66 | 230.40 | 0.80 | 4.90 | 2.10 | 2.80 | 1 | 6.00 |
| ccc | underwater_outbound / map_pixel_transition 208:222 -> 209:222 | 392.63 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0 | 7.00 |
| ccc | above_water_levitation / phase_start 209:222 | 336.30 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0 | 7.00 |
| ccc | above_water_levitation / map_pixel_transition 209:222 -> 209:221 | 271.99 | 256.00 | 0.00 | 0.00 | 0.00 | 0.00 | 1 | 7.00 |
| ccc | initial_load / first_10s | 376.78 | 256.00 | 0.00 | 16.00 | 5.00 | 7.00 | 1 | 5.00 |

Log check:

- `Time to update location`: `0` after startup; the old DFU location-layout cliff did not recur.
- Terrain update lines: `20`; worst `2476ms`, then `2290ms`, `1583ms`, `1527ms`.
- Deep Waters errors: `0`.
- Other-mod noise: one `DynamicEnemiesMod.DynamicEnemyMotor.ChargeAttack` null reference.

Finding:

- Keep.
- The zero-content `209:222` rows are now explained by `contentEligible=0`; forcing spawns there would place content into a pixel the bake says has no carved underwater area.
- This did not target the remaining performance floor. The next bottleneck is still terrain update work around transitions, especially the bbb surface/boat route where sampled FPS bottoms near `90`.

## Iteration 14 Rejected: Lazy Seafloor Collider Cooking

Hypothesis:

- Terrain update spikes might include seafloor `MeshCollider` cooking during `DaggerfallTerrain.OnPromoteTerrainData`.
- Tried moving collider cooking out of the promote path:
  - build the floor mesh during promote,
  - invalidate stale floor colliders,
  - cook a floor collider only when the swim collider gate is about to disable a nearby vanilla `TerrainCollider`.

Result CSV:

- `deep-waters-diagnostics-20260614-223459.csv`

Comparison to Iteration 13:

| Metric | Iteration 13 | Lazy Collider Test |
| --- | ---: | ---: |
| Worst sampled FPS | 89.95 | 79.78 |
| bbb surface phase_start FPS | 89.95 | 79.78 |
| bbb surface transition FPS | 90.66 | 80.49 |
| Max terrain update | 2476ms | 2557ms |
| Location update count after startup | 0 | 2 |
| Max location update | 0ms | 2775ms |
| Deep Waters errors | 0 | 0 |
| Interesting Terrain duplicate-key exceptions | 0 | 4 |

Finding:

- Rejected and reverted.
- It made the measured FPS floor worse, did not reduce terrain-update max time, and coincided with location updates plus `Monobelisk.TileDataCache.Add` duplicate-key exceptions.
- Active playable bundles were repacked back to the Iteration 13 kept code after this test.

## Iteration 15: Passive Fish Obstacle Raycast Throttling

Hypothesis:

- The remaining worst transition spikes are terrain update work, but fish-heavy windows still pay per-frame physics raycasts from every live passive fish.
- GitHub latest throttles those probes, so this is a low-risk runtime improvement that should not affect spawn correctness.

Change:

- `PassiveFishBehaviour` now:
  - probes obstacles every 5 frames instead of every frame,
  - randomizes each fish's probe phase,
  - skips obstacle probes for fish farther than `60m` from the player,
  - extends probe length to cover the skipped stride.

Result CSV:

- `deep-waters-diagnostics-20260614-230324.csv`

Comparison to Iteration 13:

| Metric | Iteration 13 | Fish Throttle |
| --- | ---: | ---: |
| Worst sampled FPS | 89.95 | 97.99 |
| bbb initial_load FPS | 99.71 | 110.66 |
| bbb surface phase_start FPS | 89.95 | 97.99 |
| bbb surface transition 207:222 -> 204:227 FPS | 90.66 | 98.97 |
| ccc initial_load FPS | 376.78 | 443.38 |
| Max terrain update | 2476ms | 2560ms |
| Deep Waters errors | 0 | 0 |
| Interesting Terrain duplicate-key exceptions | 0 | 0 |

Fish-heavy rows after the change:

| Save | Event | FPS | Fish | Decorations | TerrainUpdateActive |
| --- | --- | ---: | ---: | ---: | ---: |
| bbb | surface_boat_like / map_pixel_transition 204:227 -> 203:227 | 119.13 | 18.00 | 256.00 | 0.10 |
| bbb | surface_boat_like / map_pixel_transition 203:228 -> 203:229 | 355.20 | 16.90 | 256.00 | 0.10 |
| ccc | initial_load / first_10s | 443.38 | 16.00 | 256.00 | 0.00 |
| ccc | underwater_outbound / phase_start | 465.78 | 18.00 | 256.00 | 0.00 |
| ccc | surface_boat_like / map_pixel_transition 208:221 -> 207:221 | 257.45 | 14.20 | 256.00 | 0.10 |

Log check:

- `Time to update location`: one `2714ms` line around initial/current-location loading and DET material deserialization, not the old repeated transition-location batch.
- Terrain update lines: `20`; worst `2560ms`, then `2360ms`, `1620ms`, `1522ms`.
- Deep Waters errors: `0`.
- Other-mod noise: two `DynamicEnemiesMod.DynamicEnemyMotor.ChargeAttack` null references.

Finding:

- Keep.
- This improved sampled FPS without changing content generation or reintroducing the old location-layout cliff.
- It does not solve terrain update spikes; the next optimization needs to identify whether the remaining `~2.5s` terrain work is DFU/Interesting Terrain sampling, Deep Waters promote handlers, or another terrain-subscriber cost.

## Current Best Kept Set After Iteration 15

- Decoration cap at 256 per tile.
- Do not install `DeepWaterStreamingBuffer`.
- Install only `DeepWaterSwimWorldTracker`, which fills missing underwater `PlayerGPS.WorldX/Z` movement without changing `TerrainDistance`.
- Fine-mask bakes use exact fine-mask self-connectivity before legacy coarse-neighbor fallback.
- Passive fish obstacle raycasts are staggered, near-player only, and once per 5 frames.
- Enumerate `StreamingTarget` children for terrain lookup.
- Mesh build bundle:
  - water surface grid `64 -> 16`,
  - seafloor vertex grid `65 -> 33`,
  - simplified shore skirt,
  - no forced `Physics.SyncTransforms()` after seafloor collider assignment.
- Collider gate cadence/radius reduction:
  - `ColliderGateTileProximityMeters = 96`,
  - `ColliderGateEjectGuardPaddingMeters = 96`,
  - refresh at most every `0.15s`.
- Post-transition refresh only queues decoration refresh; no whole-ring terrain/surface rebuild.
- Transition diagnostics columns remain in CSV.
- Peripheral location layout deferral while offshore, including during save-load terrain promotion.
- Peripheral location layout deferral while any active loaded terrain tile is ocean-connected.
- Deferred-location restore pump when the player leaves deep water and the current pixel is not ocean-connected.
- Diagnostics include current/former content eligibility so dry coastal pixels are not mistaken for spawn failures.

## Next Ideas After Iteration 15

1. Manual visual pass near a coast/town from boat and underwater: verify deferred peripheral locations do not create unacceptable empty shoreline views.
2. Add targeted attribution for terrain update spikes before another streaming change: time Deep Waters promote handlers versus DFU/Interesting Terrain terrain sampling.
3. Do not retry lazy seafloor collider cooking without a tighter theory; it worsened FPS and brought back location/Interesting Terrain noise.
4. If deferred towns are too visible, tighten the skip to only locations outside a near radius or only while camera is underwater.

## Spawn Density Pass

Problem:

- The kept performance build is playable, but max decoration/enemy/fish sliders still feel nearly empty.
- The UI max was being multiplied down by conservative midpoint defaults and a hidden `SpawnRateScale = 0.4`.

Change:

- `EnemyFrequencyAtMidpoint`: `0.3 -> 0.5`
- `PassiveFishFrequencyAtMidpoint`: `0.6 -> 1.0`
- `DecorationFrequencyAtMidpoint`: `1.0 -> 1.5`
- `SpawnRateScale`: `0.4 -> 0.75`
- Enemy placement attempts per spawn: `4 -> 8`

Expected max-slider effect:

- Enemies: about `3` target spawns per pulse before placement/caps, up from about `1`.
- Fish: about `12` target spawns per deep-water pulse before placement/caps, up from about `4`.
- Decorations: `3` decoration passes per eligible tile, up from `2`.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed into both playable `.dfmod` files for manual testing.

## Spawn Visibility And Decoration Rendering Pass

Reference:

- Compared current branch against GitHub `jet082/iliac-puddle-no-more` latest `9355dc8 cleanup`.
- Latest cap/settings reference:
  - `MaxLiveFish`: default `36`, max `180`.
  - `FishParadiseMaxLiveFish`: default `72`, max `240`.
  - `DecorationPopulateRadius`: default `1`, max `3`.
  - Latest has no per-tile decoration trim cap.
  - Latest decoration material path uses `DeepWaters/UnderwaterBillboardBatchUnlit` with a material cache.
  - Latest fog shader starts its hard distance curtain at `0.50x` effective vision; this branch had drifted to `1.0x`, which reads as a tighter spotlight.

Change:

- Restored fish cap settings to `modsettings.json` and read them in `DeepWaters.Settings`.
- Restored `DecorationPopulateRadius` setting/readback.
- Raised max-slider density:
  - `PassiveFishFrequencyAtMidpoint`: `2.0` so max slider targets about `24` deep-water fish per pulse before caps/placement.
  - `DecorationFrequencyAtMidpoint`: `2.5` so max slider rolls about `5` placement passes.
- Raised per-tile decoration trim cap from `256` to `768` instead of removing it entirely.
- Decoupled passive fish from enemy clear-water spawn distance:
  - fish now spawn in a `20m..85m` ring,
  - fish view-safety distance is `25m`,
  - fish despawn distance is `160m`.
- Restored underwater unlit/cutout decoration material creation and cache, removing the source-shader preservation path that made replacement decorations too dark.
- Raised decoration base clearance from `0.25m` to `0.75m`.
- Matched source fog shader to latest softer curtain and widened live C# fog vision defaults from `70/260` to `95/360` so the currently packed compiled shader reads less like a tight cone.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Read back installed `StreamingAssets\Mods\iliac puddle no more.dfmod` and confirmed scripts plus `modsettings` contain the new values.

## Density And Decoration Alpha Follow-Up

Problem:

- Manual testing showed enemies were good, but fish and decorations still wanted about `1.5x` density.
- A global `0.75m` decoration floor clearance fixed some sinking but made ordinary decorations float.
- Some replacement decorations still rendered as opaque black rectangles because their black padding was connected to the texture edge and had alpha `1`.

Change:

- `PassiveFishFrequencyAtMidpoint`: `3.0` (`1.5x` from previous `2.0`).
- `DecorationFrequencyAtMidpoint`: `3.75` (`1.5x` from previous `2.5`).
- `MaxDecorationsPerTile`: `1152` (`1.5x` from previous `768`).
- Passive fish live caps now default/effectively scale `1.5x`, clamped to the existing latest GitHub max:
  - normal default `54`, max `180`,
  - Fish Paradise default `108`, max `240`.
- Decoration placement now uses record-aware clearance:
  - ordinary/static/replacement batches: `0.25m`,
  - archive-animated decorations: `0.75m`.
- Underwater replacement decoration materials now cache an edge-cleaned texture copy that flood-fills near-black/transparent pixels from texture edges to alpha `0`.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Read back installed `StreamingAssets\Mods\iliac puddle no more.dfmod` and confirmed the changed constants and settings are present.

## Targeted Saves DDD/EEE/FFF Shoreline Pass

Problem:

- `eee` reproduced a hard coastal visual seam where the water surface did not meet the shoreline cleanly.
- `fff` had previously been sensitive to saved camera yaw; with the corrected save-loader orientation it now loads facing the expected shallow-water shoreline.
- `ddd` reproduced the shore-exit failure during a longer straight swim: after reaching land, the player jumped from about `Y=105.95` to `Y=112.99` and ended pressed into/through city wall geometry.

Change:

- Mixed coastal terrain cap clipping now patches the promoted DFU `DaggerfallTerrain.TileMap` texture rather than relying only on the shader source, because the shipped `.dfmod` still contains the old compiled shader.
- Water surface generation no longer uses full-tile quads for mixed ocean-connected coastal pixels. Full quads are now restricted to landless ocean pixels.
- Mixed-tile water surface cells now follow the same promoted tilemap texels that the terrain cap clip removes, avoiding mismatches between `MapData.tilemapSamples` and the actual promoted terrain texture.
- Terrain cap clipping now requires the promoted water-like texel to be fully submerged. This avoids punching holes in raised shoreline/city terrain and removes the large dark under-land water sheet seen in `eee`.
- `OutdoorShoreExitAssist` now rejects landing probes more than `8m` above the ocean surface. This keeps normal beach/shore exits working while preventing the `ddd` probe from treating the top of a city wall as a valid shore landing.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- `eee,fff` diagnostics completed normally:
  - `eee` latest shoreline screenshot: `DeepWatersDiagnostics\deep-waters-eee-shoreline-hold-20260615-082252.png`.
  - `eee` mixed shoreline tiles changed from full slabs (`verts=4`) to clipped meshes (`verts=300..524` on the visible coastal tiles).
  - `fff` latest load/probe screenshots remained visually aligned with the user-confirmed correct load (`deep-waters-fff-after-load-20260615-082259.png`, `deep-waters-fff-fff_straight_seam_probe-5s-20260615-082304.png`).
- `ddd` 45-second straight run before shore-assist cap reproduced the bad jump:
  - at `29s`, player was at `Y=112.99` and the screenshot showed wall-top/inside-wall geometry.
- `ddd` 45-second straight run after shore-assist cap no longer jumped:
  - after load: `playerY=97.06`, `playerSwimming=1`.
  - shore transition: `playerY=105.80`, `playerSwimming=0`.
  - end: `playerY=105.95`, `playerSwimming=0`, no `Y=112.99` launch.
  - latest end screenshot: `DeepWatersDiagnostics\deep-waters-ddd-ddd_straight_shore_entry-end-20260615-083521.png`.

Notes:

- The remaining `ComeSailAway` nullrefs during forward shore/seam probes are still from `ComeSailAwayMod.ComeSailAway.FixedUpdate()` and appear unrelated to Deep Waters terrain/surface generation.
- `eee` is improved from a full dark under-land sheet to a clipped shoreline strip, but it still has a visibly hard coast transition. The next visual pass should target water/shore material blending or a narrow shoreline surface feather, not broad full-tile coverage.

## GGG/HHH Boundary Wall Follow-Up

Problem:

- `iii` was manually confirmed fixed.
- `hhh` still showed a see-through dark side gap between the carved water tile and the raised shore. The key diagnostic was tile `(207,222)`: it was fully carved (`16384/16384` holes) but only generated `28` boundary wall segments, so the bake-only neighbor test was skipping most of the vertical stitch.
- Disabling the water top surface did not change the image, and clamping the near-shore seafloor up to the shallow swimmable floor (`renderedSeafloorY 95.78 -> 97.31`) changed the numeric column but not the visual hole. That proved the remaining issue was a missing wall/stitch, not water film, fog, or bathymetry depth.

Change:

- Kept the shallow vanilla shore-floor clamp so generated seabed and water-column queries agree near shore while preserving the `2.7m` minimum swim depth.
- Boundary wall generation now keeps a wall when the cross-pixel bake sample is carved water but the neighboring map pixel is a mixed near-shore pixel with land cells. This fixes the false assumption that "neighbor is carved" always means "neighbor has no visible raised terrain side to stitch."
- Pure-ocean cap hiding was restored to the stricter fully-submerged rule after the diagnostic test.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- `hhh` after the boundary-wall fix:
  - `(207,222)` boundary walls increased from `28` to `256`.
  - `(208,222)` generated `69` boundary walls.
  - 10-second visual hold: `128.60 FPS`.
  - column depth: `2.70m`, `renderedSeafloorY=97.31`, `carvedSeafloorY=97.31`.
  - latest screenshot: `DeepWatersDiagnostics\deep-waters-hhh-shoreline-hold-20260615-155408.png`.
- `ggg` sanity run:
  - 10-second visual hold: `117.66 FPS`.
  - no obvious invisible-floor slab or see-through side gap in `DeepWatersDiagnostics\deep-waters-ggg-shoreline-hold-20260615-155701.png`.

## JJJ/KKKK/LLL Water Mask And Shore Handoff Sweep

Problem:

- `jjj` showed the undersea world flattening into the minimum safety floor.
- `kkkk` reproduced the shoreline bobbing problem from `iii`: moving straight toward shore could flip swim state during the transition instead of cleanly handing off to land.
- `lll` reproduced the `ggg` trouble corner where the player could swim through visible land. Diagnostics showed this was caused by water-column logic still treating legacy baked-water samples as valid even where the fine carved-water mask said the cell was land.

Change:

- The fine carved-water mask is now authoritative wherever it exists:
  - `DeepWaterWorld.TryGetWaterColumn()` rejects columns outside carved water instead of falling back to pure baked water.
  - `DeepWaterFloorBuilder.ComputeHoleMask()` only uses the broad pure-baked-water fallback for legacy/no-fine-mask tiles.
  - `OutdoorSwimDriver.IsSolidShoreForColliderGate()` uses carved water for water-like checks when fine mask data is available.
- Shore standing now wins over recent water contact:
  - standing-on-shore is computed before the water-collider gate in `Update`, `FixedUpdate`, and post-phase restore.
  - `HasRecentCenterWaterContact()` clears water contact immediately when the player is grounded on shore.
- The DFU swim surface offset now matches the Deep Waters shore-exit clearance (`0.75m`) so vanilla swim detection and the mod's exit gate stop fighting each other on alternating frames.
- Restored visible seabed detail:
  - raised seabed mesh resolution from `33x33` to `65x65`.
  - added positive shallow safety-floor relief so minimum-depth zones are not perfectly flat.
  - adjusted seabed vertex color bands so the shelf color boost no longer washes out the whole coastal shelf.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Full diagnostics sweep: `DeepWatersDiagnostics\deep-waters-diagnostics-20260615-183140.csv`.
- `jjj`:
  - 10-second visual hold: `73.68 FPS`.
  - diagnostics reported `decorations=1152`, `fish=144`, `depth=14.01m`.
  - latest screenshot: `DeepWatersDiagnostics\deep-waters-jjj-shoreline-hold-20260615-183206.png`.
- `kkkk`:
  - `201` sampled frames.
  - `swimToggles=1`, matching the real water-to-land exit.
  - `swimWhileGrounded=0`.
  - `ghostColumnFrames=0`.
  - latest transition screenshot: `DeepWatersDiagnostics\deep-waters-kkkk-kkkk_straight_shore_entry-17s-20260615-183230.png`.
- `lll`:
  - `523` sampled frames.
  - `swimToggles=1`.
  - `ghostColumnFrames=0`.
  - the later large `Y` jump happened after the player was already out of water with no active water column or collider gate, so it is no longer the old through-visible-land water-volume bug.
  - latest early probe screenshot: `DeepWatersDiagnostics\deep-waters-lll-lll_straight_shore_probe-5s-20260615-183301.png`.
- `ggg` sanity run:
  - 10-second visual hold: `102.70 FPS`.
  - latest screenshot: `DeepWatersDiagnostics\deep-waters-ggg-shoreline-hold-20260615-183350.png`.
- `hhh` sanity run:
  - 10-second visual hold: `93.84 FPS`.
  - latest screenshot: `DeepWatersDiagnostics\deep-waters-hhh-shoreline-hold-20260615-183409.png`.

Notes:

- This pass removes the ghost water columns at the trouble corner and fixes the swim/grounded fight in `kkkk`.
- `jjj` is no longer at the exact safety-minimum table depth, but the current packed build still uses the shipped unlit seabed material path. If the manual view still reads too flat, the next likely fix is either stronger bathymetry relief or a proper material/shader rebuild rather than another water-volume patch.

## MMM/NNN/OOO Shoreline Diagnostics

Problem:

- `mmm` reproduced a walk-forward water entry where the player could fall into a shoreline crack and swim into the steep inside face of the shore.
- `nnn` reproduced a bizarre vertical wall between map pixels while already swimming.
- `ooo` showed broken shoreline seams where the camera could see beneath land near the waterline.
- During long swims the test character could die, invalidating later movement samples.

Diagnostics:

- The test harness now enables `PlayerEntity.GodMode` and tops health, fatigue, and magicka at diagnostic start. This is intentionally limited to the `-deepWatersTest` path.
- Added shore-profile logging for screenshots. Each capture now samples forward from the camera and logs terrain pixel, local fraction, terrain height, baked water, carved water, water-column presence, floor height, and downward ray hit.
- `ooo` profile showed the seam is exactly at a carved-water to baked-shore transition: `d=0` is carved water with `depth=3.08m`; `d=24m` is `baked=1` but `carved=0` with rising terrain and no water column.
- Material/top-surface/cap-clipping probes did not remove the `ooo` slit, so the visible failure was a geometry stitch problem rather than only a water shader problem.

Change:

- Raised the shore skirt top from slightly below the water plane to slightly above it by changing `ShoreWallSurfaceInset` from `0.20m` to `-0.05m`. This lets the vertical stitch cover the thin look-under-land gap at shallow carved-water edges.
- Kept the boundary-wall guard from the previous pass: walls are preserved when the adjacent loaded terrain sample is land/mixed shore, but not when the adjacent sample is actually water. This avoids recreating the `nnn` between-pixel wall.
- Added a shallow underside-water fade refresh so near-shore water curtains are less likely to read as a hard dark strip.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files before the diagnostic sweep.
- Full diagnostics sweep: `DeepWatersDiagnostics\deep-waters-diagnostics-20260615-212429.csv`.
- `mmm`:
  - Started in pixel `207:223` with a `4.23m` column.
  - Crossed to pixel `207:222`.
  - Ended with a valid water column at `16.02m` depth.
  - Latest screenshot: `DeepWatersDiagnostics\deep-waters-mmm-mmm_straight_water_entry-end-20260615-212523.png`.
- `nnn`:
  - 10-second visual hold: `82.15 FPS`.
  - Shore profile stayed carved water across the tested map-pixel boundary out to `360m`.
  - Latest screenshot: `DeepWatersDiagnostics\deep-waters-nnn-shoreline-hold-20260615-212542.png`.
- `ooo`:
  - 10-second visual hold: `90.03 FPS`.
  - The obvious see-through shoreline slit is no longer present in the latest screenshot; a dark shallow band remains.
  - Latest screenshot: `DeepWatersDiagnostics\deep-waters-ooo-shoreline-hold-20260615-212602.png`.
- `ggg` sanity run:
  - 10-second visual hold: `106.14 FPS`.
  - Player has no active water column on land.
- `hhh` sanity run:
  - 10-second visual hold: `95.56 FPS`.
  - The seam is still visually abrupt, but the through-world hole was not visible in the latest diagnostic screenshot.

## QQQ/RRR/SSS Shoreline And Boat Diagnostics

Baseline:

- Reset `Assets/Game/Mods/deep-waters` to `origin/master` at `5dfb8f3` and restored the large `Resources/DistanceBake.bytes` and `Resources/DistanceBakeVanilla.bytes` files.
- Added targeted harness support for `qqq`, `rrr`, and `sss`.
- `qqq` runs as a natural forward swim probe with frame-level movement logging.
- `rrr` and `sss` run as stationary visual shoreline probes with screenshots and shore profiles.

Problems:

- `qqq`: swimming forward under a boat snapped the player upward.
- `rrr`: save started inside a shoreline ravine with high walls on both sides.
- `sss`: shoreline hole/invisible-floor case where the player could stand above water at a spot that should not be a swimmable column.
- Third-person fog: camera below water was not enough to trigger underwater fog when the player was above the water.

Diagnostics:

- Baseline `qqq` CSV: `deep-waters-diagnostics-20260617-185614.csv`.
  - Player Y jumped from `92.07` to `102.54` in one frame.
  - Maximum sampled Y reached `102.99`.
  - The player remained in the same map pixel, so this was not a streaming transition.
- Baseline `rrr`:
  - Player position reported `columnDepth=15.56m` after the first depth fix attempt and roughly `15.78m` before that.
  - Shore profile showed `localPointWater=1`, `bakedWater=0`, `carvedWater=1`.
  - The visual ravine matched seafloor relief being too deep immediately beside a fine-mask shore edge.
- Baseline `sss`:
  - Shore profile showed `localPointWater=0`, `bakedWater=0`, `carvedWater=1`, `columnPresent=1`.
  - This meant the fine carve mask was creating a water column over a dry live-terrain point.

Changes:

- Boat snap:
  - `OutdoorShoreExitAssist` now rejects shore-exit landing hits whose X/Z is still inside a real open-water column (`Depth >= 2m`).
  - This prevents boats, docks, and other overhead colliders above swimmable water from being treated as shore.
- Shoreline ravines:
  - `DeepWaterDistanceBake.SampleEdgeDistanceMeters()` now caps the coarse edge distance with a small nearby fine-mask edge search, so narrow fine-mask shore edges do not sample as fully offshore.
  - `DeepBathymetry` now fades mid/high seafloor relief in over the first `180m` from the shore edge. This keeps random bathymetry noise from cutting deep trenches right at shoreline contact.
- Dry-point water columns:
  - `DeepWaterFloorBuilder` now refuses fine-mask carving where `DeepWaterWaterClassification.IsLocalPointWater()` says the live terrain point is dry.
  - `DeepWaterWorld.TryGetWaterColumn()` applies the same live-terrain local-water gate before accepting a fine-mask water column.
- Third-person fog:
  - `UnderwaterDistanceFog.TryGetUnderwaterPresentation()` now treats the camera being below the ocean surface as underwater presentation, independent of whether the player is currently swimming.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Final combined diagnostics sweep: `DeepWatersDiagnostics\deep-waters-diagnostics-20260617-191505.csv`.
- `qqq`:
  - `2068` frame movement samples.
  - `minY=94.78`, `maxY=94.78`, `delta=0.0`.
  - `0` vertical jumps greater than `0.05m`.
  - Latest end screenshot: `DeepWatersDiagnostics\deep-waters-qqq-qqq_straight_boat_probe-end-20260617-191548.png`.
- `rrr`:
  - 10-second visual hold: `225.11 FPS`.
  - `columnDepth=3.81m`, down from roughly `15.6m`.
  - Latest screenshot shows a shallow shore slope instead of the prior ravine wall.
  - Latest screenshot: `DeepWatersDiagnostics\deep-waters-rrr-shoreline-hold-20260617-191607.png`.
- `sss`:
  - 10-second visual hold: `183.07 FPS`.
  - `columnPresent=0`, `carvedPresent=0` at the dry shoreline position.
  - Latest screenshot: `DeepWatersDiagnostics\deep-waters-sss-shoreline-hold-20260617-191625.png`.

Notes:

- The physical classifications for `qqq`, `rrr`, and `sss` now match the intended behavior in diagnostics.
- `sss` still has a visually hard water/shore edge, but it no longer reports a ghost water column at the dry standing point.
- The third-person fog fix is code-path validated by compile only; this harness does not yet automate camera-mode switching.

## QQQ/RRR/SSS Follow-Up: Shared Shoreline Classification

Root cause found:

- The C# terrain-cap patcher already had a conservative safety check: only remove promoted water texels when the whole texel is safely below ocean level.
- The shipped clip shaders ignored that decision and clipped every water-like terrain texel by tile id.
- That split explains the recurring shoreline holes: CPU code could decide a shore texel was not safe to delete, but the shader still deleted it.

Changes:

- `OutdoorSwimDriver` now uses the same live-terrain local-water gate as `DeepWaterWorld.TryGetWaterColumn()` before accepting fine-mask water for swimming/collider gating.
- `DeepWaterFloorMesh` raises near-shore seafloor vertices toward the live terrain height over the first `180m` from shore, reducing fine-mask ravines without lowering existing seabed.
- `DeepWaterTerrainCapRenderer` now rewrites unsafe shore-water texels to the nearest solid shoreline texel in the patched tilemap texture, so the currently shipped old shader no longer clips those shore texels.
- The clip shader sources were also updated to clip only an explicit magenta sentinel written by C#, for the next full Unity bundle rebuild.
- `UnderwaterDistanceFog` now hooks every active camera, so third-person below-water cameras get the fog image effect even when the player object is above water.
- Tracked `.meta` files were removed from the mod repository index; local files remain on disk.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- A full Unity bundle rebuild was attempted but Unity 2019 batch mode stopped at licensing, so the playable bundle was repacked with the TextAsset patcher. The C# tilemap rewrite makes the SSS fix testable with the current compiled shader.
- Packed both playable `.dfmod` files: live install and staging install.
- Latest combined diagnostics sweep: `DeepWatersDiagnostics\deep-waters-diagnostics-20260617-200938.csv`.
- `qqq`:
  - `1855` frame movement samples.
  - `minY=99.04`, `maxY=99.44`; no large boat snap returned.
  - Latest end screenshot: `DeepWatersDiagnostics\deep-waters-qqq-qqq_straight_boat_probe-end-20260617-201022.png`.
- `rrr`:
  - 10-second visual hold: `211.38 FPS`.
  - `columnDepth=3.81m`, `renderedSeafloorY=99.65`, `carvedPresent=1`.
  - Screenshot is improved from the original ravine, but still shows a hard/dark horizontal shoreline band.
  - Latest screenshot: `DeepWatersDiagnostics\deep-waters-rrr-shoreline-hold-20260617-201040.png`.
- `sss`:
  - 10-second visual hold: `151.36 FPS`.
  - `waterGateActive=0`, `waterGateDisabled=0`, `waterGateDesired=0`.
  - Latest screenshot no longer shows the giant deleted shoreline face/hole from the previous run.
  - Latest screenshot: `DeepWatersDiagnostics\deep-waters-sss-shoreline-hold-20260617-201059.png`.

Remaining:

- `rrr` still needs a deeper visual fix for the dark horizontal shore band/void. The next likely target is not another swim-state patch; it is the geometry/render boundary between the generated seafloor skirt and mixed live terrain.
- Third-person fog still needs a manual or automated camera-mode validation save.

## Biome Visual Probe + Inland/Lake Water Fallback

Prompt:

- User created `temperate`, `swamp`, `tropical`, `desert`, `cold`, `open ocean`, and `mystery` saves for biome visual checks.
- Goal: each underwater biome should be visually identifiable by seafloor texture and decoration mix.
- User clarified `desert` is visibly in water even though diagnostics initially reported no Deep Waters column; `mystery` starts at shore and reaches water by walking forward.

Findings:

- The first visual sweep showed most coastal biome saves using similar pale/green seabed material, because the current map pixel is usually DFU `Ocean`; nearby land climate needs to drive the underwater biome.
- `desert` and `mystery` proved a separate issue: DFU/live terrain reported water (`localPointWater=1`) but the ocean-connected fine bake reported no water (`bakedWater=0`, `carvedWater=0`, `oceanConnected=0`).
- That means these are local/inland lake water cases excluded by the ocean bake, not dry land and not a camera/save mistake.
- Terrain texture candidates were dumped to `Diagnostics/seafloor-texture-candidates`, with labeled sheets for `TEXTURE.002`, `TEXTURE.102`, `TEXTURE.302`, and `TEXTURE.402`.

Changes:

- `DeepWaterTileData` now resolves `BiomeClimateIndex` from adjacent non-ocean land climate for ocean pixels, so coastal underwater decoration/fish/material selection can follow nearby land biome while true open ocean stays ocean.
- Local visibly-wet tiles absent from the ocean fine bake now use `UsesLocalWaterFallback`, generating a shallow local floor/content path instead of being treated as dry.
- The seafloor material now chooses actual texture records by biome instead of loading record `1` from the terrain ground archive:
	- Open ocean: `402:30`.
	- Tropical/rainforest/subtropical: `402:16`.
	- Swamp: `402:25`.
	- Temperate/woodlands: `302:25`.
	- Haunted woodlands: `302:3`.
	- Cold/mountain/mountain woods: `102:3`.
	- Desert/desert2: `002:10`.
- Seafloor texture strength increased from `0.25` to `0.45`; this is still shader-local grain/tinting, but the visible texture identity now comes from different records.
- Winter ground-archive swapping was removed for seafloor material selection. Underwater cold reads as rock/silt rather than snow.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Biome probe run: `DeepWatersDiagnostics\deep-waters-diagnostics-20260618-224516.csv`.
- Visual contact sheet: `DeepWatersDiagnostics\biome-probe-contact-latest.png`.
- `desert` after fallback:
	- `columnPresent=1`, `columnDepth=11.33`, `localPointWater=1`, `bakedWater=0`, `carvedWater=1`, `oceanConnected=1`.
	- Decorations/fish are now eligible, though the current save looks across the lake surface rather than fully underwater.
- `mystery` after walking into water:
	- `columnPresent=1`, `columnDepth=10.94`, `localPointWater=1`, `bakedWater=0`, `carvedWater=1`, `oceanConnected=1`.
- Older shoreline regression probe: `DeepWatersDiagnostics\deep-waters-diagnostics-20260618-224844.csv`.
- Regression contact sheet: `DeepWatersDiagnostics\shoreline-regression-contact-latest.png`.
- `mmm` still transitions into a real water column and shows underwater content.
- `rrr`/`sss` now classify as local fallback water (`bakedWater=0`, `carvedWater=1`) rather than dry; screenshots did not show the previous huge obvious voids in this pass, but these remain part of the shoreline edge family and should be watched.

## Biome Texture Tweak + Desert Swim Gate

Prompt:

- Keep temperate and cold texture choices.
- Make swamp and tropical more distinct.
- Restore the previous open-ocean texture.
- Desert now has depth but the player is stuck at the surface.

Changes:

- Open ocean seafloor texture restored from `402:30` to `402:1`.
- Tropical/rainforest/subtropical seafloor texture changed from `402:16` to `402:28`.
- Swamp seafloor texture changed from `402:25` to `402:15`.
- `OutdoorSwimDriver.IsSolidShoreForColliderGate()` now asks `DeepWaterTileData.IsCarvedWater()` instead of querying `DeepWaterDistanceBake.IsCarvedWater()` directly, so local fallback lake/desert water is not mistaken for solid shore.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Probe run: `DeepWatersDiagnostics\deep-waters-diagnostics-20260618-230601.csv`.
- Texture contact sheet: `DeepWatersDiagnostics\texture-tweak-contact-latest.png`.
- `desert`: `columnPresent=1`, `columnDepth=11.33`, `localPointWater=1`, `bakedWater=0`, `carvedWater=1`, `playerSwimming=1`, `controllerGrounded=0`, `waterGateActive=1`.
- `mystery` after walking forward: `columnPresent=1`, `columnDepth=10.94`, `playerSwimming=1`, `controllerGrounded=0`, `waterGateActive=1`.

## Local Lake Bathymetry + Open Ocean Texture Restore

Prompt:

- Open ocean should use the same texture as the latest GitHub commit.
- `desert` and `mystery` are inland/local water and should not be flat empty bowls.
- Lakes should get about 25% max-depth behavior with non-flat bathymetry.
- Remove the random wall visible to the right on `mystery`.

Changes:

- Open ocean material now follows the latest GitHub path again: DFU seasonal ground archive from `MapsFile.GetWorldClimateSettings(worldClimate).GroundArchive`, record `1`.
- Local fallback water tiles now build a tiny 33x33 distance-to-shore field from DFU map water classification instead of using one constant shelf distance.
- Local fallback distance is scaled up and capped at 55% of the shelf ramp, which gives small/medium lakes meaningful slope without pretending they are full open ocean.
- Local fallback tiles no longer emit boundary skirts. Those skirts were the likely source of the isolated mystery wall because the tile is locally wet but absent from the ocean-connected bake.
- `mystery` is now treated as a stationary visual probe save, with an extra right-facing diagnostic screenshot to catch side-wall regressions.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Final post-pack three-save probe run: `DeepWatersDiagnostics\deep-waters-diagnostics-20260618-234305.csv`.
- Final visual contact sheet: `DeepWatersDiagnostics\lake-texture-final-contact-latest.png`.
- `desert`: `columnPresent=1`, `columnDepth=84.52`, `renderedSeafloorY=15.43`, `localPointWater=1`, `bakedWater=0`, `carvedWater=1`, `decorationsCurrent=1053.75`, `fishCurrent=42.50`.
- `mystery`: screenshot rows report `columnPresent=1`, `columnDepth=84.52`, `renderedSeafloorY=15.43`, `localPointWater=1`, `bakedWater=0`, `carvedWater=1`, `decorationsCurrent=2304`, `fishCurrent=220`.
- `open ocean`: `columnDepth=199.94`, visual floor texture is back to the darker latest-style open-ocean look.
- Mystery side-check run: `DeepWatersDiagnostics\deep-waters-diagnostics-20260618-234049.csv`.
- Mystery side-check contact sheet: `DeepWatersDiagnostics\mystery-right-check-latest.png`.
- Mystery right-look screenshot did not show the reported vertical wall after local fallback skirts were disabled.

## Desert Local Lake Seam Fix + Open Ocean Material Match

Prompt:

- `desert` showed giant broken seams after the local lake bathymetry change.
- Open ocean floor texture still did not look like the latest GitHub build.

Changes:

- Removed the per-tile local-water distance field. It made each disconnected water tile solve its own depth gradient, which could disagree across tile edges and create a cliff.
- Local fallback water now uses one capped shelf distance (`30%` of the ocean shelf ramp), so it gets modest lake depth and global noise relief without tile-edge cliffs.
- Local fallback carving now rejects non-water cells before cutting terrain holes. The previous condition only did that on baked-ocean tiles.
- Open ocean material now skips biome palette overrides and uses the latest-GitHub texture strength (`0.25`) while keeping custom biome textures for non-ocean floors.
- Added left/right desert diagnostic screenshots to catch off-angle seam regressions.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Three-save probe run: `DeepWatersDiagnostics\deep-waters-diagnostics-20260618-235428.csv`.
- Contact sheet: `DeepWatersDiagnostics\desert-lake-ocean-fix-contact-latest.png`.
- `desert`: `columnDepth=25.17`, `decorationsCurrent=1055`, `fishCurrent=75`, local water fallback (`bakedWater=0`, `carvedWater=1`), no giant seam in saved view.
- `mystery`: `columnDepth=45.70`, local water fallback still working.
- `open ocean`: `columnDepth=199.94`, old-style darker material path restored.
- Desert angle sweep: `DeepWatersDiagnostics\desert-angle-sweep-latest.png`.
- Desert left/forward/right captures did not show the reported giant seam.

## Open Ocean Texture Source Trace

Prompt:

- Desert is acceptable, but open ocean still does not match the floor texture embedded in the `open ocean` save screenshot.

Finding:

- Latest GitHub `DeepWaterFloorMaterial` did not choose an explicit ocean record. It used `MapsFile.GetWorldClimateSettings(worldClimate).GroundArchive`, then applied DFU's winter rule (`GroundArchive + 1` for non-desert climates), and loaded record `1`.
- DFU's own `TerrainMaterialProvider.GetClimateInfo()` does the same seasonal archive increment before assigning terrain materials.
- Ocean climate is classified as `Swamp` in DFU, so it is winter-adjusted. In the tested open-ocean save this means the texture source is the seasonal ocean ground archive record `1`, not hard-coded `402:1` or any guessed atlas candidate.

Changes:

- Kept pure-ocean tiles routed to the ocean material instead of borrowing a nearby coastal biome. Borrowing the neighbor biome restored texture density but produced a green/yellow coastal identity in open ocean.
- True-ocean floor now uses the latest-GitHub/DFU seasonal ground-archive path, record `1`.
- True-ocean floor skips the biome palette override and uses the latest-GitHub texture strength (`0.25`) and DFU terrain scale (`0.15625`).
- Non-ocean biome floor textures and palettes are unchanged.

Rejected candidates:

- `402:1`: color was closer but texture was too smooth.
- `402:25`: texture was too dark/green even after skipping the ocean palette.
- Neighbor-biome routing: visibly wrong green/yellow coastal floor identity.
- `302:31`: blue-gray, but produced obvious diagonal/striped UV bands.
- `402:0` at full strength: readable texture, but too saturated blue.
- `402:0` at reduced strength/scale: still not the same source; it was a visual guess, not the DFU/latest-GitHub logic.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Final comparison sheet: `DeepWatersDiagnostics\open-ocean-target-vs-current-seasonal-ground-record1.png`.

## Blue Ocean + Green Swamp Material Experiment

Prompt:

- Revert open ocean to the deeper blue visual candidate.
- Try the green swamp candidate on swamp floors.

Changes:

- True open ocean now uses explicit `402:0` with feature-texture strength `0.75`, half terrain scale, and no biome palette override.
- Swamp now uses explicit `402:25` with the same feature-texture treatment.
- Other biome floor material choices are unchanged.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Visual contact sheet: `DeepWatersDiagnostics\open-ocean-swamp-blue-green-check.png`.

## Coastal Swamp Material Correction + Darker Ocean Candidate

Prompt:

- The `swamp` save was not visibly loading the intended swamp floor texture.
- The open ocean floor should stay dramatic/dark/blue, but use a different texture than the previous bright-blue candidate.

Finding:

- The `swamp` save sits in an all-water coastal ocean map pixel (`557:296`). The biome resolver was treating all-water ocean pixels as true ocean too early, so nearby swamp/coastal biome identity could be skipped.
- After that was fixed, the swamp still looked sandy because featured ocean/swamp materials skipped `ApplyBiomePalette()`, leaving the shader's default shallow sand color in control.

Changes:

- All-water ocean pixels now only force true-ocean material when sampled across the tile and fully beyond `DeepBathymetry.ShelfBreakDistance`.
- Featured ocean/swamp materials now also apply the biome palette, so shallow swamp floors are no longer tan by default.
- Open ocean moved from `402:0` to darker blue candidate `402:36`.

Validation:

- `dotnet build .\Assembly-CSharp.csproj -v:minimal` succeeded with existing project warnings only.
- Packed both playable `.dfmod` files.
- Visual contact sheet: `DeepWatersDiagnostics\open-ocean-swamp-40236-paletted-check.png`.

Follow-up:

- The strong featured texture used local terrain-tile UVs, which made the open ocean read as obvious repeated strips.
- Seafloor UVs now use the same map-pixel-anchored coordinates as bathymetry, wrapped to a 4096m cycle to avoid GPU precision banding.
- Featured ocean/swamp texture strength was reduced from `0.75` to `0.45`, and featured textures use the normal terrain scale.
- Visual contact sheet: `DeepWatersDiagnostics\open-ocean-swamp-continuous-uv-modulo-check.png`.

Correction:

- Latest GitHub and DFU's `TerrainMaterialProvider` do not select hard-coded `402:*` records. They use `MapsFile.GetWorldClimateSettings(worldClimate).GroundArchive`, add `+1` in winter for non-desert climates, then render ground record `1`.
- Ocean and Swamp both map to DFU swamp ground archive `402`; in the tested winter saves they resolve to `403:1`.
- Ocean now uses that exact latest-GitHub path at texture strength `0.25`.
- Swamp now uses the same DFU archive/record path, with stronger texture influence and the swamp palette so shallow shelf color does not wash the texture back to beige.
- Visual contact sheet: `DeepWatersDiagnostics\open-ocean-swamp-dfu-ground-swamp-strong-check.png`.
