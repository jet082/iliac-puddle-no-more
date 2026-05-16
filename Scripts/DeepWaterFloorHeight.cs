// Project:         Iliac Puddle No More
// License:         MIT

using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Deterministic seafloor height rule shared by terrain generation and
    /// post-generation seam reconciliation.
    /// </summary>
    internal static class DeepWaterFloorHeight
    {
        public static float ComputeLoweredHeight(
            int worldHx,
            int worldHy,
            float oceanThresholdNormalised,
            float depthNormalised)
        {
            float budget = Mathf.Min(depthNormalised, oceanThresholdNormalised);
            float lift = Mathf.PerlinNoise(worldHx * 0.008f, worldHy * 0.008f) * 0.5f
                       + Mathf.PerlinNoise(worldHx * 0.030f, worldHy * 0.030f) * 0.35f
                       + Mathf.PerlinNoise(worldHx * 0.13f, worldHy * 0.13f) * 0.15f;

            lift = Mathf.Clamp(lift, 0f, 0.5f);
            return Mathf.Clamp(oceanThresholdNormalised - budget * (1f - lift), 0f, oceanThresholdNormalised);
        }
    }
}
