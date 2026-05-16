# Iliac Puddle No More - Tutorial

This tutorial walks through the mod in the same order the game uses it. It is
written for someone comfortable with C# who wants to understand how the pieces
fit together and how to extend the mod without making it harder to maintain.

## Lesson 1: Start With The Manifest

Open `deep-waters.dfmod.json`.

The manifest tells DFU which files are packaged. For this mod, notice three
groups:

- scripts in `Scripts/`;
- fish item data in `Assets/ItemTemplates.json`;
- fish and water visual assets in `Flats/` and `Shaders/`.

When you add a new fish texture or script file, it must be listed here or it
will not ship in the mod bundle.

## Lesson 2: Read Settings As Runtime Policy

Open `modsettings.json`, then `Scripts/DeepWaters.cs` and
`Scripts/DeepWaters.Settings.cs`.

`modsettings.json` defines user-facing settings. `DeepWaters.LoadSettings()`
reads those values into properties on the singleton.

The important pattern is:

```csharp
EnemyFrequency = GetScaledSliderSetting(s, "EnemyFrequency", EnemyFrequencyAtMidpoint);
```

The rate sliders use `0.5` as the normal/default point. `DeepWaters` scales
that midpoint back to the tuned internal values, so enemies at `0.5` become
`0.3`, fish become `0.6`, decorations become `1.0`, stray loot becomes `0.7`,
and treasure clusters become `0.1`. Moving a slider to `1.0` is roughly double
that default behavior.

The visual sliders use the same midpoint idea. The two water transparency
sliders keep the current look at `0.5`, become more opaque below that point,
and become clearer above it. The underside midpoint is intentionally clearer
than the top surface, and its tint follows the fog-strength slider, so the
water does not become a solid ceiling when viewed from below.
`UnderwaterFogStrength` controls the outdoor underwater density cap, while
`UnderwaterFogDistance` keeps the current falloff at `0.5`, shortens the range
below that point, and extends it above that point.

Those visual sliders also influence moving encounter range. Default-looking
water keeps fish and enemy spawns in the original 35m to 55m ring. Clearer
surface water, thinner fog, or longer fog distance can expand that ring toward
90m to 180m while still rejecting close on-screen pop-in.

The singleton also owns:

- `DeepWaters.Mod`, used to load bundled assets;
- `DeepWaters.Instance`, used by all systems to read settings.

## Lesson 3: Follow Startup Order

In `DeepWaters.Init()`:

1. Create the persistent host GameObject.
2. Load settings.
3. Wrap terrain texturing.
4. Register fish item templates.
5. Install subsystems.
6. Mark the mod ready.

The order matters. Terrain texturing and the seafloor builder must install
before terrain tiles are generated. Per-frame components can install later
because they only act once play is active.

Subsystems install even when their feature toggle is off. Each subsystem gates
its own behavior, which keeps startup simple and lets disabled features remove
stale terrain children if DFU reuses an object.

## Lesson 4: Use Shared World Queries

Open `Scripts/DeepWaterWorld.cs` and `Scripts/DeepWaterTerrainLookup.cs`.

This file is the small helper that keeps the rest of the mod simple. Most
systems eventually need the same question answered:

> At world X/Z, is there loaded ocean here, and where are the seafloor and
> surface?

The answer comes from:

```csharp
DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column)
```

The returned `DeepWaterColumn` contains the terrain objects, water height,
seafloor height, sample coordinates, parent transform, and depth.

When adding a new underwater feature, call this helper instead of copying tile
math from another spawner.

The terrain lookup helper also caches resolved terrain objects by map pixel. That matters
because fish and encounter systems can ask for water columns many times per
second. The cache is still safe for DFU's terrain reuse because each cached
terrain object is validated against the requested map pixel before use.

This file also owns the shared transient reset event. Save loads and
`StreamingWorld` teleports rebuild terrain, so terrain-parented fish, enemies,
and loot must clear themselves instead of being carried into the next place.

## Lesson 5: Carve The Seafloor

Open `Scripts/DeepBathymetry.cs`, `Scripts/DeepWaterTileData.cs`,
`Scripts/DeepWaterFloorBuilder.cs`, and `Scripts/DeepWaterFloorMesh.cs`.

The mod does not modify the vanilla heightmap. Vanilla DFU clamps every water
sample to ocean elevation, leaving a flat puddle — and that's exactly what
stays in the heightmap. Real depth comes from a procedural sub-mesh layered
beneath holes carved through the vanilla terrain.

The flow:

1. `DeepWaterFloorBuilder` listens to `DaggerfallTerrain.OnPromoteTerrainData`.
2. For each tile, it attaches a `DeepWaterTileData` cache. The cache resolves
   the climate index and computes a distance-to-coast field over the heightmap
   via a two-pass chamfer-distance approximation.
3. The cache also runs an "ocean-connectivity" heuristic: at least one
   heightmap edge must have a contiguous water run of >= 25% of its length.
   Inland lakes and narrow rivers fail this check and keep vanilla flat water.
4. For tiles that pass, the builder computes a hole mask at the heightmap
   resolution. A cell is holed when (a) all four corner heightmap samples are
   at ocean level, AND (b) the cell center is at least `HoleBufferMeters = 12m`
   from the nearest land sample. The buffer keeps marching-squares shore-
   transition tiles intact so the beach gradient still renders.
5. `terrainData.SetHoles` removes vanilla terrain mesh and collision from the
   holed cells.
6. A `DeepWaterFloorMesh` child component generates a 65x65 vertex grid over
   the tile, sampling `DeepBathymetry.SampleDepthMeters` at each vertex's
   world coords. Vertex color packs depth/climate/coast-distance for the
   shader to blend shallow-sand to deep-rock.

`DeepBathymetry` is the heart of the depth shape. It's a pure function:

```csharp
DeepBathymetry.SampleDepthMeters(worldX, worldZ, climateIndex, distanceToCoast)
```

Layers compose: climate base depth, coast-distance shelf ramp, macro Perlin
(bay-scale variation), mid Perlin (ridges), high Perlin (roughness), and a
thresholded trench mask. The whole output is scaled by the user's `WaterDepth`
setting as a fraction of `MaxAbsoluteDepth = 250m`.

Because the function is purely `f(worldX, worldZ, climate, distance)` and the
mesh samples it at world coords (not tile-local fractions), adjacent tiles'
sub-meshes automatically agree at shared edges. **No boundary reconciliation
needed** — the old `BoundaryReconciler.cs` is gone.

## Lesson 6: Draw The Water Surface

Open `Scripts/WaterSurfaceManager.cs`, `Scripts/WaterSurfaceResources.cs`, and
`Shaders/StenciledWaterSurface.shader`.

The seafloor sub-mesh provides the depth, but it does not draw water. The
surface manager creates a child mesh on any terrain tile that contains water.

Each tile gets a `DeepWaters_Surface` child, but every child shares resources
owned by `WaterSurfaceResources`:

- one flat quad mesh;
- one water material;
- one custom shader.

Each child also has a thin trigger collider plus a `DeepWatersWaterSurface`
marker. The trigger does not block the player; it gives compatibility mods a
plain Unity collider they can raycast or query to locate the raised waterline.

The shader has a top pass and a bottom pass. The top pass darkens by vertical
water-column depth and by the view-ray distance through water, so looking down
from above preserves shallow water while deep or oblique views fade toward the
underwater fog color. The bottom pass lets
underwater views see the water plane from below without turning the whole
horizon opaque.

The top pass depends on Unity's camera depth texture. `UnderwaterDistanceFog`
adds a tiny helper to DFU's main camera that requests that texture during
`OnPreCull` whenever the player is outdoors, so the water can still darken by
depth when viewed from shore or from a boat. If Unity does not provide useful
depth for that transparent surface pass, the shader falls back to the mod's
configured water depth and the view angle through the water column.

The material is shared so compatibility mods can change stencil settings once.
`WaterSurfaceManager` also applies the user-facing top and underside
transparency settings and the underside fog tint to that one shared material.

The underside shader avoids distance-fogging the water plane itself. That keeps
the raised surface from forming circular fog bands when viewed from below; the
terrain, fish, and other objects beyond it still receive DFU's normal fog and
the mod's camera-space underwater visibility pass.

## Lesson 7: Make Outdoor Swimming Work

Open `Scripts/OutdoorSwimDriver.cs` and `Scripts/OutdoorSwimDfuBridge.cs`.

DFU knows how to swim indoors. The driver temporarily makes outdoor water look
like indoor water to the parts of DFU that handle swimming.

The reflection-heavy DFU field access lives in `OutdoorSwimDfuBridge`, while
`OutdoorSwimDriver` owns the actual swim and presentation decisions.

Two execution orders make this possible:

- early component: forge the needed DFU private fields;
- late component: restore the state after DFU has done its update.

The same driver also refreshes DFU's water state in `FixedUpdate()` so DFU's
own audio and submerged-state systems see the outdoor water consistently.

Open `Scripts/OutdoorShoreExitAssist.cs`, then `Scripts/SwimmingSfxBridge.cs`.

The shore assist is separated from the reflection-heavy swim driver. It only
handles the forward raycast used to climb onto terrain or static geometry, and
it explicitly ignores passive fish and enemies near the surface.

This small bridge plays DFU's own `SplashSmall` clip through the player's
`DaggerfallAudioSource` after every 2.5 world units of swimming movement. It
does not replace swimming physics, fog, breath, or movement; it just restores an
audible swim cadence in dungeon and outdoor water.

Open `Scripts/UnderwaterWeatherSuppressor.cs`. DFU already stores the
active weather on `PlayerWeather`, including the rain and snow particle objects.
The suppressor does not change the weather. It only hides those particles while
the player is swimming outdoors, then re-applies the current `WeatherType` when
swimming ends.

The driver separates two concepts:

- swim physics: can the player move like they are swimming?
- underwater presentation: should fog, camera clear, and audio effects be on?

That split is why the player can swim with their head above water.

The shore assist uses a raycast in front of the player. It only accepts terrain
or static geometry, and it ignores passive fish and enemies.

## Lesson 8: Add Underwater Presentation

Open `Scripts/UnderwaterAmbientMuter.cs`, `Scripts/UnderwaterDistanceFog.cs`,
`Scripts/UnderwaterWaveShadowFix.cs`, and `Shaders/UnderwaterDistanceFog.shader`.

The audio muter asks `OutdoorSwimDriver.IsPresentationUnderwater(oceanY)`. If
true, it adds an `AudioLowPassFilter` to the main audio listener. If false, it
disables the filter.

This is intentionally linked to presentation state, not raw player position.
That keeps audio, fog, and camera behavior in agreement.

The distance fog component attaches a small image effect to DFU's main camera.
It only runs while the camera is underwater. DFU lighting and normal fog stay in
charge of rendered geometry. This pass only handles sky/no-depth pixels.
Upward rays stop at the water surface, so the effect does not darken the sky
above the water. Sky/no-depth pixels near the horizon become full underwater
fog, which hides the places where distant terrain is no longer rendered.

The no-depth safety pass has its own density floor. That keeps low cosmetic fog
settings from exposing skybox at the missing-terrain horizon while preserving
the player's chosen fog strength on normal rendered geometry.

The wave-shadow fix is a compatibility shim for surface wave mods such as Come
Sail Away. DFU's exterior `IndirectLight` follows the player as an unshadowed
point light, and the outdoor swim bridge can briefly make `EnablePlayerTorch`
think the player is in a dungeon while it borrows DFU's swimming logic.
Underwater, those local lights can erase contrast in a perfect circle around the
camera, so the shim suppresses both while underwater presentation is active. It
also disables real shadow casting on Come Sail Away-style wave meshes while
submerged. Their visible wave meshes remain, but their shadows are left off
underwater because Unity's shadow culling produced circular holes and floating
artifacts from below.

Open `Scripts/PlayerShipWaterlineFix.cs`.

Owned ships are exterior `HomeYourShips` locations. DFU places those locations
at terrain height, which deep water lowers to the seafloor. The fix listens for
location creation/update events and anchors only the owned ship location root to
the ocean waterline.

Open `Scripts/ArgonianWaterBreathing.cs`.

This file is tiny but instructive. DFU clears constant-effect flags each frame,
so the mod reapplies Argonian water breathing in `LateUpdate()`.

## Lesson 9: Spawn Decorations

Open `Scripts/UnderwaterDecorations.cs`,
`Scripts/UnderwaterDecorationCatalog.cs`, and
`Scripts/UnderwaterDecorationReplacementCache.cs`.
Then open `Scripts/UnderwaterDecorationPlacement.cs` and
`Scripts/UnderwaterDecorationBatchFactory.cs`.

Decorations are terrain content, not moment-to-moment encounters. The system
therefore runs from terrain events:

1. Terrain promotion or nearby tile movement enqueues a tile.
2. A worker processes the queue after terrain updates.
3. `UnderwaterDecorationPlacement` samples the tile on a stride.
4. Valid seafloor positions become billboard batch entries.
5. `UnderwaterDecorationBatchFactory` creates archive or replacement-aware
   batches for the whole tile.

Each terrain object gets a tiny marker recording which map pixel its decoration
state belongs to. If DFU reuses that terrain object for another map pixel, the
old decoration batch is removed before new decorations are considered.

This is much cheaper than creating one GameObject per plant.

If texture replacement is enabled, the replacement cache probes each decoration
record once, stores the replacement material and dimensions, then lets the
decoration spawner keep batching by record. That is the compatibility path for
DREAM-style replacements without giving up batching.

To tune it:

- `DecorationFrequency` controls how many decoration passes a candidate tile rolls.
- `SampleStride` controls density inside a populated tile.
- `WeightedRecords` controls visual variety.

## Lesson 10: Spawn Enemies

Open `Scripts/UnderwaterEnemySpawner.cs`.

Enemies are encounters, so they spawn near the player instead of across entire
terrain tiles.

Fish and enemies now share `UnderwaterEncounterPulse`, so the shoreline,
surface, and submerged states cannot reroll one population independently of the
other. The enemy spawner is now just a participant in that shared pulse:

1. Check that enemy spawning is enabled.
2. Check that loaded ocean exists near the player, not necessarily under the
   player's exact feet.
3. Pulse immediately when entering enemy-spawnable ocean.
4. Pulse again after about 35m of travel.
5. Roll a count from `EnemyFrequency`.
6. Pick random X/Z positions in the current encounter ring.
7. Reject candidates visible in the immediate camera view near the player.
8. Resolve each position with `DeepWaterWorld.TryGetWaterColumn()`.
9. Require enough water depth.
10. Spawn a DFU enemy parented to that terrain.

Treasure clusters call the rare-enemy helper. That helper obeys
`SpawnUnderwaterEnemies`, but uses its own up-to-five guard curve so treasure
hordes feel meaningfully guarded even when normal ambient enemy frequency is
moderate.

## Lesson 11: Spawn Loot

Open `Scripts/UnderwaterLootSpawner.cs`,
`Scripts/UnderwaterLootPlacement.cs`, `Scripts/UnderwaterLootCatalog.cs`,
`Scripts/UnderwaterLootObjectFactory.cs`, and
`Scripts/UnderwaterTreasureClusterSpawner.cs`.

Loot is pulse-driven. A pulse can happen when loaded lootable ocean exists near
the player or when the player travels far enough from the last pulse anchor.

The gate allows:

- underwater swimming;
- being above deep ocean, so boat travel can reveal treasure below;
- walking near shore, as long as the spawned candidates are still in loaded
  ocean.

When the player is not in or directly above deep water, both loose loot and
treasure cluster rolls are reduced to one eighth of their normal chance. This
keeps shore walking possible without making beaches the best treasure farm.

Stray loot uses a spawn ring and recent-cell memory to avoid obvious repeats.
That placement state lives in `UnderwaterLootPlacement`, which also owns the
forward-biased angle pick, seafloor resolution, and cluster loot spacing.
Treasure clusters use the same center-picking logic, then
`UnderwaterTreasureClusterSpawner` adds rubble batches, several loot
containers, and optional rare guards through `UnderwaterLootObjectFactory`.

One wrinkle: treasure pile graphics and passive fish icons both use archive
`216`. Fish occupy records `42-48`, so `UnderwaterLootCatalog` excludes those
records from treasure pile visuals. Otherwise a treasure horde can accidentally
use fish art as a container sprite.

When performance is a concern, this pulse model is the key idea: do a little
work near the player, not a lot of work across the loaded ocean.

## Lesson 12: Spawn Passive Fish

Open `Scripts/PassiveFishSpeciesCatalog.cs`, `Scripts/PassiveFishResources.cs`,
`Scripts/PassiveFishPlacement.cs`, `Scripts/PassiveFishFactory.cs`, and
`Scripts/UnderwaterPassiveFishSpawner.cs`.

Fish are lightweight GameObjects with:

- `DaggerfallBillboard` for the sprite;
- `BoxCollider` for clicking;
- `DaggerfallLoot` for pickup;
- `PassiveFishBehaviour` for movement.

Caught fish are registered as custom `UselessItems2` items. That vanilla item
group is already accepted by general stores and pawn shops, so fish can be sold
without custom trade-window code.

The species catalog is the main place to add or tune fish. Each entry defines:

- custom item template index;
- display name;
- texture record;
- spawn weight;
- billboard height;
- possible texture asset names;
- cruise speed multiplier;
- flee speed multiplier;
- minimum and maximum school size;
- minimum and maximum height multiplier;
- minimum and maximum flee zig-zag hold time.

Relative spawn weights make rarity easy. If one fish has weight `10` and
another has weight `1`, the second appears about one tenth as often.

The runtime flow is deliberately layered:

- `PassiveFishSpeciesCatalog` owns the species table and weighted pick cache.
- `PassiveFishResources` loads textures and creates inventory items.
- `PassiveFishPlacement` chooses legal water positions and school offsets.
- `PassiveFishFactory` creates a lootable billboard GameObject.
- `UnderwaterPassiveFishSpawner` runs the pulse budget and tracks live fish.

`FishParadise` is intentionally blunt: it multiplies fish spawn counts, raises
the live fish cap, and lets successful pulses happen sooner. It lives beside
the normal fish frequency slider so "more fish" is easy to reason about.
When the player is near water but not in or directly above deep water, fish
rolls are halved instead of stopped. That keeps shore-to-water transitions from
rebuilding the fish population.

Fish use the same shared encounter ring as enemies. With the default water
visuals, that ring is 35m to 55m. With clearer water or longer fog distance,
the ring expands toward 90m to 180m so open water can already look alive before
the player reaches it.

Movement is also simple:

- solo fish pick a wander direction and keep it for several seconds;
- schools cruise in one shared direction for a few seconds at a time;
- turn smoothly;
- disrupt the whole school when the player gets close to any member;
- rejoin the moving school center when the chase pressure fades;
- while fleeing, add large dart angles that hold briefly;
- clamp movement between seabed and water surface.

The clamp uses a short per-fish water-column cache. That is an intentional
performance compromise: schools still correct themselves quickly, but they do
not ask DFU for terrain data every single frame.

## Lesson 13: Add A New Fish

To add a fish, do all of these:

1. Add a new item to `Assets/ItemTemplates.json`.
2. Choose a unique template index.
3. Choose a `worldTextureRecord` and `playerTextureRecord`.
4. Add a friendly PNG to `Flats/`, such as `blue_fish.png`.
5. Add an archive-style copy to `Flats/`, such as `216_49-0.png`.
6. Add both PNGs to `deep-waters.dfmod.json`.
7. Add a template index constant in `PassiveFishSpeciesCatalog`.
8. Add the index to `CustomItemTemplateIndices`.
9. Add a `PassiveFishSpecies` entry to the `All` table.

Example species entry:

```csharp
new PassiveFishSpecies(
    BlueFishTemplateIndex,
    "Blue Fish",
    49,
    5,
    0.7f,
    GenerateTextureAssetNames("blue_fish", 49),
    1.0f,
    1.1f,
    2,
    6,
    minHeightMultiplier: 0.85f,
    maxHeightMultiplier: 1.15f,
    fleeDartHoldMin: 1.2f,
    fleeDartHoldMax: 2.4f),
```

The two positional numbers after the speed multipliers are the schooling range.
They create separate fish in the world near the same off-screen spawn anchor;
they do not create item stacks. Use `1, 1` for a solo fish.

School members share a small school state object. That object cruises in one
random direction for a few seconds at a time, and calm members steer along that
shared direction with only enough cohesion to stay together. When one member is
chased, the shared center moves away from the player for a short period, and all
members can scatter before falling back toward the moving center.

The named height multipliers are rolled once for each spawned fish and multiply
`BillboardHeight`. The named flee dart values control how long a fleeing fish
commits to each sudden zig-zag direction before picking another.

If you have a friendly source PNG and need the archive-style icon filename,
run `Tools/prepare_fish_icons.py`. The current script has the fish filenames
built in and writes `216_<record>-0.png` outputs into `Flats/`.

If a picked-up fish appears as the wrong item, check these first:

- the item template `index`;
- the template index constant in `PassiveFishSpeciesCatalog`;
- the `PassiveFishSpecies` template index;
- the `worldTextureRecord` and `playerTextureRecord`;
- the `216_<record>-0.png` filename;
- the manifest entries.

## Lesson 14: Keep The Code KISS

When adding a feature, ask these questions:

1. Can I use `DeepWaterWorld.TryGetWaterColumn()` instead of new terrain math?
2. Is this terrain content, encounter content, or presentation logic?
3. Should it run from terrain events, player movement pulses, or every frame?
4. Can it be batched instead of one GameObject per visual?
5. Does this need a new abstraction, or can a small helper keep it clear?

The current mod stays readable by keeping those boundaries intact.
