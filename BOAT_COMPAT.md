# Boat & Water-Aware Mod Compatibility

This mod (Iliac Puddle No More) renders a custom water surface mesh
on every ocean terrain tile. If you're writing a boat mod, submarine
mod, magical air-bubble mod, or anything else that needs the water
to NOT render in specific regions, this document is for you.

## TL;DR

The water surface uses a custom shader
(`DeepWaters/StenciledWaterSurface`) that supports a stencil reject
test. Set up your hull/bubble shader to write a specific stencil
value (default: 200) to the regions you want water-free. Our shader
will discard fragments where the stencil already holds that value.

## Detail

### Our shader's stencil setup

```hlsl
Stencil
{
    Ref       [_StencilRef]      // Default 200, configurable per-material
    ReadMask  [_StencilReadMask] // Default 255 (all bits)
    Comp      NotEqual           // Render only where stencil != Ref
    Pass      Keep
    Fail      Keep
    ZFail     Keep
}
```

`Pass = Keep` means we don't write the stencil ourselves, so we don't
interfere with anyone else.

### Your boat hull's stencil setup

Your hull mesh shader needs to render in the **Geometry queue**
(before our water, which renders at Geometry+10) and write 200 to
stencil for the regions inside the hull.

A minimal example pass for the hull:

```hlsl
Pass
{
    Tags { "Queue" = "Geometry" }

    // Write to stencil but don't write to color or depth — we just
    // want to mark the region. Other passes of your hull shader
    // can render the visible geometry separately.
    ColorMask 0
    ZWrite Off

    Stencil
    {
        Ref 200
        Comp Always
        Pass Replace
    }

    // ... vertex/fragment shaders, render the hull volume ...
}
```

### Putting it together

1. Your boat is added to the scene. Its hull (or just an invisible
   hull-shaped masking volume) renders in the Geometry queue and
   writes 200 to the stencil buffer for fragments inside the hull.
2. Our water mesh renders in Geometry+10. For each fragment, it
   checks the stencil. If the stencil value is 200 (set by your
   hull), it discards the fragment. Otherwise, it draws water as
   normal.
3. Result: no water visible inside the hull. Player walks on the
   deck without seeing waves clipping through the floor.

### Coordinating the stencil value

If 200 conflicts with another mod's use of the stencil buffer, you
can change ours at runtime:

```csharp
var mat = WaterSurfaceManager.GetSharedWaterMaterial();
if (mat != null)
    mat.SetInt("_StencilRef", YOUR_PREFERRED_VALUE);
```

`GetSharedWaterMaterial()` returns the single material instance
shared across every water tile in the streaming radius. One
assignment propagates everywhere automatically. It returns null if
the mod is loaded but no water tiles have promoted yet (e.g., the
player hasn't streamed in any ocean terrain). Call it lazily — when
your first boat instance spawns, not at startup.

### Read mask

`_StencilReadMask` lets you reserve some stencil bits for your own
use. For example, if you only want to use the high 4 bits and let
the low 4 bits be free for other mods, set `_StencilReadMask` to
`0b11110000` = 240.

Most mods can leave this at 255 (the default) — checking all 8 bits.

### Why the Geometry queue?

Render queues control draw order. Lower numbers render first; higher
later. The standard breakdown:

- `Background` (1000) — skybox, far backgrounds
- `Geometry` (2000) — opaque world geometry
- `AlphaTest` (2450) — alpha-cutout opaque geometry (vegetation)
- `Transparent` (3000) — transparent geometry (glass, water)
- `Overlay` (4000) — UI

Originally the Daggerfall dungeon water shader used the Transparent
queue, which renders LAST. That meant boat hulls (in Geometry) would
be drawn first, then water on top — no chance for the hull's stencil
write to mask the water unless you carefully reordered.

Our shader sits at `Geometry+10` (queue 2010) — opaque-pass, after
typical world geometry. The hull's stencil write at queue 2000
happens first; our water reads it at queue 2010 and discards
correctly.

A consequence: the water is OPAQUE rather than transparent. You
won't see the seafloor through the surface from above. We accept
that trade-off because (a) classic Daggerfall water is opaque
anyway, (b) the gain in boat-mod compatibility is much greater than
the loss in semi-transparent fanciness, and (c) you can still see
the seafloor by looking at it directly through swimming.

## Material instance sharing

The water material is shared across every water tile. When you
change `_StencilRef`, the change applies to every water tile in the
world automatically. No need to walk all DaggerfallTerrains and
update each renderer.

Unity's editor inspector may display the material name with an
"(Instance)" suffix because we construct it via `new Material(shader)`
at runtime — that suffix is cosmetic, not an actual instancing
event. The renderers truly share one material.

## Other coordination points

The mod also exposes:

- `DeepWaters.WaterDepth` — float, configured maximum water depth in
  Daggerfall height units (~34m world max). Useful if your boat
  mod's draft / draught calculations depend on water column depth.
- `DaggerfallTerrain.OnPromoteTerrainData` — DFU's standard event,
  fires when a terrain tile finishes generation. Subscribe to this
  if you need to spawn boats on water tiles as they stream in.

## Questions / coordination

If you're writing a boat mod and need a coordination feature this
doc doesn't cover, raise an issue or PR on Iliac Puddle's
repository, or ping in the DFU Discord. Boat-mod compat is a high
priority — this mod's whole point is to make oceans interesting.
