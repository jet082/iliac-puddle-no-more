// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
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
    public class DeepWaterFloorMesh : MonoBehaviour
    {
        public const int VertexGridSize = 33;
        private const float WallMinimumDrop = 0.25f;
        private const float ShoreWallSurfaceInset = 0.20f;
        private const float ShoreWallBottomOverlap = 0.10f;
        private const float ShoreWallTopTextureStrength = 0.35f;
        // Continuous shore-skirt tuning (v0.55.36). The skirt bridges the deep
        // carved floor to the shallow vanilla coast as ONE gap-free surface
        // (shared per-perimeter-vertex bottoms), with a run that widens as the
        // drop deepens so it grades in like natural seabed instead of standing
        // up as a wall.
        private const float SkirtSlopeTangent = 0.6f;
        private const float SkirtMinWidthMeters = 6.0f;
        private const float SkirtMaxWidthMeters = 40.0f;

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

        // Diagnostic counters captured per Build call, logged at the end.
        // The user runs the game with this and shares the Player.log so we
        // can verify shore wall geometry is actually being generated.
        public static bool DiagnosticLogging = false;
        public static int DiagnosticTilesBuilt = 0;
        private int diagWithinTileWalls;
        private int diagBoundaryWalls;
        private int diagSkippedShortWalls;

        public int BuildVersion
        {
            get { return buildVersion; }
        }

        public int BuiltMapPixelX { get; private set; }
        public int BuiltMapPixelY { get; private set; }

        public bool HasValidRuntimeCollider
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
        public float[,] LastBuiltHeightmapSamples { get; private set; }

        public void Build(DaggerfallTerrain owner, DeepWaterTileData tile, float oceanLocalY, bool[,] holes)
        {
            dfTerrain = owner;
            tileData = tile;
            buildVersion++;
            BuiltMapPixelX = owner != null ? owner.MapPixelX : int.MinValue;
            BuiltMapPixelY = owner != null ? owner.MapPixelY : int.MinValue;
            LastBuiltHeightmapSamples = owner != null ? owner.MapData.heightmapSamples : null;
            diagWithinTileWalls = 0;
            diagBoundaryWalls = 0;
            diagSkippedShortWalls = 0;

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

            if (DiagnosticLogging)
            {
                DiagnosticTilesBuilt++;
                // DFU convention: holes[z,x] == false IS a hole. Count those.
                int holeCount = 0;
                int rows = holes != null ? holes.GetLength(0) : 0;
                int cols = holes != null ? holes.GetLength(1) : 0;
                for (int z = 0; z < rows; z++)
                    for (int x = 0; x < cols; x++)
                        if (!holes[z, x]) holeCount++;

                Debug.Log(
                    "[DeepWaters.Mesh] tile=(" + owner.MapPixelX + "," + owner.MapPixelY + ")" +
                    " climate=" + tile.ClimateIndex +
                    " oceanY=" + oceanLocalY.ToString("F2") +
                    " holes=" + holeCount + "/" + (rows * cols) +
                    " withinWalls=" + diagWithinTileWalls +
                    " boundaryWalls=" + diagBoundaryWalls +
                    " skippedShort=" + diagSkippedShortWalls +
                    " meshVerts=" + mesh.vertexCount +
                    " meshTris=" + (mesh.triangles.Length / 3));
            }

        }

        public void TearDown()
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
        public bool TrySampleMeshLocalY(float worldX, float worldZ, out float localY)
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

        public void EnsureRuntimeCollider()
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

            float fracX = (quadX + 0.5f) / quadResolution;
            float fracZ = (quadZ + 0.5f) / quadResolution;
            int hx = Mathf.Clamp(Mathf.FloorToInt(fracX * cols), 0, cols - 1);
            int hz = Mathf.Clamp(Mathf.FloorToInt(fracZ * rows), 0, rows - 1);
            return !holes[hz, hx];
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

                    if (x > 0 && holes[z, x - 1]) { edges.Add(new SkirtEdge(x, z, x, z + 1)); diagWithinTileWalls++; }
                    if (x < cols - 1 && holes[z, x + 1]) { edges.Add(new SkirtEdge(x + 1, z + 1, x + 1, z)); diagWithinTileWalls++; }
                    if (z > 0 && holes[z - 1, x]) { edges.Add(new SkirtEdge(x + 1, z, x, z)); diagWithinTileWalls++; }
                    if (z < rows - 1 && holes[z + 1, x]) { edges.Add(new SkirtEdge(x, z + 1, x + 1, z + 1)); diagWithinTileWalls++; }

                    if (x == 0 && !IsBakedHoleAcrossBoundary(x, z, -1, 0, cols, rows, terrainOrigin, tileWorldSize)) { edges.Add(new SkirtEdge(x, z, x, z + 1)); diagBoundaryWalls++; }
                    if (x == cols - 1 && !IsBakedHoleAcrossBoundary(x, z, 1, 0, cols, rows, terrainOrigin, tileWorldSize)) { edges.Add(new SkirtEdge(x + 1, z + 1, x + 1, z)); diagBoundaryWalls++; }
                    if (z == 0 && !IsBakedHoleAcrossBoundary(x, z, 0, -1, cols, rows, terrainOrigin, tileWorldSize)) { edges.Add(new SkirtEdge(x + 1, z, x, z)); diagBoundaryWalls++; }
                    if (z == rows - 1 && !IsBakedHoleAcrossBoundary(x, z, 0, 1, cols, rows, terrainOrigin, tileWorldSize)) { edges.Add(new SkirtEdge(x, z + 1, x + 1, z + 1)); diagBoundaryWalls++; }
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
                    vertices, colors, uvs, oceanLocalY, skirtTopY, oceanThreshold, terrainOrigin, tileWorldSize);
                EnsureSkirtVertex(e.bx, e.bz, stride, cols, rows, inwardNormals, topIndex, bottomIndex,
                    vertices, colors, uvs, oceanLocalY, skirtTopY, oceanThreshold, terrainOrigin, tileWorldSize);
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
                    diagSkippedShortWalls++;
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

            // Phase B path (v4 bake with fine mask): the neighbor carves
            // when the global bake says the cross-boundary sample is carved
            // water. Do not depend on the neighboring DaggerfallTerrain being
            // promoted or having DeepWaterTileData initialized yet: streaming
            // order made that test fail transiently, permanently baking giant
            // map-pixel perimeter walls into whichever tile built first.
            if (DeepWaterDistanceBake.HasFineWaterMask)
            {
                return tileData.IsCarvedWater(sampleX, sampleZ);
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
            float tileWorldSize)
        {
            int key = vz * stride + vx;
            if (topIndex.ContainsKey(key))
                return;

            float localX = (vx / (float)cols) * tileWorldSize;
            float localZ = (vz / (float)rows) * tileWorldSize;

            // Pin the skirt top to the ACTUAL vanilla terrain height here (which
            // Interesting Terrains often carves well below sea level), not the
            // water surface — otherwise the skirt floats above the uncarved
            // terrain edge and you see a gap straight along the shoreline. The
            // min() keeps it from poking above the just-under-surface cap.
            float vanillaTopY = SampleVanillaLocalY(vx / (float)cols, vz / (float)rows, oceanLocalY, oceanThreshold);
            float topY = Mathf.Min(skirtTopY, vanillaTopY);

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
            float seafloorTop = SampleSeafloorLocalY(worldX, worldZ, oceanLocalY, out topDistance, out topDepthUnused, out topClimateBand);
            float meshTop;
            if (TrySampleMeshLocalY(worldX, worldZ, out meshTop))
                seafloorTop = meshTop;
            float drop = Mathf.Max(0f, topY - seafloorTop);

            float skirtWidth = Mathf.Clamp(drop / SkirtSlopeTangent, SkirtMinWidthMeters, SkirtMaxWidthMeters);

            float bottomLocalX = Mathf.Clamp(localX + inward.x * skirtWidth, 0f, tileWorldSize);
            float bottomLocalZ = Mathf.Clamp(localZ + inward.y * skirtWidth, 0f, tileWorldSize);
            float bottomWorldX = terrainOrigin.x + bottomLocalX;
            float bottomWorldZ = terrainOrigin.z + bottomLocalZ;

            float bottomDistance, bottomDepth, bottomClimateBand;
            float seafloorBottom = SampleSeafloorLocalY(bottomWorldX, bottomWorldZ, oceanLocalY, out bottomDistance, out bottomDepth, out bottomClimateBand);
            float meshBottom;
            if (TrySampleMeshLocalY(bottomWorldX, bottomWorldZ, out meshBottom))
                seafloorBottom = meshBottom;
            float bottomY = seafloorBottom - ShoreWallBottomOverlap;
            bottomDepth = Mathf.Max(0f, oceanLocalY - bottomY);

            topIndex[key] = vertices.Count;
            vertices.Add(new Vector3(localX, topY, localZ));
            colors.Add(CreateVertexColor(Mathf.Max(0f, oceanLocalY - topY), topClimateBand, topDistance, ShoreWallTopTextureStrength));
            uvs.Add(new Vector2(localX, localZ));

            bottomIndex[key] = vertices.Count;
            vertices.Add(new Vector3(bottomLocalX, bottomY, bottomLocalZ));
            colors.Add(CreateVertexColor(bottomDepth, bottomClimateBand, bottomDistance));
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

        private struct SkirtEdge
        {
            public int ax;
            public int az;
            public int bx;
            public int bz;

            public SkirtEdge(int ax, int az, int bx, int bz)
            {
                this.ax = ax;
                this.az = az;
                this.bx = bx;
                this.bz = bz;
            }
        }

        private float SampleSeafloorLocalY(
            float worldX,
            float worldZ,
            float oceanLocalY,
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
            return oceanLocalY - depth;
        }

        private static Color CreateVertexColor(float depth, float climateBand, float distanceToCoast, float textureStrength = 1f)
        {
            float depthBand = DeepBathymetry.DepthBand01(depth);
            float distanceBand = Mathf.Clamp01(distanceToCoast / DeepBathymetry.ShelfRampMeters);
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

            int worldClimate = dfTerrain != null ? dfTerrain.MapData.worldClimate : MapsFile.DefaultClimate;
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

            // Push to Ignore Raycast so the shore-exit-assist raycast still
            // finds vanilla shore terrain first. The seafloor remains
            // physically present for swim/fall collision via the trigger
            // path that doesn't filter by layer.
            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycastLayer >= 0)
                gameObject.layer = ignoreRaycastLayer;
        }
    }
}
