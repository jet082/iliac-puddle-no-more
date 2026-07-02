# Distance bake

The mod ships with a pre-computed global distance-to-coast field
(`Resources/DistanceBake.bytes`). Every seafloor depth query at runtime
is a lookup into this file. Adjacent tiles automatically agree at their
shared boundary because they sample the same global data — no per-tile
BFS, no cross-tile reconciliation, no streaming cascade.

Build the file in the Unity editor:

1. Open the project. Ensure a scene with DaggerfallUnity is loaded (so
   the `WoodsFile` reader is initialized).
2. Run **Tools > Deep Waters > Bake Distance Field**.
3. The progress bar runs through classification, ocean connectivity,
   shore-edge distance, quantize, and write. The default 64x64 fine mask
   is intentionally heavier than the diagnostic bake.
4. The result is written to
   `Assets/Game/Mods/deep-waters/Resources/DistanceBake.bytes` and bundled
   into the next `.dfmod` build.

Re-run whenever a terrain mod changes the world height buffer. For this
setup, run the bake with `wod-terrain` loaded so its startup code has
already installed the altered `WOODS.WLD` buffer. The baker deliberately
uses that altered height buffer as its cheap base classifier (`<= 6`),
then runs a CPU port of WOD's height path on marked shoreline and
water-adjacent-location tiles. This hybrid path captures WOD's location
and port shoreline shaping without driving
`Monobelisk.InterestingTerrainSampler` across the whole world, which
would dispatch compute shaders for every map pixel and can crash the
Unity editor.

The exact-export route remains possible as a separate tool: have WOD
write its `tileDataCache` WATER tilemap for each map pixel to a compact
file during normal, paced tile generation, then build Deep Waters' masks
from that exported tilemap instead of reproducing WOD's shader on CPU.
That would be the source-of-truth bake, but it needs a WOD-side exporter
or a throttled editor job that never runs the GPU sampler as a tight
500k-tile loop.

An experimental first step lives at **Tools > Deep Waters > Diagnostics >
WOD Exact Tilemap Mask Exporter**. It drives WOD's GPU sampler one tile
at a time across editor updates, aggregates exact WATER tilemap cells
into packed coarse/fine masks, and writes
`Assets/Game/Mods/deep-waters/Diagnostics/WodExactWaterMasks.bytes`.
It defaults to exporting one map row as a smoke test; set "Rows to
export" to `0` for the full world. Then run **Tools > Deep Waters >
Bake Distance Field from WOD Exact Masks** to turn that source mask into
`Resources/DistanceBake.bytes`.

## Format

Header (18 bytes):

| Offset | Size | Value                                           |
|--------|------|-------------------------------------------------|
| 0      | 4    | magic `0x44574442` ("DWDB")                      |
| 4      | 2    | version (currently 5)                            |
| 6      | 2    | sub-cells per map pixel, X                       |
| 8      | 2    | sub-cells per map pixel, Y                       |
| 10     | 2    | map pixel width (= 1000)                         |
| 12     | 2    | map pixel height (= 500)                         |
| 14     | 2    | distance scale in meters per byte unit (= 16)    |
| 16     | 2    | fine water-mask sub-cells per pixel              |

Body:

- Distance field: `mapPixelsY * subCellsY × mapPixelsX * subCellsX`
  bytes, row-major. Each byte holds distance to nearest land in
  `distanceScale`-meter units.
- Coarse ocean-connected water mask: packed bits at the distance-field
  resolution.
- Fine carve mask: packed bits at `fineSubCellsPerPixel` resolution.
  This mask is not pruned; it should match the live/GPU water mask so
  carving and shore-edge depth agree for ocean water and local lakes.

Current defaults are 8x8 distance cells per map pixel and 64x64 fine
carve cells per map pixel. The resulting file is roughly 324 MB. Use
**Tools > Deep Waters > Bake Distance Field (Diagnostic 32x32 Fine Mask)**
for the older ~132 MB diagnostic bake.

## Tuning

`SubCellsPerPixel` in `Editor/DistanceBakeBuilder.cs` trades distance
field file size for depth-gradient detail:

| Value | Cell width | Total cells   | File size |
|-------|------------|---------------|-----------|
| 2     | ~410 m     | 2 000 000     | 2 MB      |
| 4     | ~205 m     | 8 000 000     | 8 MB      |
| 8     | ~102 m     | 32 000 000    | 32 MB     |

Bilinear interpolation at the runtime sampler smooths the lookups, so
finer resolution is rarely worth the size hit. Bump it if you see
visible blockiness in coastal slopes.

`SubCellsPerPixelFine` controls shoreline carving detail. It is packed
while baking, so 64x64 stores a ~256 MB bitmask instead of holding a
multi-gigabyte `bool[]` in editor memory. Raise this only if the carved
terrain still visibly stops short of the waterline after a fresh bake;
128x128 would push the fine mask alone to roughly 1 GB.

`DistanceScaleMeters` is the byte → metres multiplier. 16 m/unit lets
255 cover ~4 km, comfortably past the shelf-ramp while leaving room to
lengthen it without rebaking.
