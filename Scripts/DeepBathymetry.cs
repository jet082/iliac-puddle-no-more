// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallConnect.Arena2;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Coarse seafloor geography classification. Public so external mod
    /// builders can ask: "is this a flat plain I can drop a sunken ship on,
    /// or is it a seamount peak / ravine wall / continental slope where my
    /// set piece would tilt unpleasantly?"
    /// </summary>
    public enum SeafloorGeographyKind
    {
        /// <summary>Within the inner shelf, gradual sand drop near shore.</summary>
        Shore,
        /// <summary>Flat-ish abyssal/shelf plain. Best for sunken ships,
        /// crashed wagons, and other large set pieces.</summary>
        Plain,
        /// <summary>Generic rolling mid-amplitude hills.</summary>
        Hill,
        /// <summary>Pronounced rise from the seafloor; underwater peak.</summary>
        Seamount,
        /// <summary>Trench/ravine depression with steep walls.</summary>
        Ravine,
        /// <summary>Continental slope drop. Strong vertical change.</summary>
        Slope,
    }

    /// <summary>
    /// Result of <see cref="DeepBathymetry.Classify"/>. Magnitude is the
    /// strength of the dominant feature on a 0..1 scale (0 = barely
    /// qualifies, 1 = textbook example).
    /// </summary>
    public struct SeafloorGeographyInfo
    {
        public SeafloorGeographyKind Kind;
        public float DepthMeters;
        public float Magnitude;
    }

    /// <summary>
    /// Deterministic seabed depth function. f(worldX, worldZ, climate, distanceToCoast)
    /// returns depth in meters below ocean surface.
    ///
    /// Pure code, no state. Adjacent tile sub-meshes that sample this at the same
    /// world coordinates automatically agree at shared edges, so the old per-edge
    /// boundary reconciliation is not needed.
    ///
    /// The depth value is the sum of:
    ///   1. A four-stage continental shelf curve from coastline to abyss
    ///      (Beach drop -> Inner shelf -> Mid shelf -> Continental slope).
    ///   2. A macro low-frequency layer that creates climate-scale "deep
    ///      zones" vs "shallow zones".
    ///   3. Mid- and high-frequency Perlin noise for rolling hills and
    ///      microroughness.
    ///   4. A ravine layer (sparse Perlin-thresholded deep cuts).
    ///   5. A seamount layer (sparse Perlin-thresholded shallow rises).
    /// </summary>
    internal static class DeepBathymetry
    {
        // Per-climate baseline depths in meters. Ocean is deepest; Swamp gives
        // mangrove-shallow waters; mountainwood waters lean deep like fjords.
        private const float ClimateBaseOcean        = 210f;
        private const float ClimateBaseSubtropical  = 175f;
        private const float ClimateBaseRainforest   = 150f;
        private const float ClimateBaseSwamp        = 28f;
        private const float ClimateBaseWoodlands    = 165f;
        private const float ClimateBaseHaunted      = 165f;
        private const float ClimateBaseMountainWood = 185f;
        private const float ClimateBaseMountain     = 185f;
        private const float ClimateBaseDesert       = 90f;

        // Coast-distance curve: a four-stage ramp tuned so the player reaches
        // genuine ocean depth well before the BFS distance-field saturates
        // (~410m on a 33-cell heightmap). The beach stays wadeable, then
        // depth ramps in fast so being "out in the bay" feels like an ocean
        // rather than a swimming pool. End reached at 380m so depths beyond
        // that all read as climate-base without per-tile saturation banding.
        //
        // ShelfMinDepth is intentionally tiny — at the shoreline the seafloor
        // mesh meets the water surface within a few cm, so there's no visible
        // vertical "step" between vanilla shore terrain and our seafloor.
        // The slope from there to BeachDropDepth provides the wading gradient.
        public const float ShelfMinDepth      = 0.25f;
        public const float BeachDropDistance  = 18f;
        public const float BeachDropDepth     = 4.5f;
        public const float InnerShelfDistance = 70f;
        public const float InnerShelfDepth    = 30f;
        public const float MidShelfDistance   = 210f;
        public const float MidShelfDepth      = 105f;
        public const float ShelfRampMeters    = 380f;

        // Macro layer: bay-scale "deep zones" vs "shallow zones".
        private const float MacroPeriodMeters       = 4200f;
        private const float MacroAmplitudeFraction  = 0.30f;

        // Mid layer: rolling hills and hummocks.
        private const float MidPeriodMeters    = 330f;
        private const float MidAmplitudeMeters = 18f;

        // High layer: micro-roughness.
        private const float HighPeriodMeters    = 24f;
        private const float HighAmplitudeMeters = 3.5f;

        // Ravine layer: sparse linear-feeling deep cuts. MinDistanceToCoast
        // matched to the new shelf ramp so trenches start at the same offshore
        // distance where climate-base depth is reached.
        private const float RavinePeriodMeters         = 1300f;
        private const float RavineThreshold            = 0.66f;
        private const float RavineMaxAdditionalMeters  = 110f;
        private const float RavineMinDistanceToCoast   = 280f;

        // Seamount layer: sparse pronounced rises. Subtracts from depth so
        // the seafloor pushes upward toward the surface.
        private const float SeamountPeriodMeters       = 2500f;
        private const float SeamountThreshold          = 0.72f;
        private const float SeamountMaxLiftMeters      = 95f;
        private const float SeamountMinDistanceToCoast = 320f;

        // Classification thresholds: a feature must contribute at least this
        // fraction of its max value before we call the location after that
        // feature (e.g. "this is a seamount, not just a hilly area").
        private const float SeamountClassifyFraction = 0.30f;
        private const float RavineClassifyFraction   = 0.30f;
        private const float HillSignedNoiseThreshold = 0.55f;

        public const float MaxAbsoluteDepth = 250f;

        private const float MacroSeedX    =  1000f;
        private const float MacroSeedZ    = -7000f;
        private const float MidSeedX      = -3300f;
        private const float MidSeedZ      =  4400f;
        private const float HighSeedX     =  5500f;
        private const float HighSeedZ     = -2200f;
        private const float RavineSeedX   =  9000f;
        private const float RavineSeedZ   =  9000f;
        private const float SeamountSeedX = -8400f;
        private const float SeamountSeedZ =  6700f;

        public static float SampleDepthMeters(
            float worldX,
            float worldZ,
            int climateIndex,
            float distanceToCoastMeters)
        {
            float climateBase = ClimateBaseDepth(climateIndex);
            float shelf = ComputeShelfDepth(climateBase, distanceToCoastMeters);

            float macro = SampleSignedPerlin(worldX, worldZ, MacroPeriodMeters, MacroSeedX, MacroSeedZ);
            float aroundShelf = shelf + macro * MacroAmplitudeFraction * shelf;

            float mid = SampleSignedPerlin(worldX, worldZ, MidPeriodMeters, MidSeedX, MidSeedZ) * MidAmplitudeMeters;
            float high = SampleSignedPerlin(worldX, worldZ, HighPeriodMeters, HighSeedX, HighSeedZ) * HighAmplitudeMeters;
            float ravine = ComputeRavineAddition(worldX, worldZ, distanceToCoastMeters);
            float seamount = ComputeSeamountLift(worldX, worldZ, distanceToCoastMeters);

            float rawDepth = aroundShelf + mid + high + ravine - seamount;

            float userMax = ResolveUserMaxDepth();
            float scale = userMax / MaxAbsoluteDepth;
            float scaledDepth = rawDepth * scale;
            float effectiveShelfMin = Mathf.Min(ShelfMinDepth, userMax * 0.4f);
            return Mathf.Clamp(scaledDepth, effectiveShelfMin, userMax);
        }

        public static float DepthBand01(float depthMeters)
        {
            return Mathf.Clamp01(depthMeters / MaxAbsoluteDepth);
        }

        public static float ClimateBaseDepth(int climateIndex)
        {
            switch (climateIndex)
            {
                case (int)MapsFile.Climates.Ocean:            return ClimateBaseOcean;
                case (int)MapsFile.Climates.Subtropical:      return ClimateBaseSubtropical;
                case (int)MapsFile.Climates.Rainforest:       return ClimateBaseRainforest;
                case (int)MapsFile.Climates.Swamp:            return ClimateBaseSwamp;
                case (int)MapsFile.Climates.Woodlands:        return ClimateBaseWoodlands;
                case (int)MapsFile.Climates.HauntedWoodlands: return ClimateBaseHaunted;
                case (int)MapsFile.Climates.MountainWoods:    return ClimateBaseMountainWood;
                case (int)MapsFile.Climates.Mountain:         return ClimateBaseMountain;
                case (int)MapsFile.Climates.Desert:           return ClimateBaseDesert;
                case (int)MapsFile.Climates.Desert2:          return ClimateBaseDesert;
                default:                                      return ClimateBaseOcean;
            }
        }

        /// <summary>
        /// Classify the seafloor at a position. External mod builders use this
        /// to decide where to place set pieces (e.g. sunken ships on plains).
        /// </summary>
        public static SeafloorGeographyInfo Classify(
            float worldX,
            float worldZ,
            int climateIndex,
            float distanceToCoastMeters)
        {
            SeafloorGeographyInfo info;
            info.DepthMeters = SampleDepthMeters(worldX, worldZ, climateIndex, distanceToCoastMeters);
            info.Kind = SeafloorGeographyKind.Plain;
            info.Magnitude = 0f;

            // Shore: within the inner shelf. Sandy gradual drop.
            if (distanceToCoastMeters < InnerShelfDistance)
            {
                info.Kind = SeafloorGeographyKind.Shore;
                info.Magnitude = 1f - Mathf.Clamp01(distanceToCoastMeters / InnerShelfDistance);
                return info;
            }

            // Seamount has priority — these are dramatic landmark features.
            float seamountLift = ComputeSeamountLift(worldX, worldZ, distanceToCoastMeters);
            if (seamountLift > SeamountMaxLiftMeters * SeamountClassifyFraction)
            {
                info.Kind = SeafloorGeographyKind.Seamount;
                info.Magnitude = Mathf.Clamp01(seamountLift / SeamountMaxLiftMeters);
                return info;
            }

            // Ravine: trench/canyon deepening.
            float ravineAdd = ComputeRavineAddition(worldX, worldZ, distanceToCoastMeters);
            if (ravineAdd > RavineMaxAdditionalMeters * RavineClassifyFraction)
            {
                info.Kind = SeafloorGeographyKind.Ravine;
                info.Magnitude = Mathf.Clamp01(ravineAdd / RavineMaxAdditionalMeters);
                return info;
            }

            // Continental slope: midshelf-to-ramp transition is steep.
            if (distanceToCoastMeters > MidShelfDistance && distanceToCoastMeters < ShelfRampMeters)
            {
                float dropFrac = (distanceToCoastMeters - MidShelfDistance) /
                                 Mathf.Max(1f, ShelfRampMeters - MidShelfDistance);
                info.Kind = SeafloorGeographyKind.Slope;
                info.Magnitude = 1f - Mathf.Abs(dropFrac - 0.5f) * 2f;
                return info;
            }

            // Hill vs plain based on mid-frequency layer magnitude.
            float midSigned = SampleSignedPerlin(worldX, worldZ, MidPeriodMeters, MidSeedX, MidSeedZ);
            float midAbs = Mathf.Abs(midSigned);
            if (midAbs > HillSignedNoiseThreshold)
            {
                info.Kind = SeafloorGeographyKind.Hill;
                info.Magnitude = Mathf.Clamp01((midAbs - HillSignedNoiseThreshold) /
                                               Mathf.Max(0.01f, 1f - HillSignedNoiseThreshold));
                return info;
            }

            info.Kind = SeafloorGeographyKind.Plain;
            info.Magnitude = 1f - midAbs;
            return info;
        }

        private static float ResolveUserMaxDepth()
        {
            if (DeepWaters.Instance == null)
                return MaxAbsoluteDepth;

            return Mathf.Clamp(DeepWaters.Instance.WaterDepth, 1f, MaxAbsoluteDepth);
        }

        // Four-stage continental shelf curve. Each stage uses a smoothstep
        // interpolation between its end depths, so transitions have visibly
        // gentle inflection rather than sharp kinks.
        private static float ComputeShelfDepth(float climateBase, float distanceToCoastMeters)
        {
            if (distanceToCoastMeters <= 0f)
                return ShelfMinDepth;

            if (distanceToCoastMeters < BeachDropDistance)
            {
                float t = distanceToCoastMeters / BeachDropDistance;
                float s = t * t * (3f - 2f * t);
                return Mathf.Lerp(ShelfMinDepth, BeachDropDepth, s);
            }

            if (distanceToCoastMeters < InnerShelfDistance)
            {
                float t = (distanceToCoastMeters - BeachDropDistance) /
                          (InnerShelfDistance - BeachDropDistance);
                float s = t * t * (3f - 2f * t);
                return Mathf.Lerp(BeachDropDepth, InnerShelfDepth, s);
            }

            if (distanceToCoastMeters < MidShelfDistance)
            {
                float t = (distanceToCoastMeters - InnerShelfDistance) /
                          (MidShelfDistance - InnerShelfDistance);
                float s = t * t * (3f - 2f * t);
                return Mathf.Lerp(InnerShelfDepth, MidShelfDepth, s);
            }

            if (distanceToCoastMeters < ShelfRampMeters)
            {
                float t = (distanceToCoastMeters - MidShelfDistance) /
                          (ShelfRampMeters - MidShelfDistance);
                float s = t * t * (3f - 2f * t);
                return Mathf.Lerp(MidShelfDepth, climateBase, s);
            }

            return climateBase;
        }

        private static float ComputeRavineAddition(float worldX, float worldZ, float distanceToCoastMeters)
        {
            if (distanceToCoastMeters < RavineMinDistanceToCoast)
                return 0f;

            float n = SamplePerlin01(worldX, worldZ, RavinePeriodMeters, RavineSeedX, RavineSeedZ);
            if (n < RavineThreshold)
                return 0f;

            float t = (n - RavineThreshold) / (1f - RavineThreshold);
            float smooth = t * t * (3f - 2f * t);
            return smooth * RavineMaxAdditionalMeters;
        }

        private static float ComputeSeamountLift(float worldX, float worldZ, float distanceToCoastMeters)
        {
            if (distanceToCoastMeters < SeamountMinDistanceToCoast)
                return 0f;

            float n = SamplePerlin01(worldX, worldZ, SeamountPeriodMeters, SeamountSeedX, SeamountSeedZ);
            if (n < SeamountThreshold)
                return 0f;

            float t = (n - SeamountThreshold) / (1f - SeamountThreshold);
            // Sharper peak ramp than ravines — seamounts should look like
            // pronounced rises, not gentle bumps.
            float peak = t * t;
            return peak * SeamountMaxLiftMeters;
        }

        private static float SamplePerlin01(float worldX, float worldZ, float periodMeters, float seedX, float seedZ)
        {
            float freq = 1f / Mathf.Max(0.0001f, periodMeters);
            return Mathf.PerlinNoise((worldX + seedX) * freq, (worldZ + seedZ) * freq);
        }

        private static float SampleSignedPerlin(float worldX, float worldZ, float periodMeters, float seedX, float seedZ)
        {
            return SamplePerlin01(worldX, worldZ, periodMeters, seedX, seedZ) * 2f - 1f;
        }
    }
}
