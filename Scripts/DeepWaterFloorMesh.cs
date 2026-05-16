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
        // 33x33 = 1089 verts, 2048 tris per tile. Cell spacing ~25.6m on an
        // 819m tile. Resolves the bathymetric shape without exploding the
        // MeshCollider rebuild cost across the streaming radius.
        public const int VertexGridSize = 33;
        private const float WallMinimumDrop = 0.25f;
        private const float ShoreWallSurfaceInset = 1.25f;
        private const float ShoreWallBottomOverlap = 0.10f;
        private const float ShoreWallTopTextureStrength = 0f;

        private Mesh mesh;
        private MeshCollider meshCollider;
        private DeepWaterTileData tileData;
        private DaggerfallTerrain dfTerrain;

        // Cache of the vertex grid local Y values. Decoration placement
        // samples this instead of the raw bathymetry function so it lands
        // ON the rendered mesh surface rather than on the higher-frequency
        // function value that the mesh's linear interpolation misses.
        private float[,] vertexLocalY;
        private float tileWorldSizeCached;

        // Diagnostic counters captured per Build call, logged at the end.
        // The user runs the game with this and shares the Player.log so we
        // can verify wall/skirt geometry is actually being generated.
        public static bool DiagnosticLogging = true;
        public static int DiagnosticTilesBuilt = 0;
        private int diagWithinTileWalls;
        private int diagBoundarySkirts;
        private int diagSkippedShortWalls;

        public void Build(DaggerfallTerrain owner, DeepWaterTileData tile, float oceanLocalY, bool[,] holes)
        {
            dfTerrain = owner;
            tileData = tile;
            diagWithinTileWalls = 0;
            diagBoundarySkirts = 0;
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

            float cellSpacing = tileWorldSize / (n - 1);
            int climateIndex = tile.ClimateIndex;
            float climateBand = ClimateBandSignal(climateIndex);

            for (int z = 0; z < n; z++)
            {
                float localZ = z * cellSpacing;
                float worldZ = terrainOrigin.z + localZ;
                for (int x = 0; x < n; x++)
                {
                    float localX = x * cellSpacing;
                    float worldX = terrainOrigin.x + localX;

                    float distanceToCoast = tile.GetDistanceToCoastMeters(worldX, worldZ);
                    float depth = DeepBathymetry.SampleDepthMeters(worldX, worldZ, climateIndex, distanceToCoast);
                    float localY = oceanLocalY - depth;

                    vertices.Add(new Vector3(localX, localY, localZ));
                    vertexLocalY[z, x] = localY;

                    colors.Add(CreateVertexColor(depth, climateBand, distanceToCoast));

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

            AppendHoleEdgeWalls(
                holes,
                vertices,
                colors,
                uvs,
                triangles,
                oceanLocalY,
                terrainOrigin,
                tileWorldSize,
                climateIndex,
                climateBand);

            AppendTileBoundarySkirt(
                holes,
                vertices,
                colors,
                uvs,
                triangles,
                oceanLocalY,
                terrainOrigin,
                tileWorldSize,
                climateIndex,
                climateBand);

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
                    " boundarySkirts=" + diagBoundarySkirts +
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

        private void AppendHoleEdgeWalls(
            bool[,] holes,
            List<Vector3> vertices,
            List<Color> colors,
            List<Vector2> uvs,
            List<int> triangles,
            float oceanLocalY,
            Vector3 terrainOrigin,
            float tileWorldSize,
            int climateIndex,
            float climateBand)
        {
            if (holes == null || tileData == null)
                return;

            int rows = holes.GetLength(0);
            int cols = holes.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return;

            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < cols; x++)
                {
                    if (holes[z, x])
                        continue;

                    if (x > 0 && holes[z, x - 1] &&
                        AddWallSegment(x, z, x, z + 1, cols, rows, vertices, colors, uvs, triangles, oceanLocalY, terrainOrigin, tileWorldSize, climateIndex, climateBand))
                        diagWithinTileWalls++;

                    if (x < cols - 1 && holes[z, x + 1] &&
                        AddWallSegment(x + 1, z + 1, x + 1, z, cols, rows, vertices, colors, uvs, triangles, oceanLocalY, terrainOrigin, tileWorldSize, climateIndex, climateBand))
                        diagWithinTileWalls++;

                    if (z > 0 && holes[z - 1, x] &&
                        AddWallSegment(x + 1, z, x, z, cols, rows, vertices, colors, uvs, triangles, oceanLocalY, terrainOrigin, tileWorldSize, climateIndex, climateBand))
                        diagWithinTileWalls++;

                    if (z < rows - 1 && holes[z + 1, x] &&
                        AddWallSegment(x, z + 1, x + 1, z + 1, cols, rows, vertices, colors, uvs, triangles, oceanLocalY, terrainOrigin, tileWorldSize, climateIndex, climateBand))
                        diagWithinTileWalls++;
                }
            }
        }

        /// <summary>
        /// Tile-boundary skirt: each tile drops a wall from ocean surface to its
        /// own seafloor depth at every edge cell that is a hole. This closes
        /// the cross-tile gap noted in the README's known limitations — where
        /// a per-tile distance-to-coast BFS makes adjacent tiles disagree on
        /// depth at their shared edge, leaving a visible void in the seafloor.
        /// Both tiles emitting the skirt is intentional; the overlapping upper
        /// portions z-fight invisibly because they sample the same vertex
        /// color signal, and the lower stretch is covered by whichever tile
        /// sampled the deeper depth.
        /// </summary>
        private void AppendTileBoundarySkirt(
            bool[,] holes,
            List<Vector3> vertices,
            List<Color> colors,
            List<Vector2> uvs,
            List<int> triangles,
            float oceanLocalY,
            Vector3 terrainOrigin,
            float tileWorldSize,
            int climateIndex,
            float climateBand)
        {
            if (holes == null || tileData == null)
                return;

            int rows = holes.GetLength(0);
            int cols = holes.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return;

            // DFU's TerrainData.SetHoles convention: holes[i,j] == true means
            // SOLID terrain, holes[i,j] == false means a HOLE (terrain removed,
            // seafloor mesh visible). We want walls where the seafloor mesh is
            // visible — i.e. where holes[i,j] == false — so adjacent tiles'
            // mesh-edge mismatches at that world position get a wall to bridge
            // the void. Skip the *terrain* cells, process the *hole* cells.

            // z = 0 edge (south face of tile)
            for (int x = 0; x < cols; x++)
            {
                if (holes[0, x]) continue;  // skip terrain cells
                if (AddWallSegment(x + 1, 0, x, 0, cols, rows, vertices, colors, uvs, triangles, oceanLocalY, terrainOrigin, tileWorldSize, climateIndex, climateBand, isBoundarySkirt: true))
                    diagBoundarySkirts++;
            }

            // z = rows-1 edge (north face of tile, wall sits at z=rows)
            for (int x = 0; x < cols; x++)
            {
                if (holes[rows - 1, x]) continue;
                if (AddWallSegment(x, rows, x + 1, rows, cols, rows, vertices, colors, uvs, triangles, oceanLocalY, terrainOrigin, tileWorldSize, climateIndex, climateBand, isBoundarySkirt: true))
                    diagBoundarySkirts++;
            }

            // x = 0 edge (west face of tile)
            for (int z = 0; z < rows; z++)
            {
                if (holes[z, 0]) continue;
                if (AddWallSegment(0, z, 0, z + 1, cols, rows, vertices, colors, uvs, triangles, oceanLocalY, terrainOrigin, tileWorldSize, climateIndex, climateBand, isBoundarySkirt: true))
                    diagBoundarySkirts++;
            }

            // x = cols-1 edge (east face of tile, wall sits at x=cols)
            for (int z = 0; z < rows; z++)
            {
                if (holes[z, cols - 1]) continue;
                if (AddWallSegment(cols, z + 1, cols, z, cols, rows, vertices, colors, uvs, triangles, oceanLocalY, terrainOrigin, tileWorldSize, climateIndex, climateBand, isBoundarySkirt: true))
                    diagBoundarySkirts++;
            }
        }

        // Boundary-skirt walls extend down to the climate-base depth rather
        // than the locally-sampled depth, so they always reach deep enough
        // to cover any cross-tile mesh elevation mismatch.
        private bool AddWallSegment(
            int ax,
            int az,
            int bx,
            int bz,
            int cols,
            int rows,
            List<Vector3> vertices,
            List<Color> colors,
            List<Vector2> uvs,
            List<int> triangles,
            float oceanLocalY,
            Vector3 terrainOrigin,
            float tileWorldSize,
            int climateIndex,
            float climateBand,
            bool isBoundarySkirt = false)
        {
            float localAX = (ax / (float)cols) * tileWorldSize;
            float localAZ = (az / (float)rows) * tileWorldSize;
            float localBX = (bx / (float)cols) * tileWorldSize;
            float localBZ = (bz / (float)rows) * tileWorldSize;

            float worldAX = terrainOrigin.x + localAX;
            float worldAZ = terrainOrigin.z + localAZ;
            float worldBX = terrainOrigin.x + localBX;
            float worldBZ = terrainOrigin.z + localBZ;

            float distanceA;
            float depthA;
            float bottomA = SampleSeafloorLocalY(worldAX, worldAZ, oceanLocalY, climateIndex, out distanceA, out depthA);
            float distanceB;
            float depthB;
            float bottomB = SampleSeafloorLocalY(worldBX, worldBZ, oceanLocalY, climateIndex, out distanceB, out depthB);

            // Boundary skirts bridge the cross-tile mesh-edge gap. Their TOP
            // sits at this tile's own seafloor mesh edge. Within-tile walls
            // (the buffer-to-hole shoreline cliff) pull their BOTTOM to the
            // mesh edge so the wall meets the seafloor mesh exactly. Their TOP
            // is kept below the water plane so the opaque wall cannot draw a
            // bright contour exactly on the transparent surface.
            float topAY = oceanLocalY;
            float topBY = oceanLocalY;
            float topDepthA = 0f;
            float topDepthB = 0f;

            if (!isBoundarySkirt)
            {
                float meshBottomA, meshBottomB;
                if (TrySampleMeshLocalY(worldAX, worldAZ, out meshBottomA))
                {
                    bottomA = meshBottomA;
                    depthA = Mathf.Max(0f, oceanLocalY - meshBottomA);
                }
                if (TrySampleMeshLocalY(worldBX, worldBZ, out meshBottomB))
                {
                    bottomB = meshBottomB;
                    depthB = Mathf.Max(0f, oceanLocalY - meshBottomB);
                }
                // Slight overlap below the mesh edge so any sub-millimetre FP
                // seam between wall bottom and mesh top is invisibly covered.
                bottomA -= ShoreWallBottomOverlap;
                bottomB -= ShoreWallBottomOverlap;
                depthA = Mathf.Max(0f, oceanLocalY - bottomA);
                depthB = Mathf.Max(0f, oceanLocalY - bottomB);

                // The old top=oceanY wall was the white shoreline isoline:
                // an opaque seafloor edge coplanar with a transparent water
                // plane, amplified by winter terrain textures. Start the wall
                // below the surface instead; very shallow spans naturally skip
                // via WallMinimumDrop below.
                topAY = oceanLocalY - ShoreWallSurfaceInset;
                topBY = oceanLocalY - ShoreWallSurfaceInset;
                topDepthA = ShoreWallSurfaceInset;
                topDepthB = ShoreWallSurfaceInset;
            }

            if (isBoundarySkirt)
            {
                // Critical: the wall top must align with the seafloor mesh
                // EDGE, not with DeepBathymetry's sampled depth. The mesh is
                // 33×33 (cell spacing ~25.6m) but the wall is iterated per
                // heightmap cell (~6.4m), and DeepBathymetry includes 24m and
                // 330m noise layers. Between two mesh vertices, the function
                // can swing several metres away from the linear interpolation
                // the mesh actually renders. Using the function value here
                // means the wall top alternately pokes through or sits below
                // the mesh edge. TrySampleMeshLocalY interpolates the actual
                // mesh vertices, so wall top = mesh edge exactly.
                float meshTopA, meshTopB;
                float adjustedTopAY = TrySampleMeshLocalY(worldAX, worldAZ, out meshTopA)
                    ? meshTopA : bottomA;
                float adjustedTopBY = TrySampleMeshLocalY(worldBX, worldBZ, out meshTopB)
                    ? meshTopB : bottomB;

                topDepthA = Mathf.Max(0f, oceanLocalY - adjustedTopAY);
                topDepthB = Mathf.Max(0f, oceanLocalY - adjustedTopBY);

                // Small overlap so any sub-millimetre seam between wall top
                // and mesh edge is still covered.
                const float overlapMeters = 0.25f;
                topAY = adjustedTopAY + overlapMeters;
                topBY = adjustedTopBY + overlapMeters;

                // Wall bottom: extend at most a fixed distance below the
                // mesh top to cover residual cross-tile mismatches without
                // creating unnaturally tall walls. With cross-tile BFS the
                // typical mismatch is a few metres; 18m is comfortable
                // headroom for noise and FP precision without the wall
                // visibly poking above the seafloor at coastal depths.
                // If the locally-sampled bottom is already deeper than this
                // cap (rare — happens only when noise produces a large
                // function value off the mesh edge), use that instead.
                const float maxSkirtExtension = 18f;
                float skirtBottomY = Mathf.Min(adjustedTopAY, adjustedTopBY) - maxSkirtExtension;
                bottomA = Mathf.Min(bottomA, skirtBottomY);
                bottomB = Mathf.Min(bottomB, skirtBottomY);
                depthA = Mathf.Max(0f, oceanLocalY - bottomA);
                depthB = Mathf.Max(0f, oceanLocalY - bottomB);
            }

            float verticalSpan = Mathf.Min(topAY, topBY) - Mathf.Max(bottomA, bottomB);
            // Boundary skirts accept very short spans (down to 1cm) because
            // even a tiny gap between adjacent meshes can let the player slip
            // through into the void. Within-tile walls keep the 0.25m floor.
            float minDrop = isBoundarySkirt ? 0.01f : WallMinimumDrop;
            if (verticalSpan < minDrop)
            {
                diagSkippedShortWalls++;
                return false;
            }

            int start = vertices.Count;
            vertices.Add(new Vector3(localAX, topAY, localAZ));
            vertices.Add(new Vector3(localBX, topBY, localBZ));
            vertices.Add(new Vector3(localBX, bottomB, localBZ));
            vertices.Add(new Vector3(localAX, bottomA, localAZ));

            // Within-tile walls keep their upper edge visually muted because
            // they live close to the shore buffer and can otherwise read as a
            // drawn waterline from above. Boundary skirts already sit
            // underwater throughout, so their top inherits the tile's local
            // depth/texture signal and matches the seafloor edge they bridge.
            float topTextureStrength = isBoundarySkirt ? 1f : ShoreWallTopTextureStrength;
            colors.Add(CreateVertexColor(topDepthA, climateBand, distanceA, topTextureStrength));
            colors.Add(CreateVertexColor(topDepthB, climateBand, distanceB, topTextureStrength));
            colors.Add(CreateVertexColor(depthB, climateBand, distanceB));
            colors.Add(CreateVertexColor(depthA, climateBand, distanceA));

            // Use natural along-wall × depth UV for all walls. A flat
            // (localX, localZ) UV stretches one texel column vertically
            // across the wall — that's the "vertical streak" appearance
            // visible on the shoreline cliff. One axis runs along the
            // wall's length, the other along its depth. The regional
            // terrain texture (sampled at uv * _TextureWorldScale by the
            // seafloor shader) tiles correctly in both directions.
            float uvAlongA, uvAlongB;
            if (Mathf.Abs(localAX - localBX) > Mathf.Abs(localAZ - localBZ))
            {
                uvAlongA = localAX;
                uvAlongB = localBX;
            }
            else
            {
                uvAlongA = localAZ;
                uvAlongB = localBZ;
            }
            uvs.Add(new Vector2(uvAlongA, -topAY));
            uvs.Add(new Vector2(uvAlongB, -topBY));
            uvs.Add(new Vector2(uvAlongB, -bottomB));
            uvs.Add(new Vector2(uvAlongA, -bottomA));

            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);

            if (isBoundarySkirt)
            {
                // Reverse-winding pair so the wall collides from both sides.
                // Cull Off in the shader means rendering is unchanged — the
                // duplicate triangles only matter for PhysX collision, which
                // treats each triangle's front face as the solid side. With
                // both windings present, the wall blocks the player whether
                // they approach from the seam's east or west.
                triangles.Add(start);
                triangles.Add(start + 2);
                triangles.Add(start + 1);
                triangles.Add(start);
                triangles.Add(start + 3);
                triangles.Add(start + 2);
            }
            return true;
        }

        private float SampleSeafloorLocalY(
            float worldX,
            float worldZ,
            float oceanLocalY,
            int climateIndex,
            out float distanceToCoast,
            out float depth)
        {
            distanceToCoast = tileData.GetDistanceToCoastMeters(worldX, worldZ);
            depth = DeepBathymetry.SampleDepthMeters(worldX, worldZ, climateIndex, distanceToCoast);
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

            // Boundary skirt segments (one per edge cell that is a hole).
            // DFU convention: holes[i,j] == false IS a hole.
            for (int x = 0; x < cols; x++)
            {
                if (!holes[0, x]) edges++;
                if (!holes[rows - 1, x]) edges++;
            }
            for (int z = 0; z < rows; z++)
            {
                if (!holes[z, 0]) edges++;
                if (!holes[z, cols - 1]) edges++;
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
            mc.sharedMesh = null;
            mc.convex = false;
            mc.sharedMesh = mesh;

            // Push to Ignore Raycast so the shore-exit-assist raycast still
            // finds vanilla shore terrain first. The seafloor remains
            // physically present for swim/fall collision via the trigger
            // path that doesn't filter by layer.
            int ignoreRaycastLayer = LayerMask.NameToLayer("Ignore Raycast");
            if (ignoreRaycastLayer >= 0)
                gameObject.layer = ignoreRaycastLayer;
        }

        private static float ClimateBandSignal(int climateIndex)
        {
            switch (climateIndex)
            {
                case (int)MapsFile.Climates.Ocean:            return 1.00f;
                case (int)MapsFile.Climates.Subtropical:      return 0.70f;
                case (int)MapsFile.Climates.Rainforest:       return 0.55f;
                case (int)MapsFile.Climates.Swamp:            return 0.15f;
                case (int)MapsFile.Climates.Woodlands:        return 0.60f;
                case (int)MapsFile.Climates.HauntedWoodlands: return 0.45f;
                case (int)MapsFile.Climates.MountainWoods:    return 0.65f;
                case (int)MapsFile.Climates.Mountain:         return 0.65f;
                case (int)MapsFile.Climates.Desert:           return 0.30f;
                case (int)MapsFile.Climates.Desert2:          return 0.30f;
                default:                                      return 0.80f;
            }
        }
    }
}
