// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Per-terrain visible water surface. The trigger collider is deliberately
    /// non-blocking and exists only so other mods can detect the raised surface.
    /// The physics / swim behaviour is driven by OutdoorSwimDriver based on
    /// player Y and terrain Y.
    ///
    /// Water uses one shared mesh and one shared custom material for every
    /// terrain tile. The material is intentionally exposed so boat or camera
    /// mods can coordinate stencil values without touching every tile.
    /// </summary>
    public static class WaterSurfaceManager
    {
        private const string VisualChildName = "DeepWaters_Surface";
        private const float SurfaceTriggerThickness = 0.25f;

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
            DaggerfallTerrain[] terrains = Object.FindObjectsOfType<DaggerfallTerrain>();
            for (int i = 0; i < terrains.Length; i++)
            {
                Terrain terrain = terrains[i].GetComponent<Terrain>();
                if (terrain != null && terrain.terrainData != null)
                    HandlePromote(terrains[i], terrain.terrainData);
            }
        }

        private static void HandlePromote(DaggerfallTerrain sender, TerrainData terrainData)
        {
            if (sender == null || terrainData == null)
                return;

            if (DeepWaters.Instance == null ||
                !DeepWaters.Instance.SpawnWaterSurfaces ||
                !HasWaterTile(sender.MapData))
            {
                RemoveExisting(sender);
                return;
            }

            EnsureVisibleSurface(sender, terrainData);
        }

        // DFU's AssignTilesJob encodes the "all-corners-water" case as
        // tilemapData == 0. Any tile with that value is a fully-submerged
        // tile. Our DeepWaterTexturing post-pass converts that to 1 (dirt)
        // AFTER this check reads the raw values in MapData.tilemapSamples...
        //
        // Wait — important sequencing note: tilemapSamples is populated from
        // tilemapData during CompleteMapPixelDataUpdate (BEFORE OnPromoteTerrainData
        // fires). So by the time this event reaches us, our conversion has
        // already run and submerged tiles are value 1, not 0. We therefore
        // can't use tilemapSamples==0 as the "has water" test any more.
        //
        // Practical workaround: scan the heightmap instead. Any sample at or
        // below the ocean threshold means this pixel contains water.
        private static bool HasWaterTile(MapPixelData mapData)
        {
            if (mapData.heightmapSamples == null)
                return false;

            float sampleOcean =
                DaggerfallUnity.Instance.TerrainSampler.OceanElevation /
                DaggerfallUnity.Instance.TerrainSampler.MaxTerrainHeight;

            int hDim0 = mapData.heightmapSamples.GetLength(0);
            int hDim1 = mapData.heightmapSamples.GetLength(1);

            // Sample a sparse grid first (every 16th sample) as a fast-path
            // check — if ANY are at/below ocean, we know the pixel has water
            // without scanning all ~17k samples.
            for (int y = 0; y < hDim0; y += 16)
                for (int x = 0; x < hDim1; x += 16)
                    if (mapData.heightmapSamples[y, x] <= sampleOcean + 1e-5f)
                        return true;

            // Fall back to full scan for edge cases (tiny water inlets).
            for (int y = 0; y < hDim0; y++)
                for (int x = 0; x < hDim1; x++)
                    if (mapData.heightmapSamples[y, x] <= sampleOcean + 1e-5f)
                        return true;

            return false;
        }

        private static void EnsureVisibleSurface(DaggerfallTerrain terrain, TerrainData terrainData)
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
                mf.sharedMesh = WaterSurfaceResources.GetFlatMesh();
                mr.sharedMaterial = WaterSurfaceResources.GetMaterial();
                DeepWaterRendering.DisableShadows(mr);
                EnsureSurfaceTrigger(visualGO);
            }
            else
            {
                visualGO = existing.gameObject;
                var mr = visualGO.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    Material surfaceMaterial = WaterSurfaceResources.GetMaterial();
                    if (mr.sharedMaterial != surfaceMaterial)
                        mr.sharedMaterial = surfaceMaterial;

                    DeepWaterRendering.DisableShadows(mr);
                }

                EnsureSurfaceTrigger(visualGO);
            }

            WaterSurfaceResources.ApplyMaterialSettings();
            visualGO.transform.localPosition = new Vector3(0f, oceanY, 0f);
            visualGO.transform.localScale    = new Vector3(terrainData.size.x, 1f, terrainData.size.z);
            visualGO.transform.localRotation = Quaternion.identity;
        }

        private static void EnsureSurfaceTrigger(GameObject visualGO)
        {
            var marker = visualGO.GetComponent<DeepWatersWaterSurface>();
            if (marker == null)
                visualGO.AddComponent<DeepWatersWaterSurface>();

            var collider = visualGO.GetComponent<BoxCollider>();
            if (collider == null)
                collider = visualGO.AddComponent<BoxCollider>();

            collider.isTrigger = true;
            collider.center = new Vector3(0.5f, 0f, 0.5f);
            collider.size = new Vector3(1f, SurfaceTriggerThickness, 1f);
        }

        private static void RemoveExisting(DaggerfallTerrain terrain)
        {
            var visual = terrain.transform.Find(VisualChildName);
            if (visual != null)
                Object.Destroy(visual.gameObject);

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
    /// Marker component for the non-blocking water-surface trigger.
    /// Other mods can detect this component after raycasts or trigger queries.
    /// </summary>
    public class DeepWatersWaterSurface : MonoBehaviour
    {
    }
}
