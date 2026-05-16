// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Per-DaggerfallTerrain cache. Holds the climate index for the tile, an
    /// ocean-connectivity verdict, and a distance-to-coast field in meters
    /// computed from the heightmap. Other systems read this to call
    /// <see cref="DeepBathymetry.SampleDepthMeters"/> without recomputing
    /// distance on every query.
    /// </summary>
    public class DeepWaterTileData : MonoBehaviour
    {
        private const float StraightStep = 1f;
        private const float DiagonalStep = 1.41421356f;
        private const int MaxDistanceCells = 64;

        public int ClimateIndex { get; private set; }
        public bool IsOceanConnected { get; private set; }

        private float[,] distanceMeters;
        private int hDim0;
        private int hDim1;
        private float tileWorldSize;

        public bool HasDistanceField
        {
            get { return distanceMeters != null; }
        }

        public void Initialize(DaggerfallTerrain dfTerrain, int climateIndex)
        {
            ClimateIndex = climateIndex;

            float[,] heights = dfTerrain.MapData.heightmapSamples;
            if (heights == null)
            {
                distanceMeters = null;
                IsOceanConnected = false;
                return;
            }

            hDim0 = heights.GetLength(0);
            hDim1 = heights.GetLength(1);
            tileWorldSize = MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale;

            float cellWidthMeters = (hDim1 > 1) ? tileWorldSize / (hDim1 - 1) : tileWorldSize;

            // Cross-tile BFS seeding: for each cardinal neighbor we collect
            // (a) the heightmap edge that abuts us, and (b) the neighbor's
            // already-computed distance field edge if available. The distance
            // edge is preferred because it represents the neighbor's BFS
            // result at the shared boundary — using it lets adjacent tiles
            // compute IDENTICAL distance values at the seam, so their seafloor
            // meshes meet at the same depth and slopes flow continuously.
            // Heightmap edge is a fallback when the neighbor hasn't initialized
            // its distance field yet (very first tile to load, etc).
            NeighborEdges east = GetNeighborEdges(dfTerrain, NeighborSide.East);
            NeighborEdges west = GetNeighborEdges(dfTerrain, NeighborSide.West);
            NeighborEdges north = GetNeighborEdges(dfTerrain, NeighborSide.North);
            NeighborEdges south = GetNeighborEdges(dfTerrain, NeighborSide.South);

            distanceMeters = ComputeDistanceField(
                heights, cellWidthMeters, east, west, north, south);
            IsOceanConnected = ComputeOceanConnectivity(heights);
        }

        private struct NeighborEdges
        {
            public float[] heightmapEdge;   // null = no neighbor loaded
            public float[] distanceEdge;    // null = neighbor's BFS not yet computed
        }

        private enum NeighborSide { East, West, North, South }
        private enum EdgeAxis { EastWest, NorthSouth }

        // Apply cross-tile boundary seeding for one edge of our distance grid.
        // The neighbor's distance edge takes precedence — for each cell, our
        // boundary cell is "one straight step" away from the neighbor's value.
        // If we only have heightmap data (neighbor's distance not computed yet),
        // fall back to a 0-vs-straight binary based on land/water.
        private static void SeedFromNeighbor(float[,] dist, NeighborEdges edge,
            float oceanThreshold, float straight, int rows, int cols,
            EdgeAxis axis, int atIndex)
        {
            int length = (axis == EdgeAxis.EastWest) ? rows : cols;

            if (edge.distanceEdge != null && edge.distanceEdge.Length == length)
            {
                if (axis == EdgeAxis.EastWest)
                {
                    for (int y = 0; y < rows; y++)
                        dist[y, atIndex] = Mathf.Min(dist[y, atIndex],
                            edge.distanceEdge[y] + straight);
                }
                else
                {
                    for (int x = 0; x < cols; x++)
                        dist[atIndex, x] = Mathf.Min(dist[atIndex, x],
                            edge.distanceEdge[x] + straight);
                }
                return;
            }

            if (edge.heightmapEdge != null && edge.heightmapEdge.Length == length)
            {
                if (axis == EdgeAxis.EastWest)
                {
                    for (int y = 0; y < rows; y++)
                        if (edge.heightmapEdge[y] > oceanThreshold)
                            dist[y, atIndex] = Mathf.Min(dist[y, atIndex], straight);
                }
                else
                {
                    for (int x = 0; x < cols; x++)
                        if (edge.heightmapEdge[x] > oceanThreshold)
                            dist[atIndex, x] = Mathf.Min(dist[atIndex, x], straight);
                }
            }
        }

        // Find a loaded DaggerfallTerrain immediately adjacent to `self` in the
        // given cardinal direction. Match by world-space origin within an
        // epsilon — DFU's tile placement is exact on a tileWorldSize grid.
        private static DaggerfallTerrain FindNeighbor(DaggerfallTerrain self, NeighborSide side)
        {
            if (self == null) return null;

            float tileWS = MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale;
            Vector3 origin = self.transform.position;
            Vector3 target;
            switch (side)
            {
                case NeighborSide.East:  target = origin + new Vector3(tileWS, 0, 0); break;
                case NeighborSide.West:  target = origin - new Vector3(tileWS, 0, 0); break;
                case NeighborSide.North: target = origin + new Vector3(0, 0, tileWS); break;
                case NeighborSide.South: target = origin - new Vector3(0, 0, tileWS); break;
                default: return null;
            }

            const float epsilon = 0.5f;
            DaggerfallTerrain[] terrains = UnityEngine.Object.FindObjectsOfType<DaggerfallTerrain>();
            for (int i = 0; i < terrains.Length; i++)
            {
                DaggerfallTerrain t = terrains[i];
                if (t == self) continue;
                Vector3 pos = t.transform.position;
                if (Mathf.Abs(pos.x - target.x) < epsilon &&
                    Mathf.Abs(pos.z - target.z) < epsilon)
                    return t;
            }
            return null;
        }

        // Return both the heightmap row/column AND the distance field
        // row/column of `self`'s neighbor on `side` that physically abuts
        // `self`. Heightmap is used as a fallback seed when the neighbor
        // hasn't computed its distance field yet.
        private static NeighborEdges GetNeighborEdges(DaggerfallTerrain self, NeighborSide side)
        {
            NeighborEdges result = default(NeighborEdges);
            DaggerfallTerrain neighbor = FindNeighbor(self, side);
            if (neighbor == null) return result;

            float[,] hm = neighbor.MapData.heightmapSamples;
            DeepWaterTileData neighborTile = neighbor.GetComponent<DeepWaterTileData>();
            float[,] nd = (neighborTile != null) ? neighborTile.distanceMeters : null;

            if (hm == null && nd == null) return result;

            int rows = hm != null ? hm.GetLength(0) : nd.GetLength(0);
            int cols = hm != null ? hm.GetLength(1) : nd.GetLength(1);

            switch (side)
            {
                case NeighborSide.East:
                    // East neighbor's WEST edge — its first column abuts us.
                    if (hm != null)
                    {
                        result.heightmapEdge = new float[rows];
                        for (int y = 0; y < rows; y++) result.heightmapEdge[y] = hm[y, 0];
                    }
                    if (nd != null)
                    {
                        result.distanceEdge = new float[rows];
                        for (int y = 0; y < rows; y++) result.distanceEdge[y] = nd[y, 0];
                    }
                    break;

                case NeighborSide.West:
                    // West neighbor's EAST edge — its last column abuts us.
                    if (hm != null)
                    {
                        result.heightmapEdge = new float[rows];
                        for (int y = 0; y < rows; y++) result.heightmapEdge[y] = hm[y, cols - 1];
                    }
                    if (nd != null)
                    {
                        result.distanceEdge = new float[rows];
                        for (int y = 0; y < rows; y++) result.distanceEdge[y] = nd[y, cols - 1];
                    }
                    break;

                case NeighborSide.North:
                    // North neighbor's SOUTH edge — its first row abuts us.
                    // (heightmap convention: row 0 is south, row last is north,
                    // since worldZ = origin.z + hy * cellWidth.)
                    if (hm != null)
                    {
                        result.heightmapEdge = new float[cols];
                        for (int x = 0; x < cols; x++) result.heightmapEdge[x] = hm[0, x];
                    }
                    if (nd != null)
                    {
                        result.distanceEdge = new float[cols];
                        for (int x = 0; x < cols; x++) result.distanceEdge[x] = nd[0, x];
                    }
                    break;

                case NeighborSide.South:
                    // South neighbor's NORTH edge — its last row abuts us.
                    if (hm != null)
                    {
                        result.heightmapEdge = new float[cols];
                        for (int x = 0; x < cols; x++) result.heightmapEdge[x] = hm[rows - 1, x];
                    }
                    if (nd != null)
                    {
                        result.distanceEdge = new float[cols];
                        for (int x = 0; x < cols; x++) result.distanceEdge[x] = nd[rows - 1, x];
                    }
                    break;
            }

            return result;
        }

        public float GetDistanceToCoastMeters(float worldX, float worldZ)
        {
            if (distanceMeters == null)
                return float.MaxValue;

            Vector3 origin = transform.position;
            float fracX = Mathf.Clamp01((worldX - origin.x) / tileWorldSize);
            float fracZ = Mathf.Clamp01((worldZ - origin.z) / tileWorldSize);

            float fx = fracX * (hDim1 - 1);
            float fz = fracZ * (hDim0 - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, hDim1 - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, hDim0 - 1);
            int x1 = Mathf.Min(x0 + 1, hDim1 - 1);
            int z1 = Mathf.Min(z0 + 1, hDim0 - 1);
            float tx = fx - x0;
            float tz = fz - z0;

            float d00 = distanceMeters[z0, x0];
            float d10 = distanceMeters[z0, x1];
            float d01 = distanceMeters[z1, x0];
            float d11 = distanceMeters[z1, x1];

            float dx0 = Mathf.Lerp(d00, d10, tx);
            float dx1 = Mathf.Lerp(d01, d11, tx);
            return Mathf.Lerp(dx0, dx1, tz);
        }

        private static bool ComputeOceanConnectivity(float[,] heights)
        {
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanThreshold = sampler.OceanElevation / sampler.MaxTerrainHeight;

            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            int minRun = Mathf.Max(rows, cols) / 4;

            if (HasWaterRun(heights, 0, true, cols, oceanThreshold, minRun)) return true;
            if (HasWaterRun(heights, rows - 1, true, cols, oceanThreshold, minRun)) return true;
            if (HasWaterRun(heights, 0, false, rows, oceanThreshold, minRun)) return true;
            if (HasWaterRun(heights, cols - 1, false, rows, oceanThreshold, minRun)) return true;

            return false;
        }

        private static bool HasWaterRun(float[,] heights, int fixedIndex, bool fixedIsRow, int length, float oceanThreshold, int minRun)
        {
            int run = 0;
            for (int i = 0; i < length; i++)
            {
                float h = fixedIsRow ? heights[fixedIndex, i] : heights[i, fixedIndex];
                if (h <= oceanThreshold + 1e-5f)
                {
                    run++;
                    if (run >= minRun) return true;
                }
                else
                {
                    run = 0;
                }
            }
            return false;
        }

        private static float[,] ComputeDistanceField(
            float[,] heights, float cellWidthMeters,
            NeighborEdges east, NeighborEdges west,
            NeighborEdges north, NeighborEdges south)
        {
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanThreshold = sampler.OceanElevation / sampler.MaxTerrainHeight;

            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            var dist = new float[rows, cols];

            float saturated = MaxDistanceCells * cellWidthMeters;
            float straight = StraightStep * cellWidthMeters;
            float diagonal = DiagonalStep * cellWidthMeters;

            // Initial seed from own heightmap: land = 0, water = saturated.
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    dist[y, x] = heights[y, x] > oceanThreshold ? 0f : saturated;
                }
            }

            // Cross-tile boundary seeds. Prefer the neighbor's computed distance
            // edge (so we match it exactly with one straight-step crossing)
            // over the heightmap-only fallback. Where the neighbor has reported
            // distance D at its boundary cell, our adjacent cell gets D + step
            // — that's the BFS-exact "one cell away" relationship across the
            // shared edge, which makes both tiles agree at the seam.
            SeedFromNeighbor(dist, east, oceanThreshold, straight, rows, cols, EdgeAxis.EastWest, atIndex: cols - 1);
            SeedFromNeighbor(dist, west, oceanThreshold, straight, rows, cols, EdgeAxis.EastWest, atIndex: 0);
            SeedFromNeighbor(dist, north, oceanThreshold, straight, rows, cols, EdgeAxis.NorthSouth, atIndex: rows - 1);
            SeedFromNeighbor(dist, south, oceanThreshold, straight, rows, cols, EdgeAxis.NorthSouth, atIndex: 0);

            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    float current = dist[y, x];
                    if (y > 0)
                    {
                        if (x > 0)        current = Mathf.Min(current, dist[y - 1, x - 1] + diagonal);
                        current = Mathf.Min(current, dist[y - 1, x] + straight);
                        if (x < cols - 1) current = Mathf.Min(current, dist[y - 1, x + 1] + diagonal);
                    }
                    if (x > 0)            current = Mathf.Min(current, dist[y, x - 1] + straight);
                    dist[y, x] = Mathf.Min(current, saturated);
                }
            }

            for (int y = rows - 1; y >= 0; y--)
            {
                for (int x = cols - 1; x >= 0; x--)
                {
                    float current = dist[y, x];
                    if (y < rows - 1)
                    {
                        if (x < cols - 1) current = Mathf.Min(current, dist[y + 1, x + 1] + diagonal);
                        current = Mathf.Min(current, dist[y + 1, x] + straight);
                        if (x > 0)        current = Mathf.Min(current, dist[y + 1, x - 1] + diagonal);
                    }
                    if (x < cols - 1)     current = Mathf.Min(current, dist[y, x + 1] + straight);
                    dist[y, x] = Mathf.Min(current, saturated);
                }
            }

            return dist;
        }
    }
}
