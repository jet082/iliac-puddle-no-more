# Iliac Puddle No More

A Daggerfall Unity mod that gives the Iliac Bay actual **depth** — up to 250 meters of it, with varying bathymetry. In vanilla DFU every water tile is flat: the terrain sampler clamps sub-ocean heights to exactly ocean level, so stepping into the sea means stepping onto a flat plane. This mod carves holes through vanilla terrain in ocean-connected water cells, generates a procedural seafloor sub-mesh beneath them with climate-aware deep-water bathymetry (open ocean trenches, subtropical shelves, swamp shallows), renders a water surface at the old ocean level, lets you swim down with full DFU swim mechanics (breath, fog, SFX, shallow-vs-deep transitions), and populates the depths with appropriate aquatic enemies. Argonians no longer drown.

## Install

Drop `iliac-puddle-no-more.dfmod` into `DaggerfallUnity_Data/StreamingAssets/Mods/`. It has no required dependencies, but declares optional load-after relationships for Wilderness Overhaul and BasicRoads so their terrain wrappers run first when installed.

## What it does

| File                          | Role                                                                                         |
|-------------------------------|----------------------------------------------------------------------------------------------|
| `DeepWaters.cs`               | Bootstrap. Loads settings, registers custom items, installs the other components, and applies Argonian infinite breath. |
| `DeepBathymetry.cs`           | Pure depth function `f(worldX, worldZ, climate, distanceToCoast) → depth`. Layered Perlin + climate-specific baselines + trench mask. Adjacent tiles agree at seams because they sample the same world coords. |
| `DeepWaterTileData.cs`        | Per-tile cache: climate index and distance-to-coast field. Computed once at promotion via a chamfer-distance pass over the heightmap. |
| `DeepWaterFloorBuilder.cs`    | Listens to terrain promotion. Decides if the tile is ocean-connected, computes a hole mask, calls `TerrainData.SetHoles`, spawns the seafloor sub-mesh. |
| `DeepWaterFloorMesh.cs`       | Per-tile seafloor sub-mesh plus shared material/shader resources. 65x65 vertex grid sampled from `DeepBathymetry`. |
| `WaterSurfaceManager.cs`      | Spawns a visible water-surface quad and non-blocking trigger at ocean elevation on every terrain with water. |
| `OutdoorSwimDriver.cs`        | Tricks DFU's existing dungeon-swim logic into running outdoors. Includes shore-exit assist.   |
| `UnderwaterEnemySpawner.cs`   | Spawns aquatic encounters near the player while loaded ocean is nearby.                       |
| `UnderwaterPassiveFishSpawner.cs` | Spawns, places, builds, and tracks lootable passive fish schools.                         |
| `UnderwaterLootSpawner.cs`    | Spawns stray underwater loot, wreck rubble, containers, and treasure clusters near exploration paths. |

### How the swim driver works

DFU's dungeon swim system is complete — movement, speed, SFX, breath, fog, crouch, animations. It's just gated behind `if (isPlayerInsideDungeon)` in `PlayerEnterExit.Update`. This mod reflect-sets that field to `true` for the moments PlayerEnterExit reads it, so the dungeon branch runs outdoors, then sets it back. Two MonoBehaviours at opposite Script Execution Orders bracket PlayerEnterExit's frame:

- `OutdoorSwimDriver` (SEO -32000, runs first): forge the flag, set `blockWaterLevel` to our ocean Y, force `OnExteriorWater = Swimming`.
- `OutdoorSwimDriverAfter` (SEO +32000, runs last): restore the flag, re-apply `OnExteriorWater` (PlayerMotor's own raycast clobbers it during its Update), call `UpdateFog` (vanilla skips this outdoors).

Whether the player actually swims is decided by DFU's dungeon branch comparing player.y against blockWaterLevel — exactly the mechanism used in dungeons. There's no custom motion code and no custom speed math.

`SwimmingSfxBridge.cs` contains `UnderwaterPresentationEffects`, which plays DFU's own `SplashSmall` clip through the player audio source after every 2.5 world units of swimming movement and owns the small underwater presentation effects such as audio muffling and weather suppression.

### How the Argonian breath works

Vanilla DFU only gives Argonians a 50% chance per drowning tick to NOT lose breath — they can still drown, just slower. `DeepWaters.LateUpdate()` sets `entity.IsWaterBreathing = true` every frame for Argonian player characters, which short-circuits the drowning code at line 323 of `PlayerEntity.cs`. Same flag the water-breathing potion uses, just permanent. Works everywhere — outdoors, dungeons, anywhere submerged.

## Settings

Edit via DFU's in-game mod settings UI.

| Setting                  | Default | Description                                                                 |
|--------------------------|---------|-----------------------------------------------------------------------------|
| `WaterDepth`             | `200`   | Maximum ocean depth in meters (5–250). Ocean climate reaches near this; coastal climates scale lower. |
| `SpawnWaterSurfaces`     | `true`  | Render visible water surfaces. Turn off to see the sunken terrain raw.     |
| `SpawnUnderwaterEnemies` | `true`  | Populate the bay with slaughterfish, dreugh, and lamia.                    |
| `EnemyFrequency`         | `0.5`   | Ambient enemy frequency.                                                    |
| `PassiveFishFrequency`   | `0.5`   | Passive fish frequency.                                                     |
| `FishParadise`           | `false` | Dramatically increases passive fish density.                                |
| `SpawnUnderwaterDecorations` | `true` | Add seafloor flora, rocks, and debris.                                  |
| `DecorationFrequency`    | `0.5`   | Seafloor decoration density.                                                |
| `SeafloorLootRate`       | `0.5`   | Isolated underwater loot rate.                                              |
| `TreasureClusterRate`    | `0.5`   | Treasure cluster chance.                                                    |
| `TreasureCove`           | `false` | Dramatically increases underwater treasure density.                         |
| `WaterSurfaceTopTransparency` | `0.5` | Water-surface transparency from above.                                  |
| `WaterSurfaceBottomTransparency` | `0.5` | Water-surface transparency from below.                              |
| `UnderwaterFogStrength`  | `0.5`   | Underwater fog density.                                                     |
| `UnderwaterFogDistance`  | `0.5`   | Underwater fog view distance.                                               |
| `ArgonianInfiniteBreath` | `true`  | Argonians never drown anywhere in the world.                               |

## Known limitations

- Inland lakes and most rivers stay vanilla-flat. The ocean-connectivity heuristic rejects tiles whose heightmap edges have less than ~25% water; lakes-in-a-basin and narrow rivers fall under that line. Tunable in `DeepWaterTileData.ComputeOceanConnectivity` if you want broader application.
- Bathymetry is purely procedural. No hand-placed named features yet (sunken Yokudan ruins, Maormer raid wrecks, central Iliac trench). The trench-mask noise layer creates trench-like features where its thresholded field exceeds the cutoff, but they aren't anchored to lore.
- Shore-exit climb assist is custom (not from DFU) — probes forward-and-down when you press W near shore and teleports you onto the beach. Works for beaches up to 8m above water.
- Underwater enemies despawn when their terrain unloads. They're random ambient encounters, not persistent — leave a tile and come back, you'll find different enemies.
- Cross-tile distance-to-coast: the BFS for distance-to-land runs per-tile and saturates at the tile boundary. A pure-ocean tile next to a land-containing tile won't "see" the adjacent land for shelf-ramp purposes — depths near that shared edge jump directly to climate-base. Practically invisible because the adjacent tile's shelf ramp covers the visible joint, but worth knowing.

## License

MIT.
