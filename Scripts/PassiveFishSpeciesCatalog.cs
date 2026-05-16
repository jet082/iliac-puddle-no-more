// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop.Game.Items;
using UnityEngine;

namespace DeepWaters
{
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

        public Texture2D Texture;
        public Texture2D IconTexture;

        public PassiveFishSpecies(
            int templateIndex,
            string itemName,
            int textureRecord,
            int spawnWeight,
            float billboardHeight,
            string[] textureAssetNames,
            float cruiseSpeedMultiplier,
            float fleeSpeedMultiplier,
            int minSchoolSize = 1,
            int maxSchoolSize = 1,
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
                fleeDartHoldMax: 2.2f),
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
                fleeDartHoldMax: 3.2f),
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
                fleeDartHoldMax: 2.4f),
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
                fleeDartHoldMax: 3.0f),
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
                fleeDartHoldMax: 1.8f),
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
                fleeDartHoldMax: 2.0f),
            new PassiveFishSpecies(
                FinulonTemplateIndex,
                "Finulon",
                48,
                1,
                1.8f,
                GenerateTextureAssetNames("finulon", 48),
                1.2f,
                1.2f,
                1,
                1,
                minHeightMultiplier: 0.80f,
                maxHeightMultiplier: 1.10f,
                fleeDartHoldMin: 1.4f,
                fleeDartHoldMax: 2.6f),
        };

        private static readonly List<PassiveFishSpecies> spawnableSpecies = new List<PassiveFishSpecies>();
        private static bool spawnableCacheBuilt;
        private static int totalSpawnWeight;

        public static bool HasAnySpawnableSpecies()
        {
            BuildSpawnableCache();
            return totalSpawnWeight > 0;
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
