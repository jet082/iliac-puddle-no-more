// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Per-DaggerfallTerrain seafloor sub-mesh. Generates a regular vertex grid
    /// over the tile's XZ footprint, samples <see cref="DeepBathymetry"/> at each
    /// vertex in world coordinates, and stores depth in vertex color so the
    /// seafloor shader can blend shallow-sand to deep-rock. UVs are world-meter
    /// local positions so the regional terrain texture tiles consistently across
    /// tile boundaries.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    [RequireComponent(typeof(MeshCollider))]
    internal class DeepWaterFloorMesh : MonoBehaviour
    {
        // 65x65 keeps the bathymetry readable; the cheaper 33x33 grid flattened
        // shallow saves like jjj because it undersampled the detail layer.
        internal const int VertexGridSize = 65;
        private const float WallMinimumDrop = 0.25f;
        private const float ShoreWallSurfaceInset = 0.15f;
        private const float ShoreWallBottomOverlap = 0.10f;
        private const float ShoreWallTopTextureStrength = 0.35f;
        // Continuous shore skirt. Keep it short and deterministic so stream
        // builds don't spend extra time on wide/noisy perimeter geometry.
        private const float SkirtSlopeTangent = 0.6f;
        private const float SkirtMinWidthMeters = 12.0f;
        private const float SkirtMaxWidthMeters = 40.0f;
        private const float BoundarySkirtMinWidthMeters = 2.0f;
        private const float BoundarySkirtMaxWidthMeters = 8.0f;
        private const float BoundaryMixedShoreWallMaxDistanceMeters = 48f;
        private const float ShoreTerrainFitMeters = 180f;
        private const float ShoreTerrainFitClearance = 0.05f;
        private const float FloorSurfaceClearanceMeters = 0.05f; // keep near-shore floor barely under the water surface

        private Mesh mesh;
        private MeshCollider meshCollider;
        private DeepWaterTileData tileData;
        private DaggerfallTerrain dfTerrain;
        private int buildVersion;
        private int colliderBuildVersion = -1;

        // Cache of the vertex grid local Y values. Decoration placement
        // samples this instead of the raw bathymetry function so it lands
        // ON the rendered mesh surface rather than on the higher-frequency
        // function value that the mesh's linear interpolation misses.
        private float[,] vertexLocalY;
        private bool[,] floorQuadWater;
        private float tileWorldSizeCached;

        internal int BuildVersion
        {
            get { return buildVersion; }
        }

        internal int BuiltMapPixelX { get; private set; }
        internal int BuiltMapPixelY { get; private set; }

        internal bool HasValidRuntimeCollider
        {
            get
            {
                return mesh != null &&
                       mesh.vertexCount > 0 &&
                       meshCollider != null &&
                       meshCollider.enabled &&
                       meshCollider.sharedMesh == mesh;
            }
        }

        // Reference to the heightmap array we last built against. DFU
        // allocates a fresh float[,] on every legitimate promote (verified
        // for Default, IT, and WoD samplers), so reference equality is a
        // reliable "data unchanged" signal. DeepWaterFloorBuilder uses this
        // to short-circuit RefreshPlayerArea's redundant re-promotion path,
        // which would otherwise bump BuildVersion every stream cycle and
        // cause UnderwaterDecorations to tear down + respawn the entire
        // ring on every streaming update (visible as decorations "moving
        // around rapidly" to the player).
        internal float[,] LastBuiltHeightmapSamples { get; private set; }

        internal void Build(DaggerfallTerrain owner, DeepWaterTileData tile, float oceanLocalY, bool[,] holes)
        {
            dfTerrain = owner;
            tileData = tile;
            buildVersion++;
            BuiltMapPixelX = owner != null ? owner.MapPixelX : int.MinValue;
            BuiltMapPixelY = owner != null ? owner.MapPixelY : int.MinValue;
            LastBuiltHeightmapSamples = owner != null ? owner.MapData.heightmapSamples : null;

            if (mesh == null)
            {
                mesh = new Mesh { name = "DeepWaters_FloorMesh" };
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                GetComponent<MeshFilter>().sharedMesh = mesh;
            }

            float tileWorldSize = MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale;
            tileWorldSizeCached = tileWorldSize;
            Vector3 terrainOrigin = owner.transform.position;

            int n = VertexGridSize;
            int vertexCount = n * n;
            var vertices = new List<Vector3>(vertexCount + EstimateWallVertexCapacity(holes));
            var colors = new List<Color>(vertices.Capacity);
            var uvs = new List<Vector2>(vertices.Capacity);

            if (vertexLocalY == null || vertexLocalY.GetLength(0) != n)
                vertexLocalY = new float[n, n];
            if (floorQuadWater == null || floorQuadWater.GetLength(0) != n - 1)
                floorQuadWater = new bool[n - 1, n - 1];

            float cellSpacing = tileWorldSize / (n - 1);
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanThreshold = sampler != null ? sampler.OceanElevation / sampler.MaxTerrainHeight : 0.5f;

            for (int z = 0; z < n; z++)
            {
                float localZ = z * cellSpacing;
                float worldZ = terrainOrigin.z + localZ;
                for (int x = 0; x < n; x++)
                {
                    float localX = x * cellSpacing;
                    float worldX = terrainOrigin.x + localX;

                    // Distance to the nearest shore edge (baked, global — now
                    // accurate via the CPU hybrid WOD classifier) drives the deep
                    // shelf, so the floor descends gradually over the full
                    // continental margin, consistent across tile boundaries.
                    float shoreDistance = tile.GetDistanceToEdgeMeters(worldX, worldZ);
                    // Blended (bilinear across the 4 surrounding map pixels)
                    // climate base depth + texture band, so neither steps at a
                    // map-pixel climate boundary.
                    float climateBase, vertexClimateBand;
                    tile.GetBlendedClimate(worldX, worldZ, out climateBase, out vertexClimateBand);
                    // Sample the seabed noise in map-pixel-anchored coordinates
                    // (not the floating-origin-shifted worldX) so adjacent tiles
                    // built at different origin offsets still agree at shared
                    // edges — otherwise the hills tear into seams at every
                    // map-pixel boundary.
                    float noiseX, noiseZ;
                    tile.GetNoiseWorldCoords(worldX, worldZ, out noiseX, out noiseZ);
                    float depth = DeepBathymetry.SampleDepthMeters(noiseX, noiseZ, climateBase, shoreDistance);
                    float localY = oceanLocalY - depth;
                    localY = FitSeafloorToShoreTerrain(
                        localY,
                        localX,
                        localZ,
                        oceanLocalY,
                        oceanThreshold,
                        shoreDistance,
                        terrainOrigin,
                        tileWorldSize);
                    localY = Mathf.Min(localY, oceanLocalY - FloorSurfaceClearanceMeters);
                    depth = Mathf.Max(0f, oceanLocalY - localY);

                    vertices.Add(new Vector3(localX, localY, localZ));
                    vertexLocalY[z, x] = localY;

                    colors.Add(CreateVertexColor(depth, vertexClimateBand, shoreDistance));

                    // UV in world-meter local coordinates. The shader
                    // multiplies by _TextureWorldScale (= 1 / meters-per-tile)
                    // so the regional texture tiles consistently with the
                    // surrounding land terrain.
                    uvs.Add(new Vector2(localX, localZ));
                }
            }

            int quadCount = (n - 1) * (n - 1);
            var triangles = new List<int>(quadCount * 6 + EstimateWallTriangleCapacity(holes));
            for (int z = 0; z < n - 1; z++)
            {
                for (int x = 0; x < n - 1; x++)
                {
                    bool isWaterQuad = IsFloorQuadWater(holes, x, z, n - 1);
                    floorQuadWater[z, x] = isWaterQuad;
                    if (!isWaterQuad)
                        continue;

                    int v00 = z * n + x;
                    int v10 = v00 + 1;
                    int v01 = v00 + n;
                    int v11 = v01 + 1;

                    triangles.Add(v00);
                    triangles.Add(v01);
                    triangles.Add(v11);
                    triangles.Add(v00);
                    triangles.Add(v11);
                    triangles.Add(v10);
                }
            }

            // v0.55.44: skirt RE-ADDED. Removing it (v0.55.43) exposed
            // see-through voids at the carve perimeter where the deep floor
            // doesn't reach the shallow shore — worse than walls. The skirt
            // covers those; it self-skips where the floor already meets the
            // terrain (small drop), so it only "walls" at the hard deep edges.
            AppendHoleEdgeWalls(holes, vertices, colors, uvs, triangles, oceanLocalY, terrainOrigin, tileWorldSize);

            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();

            EnsureMaterial();
            EnsureCollider();

        }

        internal void TearDown()
        {
            if (meshCollider != null)
            {
                meshCollider.sharedMesh = null;
                meshCollider = null;
            }

            if (mesh != null)
            {
                var mf = GetComponent<MeshFilter>();
                if (mf != null)
                    mf.sharedMesh = null;
                Destroy(mesh);
                mesh = null;
            }

            vertexLocalY = null;
            floorQuadWater = null;
        }

        /// <summary>
        /// Sample the mesh-interpolated local Y at a world position. Returns
        /// the EXACT value the rendered triangle has at that XZ — so placing
        /// a decoration at this Y plus a small clearance lands it on the
        /// visible surface even where the mesh's linear interpolation differs
        /// from the underlying bathymetry function.
        /// </summary>
        internal bool TrySampleMeshLocalY(float worldX, float worldZ, out float localY)
        {
            localY = 0f;
            if (vertexLocalY == null || dfTerrain == null || tileWorldSizeCached <= 0f)
                return false;

            Vector3 origin = dfTerrain.transform.position;
            float fracX = (worldX - origin.x) / tileWorldSizeCached;
            float fracZ = (worldZ - origin.z) / tileWorldSizeCached;
            if (fracX < 0f || fracX > 1f || fracZ < 0f || fracZ > 1f)
                return false;

            int n = VertexGridSize;
            float fx = fracX * (n - 1);
            float fz = fracZ * (n - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, n - 2);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, n - 2);
            if (floorQuadWater != null && !floorQuadWater[z0, x0])
                return false;

            int x1 = x0 + 1;
            int z1 = z0 + 1;
            float tx = Mathf.Clamp01(fx - x0);
            float tz = Mathf.Clamp01(fz - z0);

            float y00 = vertexLocalY[z0, x0];
            float y10 = vertexLocalY[z0, x1];
            float y01 = vertexLocalY[z1, x0];
            float y11 = vertexLocalY[z1, x1];

            // Mesh triangle indices use diagonal v00 - v11:
            //   T1 (v00, v01, v11): tz > tx half
            //   T2 (v00, v11, v10): tx >= tz half
            if (tx >= tz)
                localY = y00 * (1f - tx) + y10 * (tx - tz) + y11 * tz;
            else
                localY = y00 * (1f - tz) + y01 * (tz - tx) + y11 * tx;

            return true;
        }

        internal void EnsureRuntimeCollider()
        {
            if (HasValidRuntimeCollider && colliderBuildVersion == buildVersion)
                return;

            EnsureCollider();
        }

        private static bool IsFloorQuadWater(bool[,] holes, int quadX, int quadZ, int quadResolution)
        {
            if (holes == null || quadResolution <= 0)
                return true;

            int rows = holes.GetLength(0);
            int cols = holes.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return false;

			float minFracX = quadX / (float)quadResolution;
			float maxFracX = (quadX + 1) / (float)quadResolution;
			float minFracZ = quadZ / (float)quadResolution;
			float maxFracZ = (quadZ + 1) / (float)quadResolution;
			int minHx = Mathf.Clamp(Mathf.FloorToInt(minFracX * cols), 0, cols - 1);
			int maxHx = Mathf.Clamp(Mathf.CeilToInt(maxFracX * cols) - 1, 0, cols - 1);
			int minHz = Mathf.Clamp(Mathf.FloorToInt(minFracZ * rows), 0, rows - 1);
			int maxHz = Mathf.Clamp(Mathf.CeilToInt(maxFracZ * rows) - 1, 0, rows - 1);

			for (int hz = minHz; hz <= maxHz; hz++)
			{
				for (int hx = minHx; hx <= maxHx; hx++)
				{
					if (!holes[hz, hx])
						return true;
				}
			}

			return false;
        }

        private void AppendHoleEdgeWalls(
            bool[,] holes,
            List<Vector3> vertices,
            List<Color> colors,
            List<Vector2> uvs,
            List<int> triangles,
            float oceanLocalY,
            Vector3 terrainOrigin,
            float tileWorldSize)
        {
            if (holes == null || tileData == null)
                return;

            int rows = holes.GetLength(0);
            int cols = holes.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return;

            // 1. Collect perimeter edges: a water cell facing land inside the
            //    tile, or facing a not-carved neighbour across a tile boundary.
            //    Vertex coords run [0..cols] x [0..rows]; the A->B order is the
            //    same the old per-edge walls used, so the edge normal points
            //    toward the water (carved) side.
            //
            //    Boundary-side rule (IsBakedHoleAcrossBoundary): skirt only where
            //    the neighbour tile will NOT carve (land, or shore inside
            //    HoleBufferMeters). Without the negation every all-ocean tile
            //    skirts its whole perimeter and the streaming region explodes.
            var edges = new List<SkirtEdge>();
            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < cols; x++)
                {
                    if (holes[z, x])
                        continue;

                    if (x > 0 && holes[z, x - 1]) { edges.Add(new SkirtEdge(x, z, x, z + 1, false)); }
                    if (x < cols - 1 && holes[z, x + 1]) { edges.Add(new SkirtEdge(x + 1, z + 1, x + 1, z, false)); }
                    if (z > 0 && holes[z - 1, x]) { edges.Add(new SkirtEdge(x + 1, z, x, z, false)); }
                    if (z < rows - 1 && holes[z + 1, x]) { edges.Add(new SkirtEdge(x, z + 1, x + 1, z + 1, false)); }

                    if (x == 0 && !IsBakedHoleAcrossBoundary(x, z, -1, 0, cols, rows, terrainOrigin, tileWorldSize)) { edges.Add(new SkirtEdge(x, z, x, z + 1, true)); }
                    if (x == cols - 1 && !IsBakedHoleAcrossBoundary(x, z, 1, 0, cols, rows, terrainOrigin, tileWorldSize)) { edges.Add(new SkirtEdge(x + 1, z + 1, x + 1, z, true)); }
                    if (z == 0 && !IsBakedHoleAcrossBoundary(x, z, 0, -1, cols, rows, terrainOrigin, tileWorldSize)) { edges.Add(new SkirtEdge(x + 1, z, x, z, true)); }
                    if (z == rows - 1 && !IsBakedHoleAcrossBoundary(x, z, 0, 1, cols, rows, terrainOrigin, tileWorldSize)) { edges.Add(new SkirtEdge(x, z + 1, x + 1, z + 1, true)); }
                }
            }

            if (edges.Count == 0)
                return;

            // 2. Accumulate an inward (toward-water) normal at each perimeter
            //    vertex from its adjacent edges. Sharing this averaged normal is
            //    what lets neighbouring skirt quads meet exactly (no slots).
            int stride = cols + 1;
            float cellW = tileWorldSize / cols;
            float cellH = tileWorldSize / rows;
            var inwardNormals = new Dictionary<int, Vector2>(edges.Count * 2);
            for (int i = 0; i < edges.Count; i++)
            {
                SkirtEdge e = edges[i];
                float dX = (e.bx - e.ax) * cellW;
                float dZ = (e.bz - e.az) * cellH;
                float len = Mathf.Sqrt(dX * dX + dZ * dZ);
                if (len < 1e-4f)
                    continue;
                Vector2 n = new Vector2(dZ / len, -dX / len);
                int ka = e.az * stride + e.ax;
                int kb = e.bz * stride + e.bx;
                Vector2 cur;
                inwardNormals.TryGetValue(ka, out cur); inwardNormals[ka] = cur + n;
                inwardNormals.TryGetValue(kb, out cur); inwardNormals[kb] = cur + n;
            }

            // 3. Build one shared top+bottom vertex per perimeter vertex.
            float skirtTopY = oceanLocalY - ShoreWallSurfaceInset;
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanThreshold = sampler != null ? sampler.OceanElevation / sampler.MaxTerrainHeight : 0.5f;
            var topIndex = new Dictionary<int, int>(inwardNormals.Count);
            var bottomIndex = new Dictionary<int, int>(inwardNormals.Count);
            for (int i = 0; i < edges.Count; i++)
            {
                SkirtEdge e = edges[i];
                EnsureSkirtVertex(e.ax, e.az, stride, cols, rows, inwardNormals, topIndex, bottomIndex,
                    vertices, colors, uvs, oceanLocalY, skirtTopY, oceanThreshold, terrainOrigin, tileWorldSize, e.boundary);
                EnsureSkirtVertex(e.bx, e.bz, stride, cols, rows, inwardNormals, topIndex, bottomIndex,
                    vertices, colors, uvs, oceanLocalY, skirtTopY, oceanThreshold, terrainOrigin, tileWorldSize, e.boundary);
            }

            // 4. Two-sided quad per edge, reusing the shared vertex indices so
            //    neighbouring quads meet exactly.
            for (int i = 0; i < edges.Count; i++)
            {
                SkirtEdge e = edges[i];
                int ka = e.az * stride + e.ax;
                int kb = e.bz * stride + e.bx;
                int tA, tB, bA, bB;
                if (!topIndex.TryGetValue(ka, out tA) || !topIndex.TryGetValue(kb, out tB) ||
                    !bottomIndex.TryGetValue(ka, out bA) || !bottomIndex.TryGetValue(kb, out bB))
                    continue;

                float span = Mathf.Max(vertices[tA].y - vertices[bA].y, vertices[tB].y - vertices[bB].y);
                if (span < WallMinimumDrop)
                {
                    continue;
                }

                triangles.Add(tA); triangles.Add(tB); triangles.Add(bB);
                triangles.Add(tA); triangles.Add(bB); triangles.Add(bA);
                // Reverse winding so the skirt is solid + visible from both sides.
                triangles.Add(tA); triangles.Add(bB); triangles.Add(tB);
                triangles.Add(tA); triangles.Add(bA); triangles.Add(bB);
            }
        }

        private bool IsBakedHoleAcrossBoundary(
            int x,
            int z,
            int dx,
            int dz,
            int cols,
            int rows,
            Vector3 terrainOrigin,
            float tileWorldSize)
        {
            if (tileData == null)
                return false;

            float cellWidth = tileWorldSize / cols;
            float cellHeight = tileWorldSize / rows;
            float sampleX = terrainOrigin.x + (x + 0.5f + dx) * cellWidth;
            float sampleZ = terrainOrigin.z + (z + 0.5f + dz) * cellHeight;

			if (tileData.UsesLocalWaterFallback)
				return true;

            // Phase B path (v4 bake with fine mask): the neighbor carves
            // when the global bake says the cross-boundary sample is carved
            // water. Do not depend on the neighboring DaggerfallTerrain being
            // promoted or having DeepWaterTileData initialized yet: streaming
            // order made that test fail transiently, permanently baking giant
            // map-pixel perimeter walls into whichever tile built first.
            if (DeepWaterDistanceBake.HasFineWaterMask)
            {
                if (!tileData.IsCarvedWater(sampleX, sampleZ))
                    return false;

                return !ShouldKeepBoundaryWallAgainstMixedShore(
                    sampleX,
                    sampleZ,
                    terrainOrigin,
                    tileWorldSize);
            }

            // Pre-v4 fallback: heightmap shared-corner prediction (the
            // v0.52.1 fix), then bake-classification fallback. Kept for
            // backward compatibility with legacy bakes.
            if (dfTerrain != null)
            {
                float[,] heights = dfTerrain.MapData.heightmapSamples;
                if (heights != null)
                {
                    int hRows = heights.GetLength(0);
                    int hCols = heights.GetLength(1);

                    int c1Row, c1Col, c2Row, c2Col;
                    if (dx == -1)
                    {
                        c1Row = z;     c1Col = 0;
                        c2Row = z + 1; c2Col = 0;
                    }
                    else if (dx == 1)
                    {
                        c1Row = z;     c1Col = x + 1;
                        c2Row = z + 1; c2Col = x + 1;
                    }
                    else if (dz == -1)
                    {
                        c1Row = 0; c1Col = x;
                        c2Row = 0; c2Col = x + 1;
                    }
                    else // dz == 1
                    {
                        c1Row = z + 1; c1Col = x;
                        c2Row = z + 1; c2Col = x + 1;
                    }

                    if (c1Row >= 0 && c1Row < hRows && c1Col >= 0 && c1Col < hCols &&
                        c2Row >= 0 && c2Row < hRows && c2Col >= 0 && c2Col < hCols)
                    {
                        var sampler = DaggerfallUnity.Instance.TerrainSampler;
                        float oceanThreshold = sampler.OceanElevation / sampler.MaxTerrainHeight;
                        float waterThreshold = oceanThreshold + 1e-5f;

                        if (heights[c1Row, c1Col] <= waterThreshold ||
                            heights[c2Row, c2Col] <= waterThreshold)
                            return true;
                    }
                }
            }

            return tileData.IsBakedWater(sampleX, sampleZ) &&
                   tileData.GetDistanceToCoastMeters(sampleX, sampleZ) >= DeepWaterFloorBuilder.HoleBufferMeters;
        }

        private bool ShouldKeepBoundaryWallAgainstMixedShore(
            float sampleX,
            float sampleZ,
            Vector3 terrainOrigin,
            float tileWorldSize)
        {
            if (dfTerrain == null || tileWorldSize <= 0f)
                return false;

            int mapPixelX;
            int mapPixelY;
            float fracX;
            float fracZ;
            ResolveGlobalMapFractions(sampleX, sampleZ, terrainOrigin, tileWorldSize, out mapPixelX, out mapPixelY, out fracX, out fracZ);

            if (!DeepWaterDistanceBake.MapPixelHasLandCells(mapPixelX, mapPixelY))
                return false;

            float edgeDistance = DeepWaterDistanceBake.SampleEdgeDistanceMeters(mapPixelX, mapPixelY, fracX, fracZ);
            if (edgeDistance > BoundaryMixedShoreWallMaxDistanceMeters)
                return false;

            DaggerfallTerrain sampleDfTerrain;
            Terrain sampleTerrain;
            if (DeepWaterTerrainLookup.TryGetByWorldPosition(sampleX, sampleZ, out sampleDfTerrain, out sampleTerrain) &&
                sampleDfTerrain != null)
            {
                float sampleFracX = (sampleX - sampleDfTerrain.transform.position.x) / tileWorldSize;
                float sampleFracZ = (sampleZ - sampleDfTerrain.transform.position.z) / tileWorldSize;
                if (sampleFracX >= 0f && sampleFracX <= 1f &&
                    sampleFracZ >= 0f && sampleFracZ <= 1f &&
                    DeepWaterWaterClassification.IsLocalPointWater(sampleDfTerrain.MapData, sampleFracX, sampleFracZ))
                {
                    return false;
                }
            }

            return true;
        }

        private void ResolveGlobalMapFractions(
            float worldX,
            float worldZ,
            Vector3 terrainOrigin,
            float tileWorldSize,
            out int mapPixelX,
            out int mapPixelY,
            out float fracX,
            out float fracZ)
        {
            float localFracX = (worldX - terrainOrigin.x) / tileWorldSize;
            float localFracZ = (worldZ - terrainOrigin.z) / tileWorldSize;

            float globalX = BuiltMapPixelX + localFracX;
            float globalSouthY = BuiltMapPixelY + (1f - localFracZ);

            mapPixelX = Mathf.FloorToInt(globalX);
            mapPixelY = Mathf.FloorToInt(globalSouthY);
            fracX = globalX - mapPixelX;
            float southFrac = globalSouthY - mapPixelY;
            fracZ = 1f - southFrac;

            NormalizeMapFractionX(ref mapPixelX, ref fracX);
            NormalizeMapFractionY(ref mapPixelY, ref fracZ);
        }

        private static void NormalizeMapFractionX(ref int mapPixel, ref float frac)
        {
            if (mapPixel < 0)
            {
                mapPixel = 0;
                frac = 0f;
                return;
            }

            if (mapPixel >= MapsFile.MaxMapPixelX)
            {
                mapPixel = MapsFile.MaxMapPixelX - 1;
                frac = 1f;
                return;
            }

            frac = Mathf.Clamp01(frac);
        }

        private static void NormalizeMapFractionY(ref int mapPixel, ref float fracZ)
        {
            if (mapPixel < 0)
            {
                mapPixel = 0;
                fracZ = 1f;
                return;
            }

            if (mapPixel >= MapsFile.MaxMapPixelY)
            {
                mapPixel = MapsFile.MaxMapPixelY - 1;
                fracZ = 0f;
                return;
            }

            fracZ = Mathf.Clamp01(fracZ);
        }

        // Build (once) the shared top + bottom skirt vertices for one perimeter
        // grid vertex. The top sits just under the surface at the carve edge;
        // the bottom is pushed inward (toward water) along the vertex's averaged
        // normal by a run that scales with the local drop, landing on the seabed.
        // Because adjacent edges look up the SAME key, their quads share these
        // vertices exactly — no slots.
        private void EnsureSkirtVertex(
            int vx,
            int vz,
            int stride,
            int cols,
            int rows,
            Dictionary<int, Vector2> inwardNormals,
            Dictionary<int, int> topIndex,
            Dictionary<int, int> bottomIndex,
            List<Vector3> vertices,
            List<Color> colors,
            List<Vector2> uvs,
            float oceanLocalY,
            float skirtTopY,
            float oceanThreshold,
            Vector3 terrainOrigin,
            float tileWorldSize,
            bool boundaryEdge)
        {
            int key = vz * stride + vx;
            if (topIndex.ContainsKey(key))
                return;

            float localX = (vx / (float)cols) * tileWorldSize;
            float localZ = (vz / (float)rows) * tileWorldSize;

            // Boundary skirts stay pinned to vanilla terrain so map-pixel edges
            // do not become ledges; internal shore skirts can close the waterline.
            float vanillaTopY = SampleVanillaLocalY(vx / (float)cols, vz / (float)rows, oceanLocalY, oceanThreshold);
            vanillaTopY = Mathf.Max(vanillaTopY, SampleNearbyVanillaLocalY(localX, localZ, vanillaTopY, terrainOrigin));
            float topY = boundaryEdge ? Mathf.Min(skirtTopY, vanillaTopY) : skirtTopY;

            Vector2 inward;
            inwardNormals.TryGetValue(key, out inward);
            if (inward.sqrMagnitude > 1e-6f)
                inward = inward.normalized;
            else
                inward = Vector2.zero;

            // Drop from the surface to the seabed here decides the run: deeper
            // drop -> wider, gentler grade.
            float worldX = terrainOrigin.x + localX;
            float worldZ = terrainOrigin.z + localZ;
            float topDistance, topDepthUnused, topClimateBand;
            float seafloorTop = SampleSeafloorLocalY(worldX, worldZ, oceanLocalY, oceanThreshold, terrainOrigin, tileWorldSize, out topDistance, out topDepthUnused, out topClimateBand);
            float meshTop;
            if (TrySampleMeshLocalY(worldX, worldZ, out meshTop))
                seafloorTop = meshTop;
            float drop = Mathf.Max(0f, topY - seafloorTop);

            float minWidth = boundaryEdge ? BoundarySkirtMinWidthMeters : SkirtMinWidthMeters;
            float maxWidth = boundaryEdge ? BoundarySkirtMaxWidthMeters : SkirtMaxWidthMeters;
            float skirtWidth = Mathf.Clamp(drop / SkirtSlopeTangent, minWidth, maxWidth);

            float bottomLocalX = Mathf.Clamp(localX + inward.x * skirtWidth, 0f, tileWorldSize);
            float bottomLocalZ = Mathf.Clamp(localZ + inward.y * skirtWidth, 0f, tileWorldSize);
            float bottomWorldX = terrainOrigin.x + bottomLocalX;
            float bottomWorldZ = terrainOrigin.z + bottomLocalZ;

            float bottomDistance, bottomDepth, bottomClimateBand;
            float seafloorBottom = SampleSeafloorLocalY(bottomWorldX, bottomWorldZ, oceanLocalY, oceanThreshold, terrainOrigin, tileWorldSize, out bottomDistance, out bottomDepth, out bottomClimateBand);
            float meshBottom;
            if (TrySampleMeshLocalY(bottomWorldX, bottomWorldZ, out meshBottom))
                seafloorBottom = meshBottom;
            float bottomY = seafloorBottom - ShoreWallBottomOverlap;
            bottomDepth = Mathf.Max(0f, oceanLocalY - bottomY);

            float topDepth = Mathf.Max(0f, oceanLocalY - topY);
            float shallowWall = 1f - Mathf.Clamp01(bottomDepth / 6f);
            float wallColorDepth = Mathf.Lerp(bottomDepth, topDepth, shallowWall * 0.85f);
            float wallColorClimate = Mathf.Lerp(bottomClimateBand, topClimateBand, shallowWall * 0.85f);
            float wallColorDistance = Mathf.Lerp(bottomDistance, topDistance, shallowWall * 0.85f);

            topIndex[key] = vertices.Count;
            vertices.Add(new Vector3(localX, topY, localZ));
            colors.Add(CreateVertexColor(topDepth, topClimateBand, topDistance, ShoreWallTopTextureStrength));
            uvs.Add(new Vector2(localX, localZ));

            bottomIndex[key] = vertices.Count;
            vertices.Add(new Vector3(bottomLocalX, bottomY, bottomLocalZ));
            colors.Add(CreateVertexColor(wallColorDepth, wallColorClimate, wallColorDistance));
            uvs.Add(new Vector2(bottomLocalX, bottomLocalZ));
		}

        // Vanilla terrain local-Y at a tile fraction, from the promoted
        // heightmap. The floor mesh shares the terrain's local space, where
        // localY scales linearly with the height sample and the ocean surface
        // sits at oceanThreshold -> oceanLocalY, so a sample h maps to
        // (h / oceanThreshold) * oceanLocalY. Same [z, x] indexing as the carve.
        private float SampleVanillaLocalY(float fracX, float fracZ, float oceanLocalY, float oceanThreshold)
        {
            if (dfTerrain == null || oceanThreshold <= 1e-6f)
                return oceanLocalY;
            float[,] heights = dfTerrain.MapData.heightmapSamples;
            if (heights == null)
                return oceanLocalY;
            int hRows = heights.GetLength(0);
            int hCols = heights.GetLength(1);
            if (hRows < 2 || hCols < 2)
                return oceanLocalY;

            float fz = Mathf.Clamp01(fracZ) * (hRows - 1);
            float fx = Mathf.Clamp01(fracX) * (hCols - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, hRows - 2);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, hCols - 2);
            float tz = fz - z0;
            float tx = fx - x0;
            float h0 = Mathf.Lerp(heights[z0, x0], heights[z0, x0 + 1], tx);
            float h1 = Mathf.Lerp(heights[z0 + 1, x0], heights[z0 + 1, x0 + 1], tx);
            float h = Mathf.Lerp(h0, h1, tz);
            return (h / oceanThreshold) * oceanLocalY;
        }

        private float SampleNearbyVanillaLocalY(float localX, float localZ, float fallbackLocalY, Vector3 terrainOrigin)
        {
            const float sampleOffset = 1f;
            float worldX = terrainOrigin.x + localX;
            float worldZ = terrainOrigin.z + localZ;
            float bestWorldY = terrainOrigin.y + fallbackLocalY;

            SampleNearbyVanillaWorldY(worldX + sampleOffset, worldZ, ref bestWorldY);
            SampleNearbyVanillaWorldY(worldX - sampleOffset, worldZ, ref bestWorldY);
            SampleNearbyVanillaWorldY(worldX, worldZ + sampleOffset, ref bestWorldY);
            SampleNearbyVanillaWorldY(worldX, worldZ - sampleOffset, ref bestWorldY);

            return bestWorldY - terrainOrigin.y;
        }

        private static void SampleNearbyVanillaWorldY(float worldX, float worldZ, ref float bestWorldY)
        {
            DaggerfallTerrain sampleDfTerrain;
            Terrain sampleTerrain;
            if (!DeepWaterTerrainLookup.TryGetByWorldPosition(worldX, worldZ, out sampleDfTerrain, out sampleTerrain) ||
                sampleDfTerrain == null ||
                sampleTerrain == null ||
                sampleTerrain.terrainData == null ||
                sampleDfTerrain.MapData.heightmapSamples == null)
            {
                return;
            }

            float tileWorldSize = DeepWaterWorld.TileWorldSize;
            if (tileWorldSize <= 0f)
                return;

            Vector3 origin = sampleDfTerrain.transform.position;
            float fracX = (worldX - origin.x) / tileWorldSize;
            float fracZ = (worldZ - origin.z) / tileWorldSize;
            float[,] heights = sampleDfTerrain.MapData.heightmapSamples;
            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            if (rows < 2 || cols < 2)
                return;

            float fx = Mathf.Clamp01(fracX) * (cols - 1);
            float fz = Mathf.Clamp01(fracZ) * (rows - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, cols - 2);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, rows - 2);
            float tx = fx - x0;
            float tz = fz - z0;
            float h0 = Mathf.Lerp(heights[z0, x0], heights[z0, x0 + 1], tx);
            float h1 = Mathf.Lerp(heights[z0 + 1, x0], heights[z0 + 1, x0 + 1], tx);
            float height = Mathf.Lerp(h0, h1, tz);
            float worldY = origin.y + height * sampleTerrain.terrainData.size.y;
            if (worldY > bestWorldY)
                bestWorldY = worldY;
        }

        private float FitSeafloorToShoreTerrain(
            float localY,
            float localX,
            float localZ,
            float oceanLocalY,
            float oceanThreshold,
            float shoreDistance,
            Vector3 terrainOrigin,
            float tileWorldSize)
        {
            if (shoreDistance >= ShoreTerrainFitMeters || tileWorldSize <= 0f)
                return localY;

            float fracX = localX / tileWorldSize;
            float fracZ = localZ / tileWorldSize;
            float terrainLocalY = SampleVanillaLocalY(fracX, fracZ, oceanLocalY, oceanThreshold);
            terrainLocalY = Mathf.Max(terrainLocalY, SampleNearbyVanillaLocalY(localX, localZ, terrainLocalY, terrainOrigin));
            float shoreLocalY = Mathf.Min(oceanLocalY - ShoreTerrainFitClearance, terrainLocalY - ShoreTerrainFitClearance);
            shoreLocalY = Mathf.Max(localY, shoreLocalY);

            float t = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(shoreDistance / ShoreTerrainFitMeters));
            return Mathf.Lerp(localY, shoreLocalY, t);
        }

        private struct SkirtEdge
        {
            internal int ax;
            internal int az;
            internal int bx;
            internal int bz;
            internal bool boundary;

            internal SkirtEdge(int ax, int az, int bx, int bz, bool boundary)
            {
                this.ax = ax;
                this.az = az;
                this.bx = bx;
                this.bz = bz;
                this.boundary = boundary;
            }
        }

        private float SampleSeafloorLocalY(
            float worldX,
            float worldZ,
            float oceanLocalY,
            float oceanThreshold,
            Vector3 terrainOrigin,
            float tileWorldSize,
            out float distanceToCoast,
            out float depth,
            out float climateBand)
        {
            distanceToCoast = tileData.GetDistanceToEdgeMeters(worldX, worldZ);
            float climateBase;
            tileData.GetBlendedClimate(worldX, worldZ, out climateBase, out climateBand);
            float noiseX, noiseZ;
            tileData.GetNoiseWorldCoords(worldX, worldZ, out noiseX, out noiseZ);
            depth = DeepBathymetry.SampleDepthMeters(noiseX, noiseZ, climateBase, distanceToCoast);
            float localX = worldX - terrainOrigin.x;
            float localZ = worldZ - terrainOrigin.z;
            float localY = FitSeafloorToShoreTerrain(
                oceanLocalY - depth,
                localX,
                localZ,
                oceanLocalY,
                oceanThreshold,
                distanceToCoast,
                terrainOrigin,
                tileWorldSize);
            depth = Mathf.Max(0f, oceanLocalY - localY);
            return localY;
        }

        private static Color CreateVertexColor(float depth, float climateBand, float distanceToCoast, float textureStrength = 1f)
        {
            float depthBand = Mathf.Sqrt(DeepBathymetry.DepthBand01(depth));
            float distanceBand = Mathf.Clamp01(distanceToCoast / DeepBathymetry.ShelfBreakDistance);
            return new Color(depthBand, climateBand, distanceBand, Mathf.Clamp01(textureStrength));
        }

        private static int EstimateWallVertexCapacity(bool[,] holes)
        {
            return EstimateWallEdgeCount(holes) * 4;
        }

        private static int EstimateWallTriangleCapacity(bool[,] holes)
        {
            return EstimateWallEdgeCount(holes) * 6;
        }

        private static int EstimateWallEdgeCount(bool[,] holes)
        {
            if (holes == null)
                return 0;

            int rows = holes.GetLength(0);
            int cols = holes.GetLength(1);
            int edges = 0;
            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < cols; x++)
                {
                    if (holes[z, x])
                        continue;

                    if (x > 0 && holes[z, x - 1]) edges++;
                    if (x < cols - 1 && holes[z, x + 1]) edges++;
                    if (z > 0 && holes[z - 1, x]) edges++;
                    if (z < rows - 1 && holes[z + 1, x]) edges++;
                }
            }

            return edges;
        }

        void OnDestroy()
        {
            TearDown();
        }

        private void EnsureMaterial()
        {
            var mr = GetComponent<MeshRenderer>();
            if (mr == null) return;

            int worldClimate = tileData != null ? tileData.BiomeClimateIndex :
				(dfTerrain != null ? dfTerrain.MapData.worldClimate : MapsFile.DefaultClimate);
            mr.sharedMaterial = DeepWaterFloorMaterial.GetMaterial(worldClimate);
            DeepWaterRendering.DisableShadows(mr);
        }

        private void EnsureCollider()
        {
            // Unity ?? operator can pass a "fake null" UnityObject through.
            // Use ==-based checks at every step so the overload runs.
            MeshCollider mc = meshCollider;
            if (mc == null)
                mc = GetComponent<MeshCollider>();
            if (mc == null)
                mc = gameObject.AddComponent<MeshCollider>();
            if (mc == null) return;

            meshCollider = mc;
            mc.enabled = false;
            mc.sharedMesh = null;
            mc.convex = false;
#if UNITY_2017_3_OR_NEWER
            // This mesh is generated from a regular grid with shared skirt
            // vertices, so Unity's general-purpose cleaning/welding pass is
            // redundant and expensive during terrain streaming.
            mc.cookingOptions = MeshColliderCookingOptions.None;
#if UNITY_2019_3_OR_NEWER
            mc.cookingOptions |= MeshColliderCookingOptions.UseFastMidphase;
#endif
#endif
            mc.sharedMesh = mesh;
            mc.enabled = true;
            colliderBuildVersion = buildVersion;

			// EnemyMotor uses raycasts for ground/fall checks, so the seafloor
			// must be visible to DefaultRaycastLayers. Shore helpers filter this
			// component explicitly when they need vanilla land.
			int defaultLayer = LayerMask.NameToLayer("Default");
			if (defaultLayer >= 0)
				gameObject.layer = defaultLayer;
        }
    }

	internal static class DeepWaterFloorMaterial
	{
		private const string ShaderName = "DeepWaters/Seafloor";
		// World-meters per texture tile. DFU terrain dirt tile reads as one
		// tile every ~6.4m of world space, so 1/6.4 = 0.15625.
		private const float TerrainTextureWorldScale = 0.15625f;
		private const float TerrainTextureStrength = 0.45f;
		private const float DfuGroundTextureStrength = 0.25f;
		private const float SwampGroundTextureStrength = 0.90f;
		private const float NightAmbientBoost = 0.72f;

		private static readonly int MainTexProperty = Shader.PropertyToID("_MainTex");
		private static readonly int SandColorProperty = Shader.PropertyToID("_SandColor");
		private static readonly int MidColorProperty = Shader.PropertyToID("_MidColor");
		private static readonly int DeepColorProperty = Shader.PropertyToID("_DeepColor");
		private static readonly int SwampColorProperty = Shader.PropertyToID("_SwampColor");
		private static readonly int TextureWorldScaleProperty = Shader.PropertyToID("_TextureWorldScale");
		private static readonly int TextureStrengthProperty = Shader.PropertyToID("_TextureStrength");
		private static readonly int AmbientBoostProperty = Shader.PropertyToID("_AmbientBoost");
		private static readonly Dictionary<int, Material> materials = new Dictionary<int, Material>();

		internal static void UpdateLighting()
		{
			float ambientBoost = Mathf.Lerp(NightAmbientBoost, 1f, DeepWaters.GetDaylightFactor());
			foreach (Material material in materials.Values)
			{
				if (material != null && material.HasProperty(AmbientBoostProperty))
					material.SetFloat(AmbientBoostProperty, ambientBoost);
			}
		}

		internal static Material GetMaterial(int worldClimate)
		{
			int textureArchive;
			int textureRecord;
			ResolveSeafloorTexture(worldClimate, out textureArchive, out textureRecord);
			int key = textureArchive * 1024 + textureRecord * 16 + worldClimate;
			Material material;
			if (materials.TryGetValue(key, out material) && material != null && material.shader != null)
				return material;

			Shader shader = ResolveShaderWithFallbacks();
			if (shader == null)
			{
				Debug.LogError("[DeepWaters] Could not resolve any shader for seafloor mesh.");
				return null;
			}

			try
			{
				material = new Material(shader);
				material.name = "DeepWaters.Seafloor." + textureArchive + "." + textureRecord + "." + worldClimate;
				bool dfuGroundTexture = UsesDfuGroundTexture(worldClimate);
				float textureStrength = worldClimate == (int)MapsFile.Climates.Swamp ? SwampGroundTextureStrength :
					(dfuGroundTexture ? DfuGroundTextureStrength : TerrainTextureStrength);
				ApplyRegionalTexture(material, textureArchive, textureRecord, textureStrength, TerrainTextureWorldScale);
				if (!dfuGroundTexture || worldClimate == (int)MapsFile.Climates.Swamp)
					ApplyBiomePalette(material, worldClimate);
				if (material.HasProperty(AmbientBoostProperty))
					material.SetFloat(AmbientBoostProperty, Mathf.Lerp(NightAmbientBoost, 1f, DeepWaters.GetDaylightFactor()));
				materials[key] = material;
			}
			catch (System.Exception ex)
			{
				Debug.LogError("[DeepWaters] Failed to construct seafloor material: " + ex.Message);
				material = null;
			}

			return material;
		}

		private static void ApplyBiomePalette(Material material, int worldClimate)
		{
			switch (worldClimate)
			{
				case (int)MapsFile.Climates.Subtropical:
				case (int)MapsFile.Climates.Rainforest:
					SetPalette(material,
						new Color(0.52f, 0.67f, 0.49f, 1f),
						new Color(0.24f, 0.42f, 0.32f, 1f),
						new Color(0.08f, 0.20f, 0.20f, 1f),
						new Color(0.16f, 0.32f, 0.22f, 1f));
					break;
				case (int)MapsFile.Climates.Swamp:
					SetPalette(material,
						new Color(0.38f, 0.36f, 0.21f, 1f),
						new Color(0.23f, 0.25f, 0.15f, 1f),
						new Color(0.08f, 0.12f, 0.09f, 1f),
						new Color(0.18f, 0.17f, 0.09f, 1f));
					break;
				case (int)MapsFile.Climates.Mountain:
				case (int)MapsFile.Climates.MountainWoods:
					SetPalette(material,
						new Color(0.58f, 0.63f, 0.61f, 1f),
						new Color(0.34f, 0.43f, 0.47f, 1f),
						new Color(0.12f, 0.18f, 0.24f, 1f),
						new Color(0.24f, 0.30f, 0.32f, 1f));
					break;
				case (int)MapsFile.Climates.Desert:
				case (int)MapsFile.Climates.Desert2:
					SetPalette(material,
						new Color(0.78f, 0.65f, 0.42f, 1f),
						new Color(0.50f, 0.39f, 0.22f, 1f),
						new Color(0.23f, 0.18f, 0.13f, 1f),
						new Color(0.45f, 0.34f, 0.18f, 1f));
					break;
				case (int)MapsFile.Climates.Woodlands:
				case (int)MapsFile.Climates.HauntedWoodlands:
					SetPalette(material,
						new Color(0.58f, 0.62f, 0.45f, 1f),
						new Color(0.33f, 0.39f, 0.28f, 1f),
						new Color(0.13f, 0.18f, 0.15f, 1f),
						new Color(0.24f, 0.30f, 0.19f, 1f));
					break;
				default:
					SetPalette(material,
						new Color(0.30f, 0.42f, 0.43f, 1f),
						new Color(0.16f, 0.23f, 0.26f, 1f),
						new Color(0.06f, 0.08f, 0.11f, 1f),
						new Color(0.12f, 0.18f, 0.16f, 1f));
					break;
			}
		}

		private static void SetPalette(Material material, Color sand, Color mid, Color deep, Color swamp)
		{
			material.SetColor(SandColorProperty, sand);
			material.SetColor(MidColorProperty, mid);
			material.SetColor(DeepColorProperty, deep);
			material.SetColor(SwampColorProperty, swamp);
		}

		private static void ResolveSeafloorTexture(int worldClimate, out int archive, out int record)
		{
			if (UsesDfuGroundTexture(worldClimate))
			{
				archive = ResolveGroundArchive(worldClimate);
				record = 1;
				return;
			}

			switch (worldClimate)
			{
				case (int)MapsFile.Climates.Subtropical:
				case (int)MapsFile.Climates.Rainforest:
					archive = 402; record = 28; return;
				case (int)MapsFile.Climates.Mountain:
				case (int)MapsFile.Climates.MountainWoods:
					archive = 102; record = 3; return;
				case (int)MapsFile.Climates.Desert:
				case (int)MapsFile.Climates.Desert2:
					archive = 2; record = 10; return;
				case (int)MapsFile.Climates.HauntedWoodlands:
					archive = 302; record = 3; return;
				case (int)MapsFile.Climates.Woodlands:
					archive = 302; record = 25; return;
				default:
					DFLocation.ClimateSettings climate = MapsFile.GetWorldClimateSettings(worldClimate);
					archive = climate.GroundArchive;
					record = 1;
					return;
			}
		}

		private static bool UsesDfuGroundTexture(int worldClimate)
		{
			return worldClimate == (int)MapsFile.Climates.Ocean ||
				worldClimate == (int)MapsFile.Climates.Swamp;
		}

		private static int ResolveGroundArchive(int worldClimate)
		{
			DFLocation.ClimateSettings climate = MapsFile.GetWorldClimateSettings(worldClimate);
			int groundArchive = climate.GroundArchive;

			if (climate.ClimateType != DFLocation.ClimateBaseType.Desert &&
				DaggerfallUnity.Instance != null &&
				DaggerfallUnity.Instance.WorldTime != null &&
				DaggerfallUnity.Instance.WorldTime.Now.SeasonValue == DaggerfallDateTime.Seasons.Winter)
			{
				groundArchive++;
			}

			return groundArchive;
		}

		private static void ApplyRegionalTexture(Material material, int textureArchive, int textureRecord, float textureStrength, float textureWorldScale)
		{
			if (material == null || DaggerfallUnity.Instance == null || DaggerfallUnity.Instance.MaterialReader == null)
				return;

			try
			{
				Texture2D texture = DaggerfallUnity.Instance.MaterialReader.TextureReader.GetTexture2D(
					textureArchive,
					textureRecord,
					0);

				if (texture == null)
					return;

				texture.wrapMode = TextureWrapMode.Repeat;
				texture.filterMode = FilterMode.Point;

				material.SetTexture(MainTexProperty, texture);
				material.SetFloat(TextureWorldScaleProperty, textureWorldScale);
				material.SetFloat(TextureStrengthProperty, textureStrength);
			}
			catch (System.Exception ex)
			{
				Debug.LogWarning("[DeepWaters] Failed to load regional seafloor texture " + textureArchive + ":" + textureRecord + ": " + ex.Message);
			}
		}

		private static Shader ResolveShaderWithFallbacks()
		{
			Shader shader = TryLoadShader();
			if (shader != null)
				return shader;

			string[] fallbacks =
			{
				"Legacy Shaders/Diffuse",
				"Diffuse",
				"Standard",
				"Unlit/Texture",
				"Unlit/Color",
			};
			for (int i = 0; i < fallbacks.Length; i++)
			{
				shader = Shader.Find(fallbacks[i]);
				if (shader != null)
				{
					Debug.LogWarning("[DeepWaters] Seafloor shader unavailable; using fallback " + fallbacks[i]);
					return shader;
				}
			}

			return null;
		}

		private static Shader TryLoadShader()
		{
			if (DeepWaters.Mod != null)
			{
				Shader shader = DeepWaters.Mod.GetAsset<Shader>("DeepWaterSeafloor");
				if (shader != null)
					return shader;

				shader = DeepWaters.Mod.GetAsset<Shader>("DeepWaterSeafloor.shader");
				if (shader != null)
					return shader;
			}

			return Shader.Find(ShaderName);
		}
	}
}
