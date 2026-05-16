// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Snapshot of seafloor state at a world XZ position. Returned by
    /// <see cref="DeepWatersApi.TryGetSeafloor"/> for external mod builders.
    /// </summary>
    public struct SeafloorInfo
    {
        /// <summary>Is this position over a streaming ocean-connected tile?
        /// If false, the other fields are not meaningful.</summary>
        public bool IsOcean;

        /// <summary>World-space Y of the seafloor surface.</summary>
        public float WorldY;

        /// <summary>World-space Y of the ocean surface (water plane).</summary>
        public float OceanWorldY;

        /// <summary>Water depth in meters (= OceanWorldY - WorldY).</summary>
        public float DepthMeters;

        /// <summary>Coarse geography classification at this position.</summary>
        public SeafloorGeographyKind Kind;

        /// <summary>Strength of the dominant feature on a 0..1 scale.</summary>
        public float Magnitude;

        /// <summary>Local slope in degrees, sampled from cardinal probes
        /// around the query position. Useful for set pieces that should
        /// only land on near-flat terrain.</summary>
        public float SlopeDegrees;
    }

    /// <summary>
    /// Public query surface for external mods. Provides the answers needed
    /// to safely place set pieces (sunken ships, crashed wagons, ruins) on
    /// the deep-waters seafloor — including a "Plain" classification that
    /// flags flat regions where large props won't tilt or clip into walls.
    /// </summary>
    public static class DeepWatersApi
    {
        // Slope probe radius. Matches the radius used by decoration
        // placement so external callers see consistent slope judgement.
        private const float SlopeProbeDistance = 4f;

        /// <summary>
        /// Resolve a full seafloor info record at a world position. Returns
        /// false if the position is not over a streaming ocean-connected
        /// tile (i.e. land, inland water, or not currently streamed).
        /// </summary>
        public static bool TryGetSeafloor(float worldX, float worldZ, out SeafloorInfo info)
        {
            info = default(SeafloorInfo);

            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
                return false;

            DaggerfallTerrain dfTerrain = column.DaggerfallTerrain;
            if (dfTerrain == null)
                return false;

            DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();
            if (tile == null || !tile.IsOceanConnected || !tile.HasDistanceField)
                return false;

            float distanceToCoast = tile.GetDistanceToCoastMeters(worldX, worldZ);
            SeafloorGeographyInfo geography = DeepBathymetry.Classify(
                worldX, worldZ, tile.ClimateIndex, distanceToCoast);

            info.IsOcean = true;
            info.WorldY = column.SeafloorWorldY;
            info.OceanWorldY = column.OceanWorldY;
            info.DepthMeters = column.Depth;
            info.Kind = geography.Kind;
            info.Magnitude = geography.Magnitude;
            info.SlopeDegrees = ComputeSlopeDegrees(worldX, worldZ);
            return true;
        }

        /// <summary>
        /// Shortcut for the most common external query: "is this a flat
        /// plain I can drop a large set piece on?" Returns true only when
        /// the classification is Plain AND the local slope is gentle
        /// enough that a large flat-bottom object will sit cleanly.
        /// </summary>
        public static bool IsPlain(float worldX, float worldZ, float maxSlopeDegrees = 12f)
        {
            SeafloorInfo info;
            if (!TryGetSeafloor(worldX, worldZ, out info))
                return false;

            return info.Kind == SeafloorGeographyKind.Plain &&
                   info.SlopeDegrees <= maxSlopeDegrees;
        }

        /// <summary>
        /// Minimum-depth filter helper. True if the seafloor at this
        /// position lies at or beyond the requested depth.
        /// </summary>
        public static bool IsDeepEnough(float worldX, float worldZ, float minDepthMeters)
        {
            SeafloorInfo info;
            return TryGetSeafloor(worldX, worldZ, out info) && info.DepthMeters >= minDepthMeters;
        }

        private static float ComputeSlopeDegrees(float worldX, float worldZ)
        {
            DeepWaterColumn left, right, back, forward;
            if (!DeepWaterWorld.TryGetWaterColumn(worldX - SlopeProbeDistance, worldZ, out left) ||
                !DeepWaterWorld.TryGetWaterColumn(worldX + SlopeProbeDistance, worldZ, out right) ||
                !DeepWaterWorld.TryGetWaterColumn(worldX, worldZ - SlopeProbeDistance, out back) ||
                !DeepWaterWorld.TryGetWaterColumn(worldX, worldZ + SlopeProbeDistance, out forward))
            {
                return 90f;
            }

            float dhdx = (right.SeafloorWorldY - left.SeafloorWorldY) / (SlopeProbeDistance * 2f);
            float dhdz = (forward.SeafloorWorldY - back.SeafloorWorldY) / (SlopeProbeDistance * 2f);
            return Mathf.Atan(Mathf.Sqrt(dhdx * dhdx + dhdz * dhdz)) * Mathf.Rad2Deg;
        }
    }
}
