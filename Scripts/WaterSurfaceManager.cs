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
    /// Water uses generated per-tile meshes and one shared custom material for
    /// every terrain tile. The material is intentionally exposed so boat or
    /// camera mods can coordinate stencil values without touching every tile.
    /// </summary>
    public static class WaterSurfaceManager
    {
        private const string VisualChildName = "DeepWaters_Surface";
        private const string GeneratedMeshName = "DeepWaters.SurfaceMesh";
        private const int SurfaceGridResolution = 64;

        private static bool installed;

        public static void Install()
        {
            if (installed)
                return;

            DaggerfallTerrain.OnPromoteTerrainData += HandlePromote;
            installed = true;
        }

        public static void Uninstall()
        {
            if (!installed)
                return;

            DaggerfallTerrain.OnPromoteTerrainData -= HandlePromote;
            installed = false;
        }

        public static void RefreshLoadedSurfaces()
        {
            // Lean-mode isolation build installs no water surfaces; skip the
            // post-load refresh too so none get created behind its back.
            if (DeepWaters.LeanMode)
                return;

            DaggerfallTerrain[] terrains = Object.FindObjectsOfType<DaggerfallTerrain>();
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain terrain = terrains[i].GetComponent<Terrain>();
                if (terrain != null && terrain.terrainData != null)
                    HandlePromoteCore(terrains[i], terrain.terrainData, false);
            }
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

                var mf = visualGO.AddComponent<MeshFilter>();
                var mr = visualGO.AddComponent<MeshRenderer>();
                mf.sharedMesh = surfaceMesh;
                mr.sharedMaterial = WaterSurfaceResources.GetMaterial();
                DeepWaterRendering.DisableShadows(mr);
                EnsureSurfaceMarker(visualGO);
            }
            else
            {
                visualGO = existing.gameObject;
                var mf = visualGO.GetComponent<MeshFilter>();
                if (mf == null)
                    mf = visualGO.AddComponent<MeshFilter>();
                ReplaceSurfaceMesh(mf, surfaceMesh);

                var mr = visualGO.GetComponent<MeshRenderer>();
                if (mr == null)
                    mr = visualGO.AddComponent<MeshRenderer>();
                if (mr != null)
                {
                    Material surfaceMaterial = WaterSurfaceResources.GetMaterial();
                    if (mr.sharedMaterial != surfaceMaterial)
                        mr.sharedMaterial = surfaceMaterial;

                    DeepWaterRendering.DisableShadows(mr);
                }

                EnsureSurfaceMarker(visualGO);
            }

            WaterSurfaceResources.ApplyMaterialSettings();
            visualGO.transform.localPosition = new Vector3(0f, oceanY, 0f);
            visualGO.transform.localScale    = Vector3.one;
            visualGO.transform.localRotation = Quaternion.identity;
        }

        private static void EnsureSurfaceMarker(GameObject visualGO)
        {
            var marker = visualGO.GetComponent<DeepWatersWaterSurface>();
            if (marker == null)
                visualGO.AddComponent<DeepWatersWaterSurface>();

            var collider = visualGO.GetComponent<BoxCollider>();
            if (collider != null)
                Object.Destroy(collider);
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

                    if (!DeepWaterWaterClassification.IsLocalPointWater(terrain.MapData, fracXMid, fracZMid))
                        continue;

                    if (useBakeMask &&
                        !DeepWaterDistanceBake.IsCarvedWater(mapPixelX, mapPixelY, fracXMid, fracZMid))
                    {
                        continue;
                    }

                    // Only place the water surface where the WHOLE cell is
                    // submerged — the same gate the carve uses. The midpoint /
                    // shore-tile classification above is permissive (0.25 m
                    // headroom, shore tiles count as water), which otherwise lays
                    // a sea-level water film over shoreline cells that have no
                    // carved hole under them: the "0-depth water above land".
                    if (!DeepWaterWaterClassification.IsCellFullySubmerged(terrain.MapData, x, z, n))
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
            mesh.RecalculateNormals();
            return mesh;
        }

        private static void ReplaceSurfaceMesh(MeshFilter meshFilter, Mesh newMesh)
        {
            Mesh oldMesh = meshFilter.sharedMesh;
            meshFilter.sharedMesh = newMesh;
            if (oldMesh != null && oldMesh.name == GeneratedMeshName)
                Object.Destroy(oldMesh);
        }

        private static void RemoveExisting(DaggerfallTerrain terrain)
        {
            var visual = terrain.transform.Find(VisualChildName);
            if (visual != null)
            {
                var meshFilter = visual.GetComponent<MeshFilter>();
                if (meshFilter != null &&
                    meshFilter.sharedMesh != null &&
                    meshFilter.sharedMesh.name == GeneratedMeshName)
                {
                    Object.Destroy(meshFilter.sharedMesh);
                }

                Object.Destroy(visual.gameObject);
            }

            // Legacy cleanup: remove any v0.1/v0.2 BoxCollider + marker.
            var marker = terrain.GetComponent<DeepWatersSurfaceMarker>();
            if (marker != null)
            {
                if (marker.Surface != null)
                    Object.Destroy(marker.Surface);
                Object.Destroy(marker);
            }
        }

        public static Material GetSharedWaterMaterial()
        {
            return WaterSurfaceResources.GetSharedMaterial();
        }
    }

    /// <summary>
    /// Legacy marker from v0.1/v0.2 (collider-based surface). Retained so
    /// we can clean up leftover colliders on terrain GOs recycled across
    /// mod versions.
    /// </summary>
    public class DeepWatersSurfaceMarker : MonoBehaviour
    {
        public BoxCollider Surface;
    }

    /// <summary>
    /// Marker component for generated water-surface renderers.
    /// </summary>
    public class DeepWatersWaterSurface : MonoBehaviour
    {
    }
}
