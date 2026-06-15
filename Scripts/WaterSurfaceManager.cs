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
        private const int SurfaceGridResolution = 16;

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
                    !HasWaterTile(sender.MapData))
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

        // Use the same promoted tilemap/heightmap test as the hole builder so
        // the visible water plane and carved seabed cover the same map pixels.
        private static bool HasWaterTile(MapPixelData mapData)
        {
            return DeepWaterWaterClassification.MapDataHasWater(mapData);
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
            visualGO.transform.localPosition = new Vector3(0f, oceanY, 0f);
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

            var vertices = new List<Vector3>(n * n * 4);
            var uvs = new List<Vector2>(n * n * 4);
            var triangles = new List<int>(n * n * 6);

            int mapPixelX = terrain.MapPixelX;
            int mapPixelY = terrain.MapPixelY;
            bool useBakeMask = DeepWaterDistanceBake.HasFineWaterMask;

            // Ocean-connected tiles get the terrain water-texel clip (the
            // painted sea-level water is discarded by the clip shader), so the
            // surface film must cover every cell containing a pure-water tile —
            // the exact area the clip uncovered. Anything narrower shows a bare
            // seabed band between the film's edge and the shoreline. Other
            // tiles (inland lakes/rivers, never clipped) keep the conservative
            // legacy criteria so their painted vanilla water stays untouched.
            DeepWaterTileData tile = terrain.GetComponent<DeepWaterTileData>();
            bool matchWaterTexelClip = tile != null && tile.IsOceanConnected && tile.HasDistanceField;

            for (int z = 0; z < n; z++)
            {
                float fracZ0 = z / (float)n;
                float fracZ1 = (z + 1) / (float)n;
                float fracZMid = (z + 0.5f) / n;
                for (int x = 0; x < n; x++)
                {
                    float fracX0 = x / (float)n;
                    float fracX1 = (x + 1) / (float)n;
                    float fracXMid = (x + 0.5f) / n;

                    // Carve-aligned coverage (partially submerged + bake-carved):
                    // film over every cell whose ground dips below the
                    // waterline. Cells fully above the surface get no film;
                    // ground poking above the film occludes it via the depth
                    // test, so the waterline follows the terrain/ocean
                    // intersection without bare shore bands.
                    bool carveAligned =
                        DeepWaterWaterClassification.IsLocalPointWater(terrain.MapData, fracXMid, fracZMid) &&
                        (!useBakeMask || DeepWaterDistanceBake.IsCarvedWater(mapPixelX, mapPixelY, fracXMid, fracZMid)) &&
                        DeepWaterWaterClassification.IsCellPartiallySubmerged(terrain.MapData, x, z, n);

                    // Clip-aligned coverage: on clipped (ocean-connected) tiles
                    // the film must also cover every pure-water texel whose
                    // painted vanilla water the clip shader removed, or a bare
                    // band appears along the shoreline.
                    bool clipAligned = matchWaterTexelClip &&
                        DeepWaterWaterClassification.CellContainsPureWaterTile(terrain.MapData, x, z, n);

                    if (!carveAligned && !clipAligned)
                        continue;

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
            }

            if (vertices.Count == 0)
                return null;

            var mesh = new Mesh { name = GeneratedMeshName };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
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
