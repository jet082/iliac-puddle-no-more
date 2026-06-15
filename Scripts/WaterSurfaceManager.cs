// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Per-terrain visible water surface. The mesh is clipped to the same
    /// local-water classification used for seabed holes and swimming.
    ///
    /// Water uses generated per-tile meshes and shared custom materials for
    /// every terrain tile. The top and underside are separate renderers so
    /// above-water transparency cannot be overridden by underwater behavior.
    /// </summary>
    public static class WaterSurfaceManager
    {
        private const string VisualChildName = "DeepWaters_Surface";
        private const string TopSurfaceChildName = "DeepWaters_Surface_Top";
        private const string UndersideSurfaceChildName = "DeepWaters_Surface_Underside";
        private const string GeneratedMeshName = "DeepWaters.SurfaceMesh";
        private static readonly bool RenderFullWaterTileSurface = true;
        private const int SurfaceGridResolution = 128;
        private const int ShorelineSeedScanCells = 32;
        private const int ShorelineSurfaceFeatherCells = 4;
        private const float SurfaceRenderYOffset = 0.03f;

        private static bool installed;

        public static void Install()
        {
            if (installed)
                return;

            DaggerfallTerrain.OnPromoteTerrainData += HandlePromote;
            installed = true;
        }

        public static void RefreshLoadedSurfaces()
        {
            DaggerfallTerrain[] terrains = Object.FindObjectsOfType<DaggerfallTerrain>();
            for (int i = 0; i < terrains.Length; i++)
                RefreshLoadedSurface(terrains[i]);
        }

        public static void RefreshLoadedSurface(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null)
                return;

            Terrain terrain = dfTerrain.GetComponent<Terrain>();
            if (terrain != null && terrain.terrainData != null)
                HandlePromoteCore(dfTerrain, terrain.terrainData, false);
        }

        private static void HandlePromote(DaggerfallTerrain sender, TerrainData terrainData)
        {
            HandlePromoteCore(sender, terrainData, true);
        }

        // The genuine promote event is the safe pre-first-render window to build
        // the surface even while terrain is streaming, so it must NOT be gated on
        // CanMutateTerrainData — otherwise tiles streamed in as the player swims
        // get a carved seabed but no water surface above (gaps in the ceiling).
        // Only the forced refresh of already-live terrains is gated. The surface
        // build never mutates terrainData; it only adds a child mesh renderer.
        private static void HandlePromoteCore(DaggerfallTerrain sender, TerrainData terrainData, bool fromPromoteEvent)
        {
            System.Diagnostics.Stopwatch profile = DeepWaterRuntime.StartProfile();
            try
            {
                if (sender == null || terrainData == null)
                    return;

                if (!fromPromoteEvent && !DeepWaterRuntime.CanMutateTerrainData)
                    return;

                if (DeepWaters.Instance == null ||
                    !DeepWaters.Instance.SpawnWaterSurfaces ||
                    !ShouldHaveSurface(sender))
                {
                    RemoveExisting(sender);
                    return;
                }

                Mesh surfaceMesh = BuildSurfaceMesh(sender, terrainData);
                if (surfaceMesh == null)
                {
                    RemoveExisting(sender);
                    return;
                }

                EnsureVisibleSurface(sender, terrainData, surfaceMesh);
            }
            finally
            {
                DeepWaterRuntime.LogProfile(profile, "surface-promote", sender);
            }
        }

        private static void EnsureVisibleSurface(DaggerfallTerrain terrain, TerrainData terrainData, Mesh surfaceMesh)
        {
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanY = sampler.OceanElevation / sampler.MaxTerrainHeight * terrainData.size.y;

            Transform existing = terrain.transform.Find(VisualChildName);
            GameObject visualGO;
            if (existing == null)
            {
                visualGO = new GameObject(VisualChildName);
                visualGO.transform.SetParent(terrain.transform, false);
            }
            else
            {
                visualGO = existing.gameObject;
            }

            EnsureSurfaceMarker(visualGO);

            MeshFilter topFilter = EnsureSurfaceRenderer(
                visualGO.transform,
                TopSurfaceChildName,
                WaterSurfaceResources.GetTopMaterial(),
                WaterSurfaceResources.IsTopSurfaceVisible());
            MeshFilter undersideFilter = EnsureSurfaceRenderer(
                visualGO.transform,
                UndersideSurfaceChildName,
                WaterSurfaceResources.GetUndersideMaterial(),
                WaterSurfaceResources.IsUndersideSurfaceVisible());
            ReplaceSurfaceMesh(topFilter, undersideFilter, surfaceMesh);

            WaterSurfaceResources.ApplyMaterialSettings();
            visualGO.transform.localPosition = new Vector3(0f, oceanY + SurfaceRenderYOffset, 0f);
            visualGO.transform.localScale    = Vector3.one;
            visualGO.transform.localRotation = Quaternion.identity;
        }

        private static MeshFilter EnsureSurfaceRenderer(
            Transform root,
            string childName,
            Material material,
            bool visible)
        {
            Transform existing = root.Find(childName);
            GameObject surfaceGO;
            if (existing == null)
            {
                surfaceGO = new GameObject(childName);
                surfaceGO.transform.SetParent(root, false);
            }
            else
            {
                surfaceGO = existing.gameObject;
            }

            surfaceGO.transform.localPosition = Vector3.zero;
            surfaceGO.transform.localRotation = Quaternion.identity;
            surfaceGO.transform.localScale = Vector3.one;

            var meshFilter = surfaceGO.GetComponent<MeshFilter>();
            if (meshFilter == null)
                meshFilter = surfaceGO.AddComponent<MeshFilter>();

            var meshRenderer = surfaceGO.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                meshRenderer = surfaceGO.AddComponent<MeshRenderer>();

            if (meshRenderer.sharedMaterial != material)
                meshRenderer.sharedMaterial = material;

            meshRenderer.enabled = material != null && visible;
            DeepWaterRendering.DisableShadows(meshRenderer);
            return meshFilter;
        }

        private static void EnsureSurfaceMarker(GameObject visualGO)
        {
            var marker = visualGO.GetComponent<DeepWatersWaterSurface>();
            if (marker == null)
                visualGO.AddComponent<DeepWatersWaterSurface>();
        }

        private static Mesh BuildSurfaceMesh(DaggerfallTerrain terrain, TerrainData terrainData)
        {
            int n = SurfaceGridResolution;
            float sizeX = terrainData.size.x;
            float sizeZ = terrainData.size.z;
            bool hasOwnWater = DeepWaterWaterClassification.MapDataHasWater(terrain.MapData);
            bool hasBakedWater =
                DeepWaterDistanceBake.IsLoaded &&
                DeepWaterDistanceBake.MapPixelHasWaterCells(terrain.MapPixelX, terrain.MapPixelY);

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            if (ShouldRenderFullTileSurface(terrain, hasOwnWater))
            {
                AppendSurfaceQuad(
                    0f, 1f,
                    0f, 1f,
                    sizeX,
                    sizeZ,
                    vertices,
                    uvs,
                    triangles);
                return CreateSurfaceMesh(vertices, uvs, triangles);
            }

            bool[,] cells = new bool[n, n];
            bool[,] used = new bool[n, n];
            DeepWaterTileData tile = terrain.GetComponent<DeepWaterTileData>();

            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    cells[z, x] = (hasOwnWater || hasBakedWater) &&
                        IsSurfaceCellWater(terrain, terrainData, tile, x, z, n);
                }
            }

            AddNeighborWaterConnectedShoreline(terrain, cells, n);
            AddLocalShorelineFeather(terrain, cells, n);

            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    if (!cells[z, x] || used[z, x])
                        continue;

                    int width = 1;
                    while (x + width < n && cells[z, x + width] && !used[z, x + width])
                        width++;

                    int height = 1;
                    bool canGrow = true;
                    while (z + height < n && canGrow)
                    {
                        for (int xx = x; xx < x + width; xx++)
                        {
                            if (!cells[z + height, xx] || used[z + height, xx])
                            {
                                canGrow = false;
                                break;
                            }
                        }

                        if (canGrow)
                            height++;
                    }

                    for (int zz = z; zz < z + height; zz++)
                        for (int xx = x; xx < x + width; xx++)
                            used[zz, xx] = true;

                    AppendSurfaceQuad(
                        x / (float)n,
                        (x + width) / (float)n,
                        z / (float)n,
                        (z + height) / (float)n,
                        sizeX,
                        sizeZ,
                        vertices,
                        uvs,
                        triangles);
                }
            }

            if (vertices.Count == 0)
                return null;

            return CreateSurfaceMesh(vertices, uvs, triangles);
        }

        private static bool ShouldRenderFullTileSurface(DaggerfallTerrain terrain, bool hasOwnWater)
        {
            if (!RenderFullWaterTileSurface || !hasOwnWater || terrain == null)
                return false;

            if (!DeepWaterDistanceBake.IsLoaded)
                return true;

            if (!DeepWaterDistanceBake.MapPixelHasWaterCells(terrain.MapPixelX, terrain.MapPixelY) ||
                DeepWaterDistanceBake.MapPixelHasLandCells(terrain.MapPixelX, terrain.MapPixelY))
                return false;

            return DeepWaterWaterClassification.MapDataFullySubmerged(terrain.MapData);
        }

        private static bool ShouldHaveSurface(DaggerfallTerrain terrain)
        {
            if (terrain == null)
                return false;

            if (DeepWaterWaterClassification.MapDataHasWater(terrain.MapData))
                return true;

            if (!DeepWaterDistanceBake.IsLoaded)
                return false;

            int px = terrain.MapPixelX;
            int py = terrain.MapPixelY;
            return DeepWaterDistanceBake.MapPixelHasWaterCells(px, py) ||
                   DeepWaterDistanceBake.MapPixelHasWaterCells(px - 1, py) ||
                   DeepWaterDistanceBake.MapPixelHasWaterCells(px + 1, py) ||
                   DeepWaterDistanceBake.MapPixelHasWaterCells(px, py - 1) ||
                   DeepWaterDistanceBake.MapPixelHasWaterCells(px, py + 1);
        }

        private static bool IsSurfaceCellWater(
            DaggerfallTerrain terrain,
            TerrainData terrainData,
            DeepWaterTileData tile,
            int cellX,
            int cellZ,
            int resolution)
        {
            if (terrain == null ||
                terrainData == null)
            {
                return false;
            }

            if (CellContainsPromotedClippedWaterTile(terrain, cellX, cellZ, resolution) ||
                DeepWaterWaterClassification.CellContainsWaterTile(terrain.MapData, cellX, cellZ, resolution))
                return true;

            if (!DeepWaterWaterClassification.IsCellVisuallyWet(terrain.MapData, cellX, cellZ, resolution))
                return false;

            float x0 = cellX / (float)resolution;
            float x1 = (cellX + 1) / (float)resolution;
            float z0 = cellZ / (float)resolution;
            float z1 = (cellZ + 1) / (float)resolution;
            if (IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.5f), Mathf.Lerp(z0, z1, 0.5f)) ||
                IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.25f), Mathf.Lerp(z0, z1, 0.25f)) ||
                IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.75f), Mathf.Lerp(z0, z1, 0.25f)) ||
                IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.25f), Mathf.Lerp(z0, z1, 0.75f)) ||
                IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.75f), Mathf.Lerp(z0, z1, 0.75f)))
            {
                return true;
            }

            return IsSurfaceSampleWater(terrain, terrainData, tile, Mathf.Lerp(x0, x1, 0.25f), Mathf.Lerp(z0, z1, 0.25f)) ||
                   IsSurfaceSampleWater(terrain, terrainData, tile, Mathf.Lerp(x0, x1, 0.75f), Mathf.Lerp(z0, z1, 0.25f)) ||
                   IsSurfaceSampleWater(terrain, terrainData, tile, Mathf.Lerp(x0, x1, 0.25f), Mathf.Lerp(z0, z1, 0.75f)) ||
                   IsSurfaceSampleWater(terrain, terrainData, tile, Mathf.Lerp(x0, x1, 0.75f), Mathf.Lerp(z0, z1, 0.75f));
        }

        private static bool IsBakedSurfaceWater(DaggerfallTerrain terrain, float fracX, float fracZ)
        {
            return terrain != null &&
                DeepWaterDistanceBake.IsLoaded &&
                DeepWaterDistanceBake.IsWaterAt(terrain.MapPixelX, terrain.MapPixelY, fracX, fracZ);
        }

        private static bool CellContainsPromotedClippedWaterTile(
            DaggerfallTerrain terrain,
            int cellX,
            int cellZ,
            int resolution)
        {
            if (terrain == null || terrain.TileMap == null || resolution <= 0)
                return false;

            Color32[] tileMap = terrain.TileMap;
            int dim = Mathf.RoundToInt(Mathf.Sqrt(tileMap.Length));
            if (dim <= 0 || dim * dim != tileMap.Length)
                return false;

            int x0 = Mathf.Clamp(cellX * dim / resolution, 0, dim - 1);
            int x1 = Mathf.Clamp(((cellX + 1) * dim - 1) / resolution, 0, dim - 1);
            int z0 = Mathf.Clamp(cellZ * dim / resolution, 0, dim - 1);
            int z1 = Mathf.Clamp(((cellZ + 1) * dim - 1) / resolution, 0, dim - 1);
            bool textureArray = TerrainUsesTextureArrayShader(terrain);

            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (DeepWaterTerrainCapRenderer.ShouldClipPromotedWaterTexel(
                        terrain,
                        tileMap[z * dim + x].a,
                        textureArray,
                        x,
                        z,
                        dim))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TerrainUsesTextureArrayShader(DaggerfallTerrain terrain)
        {
            Terrain unityTerrain = terrain != null ? terrain.GetComponent<Terrain>() : null;
            Material material = unityTerrain != null ? unityTerrain.materialTemplate : null;
            Shader shader = material != null ? material.shader : null;
            string name = shader != null ? shader.name : null;
            return name == "Daggerfall/TilemapTextureArray" ||
                   name == "DeepWaters/TilemapTextureArrayClipWater";
        }

        private static bool IsSurfaceSampleWater(
            DaggerfallTerrain terrain,
            TerrainData terrainData,
            DeepWaterTileData tile,
            float fracX,
            float fracZ)
        {
            if (DeepWaterWaterClassification.IsLocalPointWater(terrain.MapData, fracX, fracZ))
                return true;

            if (tile == null || !tile.IsOceanConnected || !tile.HasDistanceField)
                return false;

            float worldX = terrain.transform.position.x + fracX * terrainData.size.x;
            float worldZ = terrain.transform.position.z + fracZ * terrainData.size.z;
            return
                DeepWaterWaterClassification.IsLocalPointPureWaterTile(terrain.MapData, fracX, fracZ) &&
                tile.IsBakedWater(worldX, worldZ);
        }

        private static void AddNeighborWaterConnectedShoreline(DaggerfallTerrain terrain, bool[,] cells, int n)
        {
            if (terrain == null || !DeepWaterDistanceBake.IsLoaded)
                return;

            DeepWaterTileData tile = terrain.GetComponent<DeepWaterTileData>();
            if (tile == null || !tile.IsOceanConnected)
                return;

            int px = terrain.MapPixelX;
            int py = terrain.MapPixelY;
            bool westWater = NeighborHasWater(px - 1, py);
            bool eastWater = NeighborHasWater(px + 1, py);
            bool northWater = NeighborHasWater(px, py - 1);
            bool southWater = NeighborHasWater(px, py + 1);
            if (!westWater && !eastWater && !northWater && !southWater)
                return;

            bool[,] visited = new bool[n, n];
            var queue = new Queue<int>();

            if (westWater)
                for (int z = 0; z < n; z++)
                    EnqueueFirstSubmergedShoreCell(terrain, visited, queue, 0, 1, z, 0, n);

            if (eastWater)
                for (int z = 0; z < n; z++)
                    EnqueueFirstSubmergedShoreCell(terrain, visited, queue, n - 1, -1, z, 0, n);

            if (northWater)
                for (int x = 0; x < n; x++)
                    EnqueueFirstSubmergedShoreCell(terrain, visited, queue, x, 0, 0, 1, n);

            if (southWater)
                for (int x = 0; x < n; x++)
                    EnqueueFirstSubmergedShoreCell(terrain, visited, queue, x, 0, n - 1, -1, n);

            while (queue.Count > 0)
            {
                int encoded = queue.Dequeue();
                int x = encoded & 0xffff;
                int z = encoded >> 16;
                cells[z, x] = true;

                EnqueueShoreCell(terrain, visited, queue, x - 1, z, n);
                EnqueueShoreCell(terrain, visited, queue, x + 1, z, n);
                EnqueueShoreCell(terrain, visited, queue, x, z - 1, n);
                EnqueueShoreCell(terrain, visited, queue, x, z + 1, n);
            }
        }

        private static void AddLocalShorelineFeather(DaggerfallTerrain terrain, bool[,] cells, int n)
        {
            if (terrain == null || cells == null || n <= 0 || ShorelineSurfaceFeatherCells <= 0)
                return;

            bool[,] source = new bool[n, n];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    source[z, x] = cells[z, x];

            for (int pass = 0; pass < ShorelineSurfaceFeatherCells; pass++)
            {
                bool changed = false;
                bool[,] next = new bool[n, n];
                for (int z = 0; z < n; z++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        next[z, x] = source[z, x];
                        if (source[z, x] ||
                            !HasAdjacentSurfaceCell(source, x, z, n) ||
                            (!DeepWaterWaterClassification.IsCellVisuallyWet(terrain.MapData, x, z, n) &&
                             !IsBakedShoreSurfaceCell(terrain, x, z, n)))
                        {
                            continue;
                        }

                        next[z, x] = true;
                        changed = true;
                    }
                }

                source = next;
                if (!changed)
                    break;
            }

            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    cells[z, x] = source[z, x];
        }

        private static bool HasAdjacentSurfaceCell(bool[,] cells, int x, int z, int n)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                        continue;

                    int xx = x + dx;
                    int zz = z + dz;
                    if (xx >= 0 && zz >= 0 && xx < n && zz < n && cells[zz, xx])
                        return true;
                }
            }

            return false;
        }

        private static bool IsBakedShoreSurfaceCell(DaggerfallTerrain terrain, int cellX, int cellZ, int resolution)
        {
            if (terrain == null || !DeepWaterDistanceBake.IsLoaded || resolution <= 0)
                return false;

            float x0 = cellX / (float)resolution;
            float x1 = (cellX + 1) / (float)resolution;
            float z0 = cellZ / (float)resolution;
            float z1 = (cellZ + 1) / (float)resolution;
            return IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.5f), Mathf.Lerp(z0, z1, 0.5f)) ||
                   IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.25f), Mathf.Lerp(z0, z1, 0.25f)) ||
                   IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.75f), Mathf.Lerp(z0, z1, 0.25f)) ||
                   IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.25f), Mathf.Lerp(z0, z1, 0.75f)) ||
                   IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.75f), Mathf.Lerp(z0, z1, 0.75f));
        }

        private static bool NeighborHasWater(int mapPixelX, int mapPixelY)
        {
            return DeepWaterDistanceBake.MapPixelHasWaterCells(mapPixelX, mapPixelY) ||
                   DeepWaterDistanceBake.MapPixelHasFineWaterCells(mapPixelX, mapPixelY);
        }

        private static void EnqueueFirstSubmergedShoreCell(
            DaggerfallTerrain terrain,
            bool[,] visited,
            Queue<int> queue,
            int startX,
            int stepX,
            int startZ,
            int stepZ,
            int n)
        {
            int maxScan = Mathf.Min(ShorelineSeedScanCells, n);
            for (int i = 0; i < maxScan; i++)
            {
                int x = startX + stepX * i;
                int z = startZ + stepZ * i;
                if (x < 0 || z < 0 || x >= n || z >= n)
                    break;

                if (DeepWaterWaterClassification.IsCellVisuallyWet(terrain.MapData, x, z, n))
                {
                    EnqueueShoreCell(terrain, visited, queue, x, z, n);
                    break;
                }
            }
        }

        private static void EnqueueShoreCell(
            DaggerfallTerrain terrain,
            bool[,] visited,
            Queue<int> queue,
            int x,
            int z,
            int n)
        {
            if (x < 0 || z < 0 || x >= n || z >= n || visited[z, x])
                return;

            visited[z, x] = true;
            if (DeepWaterWaterClassification.IsCellVisuallyWet(terrain.MapData, x, z, n))
                queue.Enqueue((z << 16) | x);
        }

        private static Mesh CreateSurfaceMesh(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
        {
            var mesh = new Mesh { name = GeneratedMeshName };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AppendSurfaceQuad(
            float fracX0,
            float fracX1,
            float fracZ0,
            float fracZ1,
            float sizeX,
            float sizeZ,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> triangles)
        {
            float x0 = fracX0 * sizeX;
            float x1 = fracX1 * sizeX;
            float z0 = fracZ0 * sizeZ;
            float z1 = fracZ1 * sizeZ;

            int start = vertices.Count;
            vertices.Add(new Vector3(x0, 0f, z0));
            vertices.Add(new Vector3(x1, 0f, z0));
            vertices.Add(new Vector3(x1, 0f, z1));
            vertices.Add(new Vector3(x0, 0f, z1));

            uvs.Add(new Vector2(fracX0, fracZ0));
            uvs.Add(new Vector2(fracX1, fracZ0));
            uvs.Add(new Vector2(fracX1, fracZ1));
            uvs.Add(new Vector2(fracX0, fracZ1));

            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 1);
            triangles.Add(start);
            triangles.Add(start + 3);
            triangles.Add(start + 2);
        }

        private static void ReplaceSurfaceMesh(MeshFilter topFilter, MeshFilter undersideFilter, Mesh newMesh)
        {
            Mesh oldTopMesh = topFilter.sharedMesh;
            Mesh oldUndersideMesh = undersideFilter.sharedMesh;

            topFilter.sharedMesh = newMesh;
            undersideFilter.sharedMesh = newMesh;

            DestroyGeneratedMesh(oldTopMesh, newMesh);
            if (oldUndersideMesh != oldTopMesh)
                DestroyGeneratedMesh(oldUndersideMesh, newMesh);
        }

        private static void DestroyGeneratedMesh(Mesh mesh, Mesh replacement)
        {
            if (mesh != null && mesh != replacement && mesh.name == GeneratedMeshName)
                Object.Destroy(mesh);
        }

        private static void RemoveExisting(DaggerfallTerrain terrain)
        {
            var visual = terrain.transform.Find(VisualChildName);
            if (visual != null)
            {
                var destroyedMeshes = new HashSet<Mesh>();
                MeshFilter[] meshFilters = visual.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < meshFilters.Length; i++)
                {
                    Mesh mesh = meshFilters[i].sharedMesh;
                    if (mesh != null &&
                        mesh.name == GeneratedMeshName &&
                        destroyedMeshes.Add(mesh))
                    {
                        Object.Destroy(mesh);
                    }
                }

                Object.Destroy(visual.gameObject);
            }
        }

    }

    /// <summary>
    /// Marker component for generated water-surface renderers.
    /// </summary>
    public class DeepWatersWaterSurface : MonoBehaviour
    {
    }
}
