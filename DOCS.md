# Iliac Puddle No More - Code Documentation

This document describes how the mod works after the cleanup pass. It is meant
to be a practical map of the code: where each responsibility lives, how data
flows between systems, and which knobs are safe to edit.

The mod has four major jobs:

1. Lower ocean, lake, and river terrain so water has real depth.
2. Draw a transparent water surface above the lowered seabed.
3. Convince Daggerfall Unity's indoor swimming systems to work outdoors.
4. Populate deep water with decorations, enemies, loot, and passive fish.

## File Map

- `DeepWaters.cs`: mod entry point and persistent singleton.
- `DeepWaters.Settings.cs`: loaded settings, slider scaling, and derived runtime values.
- `DeepWaters.Bootstrap.cs`: subsystem installation, fish item registration, and
  DFU terrain-texturing wrapping.
- `DeepWaterWorld.cs`: shared world/terrain helpers.
- `DeepWaterTerrainLookup.cs`: cached map-pixel terrain lookup.
- `DeepWaterFloorHeight.cs`: deterministic seafloor-height rule shared by
  texturing and seam reconciliation.
- `DeepWaterRuntime.cs`: shared reset signal for save loads and teleports.
- `UnderwaterEncounterPulse.cs`: shared pulse clock for enemies and passive fish.
- `TransientObjectTracker.cs`: shared cleanup for spawned transient objects.
- `DeepWaterRendering.cs`: shared rendering policies for cheap transient visuals.
- `DeepWaterTexturing.cs`: terrain height lowering jobs and floor tile conversion.
- `BoundaryReconciler.cs`: post-generation seam repair between adjacent terrain tiles.
- `WaterSurfaceManager.cs`: per-tile water surface lifecycle and non-blocking
  detection trigger.
- `WaterSurfaceResources.cs`: shared water mesh, material, shader, texture, and
  user-facing material settings.
- `UnderwaterDecorationCatalog.cs`: weighted decoration archive records.
- `UnderwaterDecorationReplacementCache.cs`: DREAM/texture-replacer material cache
  for decoration batches.
- `UnderwaterDecorationPlacement.cs`: seafloor decoration sampling and spacing.
- `UnderwaterDecorationBatchFactory.cs`: archive and texture-replacer decoration
  batch construction.
- `StenciledWaterSurface.shader`: transparent top/underside water rendering.
- `OutdoorSwimDriver.cs`: outdoor swimming bridge and underwater presentation control.
- `OutdoorSwimDfuBridge.cs`: reflection wrapper for DFU swim/dungeon state fields.
- `OutdoorShoreExitAssist.cs`: terrain/static-geometry shore snap helper.
- `PlayerShipWaterlineFix.cs`: owned ship exterior scene anchoring.
- `UnderwaterDistanceFog.cs` and `UnderwaterDistanceFog.shader`: underwater
  sky/no-depth fog pass.
- `UnderwaterAmbientMuter.cs`: underwater audio low-pass filter.
- `SwimmingSfxBridge.cs`: lightweight swim splash cadence.
- `UnderwaterWeatherSuppressor.cs`: rain and snow particle suppression while swimming.
- `UnderwaterWaveShadowFix.cs`: underwater compatibility for player-following
  lights and third-party wave shadows.
- `ArgonianWaterBreathing.cs`: optional Argonian infinite breath.
- `UnderwaterDecorations.cs`: seafloor flora and debris batches.
- `UnderwaterEnemySpawner.cs`: aquatic enemies and rare treasure guards.
- `UnderwaterLootSpawner.cs`: stray loot and treasure cluster pulse rules.
- `UnderwaterLootCatalog.cs`: treasure container sprites and random item mix.
- `UnderwaterLootPlacement.cs`: loot spawn-ring, seafloor, cluster-spacing, and
  recent-cell placement state.
- `UnderwaterLootObjectFactory.cs`: loot container and rubble-batch construction.
- `UnderwaterTreasureClusterSpawner.cs`: treasure cluster assembly.
- `UnderwaterPassiveFishSpawner.cs`: passive fish pulse budgeting and school
  orchestration.
- `PassiveFishSpeciesCatalog.cs`: editable passive fish species table.
- `PassiveFishResources.cs`: passive fish texture loading, custom item creation, and inventory icons.
- `PassiveFishPlacement.cs`: passive fish water-column and school-member placement.
- `PassiveFishFactory.cs`: runtime creation of lootable fish billboard GameObjects.
- `FishLootIconBridge.cs`: inventory-window icon bridge for picked-up fish.
- `PassiveFishSchool.cs` and `PassiveFishBehaviour.cs`: school movement and fish behavior.
- `ItemTemplates.json`: custom fish item templates.

## Startup Flow

`DeepWaters.Init()` is the only entry point. It is invoked by DFU during
`StateManager.StateTypes.Start` at priority `200`.

Startup order:

1. Store the `Mod` handle and create a persistent host GameObject.
2. Load settings from `modsettings.json`.
3. Wrap DFU's current `ITerrainTexturing` with `DeepWaterTexturing`.
4. Register passive fish item templates.
5. Install terrain and gameplay subsystems.
6. Mark the mod as ready.

`BoundaryReconciler` installs first because it needs to hear every terrain
promotion. The water surface, enemy, decoration, loot, fish, swim, breath,
weather, and audio systems all install at startup and gate their own behavior
from settings. That lets disabled subfeatures also remove stale terrain children
when DFU reuses terrain objects.

The manifest lists `wilderness overhaul`, `Wilderness Overhaul`, `basicroads`,
and `BasicRoads` as optional non-peer dependencies. DFU's dependency sorter uses
these file names to load this mod after those terrain wrappers when present,
without requiring either mod to be installed.

## Settings

All settings are read in `DeepWaters.LoadSettings()`. The mod expects its
settings file to be present and correct; a broken install should fail loudly
rather than hide the problem behind fallback behavior.

Important settings:

- `WaterDepth`: amount of vertical lowering applied to water samples.
- `SpawnWaterSurfaces`: controls visual water meshes.
- `SpawnUnderwaterEnemies`: controls enemy and treasure-guard spawning.
- `EnemyFrequency`: enemy spawn multiplier.
- `PassiveFishFrequency`: passive fish spawn multiplier.
- `FishParadise`: high-density passive fish mode.
- `SpawnUnderwaterDecorations`: controls seafloor decoration batches.
- `DecorationFrequency`: per-tile decoration chance.
- `SeafloorLootRate`: stray loot pulse rate.
- `TreasureClusterRate`: treasure cluster pulse chance.
- `TreasureCove`: boosts loot and cluster density for a high-treasure mode.
- `WaterSurfaceTopTransparency`: transparency of the water surface from above.
- `WaterSurfaceBottomTransparency`: transparency of the water surface from below.
- `UnderwaterFogStrength`: density cap for the underwater fog effect.
- `UnderwaterFogDistance`: view-distance multiplier for underwater fog.
- `ArgonianInfiniteBreath`: gives Argonians permanent water breathing.

The five frequency/rate sliders are normalized so the UI midpoint, `0.5`, is
the intended default feel. Internally that maps to the tuned defaults from
earlier builds: enemies `0.3`, fish `0.6`, decorations `1.0`, stray loot `0.7`,
and treasure clusters `0.1`. Setting a slider to `1.0` is roughly double that
default behavior.

The visual sliders also use `0.5` as the current default look. Surface
transparency below `0.5` becomes more opaque; above `0.5` becomes clearer. The
underside uses a gentler midpoint than the top surface, and its tint follows
`UnderwaterFogStrength`, so looking up from below does not turn the water plane
into a hard lid at moderate settings. `UnderwaterFogStrength` controls a tuned
outdoor fog density cap. `UnderwaterFogDistance` keeps the current falloff at
`0.5`, shortens fog range below that point, and extends it above that point.

Fish and enemy encounter range responds to clear-water visuals. At the default
look, encounters use the classic 35m to 55m spawn ring and reject immediate
on-screen pop-in. If the water surface becomes very transparent, fog becomes
thin, or fog distance is extended, the ring expands up to 90m to 180m while
retaining the same nearby-view safety distance.

## Shared Water-Column Logic

`DeepWaterWorld.cs` is intentionally small and central. Spawners used to each
recreate their own map-pixel and heightmap lookup logic; now they call one
helper.

The key method is:

```csharp
DeepWaterWorld.TryGetWaterColumn(float worldX, float worldZ, out DeepWaterColumn column)
```

It resolves:

- target map pixel from world X/Z and the player's current terrain tile;
- `DaggerfallTerrain`, `Terrain`, and `TerrainData`;
- sample X/Y inside the heightmap;
- seafloor local/world Y;
- ocean local/world Y;
- terrain transform parent;
- column depth.

Use this helper whenever new code needs to know whether a world position is
loaded ocean and where its seafloor is. That keeps all systems consistent about
tile orientation, DFU scale, and world compensation.

`DeepWaterWorld` also contains small shared helpers for common encounter work:

- outdoor-player gating;
- random ring positions;
- fractional spawn-count rolls;
- immediate camera-view rejection;
- transient reset events for save loads and `StreamingWorld` teleports.

Terrain lookups are cached by map-pixel key in `DeepWaterTerrainLookup`. Fish and
spawn systems ask for water columns often, so this avoids repeated DFU terrain
object lookups while still validating that cached terrain objects still belong
to the requested map pixel.

Fish, enemies, and loot all listen to that reset event so terrain-parented
objects cannot ride along when DFU rebuilds or reuses terrain objects.

## Terrain Lowering

`DeepWaterTexturing` decorates DFU's `ITerrainTexturing`. It inserts two jobs
before DFU assigns terrain tile textures:

1. `MarkDeepWaterJob` marks water samples that can be lowered.
2. `LowerMarkedHeightsJob` lowers those samples using deterministic Perlin
   variation keyed by world heightmap coordinates.

Then the inner DFU texturing job runs. After that, `ConvertFloorTilesJob`
changes pure water floor tiles from tile value `0` to dirt value `1`, leaving
shore transition tiles alone. This prevents the lowered seabed from looking
like a flat water texture under the actual water surface.

The lowering formula lives in two places:

- managed code: `DeepWaterTexturing.ComputeLoweredHeight()`;
- Burst job inline copy: `LowerMarkedHeightsJob.Execute()`.

Keep those two blocks in lockstep. `BoundaryReconciler` depends on the managed
version producing the same values as the job version.

## Boundary Reconciliation

DFU generates terrain tiles independently. Adjacent tiles can disagree on their
shared edge samples, especially where one tile sees land near the edge and the
other sees only water.

`BoundaryReconciler` runs after terrain promotion. For each promoted tile, it
checks left, right, top, and bottom neighbors that are already loaded.

For each shared edge sample:

- if either edge sample is land, leave it alone;
- if either inside sample is land, keep the shared edge at ocean height;
- otherwise compute the deterministic lowered water height and write it to both
  tiles.

When either tile changes, `TerrainData.SetHeights()` is called so Unity rebuilds
the terrain mesh.

## Water Surface Rendering

`WaterSurfaceManager` listens to `DaggerfallTerrain.OnPromoteTerrainData`.
When a terrain tile contains water, it creates one child object named
`DeepWaters_Surface`.

Each surface uses:

- a shared top-only quad mesh from `WaterSurfaceResources`;
- one shared `DeepWaters.WaterSurface` material from `WaterSurfaceResources`;
- the custom `DeepWaters/StenciledWaterSurface` shader;
- Daggerfall's terrain water tile texture `(302,0)` when available;
- a thin `BoxCollider` trigger with a `DeepWatersWaterSurface` marker component.

The material is shared intentionally. Boat mods can call
`WaterSurfaceManager.GetSharedWaterMaterial()` and set stencil properties once.
All water tiles then inherit the same values.

The trigger collider is also intentional. It does not block movement, but other
mods can raycast or trigger-query `DeepWaters_Surface` objects to find the new
waterline.

The shader has two passes:

- `TOP_SURFACE`: transparent water seen from above, with vertical water-column
  darkening based on how far the terrain is below the surface and how far the
  view ray travels through water.
- `BOTTOM_SURFACE`: transparent underside seen from below, tinted by the same
  fog-strength setting that controls underwater murkiness.

The top pass needs Unity's camera depth texture so it can estimate the water
column between the surface and the terrain below. `UnderwaterDistanceFog` keeps
that depth texture available from a tiny camera `OnPreCull` helper while
outdoors, even when the player is not underwater, so looking down from shore or
a boat can still fog deep water. Because transparent-surface depth can be
inconsistent across exterior render paths, the shader also has a fallback based
on the configured water depth and the view angle through the water column.

The underside pass deliberately does not apply per-pixel distance fog to the
water plane itself. From below, the surface is a huge horizontal plane close to
the camera, and distance-fogging that plane creates circular bowl artifacts
around the player. World geometry and fish behind the surface still receive
normal DFU fog.

The stencil settings let compatible boat or hull shaders prevent water from
rendering through boat interiors.

## Underwater Visibility

DFU's normal underwater fog is still the foundation. `OutdoorSwimDriver`
configures `PlayerEnterExit.UnderwaterFog` and lets DFU apply `RenderSettings`
fog to normal rendered geometry.

That is not enough for the open ocean, because sky/no-depth pixels are not
fogged by Unity's normal fog. When distant terrain stops rendering, the camera
can otherwise see skybox through the waterline. `UnderwaterDistanceFog` attaches
a lightweight image effect to the main camera while the player is underwater.
It reads the camera depth texture and darkens only sky/no-depth pixels toward
DFU's underwater fog color.

The pass is water-volume aware:

- rendered underwater geometry keeps DFU lighting and normal `RenderSettings`
  fog;
- sky/no-depth pixels fade by the distance from the camera to the water surface;
- when looking up, fog stops at the water surface instead of continuing into the
  sky above it;
- when looking toward the horizon, the ray travels a long way through water, so
  the view reaches full fog before distant missing terrain reveals skybox.

`UnderwaterFogStrength` controls DFU's normal fog on rendered geometry. The
no-depth pass intentionally keeps its own minimum density even when that setting
is low, because it is hiding skybox/missing-terrain leaks rather than styling
nearby objects. `UnderwaterFogDistance` stretches or compresses that safety fade
through `DeepWaters.UnderwaterFogDistanceMultiplier`.

`UnderwaterWaveShadowFix` handles the compatibility side:
DFU has player-following local lights that do not cast shadows. The exterior
`IndirectLight` is always near the player, and `EnablePlayerTorch` can briefly
enable the player torch because `OutdoorSwimDriver` borrows DFU's dungeon
swimming state for part of the frame. Underwater, either light can wash out
contrast in a perfect circular radius around the camera. The fix suppresses
those player-following lights only while underwater presentation is active,
after DFU has updated lighting for the frame. It also disables real shadow
casting on Come Sail Away-style wave renderers while submerged, because those
large, thin, above-camera meshes are culled inconsistently from Unity's shadow
frustum when viewed from below. Their normal visual rendering remains intact,
but their shadows are intentionally left off while submerged. A missing wave
shadow is less distracting than circular shadow holes or floating artifacts,
and their original shadow settings are restored when the player leaves
underwater presentation.

## Outdoor Swimming

DFU already knows how to swim in dungeon water. `OutdoorSwimDriver` temporarily
forges the fields DFU checks so the same swimming behavior works outdoors.

It uses two components:

- `OutdoorSwimDriver` runs very early in the frame.
- `OutdoorSwimDriverAfter` runs very late in the frame.

Early phase:

1. Check that the player is outside.
2. Resolve the ocean surface height.
3. Decide whether the player's swim check point is in water.
4. Temporarily set DFU private fields so the player is treated like they are in
   a dungeon water block.
5. Apply or remove underwater presentation effects.

Late phase:

1. Restore the "inside dungeon" field.
2. Keep the exterior water method on swimming while needed.
3. Update DFU submerged/fog state based on presentation state.

`PlayerFootsteps` runs from `FixedUpdate()`, not `Update()`. The driver also
refreshes DFU's exterior-water and water-level fields in `FixedUpdate()` so
DFU's water audio and submerged state remain current.

`SwimmingSfxBridge` plays DFU's own `SplashSmall`
clip through the player's `DaggerfallAudioSource` after every 2.5 world units
of swimming movement. This covers both dungeon and outdoor swimming and avoids
copying the rest of DFU's footstep logic.

`UnderwaterWeatherSuppressor` works with DFU's
`PlayerWeather` owns the rain and snow particle GameObjects, so the suppressor
only disables those particles while the player is swimming outdoors. It leaves
the actual weather type, music, sky, and lighting alone, then reapplies
`PlayerWeather.WeatherType` when the player leaves the water.

Presentation state is intentionally separate from swim physics. This lets the
player swim with their head above water without fog or muffled audio, while
still keeping underwater effects active for third-person cameras when the
camera is below the effective waterline.

## Shore Exit Assist

`OutdoorSwimDriver.TryShoreExitAssist()` helps the player climb out when moving
forward toward shore. It raycasts downward in front of the player and only acts
on safe surfaces:

- `Terrain`;
- parents containing `Terrain`;
- transforms tagged with DFU's `StaticGeometry` tag.

Passive fish and enemies are ignored so the player cannot snap onto creatures
near the waterline.

## Audio And Breath

`UnderwaterAmbientMuter` adds an `AudioLowPassFilter` to the active audio
listener when `OutdoorSwimDriver.IsPresentationUnderwater()` is true. The filter
is disabled when the player surfaces, goes indoors, or the component is disabled.

`ArgonianWaterBreathing` runs in `LateUpdate()` and reapplies
`PlayerEntity.IsWaterBreathing = true` for Argonians. It runs late because DFU
clears constant effects earlier in the frame.

## Decorations

`UnderwaterDecorations` populates seafloor billboards. It does not spawn every
loaded tile immediately. Instead:

- terrain promotions and nearby map-pixel changes enqueue candidate tiles;
- a worker processes the queue at `StreamingWorld.OnUpdateTerrainsEnd`;
- each terrain object records which map pixel its decoration state belongs to;
- stale batches are removed when DFU reuses a terrain object for a new map pixel;
- `UnderwaterDecorationPlacement` generates positions on a stride through the
  heightmap and enforces spacing;
- `UnderwaterDecorationBatchFactory` creates archive or texture-replacer
  billboard batches.

Decorations use archive `105`, weighted toward flora records and lightly toward
debris records. The frequency setting rolls decoration passes, so values above
the old `1.0` density can add more seafloor dressing instead of simply capping.

When asset injection is enabled, `UnderwaterDecorationReplacementCache` probes
each decoration record once and reuses the replacement material and batch sizing.
That keeps DREAM-compatible decorations from falling back to white boxes without
creating one GameObject per decoration.

## Enemies

`UnderwaterEncounterPulse` owns one shared pulse clock for enemies and fish.
`UnderwaterEnemySpawner` is a participant in that pulse; it does not fill entire
terrain tiles. Instead, while loaded ocean exists near the player, it rolls a
scaled count and tries positions in a ring around the player:

- normal range: 35m to 55m;
- clear-water range: up to 90m to 180m;
- pulse distance: 35m;
- spawn Y: just above the seafloor;
- minimum water depth: 4m;
- parent: resolved terrain transform.

The gate allows nearby ocean, not only the player's exact X/Z. That means
enemies can spawn while the player is near shore as long as the candidates are
in loaded ocean. Outdoor swimming uses DFU's dungeon-swim state internally, so
the shared world helper treats that forged swim frame as an exterior water
context instead of pausing pulses.

The normal pool is mostly slaughterfish, with dreugh and lamia mixed in. A small
rare chance can pull from undead or ice atronach types.

Treasure clusters call:

```csharp
UnderwaterEnemySpawner.TrySpawnRareEnemiesNearTreasureCluster(centre)
```

That method obeys `SpawnUnderwaterEnemies` and `EnemyFrequency`, but uses a
separate treasure-guard curve that can spawn up to 5 rare guards at frequency
`1.0`. At `0.4`, the target is about 2 guards before placement failures.

## Loot

`UnderwaterLootSpawner` uses pulse spawning rather than pre-populating the
ocean. This keeps work proportional to exploration. Placement details live in
`UnderwaterLootPlacement`, including the forward-biased spawn ring, seafloor
resolution, cluster loot spacing, and recent-cell memory.

A pulse can happen when:

- loaded lootable ocean exists near the player;
- the player travels far enough horizontally from the last pulse anchor.

Loot pulses run near water whether the player is underwater, above deep ocean,
or walking near shore. If the player is not in or directly above deep water, the
stray-loot and treasure-cluster rolls are reduced to one eighth of their normal
rate.

Stray loot:

- rolls a small count per pulse;
- spawns in a ring outside immediate view;
- remembers recent spawn cells to avoid clumping.

Treasure clusters:

- roll as a per-pulse chance;
- delegate cluster assembly to `UnderwaterTreasureClusterSpawner`;
- build containers and rubble batches through `UnderwaterLootObjectFactory`;
- optionally spawn rare enemy guards.

Treasure container graphics use archive `216`, but passive fish item icons also
live in that archive at records `42-48`. The treasure container visual pool
therefore excludes those records so containers do not appear as fish.

## Passive Fish

`UnderwaterPassiveFishSpawner` spawns lightweight, lootable fish billboards. It
uses the shared enemy/fish encounter pulse and can run as long as a fishable
ocean column is near the player. Unlike loot, the player does not need to be
over deep water at their exact position; fish may populate nearby water when the
player is near shore.

The fish path is intentionally split:

- `PassiveFishSpeciesCatalog` picks a weighted species.
- `PassiveFishPlacement` resolves water columns and school-member positions.
- `PassiveFishFactory` builds the billboard, collider, loot component, and
  behaviour.
- `UnderwaterPassiveFishSpawner` owns pulse budgeting and live-object cleanup.

Spawn rules:

- `PassiveFishFrequency` scales the target count.
- `FishParadise` multiplies fish spawn counts, raises the live-fish cap, and
  shortens the successful pulse interval.
- Fish use the same global spawn-rate scale as enemies.
- If the player is near water but not in or above deep water, the fish roll is
  halved.
- Spawn positions are 35m to 55m from the player by default.
- With clear water or long-range fog settings, spawn positions can expand up to
  90m to 180m from the player.
- Candidates inside the immediate camera view are rejected only near the player,
  so clear-water setups can show distant populated water without close pop-in.
- Candidates must be in loaded ocean with at least 4m depth.
- Surface spawns are placed below the waterline, not on it.
- School members are spaced apart during spawn so larger schools do not collapse
  into a single stacked billboard.
- A max live fish cap prevents unbounded growth.
- Far fish are despawned during later pulses.
- Caught fish are custom `UselessItems2` items, which lets DFU's existing
  general-store and pawn-shop sell filters accept them without patching trade UI.

Species are configured in one table in `PassiveFishSpeciesCatalog`:

```csharp
new PassiveFishSpecies(
    TemplateIndex,
    "Display Name",
    TextureRecord,
    SpawnWeight,
    BillboardHeight,
    GenerateTextureAssetNames("asset_base_name", TextureRecord),
    CruiseSpeedMultiplier,
    FleeSpeedMultiplier,
    MinSchoolSize,
    MaxSchoolSize,
    minHeightMultiplier: 0.85f,
    maxHeightMultiplier: 1.15f,
    fleeDartHoldMin: 1.2f,
    fleeDartHoldMax: 2.4f)
```

`SpawnWeight` is relative. A weight of `1` appears one tenth as often as a
weight of `10`, assuming both textures load.

`BillboardHeight` is used as the billboard's world-space height. Width is then
computed from the texture aspect ratio, so the fish scales uniformly rather than
stretching to a fixed width. The height multiplier range is rolled once per
spawned fish, then multiplied into `BillboardHeight`, so fish of the same
species can vary slightly without changing their source art.

`MinSchoolSize` and `MaxSchoolSize` control world spawns, not inventory stacks.
One spawn roll picks a species and an off-screen anchor, then creates separate
fish GameObjects near that point. Each fish remains a single pickup item. School
members follow a shared moving center and steer along the same cruise direction
while calm; when the player gets close to any member, the school center is
pushed away and every member can scatter from that shared disruption state. Once
pressure fades, the center resumes its deliberate cruise and escaped fish
naturally rejoin it.

`fleeDartHoldMin` and `fleeDartHoldMax` control how long a fleeing fish keeps
each sudden zig-zag direction before choosing another.

Fish movement:

- solo wander direction changes every few seconds;
- schools have their own moving center and change shared direction every 2-4 seconds;
- cruise motion uses gentle turn smoothing;
- when the player approaches, the whole school is briefly disrupted;
- fleeing includes large randomized dart angles that hold for a short duration;
- motion is clamped between seafloor and surface clearance;
- if a fish leaves a valid water column, it returns to its last safe position.

Each fish caches its current water column for a short interval. That keeps large
schools from resolving terrain every frame while still correcting movement back
into legal water quickly.

Pickup:

1. A fish GameObject gets a `DaggerfallLoot` component.
2. A custom `DaggerfallUnityItem` is created from its template index.
3. The item is added to the loot container.
4. Clicking opens the normal DFU item pickup UI.
5. If the item template is missing or mismatched, the fish is not spawned.

That last check prevents a bad template index from silently becoming an
unrelated blank item.

## Adding Passive Fish

To add a fish:

1. Add a template to `Assets/ItemTemplates.json`.
2. Add both the friendly PNG and archive-style PNG to `Flats/`.
3. Add both PNG paths to `deep-waters.dfmod.json`.
4. Add a new template index constant to `PassiveFishSpeciesCatalog`.
5. Add the index to `CustomItemTemplateIndices`.
6. Add a `PassiveFishSpecies` entry to the `All` table.

The archive-style filename must match the item template texture record:

```text
216_<record>-0.png
```

For example, texture record `48` needs `216_48-0.png`.

`Tools/prepare_fish_icons.py` can rebuild the current archive-style fish icon
PNGs from the friendly source names. It crops transparent padding, preserves the
pixel-art aspect ratio, scales with nearest-neighbor sampling, and centers each
fish on a 128x128 transparent canvas. That size is deliberately larger than
DFU's normal item icon slots so replacements stay crisp when the UI scales them
down.

## Performance Notes

The mod avoids the worst-case "fill the ocean" approach.

- Terrain lowering happens in Burst jobs as part of DFU terrain generation.
- Seam reconciliation only touches promoted neighbor edges.
- Water surfaces share one mesh and one material.
- Decorations are one billboard batch per tile.
- Treasure rubble is batched per parent terrain.
- Loot and fish use pulses near the player rather than full-tile population.
- Recent loot spawn cells reduce repeated clumping and unnecessary retries.
- Loot's nearby-water gate is cached briefly rather than probing terrain every frame.
- Shared water-column terrain lookups are cached by map pixel.
- Fish water-column clamps refresh on a short timer instead of every frame.
- Fish texture loading and spawnable-species weights are cached once.
