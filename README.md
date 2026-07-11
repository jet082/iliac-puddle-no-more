# Deep Waters API

Deep Waters exposes stable, feature-neutral APIs for mods that need to classify ocean water, inspect a loaded water column, attach content to the visible surface, observe streamed surface or seafloor builds, edit the seafloor, or react to the player's outdoor swim state.

The complete API described here is available in **Iliac Puddle No More 1.2.x+**.

## Declaring the dependency

DFU matches dependency names against the lowercase `.dfmod` filename stem, not the display title.

```json
"Dependencies": [
	{
		"Name": "iliac puddle no more",
		"IsOptional": false,
		"IsPeer": false,
		"Version": "1.2.2"
	}
]
```

The mod GUID is `f1e8a1b3-8a4f-4f4e-bb6e-3d3a8a1b3f1e`.

## API overview

All public types are in the `DeepWaters` namespace.

| Entry point | Purpose |
| --- | --- |
| `DeepWaterDistanceBake` | Deterministic water classification and distance-to-boundary queries, including unloaded map pixels. |
| `DeepWaterWorld` | Logical ocean elevation and authoritative loaded water-column queries. |
| `WaterSurfaceManager` | Visible surface lifecycle, build validation, root/renderer/trigger access, and refresh requests. |
| `DeepWaterFloorBuilder` | Streamed seafloor lifecycle, mesh/collider access, raycasts, commits, and refresh requests. |
| `DeepWaterPlayer` | Read-only outdoor water/swim state, player-column queries, and swim-suppression integration. |
| `UnderwaterEnemySpawner` | Defensive snapshots of the complete underwater encounter roster. |

All Unity calls and event handlers must run on Unity's main thread.

## Underwater encounter roster

`UnderwaterEnemySpawner.GetEnemyRoster()` returns a new `MobileTypes[]` containing every enemy Deep Waters can place underwater through its normal, rare-guard, and boss pools. Callers can safely reorder or weight their copy; changing it does not affect Deep Waters.

## Coordinates and waterlines

Distance-bake methods accept:

- `mapPixelX`, `mapPixelY`: DFU map-pixel coordinates.
- `fracX`, `fracZ`: normalized coordinates inside that terrain tile, clamped to `[0, 1]`.
- `fracX = 0` is west and `fracX = 1` is east.
- `fracZ = 0` is south and `fracZ = 1` is north.

For a world position on a loaded terrain:

```csharp
float fracX = (worldX - terrain.transform.position.x) / terrainData.size.x;
float fracZ = (worldZ - terrain.transform.position.z) / terrainData.size.z;
```

`DeepWaterWorld.TryGetOceanSurfaceWorldY()` returns the logical ocean waterline. The visible surface root is rendered about 0.03 metres above that line to avoid z-fighting. Use the logical waterline for placement, physics, depth, and gameplay calculations; use the returned surface root when an object must visually follow the rendered plane.

## Water classification and distance

Always check `DeepWaterDistanceBake.IsLoaded` before querying the bake.

| Member | Meaning |
| --- | --- |
| `IsLoaded` | A compatible distance bake is ready. |
| `HasFineWaterMask` | Fine, bake-aligned carved-water data is available. |
| `IsWaterAt(x, y, fx, fz)` | The coarse bake classifies this position as water. |
| `IsCarvedWater(x, y, fx, fz)` | The fine mask says Deep Waters carves this position. Returns `false` when no fine mask exists. |
| `SampleDistanceMeters(...)` | Baked distance to coast in metres. |
| `SampleEdgeDistanceMeters(...)` | Distance to the nearest carved-water boundary; falls back to coast distance on older bakes. |
| `SampleLocalEdgeDistanceMeters(...)` | Distance to the local edge field, or `float.MaxValue` when unavailable. |
| `MapPixelHasWaterCells(x, y)` | The coarse mask contains water in this map pixel. |
| `MapPixelHasFineWaterCells(x, y)` | The fine mask contains any water in this map pixel. |
| `MapPixelHasLandCells(x, y)` | The coarse mask contains land in this map pixel. |
| `MapPixelOrCardinalNeighborHasWaterCells(x, y)` | This pixel or a cardinal neighbour contains coarse-mask water. |

Example: require open carved water at least 250 metres from its nearest boundary.

```csharp
bool IsOpenWater(int mapX, int mapY, float fracX, float fracZ)
{
	return DeepWaterDistanceBake.IsLoaded &&
		DeepWaterDistanceBake.IsCarvedWater(mapX, mapY, fracX, fracZ) &&
		DeepWaterDistanceBake.SampleEdgeDistanceMeters(mapX, mapY, fracX, fracZ) >= 250f;
}
```

## Loaded world queries

Use the bake for deterministic map-wide questions and `DeepWaterWorld` for live geometry.

```csharp
float oceanY;
if (DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY))
{
	// oceanY follows DFU terrain scale and floating-origin compensation.
}
```

The public query is strict and returns `false` when no game or streaming world is active; it never returns a cached waterline from a previous session.

`TryGetWaterColumn()` succeeds only on a loaded, positive-depth Deep Waters column. Unlike a raw bake-mask lookup, it rejects shore cells where the live carve produced no seafloor quad. It does not apply the player's minimum swim-depth policy, so shallow-water placement and surface effects can use it too.

```csharp
DaggerfallTerrain terrain;
float surfaceY;
float floorY;
float depth;
if (DeepWaterWorld.TryGetWaterColumn(
	worldX,
	worldZ,
	out terrain,
	out surfaceY,
	out floorY,
	out depth))
{
	// depth == surfaceY - floorY
}
```

When the streamed floor is built, `floorY` reflects its committed mesh, including edits made through the seafloor API. During the brief pre-build streaming window, Deep Waters can return its bathymetry fallback instead.

## Visible surface API

Deep Waters builds a surface root per streamed water terrain. The root owns separate top and underside renderers plus a broad-phase trigger.

### Surface lifecycle

`WaterSurfaceManager.OnSurfaceBuilt` is an `Action<DaggerfallTerrain>`. It is raised after the root, mesh, both renderers, material bindings, trigger, and build version are ready. Subscriber exceptions are isolated and logged.

The floor builder subscribes before the surface manager, so a tile's seafloor callbacks finish before `OnSurfaceBuilt` is raised.

```csharp
void OnEnable()
{
	WaterSurfaceManager.OnSurfaceBuilt += HandleSurfaceBuilt;
}

void OnDisable()
{
	WaterSurfaceManager.OnSurfaceBuilt -= HandleSurfaceBuilt;
}
```

DFU recycles terrain objects. Treat every callback as a potentially new map pixel, and pair any cached build version with `terrain.MapPixelX` and `terrain.MapPixelY`.

### Reading and attaching to a surface

```csharp
void HandleSurfaceBuilt(DaggerfallTerrain terrain)
{
	Transform root;
	Mesh mesh;
	MeshRenderer topRenderer;
	MeshRenderer undersideRenderer;
	Collider trigger;
	if (!WaterSurfaceManager.IsSurfaceCurrent(terrain) ||
		!WaterSurfaceManager.TryGetSurface(
			terrain,
			out root,
			out mesh,
			out topRenderer,
			out undersideRenderer,
			out trigger))
	{
		return;
	}

	int buildVersion = WaterSurfaceManager.GetSurfaceBuildVersion(terrain);
	GameObject effect = BuildMySurfaceEffect(terrain, buildVersion);
	effect.transform.SetParent(root, false);
}
```

`GetSurfaceBuildVersion()` returns `-1` when no generated root exists. `IsSurfaceCurrent()` validates the map pixel, source heightmap, mesh bindings, renderers, and trigger.

Important surface rules:

- Treat the returned `Mesh` as read-only. Animated full-water tiles can share one mesh globally.
- Treat the renderers' shared materials as read-only. Deep Waters owns and refreshes their shared properties.
- Add independent child objects under `root` for wakes, markers, particles, entrances, decals, or other effects.
- The returned trigger is a full-tile broad-phase trigger, not a water-clipped mesh collider. Validate its X/Z position with `TryGetWaterColumn()` before treating a hit as Deep Waters water.
- A forced rebuild can reuse the same root. Use map coordinates and build version to replace stale child content rather than assuming the old root was destroyed.

### Requesting surface rebuilds

```csharp
WaterSurfaceManager.RefreshLoadedSurface(terrain, force: true);
WaterSurfaceManager.RefreshLoadedSurfaces(force: true);
```

- Without `force`, a current surface is skipped.
- With `force`, Deep Waters rebuilds its generated mesh and raises `OnSurfaceBuilt` again.
- A request can be rejected while runtime terrain work is unsafe. Treat the event as confirmation rather than assuming the request completed synchronously.
- A tile has no surface when visible surfaces are disabled or when it contains no eligible water.

## Seafloor lifecycle

Deep Waters recycles terrain objects and can defer distant floor builds. Do not cache a `Mesh`, `MeshCollider`, or build version across terrain promotions without validating it again.

### `OnSeafloorBuilt`

Raised after a tile's base seafloor mesh and collider have been built, but before dependent Deep Waters content refreshes. This is the correct event for synchronous mesh deformation:

1. Get the current mesh with `TryGetSeafloor()`.
2. Edit it during the callback.
3. Call `CommitSeafloorChanges()` before returning.

### `OnFloorRefreshed`

Raised after seafloor construction and Deep Waters terrain integration are complete. Use this for observation, placement, or refreshing content that depends on final floor geometry.

Subscriber exceptions are isolated and logged.

```csharp
void OnEnable()
{
	DeepWaterFloorBuilder.OnSeafloorBuilt += HandleSeafloorBuilt;
	DeepWaterFloorBuilder.OnFloorRefreshed += HandleFloorRefreshed;
}

void OnDisable()
{
	DeepWaterFloorBuilder.OnSeafloorBuilt -= HandleSeafloorBuilt;
	DeepWaterFloorBuilder.OnFloorRefreshed -= HandleFloorRefreshed;
}
```

### Reading a streamed seafloor

```csharp
Mesh mesh;
MeshCollider collider;
if (DeepWaterFloorBuilder.IsSeafloorCurrent(terrain) &&
	DeepWaterFloorBuilder.TryGetSeafloor(terrain, out mesh, out collider))
{
	int buildVersion = DeepWaterFloorBuilder.GetSeafloorBuildVersion(terrain);
	// Use mesh/collider only while this terrain still owns buildVersion.
}
```

`GetSeafloorBuildVersion()` returns `-1` when no floor exists. Pair the value with the map-pixel coordinates because streamed terrain objects are reused.

### Raycasting

```csharp
RaycastHit hit;
Ray ray = new Ray(new Vector3(worldX, oceanY + 5f, worldZ), Vector3.down);
if (DeepWaterFloorBuilder.RaycastSeafloor(terrain, ray, 1000f, out hit))
	Debug.Log("Floor Y: " + hit.point.y);
```

The hit uses the committed runtime `MeshCollider`, so it reflects external mesh edits.

### Editing the seafloor safely

```csharp
void HandleSeafloorBuilt(DaggerfallTerrain terrain)
{
	Mesh mesh;
	MeshCollider collider;
	if (!DeepWaterFloorBuilder.TryGetSeafloor(terrain, out mesh, out collider))
		return;

	Vector3[] vertices = mesh.vertices;
	for (int i = 0; i < vertices.Length; i++)
		vertices[i].y += CalculateLocalOffset(terrain, vertices[i]);

	mesh.vertices = vertices;
	if (!DeepWaterFloorBuilder.CommitSeafloorChanges(terrain))
		Debug.LogWarning("Could not commit external seafloor changes.");
}
```

`CommitSeafloorChanges()` refreshes cached regular-grid heights, recalculates bounds, and recooks the runtime collider. Vertex deformation is the safest extension. Topology edits must leave the original regular-grid vertices first and in their original order so Deep Waters' interpolated-height sampling remains valid.

Do not apply the same deformation twice to one build. Record `GetSeafloorBuildVersion()` in a terrain-local marker and reapply only when that version changes.

### Requesting seafloor rebuilds

```csharp
DeepWaterFloorBuilder.RefreshLoadedTile(terrain, force: true);
DeepWaterFloorBuilder.RefreshLoadedTiles(force: true);
```

- Without `force`, an already-current floor is skipped.
- With `force`, Deep Waters rebuilds from source bathymetry and raises both lifecycle events again.
- A request can be deferred or rejected while terrain mutation is unsafe. Treat `OnSeafloorBuilt` as confirmation.
- A forced rebuild replaces external mesh edits, so reapply them from `OnSeafloorBuilt`.

## Player and swim state

`DeepWaterPlayer` publishes Deep Waters' exterior state. It deliberately does not expose DFU's forged dungeon fields, collider gate, thresholds, input overrides, or movement setters.

| Property | Meaning |
| --- | --- |
| `IsInWater` | Grace-smoothed water contact after the shore veto. This can be true before full swimming begins. |
| `IsSwimming` | Deep Waters is currently driving exterior swim movement. |
| `IsHeadSubmerged` | Deep Waters classifies the player's head as submerged; use this for breath or equipment rules. |
| `IsUnderwater` | Deep Waters' underwater presentation is active. This uses camera/head-aware hysteresis and is the right signal for view effects. |

These values are false in interiors, on recognised boats, while a suppression hook is active, during teardown/load gaps, and when no game is active. They do not describe native dungeon swimming.

### State changes

`OnStateChanged` is parameterless. Read all properties inside the handler to get one atomic snapshot. State is calculated in Deep Waters' early phase and the event is raised in its late phase after DFU's per-frame water state has been restored.

```csharp
void OnEnable()
{
	DeepWaterPlayer.OnStateChanged += HandleWaterStateChanged;
}

void OnDisable()
{
	DeepWaterPlayer.OnStateChanged -= HandleWaterStateChanged;
}

void HandleWaterStateChanged()
{
	breathMeter.enabled = DeepWaterPlayer.IsHeadSubmerged;
	underwaterOverlay.enabled = DeepWaterPlayer.IsUnderwater;
}
```

Subscriber exceptions are isolated and logged.

### Player column and depth

`TryGetWaterColumn()` uses the same centre-and-capsule probe as Deep Waters' swim contact logic. It returns `false` outside the current exterior context. It can succeed while the player is above the water, so combine it with the state property appropriate to your feature.

```csharp
DaggerfallTerrain terrain;
float surfaceY;
float floorY;
float columnDepth;
if (DeepWaterPlayer.TryGetWaterColumn(
	out terrain,
	out surfaceY,
	out floorY,
	out columnDepth))
{
	float cameraDepth = Mathf.Max(0f, surfaceY - GameManager.Instance.MainCamera.transform.position.y);
	float playerHeightAboveFloor = GameManager.Instance.PlayerObject.transform.position.y - floorY;
}
```

### Suppressing outdoor swimming

Boat, moving-platform, cutscene, or special movement mods can participate without modifying Deep Waters' state. If any `ShouldSuppressOutdoorSwimming` subscriber returns `true`, Deep Waters leaves its exterior swim state off for that frame.

```csharp
void OnEnable()
{
	DeepWaterPlayer.ShouldSuppressOutdoorSwimming += IsPlayerOnMyPlatform;
}

void OnDisable()
{
	DeepWaterPlayer.ShouldSuppressOutdoorSwimming -= IsPlayerOnMyPlatform;
}

bool IsPlayerOnMyPlatform()
{
	return currentPlatform != null && currentPlatform.ContainsPlayer;
}
```

The predicate runs once per rendered frame before Deep Waters makes collider-gate or swim decisions. Keep it fast, do not change Deep Waters state from inside it, and unsubscribe when your component is disabled. Subscriber exceptions are isolated; a failing callback cannot break swimming.

## Source mods and reflection

A DFU manifest dependency controls load order and version compatibility, but does not automatically give a source-compiled mod a CLR compile-time reference to another mod assembly. If your build does not reference the Deep Waters assembly directly, resolve its public types through the loaded mod:

```csharp
const string deepWatersGuid = "f1e8a1b3-8a4f-4f4e-bb6e-3d3a8a1b3f1e";

Mod dependency = ModManager.Instance.GetModFromGUID(deepWatersGuid);
Type bakeApi = dependency != null
	? dependency.GetCompiledType("DeepWaters.DeepWaterDistanceBake")
	: null;
Type worldApi = dependency != null
	? dependency.GetCompiledType("DeepWaters.DeepWaterWorld")
	: null;
Type surfaceApi = dependency != null
	? dependency.GetCompiledType("DeepWaters.WaterSurfaceManager")
	: null;
Type floorApi = dependency != null
	? dependency.GetCompiledType("DeepWaters.DeepWaterFloorBuilder")
	: null;
Type playerApi = dependency != null
	? dependency.GetCompiledType("DeepWaters.DeepWaterPlayer")
	: null;
Type enemyApi = dependency != null
	? dependency.GetCompiledType("DeepWaters.UnderwaterEnemySpawner")
	: null;
```

Public event signatures are:

- `WaterSurfaceManager.OnSurfaceBuilt`: `Action<DaggerfallTerrain>`
- `DeepWaterFloorBuilder.OnSeafloorBuilt`: `Action<DaggerfallTerrain>`
- `DeepWaterFloorBuilder.OnFloorRefreshed`: `Action<DaggerfallTerrain>`
- `DeepWaterPlayer.OnStateChanged`: `Action`
- `DeepWaterPlayer.ShouldSuppressOutdoorSwimming`: `Func<bool>`

Public API parameters use DFU, Unity, base-library, and primitive types so reflection integrations do not need to exchange Deep Waters implementation objects. Resolve members once during initialization; never perform reflection in per-frame hot paths.

## Compatibility rules

- Require `iliac puddle no more` 1.2.x or newer.
- Validate map coordinates, currentness, and build version before reusing streamed surface or floor state.
- Keep callbacks short; player-adjacent terrain builds and player-state transitions run on the main thread.
- Unsubscribe every event or predicate when your component is disabled or destroyed.
- Use the bake for unloaded deterministic placement, then validate live placement with `DeepWaterWorld.TryGetWaterColumn()` after the terrain streams in.
- Do not mutate returned surface meshes or shared materials. Add independent children under the surface root instead.
