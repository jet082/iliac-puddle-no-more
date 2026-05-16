// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Forces adjacent terrain tiles to agree on shared edge heights after
    /// promotion. If either tile has land just inside the seam, the edge stays
    /// at ocean level to preserve a coast wall; otherwise both sides use the
    /// same deterministic deep-water floor height.
    /// </summary>
    public static class BoundaryReconciler
    {
        private static bool installed;

        public static void Install()
        {
            if (installed)
                return;

            DaggerfallTerrain.OnPromoteTerrainData += OnPromoted;
            installed = true;
        }

        public static void Uninstall()
        {
            if (!installed)
                return;

            DaggerfallTerrain.OnPromoteTerrainData -= OnPromoted;
            installed = false;
        }

        private static void OnPromoted(DaggerfallTerrain promoted, TerrainData _)
        {
            if (DeepWaters.Instance == null || DeepWaters.Instance.WaterDepth <= 0f) return;

            var sw = GameManager.Instance?.StreamingWorld;
            if (sw == null) return;

            int x = promoted.MapPixelX;
            int y = promoted.MapPixelY;

            // For each direction, get the neighbour and reconcile.
            // Direction names use DFU convention:
            //   Left  = (X-1, Y) — west / lower X
            //   Right = (X+1, Y) — east / higher X
            //   Top   = (X, Y-1) — north / lower mapPixelY = higher world Z
            //   Bottom= (X, Y+1) — south / higher mapPixelY = lower world Z
            //
            // Heightmap row 0 is the BOTTOM edge (south); row hDim-1 is
            // the TOP edge (north). Heightmap col 0 is LEFT (west);
            // col hDim-1 is RIGHT (east).
            ReconcilePair(promoted, GetDfTerrain(sw, x - 1, y), Side.Left);
            ReconcilePair(promoted, GetDfTerrain(sw, x + 1, y), Side.Right);
            ReconcilePair(promoted, GetDfTerrain(sw, x,     y - 1), Side.Top);
            ReconcilePair(promoted, GetDfTerrain(sw, x,     y + 1), Side.Bottom);
        }

        private static DaggerfallTerrain GetDfTerrain(StreamingWorld sw, int mx, int my)
        {
            var go = sw.GetTerrainFromPixel(mx, my);
            if (go == null) return null;
            return go.GetComponent<DaggerfallTerrain>();
        }

        private enum Side { Left, Right, Top, Bottom }

        private static void ReconcilePair(DaggerfallTerrain me, DaggerfallTerrain neighbour, Side meSide)
        {
            if (neighbour == null) return;
            if (me == null) return;
            var meHeights = me.MapData.heightmapSamples;
            var nHeights  = neighbour.MapData.heightmapSamples;
            if (meHeights == null || nHeights == null) return;
            int hDim = meHeights.GetLength(0);
            if (nHeights.GetLength(0) != hDim || nHeights.GetLength(1) != hDim) return;

            var sampler = DaggerfallUnity.Instance?.TerrainSampler;
            if (sampler == null) return;
            float oceanThr = sampler.OceanElevation / sampler.MaxTerrainHeight;
            float depthNorm = DeepWaters.Instance.WaterDepth / sampler.MaxTerrainHeight;

            bool meChanged = false, nChanged = false;

            for (int i = 0; i < hDim; i++)
            {
                // Resolve indices into both heightmaps depending on
                // which side of `me` connects to the neighbour.
                int meEdgeRow, meEdgeCol, meInsideRow, meInsideCol;
                int nEdgeRow,  nEdgeCol,  nInsideRow,  nInsideCol;
                int sharedWorldHx, sharedWorldHy;

                switch (meSide)
                {
                    case Side.Left:
                        // me's left edge (col 0) <-> neighbour's right edge (col hDim-1)
                        meEdgeRow = i; meEdgeCol = 0;
                        meInsideRow = i; meInsideCol = 1;
                        nEdgeRow = i; nEdgeCol = hDim - 1;
                        nInsideRow = i; nInsideCol = hDim - 2;
                        sharedWorldHx = me.MapPixelX * (hDim - 1) + 0;
                        sharedWorldHy = (500 - me.MapPixelY) * (hDim - 1) + i;
                        break;
                    case Side.Right:
                        meEdgeRow = i; meEdgeCol = hDim - 1;
                        meInsideRow = i; meInsideCol = hDim - 2;
                        nEdgeRow = i; nEdgeCol = 0;
                        nInsideRow = i; nInsideCol = 1;
                        sharedWorldHx = me.MapPixelX * (hDim - 1) + (hDim - 1);
                        sharedWorldHy = (500 - me.MapPixelY) * (hDim - 1) + i;
                        break;
                    case Side.Top:
                        // me's top edge (row hDim-1) <-> neighbour's bottom edge (row 0)
                        meEdgeRow = hDim - 1; meEdgeCol = i;
                        meInsideRow = hDim - 2; meInsideCol = i;
                        nEdgeRow = 0; nEdgeCol = i;
                        nInsideRow = 1; nInsideCol = i;
                        sharedWorldHx = me.MapPixelX * (hDim - 1) + i;
                        sharedWorldHy = (500 - me.MapPixelY) * (hDim - 1) + (hDim - 1);
                        break;
                    case Side.Bottom:
                        meEdgeRow = 0; meEdgeCol = i;
                        meInsideRow = 1; meInsideCol = i;
                        nEdgeRow = hDim - 1; nEdgeCol = i;
                        nInsideRow = hDim - 2; nInsideCol = i;
                        sharedWorldHx = me.MapPixelX * (hDim - 1) + i;
                        sharedWorldHy = (500 - me.MapPixelY) * (hDim - 1) + 0;
                        break;
                    default:
                        return;
                }

                float meEdge = meHeights[meEdgeRow, meEdgeCol];
                float nEdge  = nHeights [nEdgeRow,  nEdgeCol];
                float meInside = meHeights[meInsideRow, meInsideCol];
                float nInside  = nHeights [nInsideRow,  nInsideCol];

                // If the shared edge sample is itself land (above
                // ocean threshold), leave it alone. Both tiles should
                // agree it's land (same world coord, same DFU sampler),
                // so no reconciliation needed and writing oceanThr
                // would incorrectly LOWER an actual land sample (e.g.
                // a peninsula extending across a tile boundary).
                if (meEdge > oceanThr || nEdge > oceanThr)
                    continue;

                // RECONCILIATION RULE
                float correctValue;
                if (meInside > oceanThr || nInside > oceanThr)
                {
                    // Either side has land just inside the seam.
                    // Preserve the shared edge at ocean level so
                    // Unity's terrain mesh forms a vertical wall
                    // between the land and the deeper water in
                    // either direction.
                    correctValue = oceanThr;
                }
                else
                {
                    // Both interiors are water. Use the deterministic
                    // lowering function so both tiles agree on the
                    // shared edge sample.
                    correctValue = DeepWaterFloorHeight.ComputeLoweredHeight(
                        sharedWorldHx, sharedWorldHy, oceanThr, depthNorm);
                }

                if (Mathf.Abs(meEdge - correctValue) > 1e-6f)
                {
                    meHeights[meEdgeRow, meEdgeCol] = correctValue;
                    meChanged = true;
                }
                if (Mathf.Abs(nEdge - correctValue) > 1e-6f)
                {
                    nHeights[nEdgeRow, nEdgeCol] = correctValue;
                    nChanged = true;
                }
            }

            if (meChanged) ApplyToTerrain(me);
            if (nChanged) ApplyToTerrain(neighbour);
        }

        private static void ApplyToTerrain(DaggerfallTerrain dfTerrain)
        {
            var t = dfTerrain.GetComponent<Terrain>();
            if (t == null || t.terrainData == null) return;
            // SetHeights regenerates the terrain mesh. Boundary corners
            // shared by 4 tiles need this called whenever any of them
            // updates so the mesh re-stitches.
            t.terrainData.SetHeights(0, 0, dfTerrain.MapData.heightmapSamples);
        }
    }
}
