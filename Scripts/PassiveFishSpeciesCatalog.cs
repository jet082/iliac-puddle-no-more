// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Game.Items;
using UnityEngine;

namespace DeepWaters
{
    // Coarse water-biome buckets derived from the owning map pixel's land
    // climate. Flags so a species can inhabit several biomes; species that list
    // only one read as biome-exclusive. (issue 6)
    // NOTE: explicit integer literals (not 1<<n, and Any as a literal rather than
    // an OR of members). DFU compiles mods with Mono.CSharp, which is stricter
    // than Roslyn and reports "Enumeration type is not defined" on shift-based or
    // self-referential enum initializers.
    [System.Flags]
    internal enum WaterBiome
    {
        None      = 0,
        OpenOcean = 1,    // Ocean climate
        Tropical  = 2,    // Subtropical, Rainforest
        Temperate = 4,    // Woodlands, Haunted Woodlands
        Swamp     = 8,    // Swamp
        Cold      = 16,   // Mountain, Mountain Woods
        Desert    = 32,   // Desert coasts / oases
        Any       = 63,   // OpenOcean | Tropical | Temperate | Swamp | Cold | Desert
    }

    internal sealed class PassiveFishSpecies
    {
        public readonly int TemplateIndex;
        public readonly string ItemName;
        public readonly int TextureArchive;
        public readonly int TextureRecord;
        public readonly int SpawnWeight;
        public readonly float BillboardHeight;
        public readonly string[] TextureAssetNames;
        public readonly float CruiseSpeedMultiplier;
        public readonly float FleeSpeedMultiplier;
        public readonly int MinSchoolSize;
        public readonly int MaxSchoolSize;
        public readonly float MinHeightMultiplier;
        public readonly float MaxHeightMultiplier;
        public readonly float FleeDartHoldMin;
        public readonly float FleeDartHoldMax;
        // Biomes this species inhabits, and the band of the water column (as a
        // fraction of the location's max depth) it prefers. (issues 6 & 7)
        public readonly WaterBiome Biomes;
        public readonly float MinDepthFraction;
        public readonly float MaxDepthFraction;

        public Texture2D Texture;
        public Texture2D IconTexture;

        // NOTE: biomes / minDepthFraction / maxDepthFraction are REQUIRED (no
        // defaults), placed before the remaining optional params. DFU's old
        // Mono.CSharp hard-crashes the whole mod compile on an enum-typed
        // optional parameter that has a default value (both `WaterBiome b = X`
        // and `WaterBiome? b = null` fail), so the biome arg must not be
        // optional. Every species entry already supplies all three by name, so
        // this needs no call-site changes.
        public PassiveFishSpecies(
            int templateIndex,
            string itemName,
            int textureRecord,
            int spawnWeight,
            float billboardHeight,
            string[] textureAssetNames,
            float cruiseSpeedMultiplier,
            float fleeSpeedMultiplier,
            int minSchoolSize,
            int maxSchoolSize,
            WaterBiome biomes,
            float minDepthFraction,
            float maxDepthFraction,
            float minHeightMultiplier = 1f,
            float maxHeightMultiplier = 1f,
            float fleeDartHoldMin = 1.6f,
            float fleeDartHoldMax = 2.8f,
            int textureArchive = 216)
        {
            TemplateIndex = templateIndex;
            ItemName = itemName;
            TextureArchive = textureArchive;
            TextureRecord = textureRecord;
            SpawnWeight = spawnWeight;
            BillboardHeight = billboardHeight;
            TextureAssetNames = textureAssetNames;
            CruiseSpeedMultiplier = cruiseSpeedMultiplier;
            FleeSpeedMultiplier = fleeSpeedMultiplier;
            MinSchoolSize = Mathf.Max(1, minSchoolSize);
            MaxSchoolSize = Mathf.Max(MinSchoolSize, maxSchoolSize);
            MinHeightMultiplier = Mathf.Max(0.05f, minHeightMultiplier);
            MaxHeightMultiplier = Mathf.Max(MinHeightMultiplier, maxHeightMultiplier);
            FleeDartHoldMin = Mathf.Max(0.1f, fleeDartHoldMin);
            FleeDartHoldMax = Mathf.Max(FleeDartHoldMin, fleeDartHoldMax);
            Biomes = biomes == WaterBiome.None ? WaterBiome.Any : biomes;
            MinDepthFraction = Mathf.Clamp01(Mathf.Min(minDepthFraction, maxDepthFraction));
            MaxDepthFraction = Mathf.Clamp01(Mathf.Max(minDepthFraction, maxDepthFraction));
        }

        // Soft depth preference: full weight inside [min,max], tapering to zero
        // over DepthEdgeSoftness outside it. Keeps depth-dwellers out of the
        // shallows (and reef fish off the abyssal plain) without hard popping.
        public float DepthWeight01(float depthFraction)
        {
            if (depthFraction >= MinDepthFraction && depthFraction <= MaxDepthFraction)
                return 1f;

            float distance = depthFraction < MinDepthFraction
                ? MinDepthFraction - depthFraction
                : depthFraction - MaxDepthFraction;
            return Mathf.Clamp01(1f - distance / PassiveFishSpeciesCatalog.DepthEdgeSoftness);
        }
    }

    internal static class PassiveFishSpeciesCatalog
    {
        public const int LongnoseButterflyfishTemplateIndex = 9001;
        public const int LargemouthBassTemplateIndex = 9002;
        public const int CanaryRockfishTemplateIndex = 9003;
        public const int CrucianCarpTemplateIndex = 9004;
        public const int MackerelTemplateIndex = 9005;
        public const int WhiteZebraAngelfishTemplateIndex = 9006;
        public const int FinulonTemplateIndex = 9007;
        public const ItemGroups FishItemGroup = ItemGroups.UselessItems2;

        // How far outside a species' depth band its weight tapers to zero, as a
        // fraction of max depth.
        public const float DepthEdgeSoftness = 0.18f;

        public static readonly int[] CustomItemTemplateIndices =
        {
            LongnoseButterflyfishTemplateIndex,
            LargemouthBassTemplateIndex,
            CanaryRockfishTemplateIndex,
            CrucianCarpTemplateIndex,
            MackerelTemplateIndex,
            WhiteZebraAngelfishTemplateIndex,
            FinulonTemplateIndex
        };

        // Add new passive fish here. spawnWeight is relative: 10 vs 1 means
        // the second fish appears at one tenth the rate of the first.
        //
        // biomes / depth band give each region a distinct cast: reef fish only in
        // tropical shallows, freshwater fish in swamp/temperate coasts, the
        // mackerel everywhere as the ubiquitous pelagic filler, and rockfish /
        // the rare Finulon out in the cold deep and abyss.
        public static readonly PassiveFishSpecies[] All =
        {
            new PassiveFishSpecies(
                LongnoseButterflyfishTemplateIndex,
                "Longnose Butterflyfish",
                42,
                10,
                0.5f,
                GenerateTextureAssetNames("longnose_butterflyfish", 42),
                1.0f,
                1.0f,
                8,
                16,
                minHeightMultiplier: 0.85f,
                maxHeightMultiplier: 1.15f,
                fleeDartHoldMin: 1.1f,
                fleeDartHoldMax: 2.2f,
                biomes: WaterBiome.Tropical,
                minDepthFraction: 0f,
                maxDepthFraction: 0.35f),
            new PassiveFishSpecies(
                LargemouthBassTemplateIndex,
                "Largemouth Bass",
                43,
                4,
                1.2f,
                GenerateTextureAssetNames("largemouth_bass", 43),
                1.3f,
                1.3f,
                1,
                2,
                minHeightMultiplier: 0.90f,
                maxHeightMultiplier: 1.25f,
                fleeDartHoldMin: 1.8f,
                fleeDartHoldMax: 3.2f,
                biomes: WaterBiome.Temperate | WaterBiome.Swamp | WaterBiome.Desert,
                minDepthFraction: 0f,
                maxDepthFraction: 0.45f),
            new PassiveFishSpecies(
                CanaryRockfishTemplateIndex,
                "Canary Rockfish",
                44,
                8,
                0.5f,
                GenerateTextureAssetNames("canary_rockfish", 44),
                1.2f,
                1.2f,
                2,
                4,
                minHeightMultiplier: 0.85f,
                maxHeightMultiplier: 1.15f,
                fleeDartHoldMin: 1.3f,
                fleeDartHoldMax: 2.4f,
                biomes: WaterBiome.Cold | WaterBiome.OpenOcean | WaterBiome.Temperate | WaterBiome.Desert,
                minDepthFraction: 0.35f,
                maxDepthFraction: 1.0f),
            new PassiveFishSpecies(
                CrucianCarpTemplateIndex,
                "Crucian Carp",
                45,
                6,
                1.2f,
                GenerateTextureAssetNames("crucian_carp", 45),
                0.8f,
                0.8f,
                1,
                3,
                minHeightMultiplier: 0.85f,
                maxHeightMultiplier: 1.20f,
                fleeDartHoldMin: 1.6f,
                fleeDartHoldMax: 3.0f,
                biomes: WaterBiome.Swamp | WaterBiome.Temperate,
                minDepthFraction: 0f,
                maxDepthFraction: 0.40f),
            new PassiveFishSpecies(
                MackerelTemplateIndex,
                "Mackerel",
                46,
                15,
                0.8f,
                GenerateTextureAssetNames("mackerel", 46),
                1.1f,
                1.1f,
                5,
                12,
                minHeightMultiplier: 0.80f,
                maxHeightMultiplier: 1.20f,
                fleeDartHoldMin: 0.9f,
                fleeDartHoldMax: 1.8f,
                biomes: WaterBiome.Any,
                minDepthFraction: 0.10f,
                maxDepthFraction: 1.0f),
            new PassiveFishSpecies(
                WhiteZebraAngelfishTemplateIndex,
                "White Zebra Angelfish",
                47,
                2,
                0.4f,
                GenerateTextureAssetNames("white_zebra_angelfish", 47),
                1.3f,
                1.3f,
                1,
                1,
                minHeightMultiplier: 0.85f,
                maxHeightMultiplier: 1.15f,
                fleeDartHoldMin: 1.1f,
                fleeDartHoldMax: 2.0f,
                biomes: WaterBiome.Tropical,
                minDepthFraction: 0f,
                maxDepthFraction: 0.35f),
            new PassiveFishSpecies(
                FinulonTemplateIndex,
                "Finulon",
                48,
                5,
                1.8f,
                GenerateTextureAssetNames("finulon", 48),
                1.2f,
                1.2f,
                1,
                1,
                minHeightMultiplier: 0.80f,
                maxHeightMultiplier: 1.10f,
                fleeDartHoldMin: 1.4f,
                fleeDartHoldMax: 2.6f,
                biomes: WaterBiome.OpenOcean | WaterBiome.Cold,
                minDepthFraction: 0.60f,
                maxDepthFraction: 1.0f),
        };

        private static readonly List<PassiveFishSpecies> spawnableSpecies = new List<PassiveFishSpecies>();
        private static bool spawnableCacheBuilt;
        private static int totalSpawnWeight;

        public static bool HasAnySpawnableSpecies()
        {
            BuildSpawnableCache();
            return totalSpawnWeight > 0;
        }

        public static WaterBiome ClimateToBiome(int climateIndex)
        {
            switch (climateIndex)
            {
                case (int)MapsFile.Climates.Ocean:            return WaterBiome.OpenOcean;
                case (int)MapsFile.Climates.Subtropical:      return WaterBiome.Tropical;
                case (int)MapsFile.Climates.Rainforest:       return WaterBiome.Tropical;
                case (int)MapsFile.Climates.Swamp:            return WaterBiome.Swamp;
                case (int)MapsFile.Climates.Woodlands:        return WaterBiome.Temperate;
                case (int)MapsFile.Climates.HauntedWoodlands: return WaterBiome.Temperate;
                case (int)MapsFile.Climates.MountainWoods:    return WaterBiome.Cold;
                case (int)MapsFile.Climates.Mountain:         return WaterBiome.Cold;
                case (int)MapsFile.Climates.Desert:           return WaterBiome.Desert;
                case (int)MapsFile.Climates.Desert2:          return WaterBiome.Desert;
                default:                                      return WaterBiome.OpenOcean;
            }
        }

        /// <summary>
        /// Weighted species pick that favours the location's biome and depth.
        /// Falls back to a plain weighted pick if nothing fits (so a spawn pulse
        /// never silently fails).
        /// </summary>
        public static PassiveFishSpecies PickRandom(int climateIndex, float depthFraction)
        {
            BuildSpawnableCache();
            if (totalSpawnWeight <= 0)
                return null;

            WaterBiome biome = ClimateToBiome(climateIndex);
            depthFraction = Mathf.Clamp01(depthFraction);

            float weightTotal = 0f;
            for (int i = 0; i < spawnableSpecies.Count; i++)
                weightTotal += EffectiveWeight(spawnableSpecies[i], biome, depthFraction);

            if (weightTotal <= 0f)
                return PickRandom();

            float roll = Random.value * weightTotal;
            for (int i = 0; i < spawnableSpecies.Count; i++)
            {
                float w = EffectiveWeight(spawnableSpecies[i], biome, depthFraction);
                if (roll < w)
                    return spawnableSpecies[i];

                roll -= w;
            }

            return spawnableSpecies[spawnableSpecies.Count - 1];
        }

        public static PassiveFishSpecies PickRandom()
        {
            BuildSpawnableCache();
            if (totalSpawnWeight <= 0)
                return null;

            int roll = Random.Range(0, totalSpawnWeight);
            for (int i = 0; i < spawnableSpecies.Count; i++)
            {
                PassiveFishSpecies species = spawnableSpecies[i];
                if (roll < species.SpawnWeight)
                    return species;

                roll -= species.SpawnWeight;
            }

            return spawnableSpecies[0];
        }

        private static float EffectiveWeight(PassiveFishSpecies species, WaterBiome biome, float depthFraction)
        {
            if ((species.Biomes & biome) == WaterBiome.None)
                return 0f;

            return species.SpawnWeight * species.DepthWeight01(depthFraction);
        }

        private static void BuildSpawnableCache()
        {
            if (spawnableCacheBuilt)
                return;

            spawnableSpecies.Clear();
            totalSpawnWeight = 0;

            for (int i = 0; i < All.Length; i++)
            {
                PassiveFishSpecies species = All[i];
                if (species.SpawnWeight <= 0 || !PassiveFishResources.LoadFishTexture(species))
                    continue;

                spawnableSpecies.Add(species);
                totalSpawnWeight += species.SpawnWeight;
            }

            spawnableCacheBuilt = true;
        }

        private static string[] GenerateTextureAssetNames(string baseName, int textureRecord)
        {
            string archiveName = "216_" + textureRecord + "-0";
            return new[]
            {
                baseName,
                baseName + ".png",
                archiveName,
                archiveName + ".png",
                "Assets/Game/Mods/deep-waters/Flats/" + baseName + ".png",
            };
        }
    }
}
