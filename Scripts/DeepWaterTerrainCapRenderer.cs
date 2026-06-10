// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// The vanilla terrain is never carved (real Unity terrain holes
    /// native-crash Unity 2019.4), so its sea-level heightmap renderer would
    /// sit as an opaque cap over fish, decorations, water surfaces, and the
    /// generated seafloor. On pure ocean map pixels that renderer can safely
    /// be hidden so the generated Deep Waters meshes own the visual scene.
    /// </summary>
    internal static class DeepWaterTerrainCapRenderer
    {
        private sealed class HiddenCapMarker : MonoBehaviour
        {
            public bool HasOriginalDrawHeightmap;
            public bool OriginalDrawHeightmap;
            public bool Hidden;
            public Shader OriginalShader;
            public bool WaterTexelsClipped;
        }

        private static Shader tilemapTextureArrayClipShader;
        private static Shader tilemapClipShader;
        private static bool clipShadersResolved;
        private static bool loggedUnknownTerrainShader;

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

            ApplyWaterTexelClip(dfTerrain, false);

            HiddenCapMarker marker = dfTerrain.GetComponent<HiddenCapMarker>();
            if (marker == null || !marker.Hidden)
                return;

            Terrain terrain = dfTerrain.GetComponent<Terrain>();
            if (terrain != null && marker.HasOriginalDrawHeightmap)
                terrain.drawHeightmap = marker.OriginalDrawHeightmap;

            marker.Hidden = false;
        }

        /// <summary>
        /// Render-side water hole punching for MIXED land/water tiles, where the
        /// whole-renderer cap hide is not an option (the land must render).
        /// Swaps the tile's terrain material to a copy of DFU's terrain shader
        /// that clips pure-water texels, so the sea-level painted water
        /// disappears and the carved seafloor below shows through the
        /// transparent water surface. The material instance is per-tile (DFU's
        /// TerrainMaterialProvider creates one per terrain), so only the shader
        /// reference is swapped and all textures/properties are preserved.
        /// </summary>
        public static void ApplyWaterTexelClip(DaggerfallTerrain dfTerrain, bool clip)
        {
            if (dfTerrain == null)
                return;

            Terrain terrain = dfTerrain.GetComponent<Terrain>();
            Material material = terrain != null ? terrain.materialTemplate : null;
            if (material == null || material.shader == null)
                return;

            HiddenCapMarker marker = dfTerrain.GetComponent<HiddenCapMarker>();

            if (!clip)
            {
                if (marker != null && marker.WaterTexelsClipped)
                {
                    if (marker.OriginalShader != null)
                        material.shader = marker.OriginalShader;
                    marker.WaterTexelsClipped = false;
                }
                return;
            }

            if (marker != null && marker.WaterTexelsClipped)
                return;

            Shader clipShader = ResolveClipShader(material.shader.name);
            if (clipShader == null)
                return;

            if (marker == null)
                marker = dfTerrain.gameObject.AddComponent<HiddenCapMarker>();

            marker.OriginalShader = material.shader;
            material.shader = clipShader;
            marker.WaterTexelsClipped = true;
        }

        private static Shader ResolveClipShader(string currentShaderName)
        {
            if (!clipShadersResolved)
            {
                clipShadersResolved = true;
                tilemapTextureArrayClipShader = LoadModShader(
                    "DeepWaters/TilemapTextureArrayClipWater",
                    "DeepWaterTilemapTextureArrayClipWater.shader");
                tilemapClipShader = LoadModShader(
                    "DeepWaters/TilemapClipWater",
                    "DeepWaterTilemapClipWater.shader");
            }

            // Already swapped (tile re-promoted while clipped).
            if (currentShaderName == "DeepWaters/TilemapTextureArrayClipWater")
                return tilemapTextureArrayClipShader;
            if (currentShaderName == "DeepWaters/TilemapClipWater")
                return tilemapClipShader;

            if (currentShaderName == "Daggerfall/TilemapTextureArray")
                return tilemapTextureArrayClipShader;
            if (currentShaderName == "Daggerfall/Tilemap")
                return tilemapClipShader;

            // Unknown terrain shader (another terrain rendering mod owns it):
            // leave it alone — the painted water cap stays on mixed tiles there.
            if (!loggedUnknownTerrainShader)
            {
                loggedUnknownTerrainShader = true;
                Debug.Log("[DeepWaters.Cap] Terrain uses shader '" + currentShaderName +
                          "' — no water-texel clip variant available, so the sea-level " +
                          "water cap stays visible on mixed land/water map pixels.");
            }

            return null;
        }

        private static Shader LoadModShader(string shaderName, string assetName)
        {
            Shader shader = Shader.Find(shaderName);

            if (shader == null && DeepWaters.Mod != null)
                shader = DeepWaters.Mod.GetAsset<Shader>(assetName);

            if (shader == null)
                Debug.LogWarning("[DeepWaters.Cap] " + shaderName + " shader not found; " +
                                 "water-texel clipping unavailable for this terrain mode.");

            return shader;
        }

        public static bool ShouldHidePureOceanCap(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null)
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
