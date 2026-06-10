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

        // Continental-margin depth curve (v0.55.38). distance-to-coast drives a
        // single smooth descent that mirrors a real ocean margin: gentle and
        // wadeable across the near-shore SHELF, steepest through the mid-depth
        // continental SLOPE, then easing onto the abyssal plain at the FOOT.
        // One smoothstep gives that gentle-steep-gentle shape with no kinks.
        //
        // Genuine ocean depth now arrives much further out (full depth at
        // ShelfRampMeters) than the old steep 290m ramp — the deliberate trade
        // for a gradual, natural descent. At 2700m the ~210m climate-base drop
        // is spread over a long, wadeable slope (avg ~8% grade, no hard wall).
        // This MUST stay inside the baked edge-distance field's range: with the
        // baker's DistanceScaleMeters = 16 that range is 0..4080m, so 2700m is
        // reached with headroom and full ocean depth is still attained. The ramp
        // is a pure RUNTIME knob — tune it freely up to ~4000m with a recompile
        // (no rebake). Only shorten the bake's DistanceScaleMeters if you also
        // shorten this to match (ramp <= 255 * scale).
        //
        // Minimum depth of ANY carved water, including right at the carve
        // edge. Entering the swim state from standing needs a column deeper
        // than ~1.1m (the swim check point = transform + 0.30 must dip below
        // ocean + 0.10 with a standing controller); anything shallower is an
        // unswimmable wading bowl — water you're "in" but can neither swim
        // nor stand clear of. 1.6m guarantees swimmability everywhere there
        // is carved water; the vertical step this creates at the carve edge
        // is bridged by the seafloor mesh's shore skirt.
        // (Was 0.25 — "no visible step" — which produced the bowls.)
        public const float ShelfMinDepth      = 1.6f;
        public const float ShelfBreakDistance = 360f;  // shelf/slope split (geography classify only)
        public const float ShelfRampMeters    = 2700f; // distance at which full climate-base depth is reached
        // Fraction of straight-line descent blended into the smoothstep shelf
        // curve. Smoothstep alone has ~zero slope right at the shore (reads as
        // too flat off the beach); a linear term has real slope there. 0 = pure
        // smoothstep, higher = faster drop near shore. Both still reach full
        // depth at ShelfRampMeters, so the deep-water distance is unchanged.
        public const float ShelfNearShoreSteepen = 0.30f;

        // Macro layer: bay-scale "deep zones" vs "shallow zones".
        private const float MacroPeriodMeters       = 4200f;
        private const float MacroAmplitudeFraction  = 0.30f;
        private const float DeepMacroAmplitudeScale = 0.55f;

        // Mid layer: rolling hills and hummocks.
        private const float MidPeriodMeters    = 330f;
        private const float MidAmplitudeMeters = 18f;

        // High layer: micro-roughness.
        private const float HighPeriodMeters    = 24f;
        private const float HighAmplitudeMeters = 3.5f;

        // Abyssal layer: small, persistent deep-sea relief. The previous deep
        // basin often saturated at WaterDepth, clipping ordinary noise into a
        // flat sheet. These layers come in only offshore and ride on a slightly
        // shallower baseline so the final clamp does not erase them.
        private const float DeepPlainHeadroomMeters = 42f;
        private const float AbyssalSwellPeriodMeters = 680f;
        private const float AbyssalSwellAmplitudeMeters = 12f;
        private const float AbyssalUndulationPeriodMeters = 115f;
        private const float AbyssalUndulationAmplitudeMeters = 5.5f;

        // Shallow-water safety cap: ordinary negative noise can otherwise lift
        // shelf hills close enough to the waterline that the player can stand
        // on the seafloor with their head in open air. Preserve the true shore
        // contact, then ramp to a hard offshore minimum depth.
        private const float MinimumOffshoreNavigableDepthMeters = 11.2f;
        private const float NoStandDepthRampMeters = 128f;

        // Ravine layer: sparse linear-feeling deep cuts. Start well offshore so
        // trenches only cut the deep slope/abyss, not the shallow shelf. Scaled
        // out with the longer 2700m shelf ramp so they still land in deep water.
        private const float RavinePeriodMeters         = 1300f;
        private const float RavineThreshold            = 0.66f;
        private const float RavineMaxAdditionalMeters  = 110f;
        private const float RavineMinDistanceToCoast   = 1500f;

        // Seamount layer: sparse pronounced rises. Subtracts from depth so the
        // seafloor pushes upward. In deep water these scale from the local water
        // column instead of a fixed meter cap, allowing major peaks to climb
        // most of the way toward the surface without breaching it.
        private const float SeamountPeriodMeters       = 2500f;
        private const float SeamountThreshold          = 0.62f;
        private const float SeamountLiftDepthFraction  = 0.82f;
        private const float SeamountMinDistanceToCoast = 1500f;
        private const float VolcanicConePeriodMeters       = 1800f;
        private const float VolcanicConeThreshold          = 0.70f;
        private const float VolcanicConeLiftDepthFraction  = 0.88f;
        private const float VolcanicConeMinDistanceToCoast = 1800f;

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
            float climateBaseDepth,
            float distanceToCoastMeters)
        {
            float userMax = ResolveUserMaxDepth();
            float minimumDepth = ComputeMinimumNavigableDepth(distanceToCoastMeters, userMax);
            float scale = userMax / 200f;
            float deepOcean = ComputeDeepOcean01(distanceToCoastMeters);

            // climateBaseDepth is supplied PRE-BLENDED by the caller (bilinearly
            // across the 4 surrounding map pixels), so the per-climate base no
            // longer STEPS at map-pixel climate boundaries — the harsh walls,
            // seams, and abrupt shelf/deep texture changes that used to fall
            // exactly on a pixel line are gone, while regional depth variety
            // (shallow swamps, deep open ocean) is preserved between boundaries.
            climateBaseDepth = ApplyDeepPlainHeadroom(climateBaseDepth, userMax, minimumDepth, scale, deepOcean);
            float shelf = ComputeShelfDepth(climateBaseDepth, distanceToCoastMeters);

            float macro = SampleSignedPerlin(worldX, worldZ, MacroPeriodMeters, MacroSeedX, MacroSeedZ);
            float macroScale = Mathf.Lerp(1f, DeepMacroAmplitudeScale, deepOcean);
            float aroundShelf = shelf + macro * MacroAmplitudeFraction * macroScale * shelf;

            float mid = SampleSignedPerlin(worldX, worldZ, MidPeriodMeters, MidSeedX, MidSeedZ) * MidAmplitudeMeters;
            float high = SampleSignedPerlin(worldX, worldZ, HighPeriodMeters, HighSeedX, HighSeedZ) * HighAmplitudeMeters;
            float abyssal = ComputeAbyssalRelief(worldX, worldZ, deepOcean);
            float ravine = ComputeRavineAddition(worldX, worldZ, distanceToCoastMeters);
            float featureBaseDepth = aroundShelf + mid + high + abyssal + ravine;
            float minimumRawDepth = minimumDepth / Mathf.Max(0.0001f, scale);
            float seamount = ComputeSeamountLift(
                worldX,
                worldZ,
                distanceToCoastMeters,
                ComputePeakLiftCapacity(featureBaseDepth, minimumRawDepth, SeamountLiftDepthFraction));
            float volcanicCone = ComputeVolcanicConeLift(
                worldX,
                worldZ,
                distanceToCoastMeters,
                ComputePeakLiftCapacity(featureBaseDepth, minimumRawDepth, VolcanicConeLiftDepthFraction));

            float rawDepth = featureBaseDepth - seamount - volcanicCone;

            // The setting is presented as "maximum ocean depth". The original
            // 250m normalization made the default 200m setting scale every
            // bathymetry feature down to 80%, so even open bay tiles felt too
            // shallow. Use 200m as the authoring baseline, then clamp the
            // final value to the configured maximum.
            float scaledDepth = rawDepth * scale;
            return Mathf.Clamp(scaledDepth, minimumDepth, userMax);
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

        // Per-climate seafloor texture-band signal (vertex colour G). Public so
        // DeepWaterTileData can blend it across map-pixel boundaries the same way
        // it blends the base depth, keeping the texture continuous too.
        public static float ClimateBandSignal(int climateIndex)
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

        /// <summary>
        /// Classify the seafloor at a position. External mod builders use this
        /// to decide where to place set pieces (e.g. sunken ships on plains).
        /// </summary>
        public static SeafloorGeographyInfo Classify(
            float worldX,
            float worldZ,
            float climateBaseDepth,
            float distanceToCoastMeters)
        {
            SeafloorGeographyInfo info;
            info.DepthMeters = SampleDepthMeters(worldX, worldZ, climateBaseDepth, distanceToCoastMeters);
            info.Kind = SeafloorGeographyKind.Plain;
            info.Magnitude = 0f;

            // Shore: across the gentle near-shore shelf. Sandy gradual drop.
            if (distanceToCoastMeters < ShelfBreakDistance)
            {
                info.Kind = SeafloorGeographyKind.Shore;
                info.Magnitude = 1f - Mathf.Clamp01(distanceToCoastMeters / ShelfBreakDistance);
                return info;
            }

            // Seamount has priority — these are dramatic landmark features.
            float totalPeakProfile = Mathf.Max(
                ComputeSeamountProfile(worldX, worldZ, distanceToCoastMeters),
                ComputeVolcanicConeProfile(worldX, worldZ, distanceToCoastMeters));
            if (totalPeakProfile > SeamountClassifyFraction)
            {
                info.Kind = SeafloorGeographyKind.Seamount;
                info.Magnitude = Mathf.Clamp01(totalPeakProfile);
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

            // Continental slope: the steep mid-margin descent to the abyss.
            if (distanceToCoastMeters > ShelfBreakDistance && distanceToCoastMeters < ShelfRampMeters)
            {
                float dropFrac = (distanceToCoastMeters - ShelfBreakDistance) /
                                 Mathf.Max(1f, ShelfRampMeters - ShelfBreakDistance);
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

        private static float ComputeMinimumNavigableDepth(float distanceToCoastMeters, float userMaxDepth)
        {
            float effectiveShelfMin = Mathf.Min(ShelfMinDepth, userMaxDepth * 0.4f);
            float noStandDepth = Mathf.Min(MinimumOffshoreNavigableDepthMeters, userMaxDepth);
            if (noStandDepth <= effectiveShelfMin)
                return effectiveShelfMin;

            float t = Mathf.Clamp01(distanceToCoastMeters / NoStandDepthRampMeters);
            float smooth = t * t * (3f - 2f * t);
            return Mathf.Lerp(effectiveShelfMin, noStandDepth, smooth);
        }

        private static float ComputeDeepOcean01(float distanceToCoastMeters)
        {
            float t = (distanceToCoastMeters - ShelfBreakDistance) /
                      Mathf.Max(1f, ShelfRampMeters - ShelfBreakDistance);
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static float ApplyDeepPlainHeadroom(
            float climateBaseDepth,
            float userMaxDepth,
            float minimumDepth,
            float scale,
            float deepOcean)
        {
            if (scale <= 0f || deepOcean <= 0f)
                return climateBaseDepth;

            float headroom = Mathf.Min(DeepPlainHeadroomMeters, userMaxDepth * 0.28f) * deepOcean;
            float cappedUserDepth = Mathf.Max(minimumDepth, userMaxDepth - headroom);
            return Mathf.Min(climateBaseDepth, cappedUserDepth / scale);
        }

        private static float ComputeAbyssalRelief(float worldX, float worldZ, float deepOcean)
        {
            if (deepOcean <= 0f)
                return 0f;

            float swell = SampleSignedPerlin(worldX, worldZ, AbyssalSwellPeriodMeters, -6100f, 2700f) *
                          AbyssalSwellAmplitudeMeters;
            float ripple = SampleSignedPerlin(worldX, worldZ, AbyssalUndulationPeriodMeters, 4200f, -5100f) *
                           AbyssalUndulationAmplitudeMeters;
            return (swell + ripple) * deepOcean;
        }

        private static float ComputePeakLiftCapacity(
            float featureBaseDepth,
            float minimumRawDepth,
            float depthFraction)
        {
            return Mathf.Max(0f, featureBaseDepth - minimumRawDepth) *
                   Mathf.Clamp01(depthFraction);
        }

        // Four-stage continental shelf curve. Each stage uses a smoothstep
        // interpolation between its end depths, so transitions have visibly
        // gentle inflection rather than sharp kinks.
        private static float ComputeShelfDepth(float climateBase, float distanceToCoastMeters)
        {
            if (distanceToCoastMeters <= 0f)
                return ShelfMinDepth;
            if (distanceToCoastMeters >= ShelfRampMeters)
                return climateBase;

            // One smooth descent across the whole margin: gentle on the shelf
            // near shore, steepest through the mid-depth continental slope,
            // easing onto the abyssal plain at the foot. Smoothstep gives this
            // gentle-steep-gentle shape with no kinks — but it's dead flat right
            // at the shore, so blend in a little straight-line descent (which has
            // real slope at t=0) to drop a bit faster off the beach.
            float t = distanceToCoastMeters / ShelfRampMeters;
            float smooth = t * t * (3f - 2f * t);
            float s = ShelfNearShoreSteepen * t + (1f - ShelfNearShoreSteepen) * smooth;
            return Mathf.Lerp(ShelfMinDepth, climateBase, s);
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

        private static float ComputeSeamountProfile(float worldX, float worldZ, float distanceToCoastMeters)
        {
            if (distanceToCoastMeters < SeamountMinDistanceToCoast)
                return 0f;

            float n = SamplePerlin01(worldX, worldZ, SeamountPeriodMeters, SeamountSeedX, SeamountSeedZ);
            if (n < SeamountThreshold)
                return 0f;

            float t = (n - SeamountThreshold) / (1f - SeamountThreshold);
            // Broader than the old squared profile: the previous high-threshold
            // curve almost never reached the advertised lift fraction in real
            // Perlin fields, so large seamounts looked like modest hummocks.
            float peak = t * t * (3f - 2f * t);
            return Mathf.Clamp01(peak);
        }

        private static float ComputeSeamountLift(
            float worldX,
            float worldZ,
            float distanceToCoastMeters,
            float maxLiftMeters)
        {
            return ComputeSeamountProfile(worldX, worldZ, distanceToCoastMeters) *
                   Mathf.Max(0f, maxLiftMeters);
        }

        private static float ComputeVolcanicConeProfile(float worldX, float worldZ, float distanceToCoastMeters)
        {
            if (distanceToCoastMeters < VolcanicConeMinDistanceToCoast)
                return 0f;

            float n = SamplePerlin01(worldX, worldZ, VolcanicConePeriodMeters, 1800f, -9600f);
            if (n < VolcanicConeThreshold)
                return 0f;

            float t = (n - VolcanicConeThreshold) / (1f - VolcanicConeThreshold);
            // Keep cones pointier than seamounts, but avoid cubic suppression:
            // at high max depth, mid-strong volcano signals should visibly use
            // the available water column instead of requiring n ~= 1.
            float cone = t * Mathf.Sqrt(Mathf.Clamp01(t));
            return Mathf.Clamp01(cone);
        }

        private static float ComputeVolcanicConeLift(
            float worldX,
            float worldZ,
            float distanceToCoastMeters,
            float maxLiftMeters)
        {
            return ComputeVolcanicConeProfile(worldX, worldZ, distanceToCoastMeters) *
                   Mathf.Max(0f, maxLiftMeters);
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
