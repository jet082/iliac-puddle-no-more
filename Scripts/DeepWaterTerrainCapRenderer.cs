// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Render-only fallback for the no-runtime-terrain-holes mode.
    /// Unity Terrain holes can native-crash on some load paths, but leaving the
    /// vanilla ocean-height terrain renderer visible creates an opaque cap over
    /// fish, decorations, water surfaces, and the generated seafloor. On pure
    /// ocean map pixels we can safely hide that heightmap renderer and let the
    /// generated Deep Waters meshes own the visual scene.
    /// </summary>
    internal static class DeepWaterTerrainCapRenderer
    {
        private sealed class HiddenCapMarker : MonoBehaviour
        {
            public bool HasOriginalDrawHeightmap;
            public bool OriginalDrawHeightmap;
            public bool Hidden;
        }

        public static void Apply(DaggerfallTerrain dfTerrain, bool hide)
        {
            if (dfTerrain == null)
                return;

            Terrain terrain = dfTerrain.GetComponent<Terrain>();
            if (terrain == null)
                return;

            if (!hide)
            {
                Restore(dfTerrain);
                return;
            }

            HiddenCapMarker marker = dfTerrain.GetComponent<HiddenCapMarker>();
            if (marker == null)
                marker = dfTerrain.gameObject.AddComponent<HiddenCapMarker>();

            if (!marker.HasOriginalDrawHeightmap)
            {
                marker.OriginalDrawHeightmap = terrain.drawHeightmap;
                marker.HasOriginalDrawHeightmap = true;
            }

            terrain.drawHeightmap = false;
            marker.Hidden = true;
        }

        public static void Restore(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null)
                return;

            HiddenCapMarker marker = dfTerrain.GetComponent<HiddenCapMarker>();
            if (marker == null || !marker.Hidden)
                return;

            Terrain terrain = dfTerrain.GetComponent<Terrain>();
            if (terrain != null && marker.HasOriginalDrawHeightmap)
                terrain.drawHeightmap = marker.OriginalDrawHeightmap;

            marker.Hidden = false;
        }

        public static bool ShouldHidePureOceanCap(DaggerfallTerrain dfTerrain)
        {
            if (!DeepWaterHoleApplier.DisableRuntimeTerrainHoles || dfTerrain == null)
                return false;

            if (!DeepWaterDistanceBake.IsLoaded ||
                !DeepWaterDistanceBake.MapPixelHasWaterCells(dfTerrain.MapPixelX, dfTerrain.MapPixelY))
            {
                return false;
            }

            if (DeepWaterDistanceBake.MapPixelHasLandCells(dfTerrain.MapPixelX, dfTerrain.MapPixelY))
                return false;

            return DeepWaterWaterClassification.MapDataHasWater(dfTerrain.MapData);
        }
    }
}
