// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using System.Reflection;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Utility;
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
        internal readonly int TemplateIndex;
        internal readonly string ItemName;
        internal readonly int TextureArchive;
        internal readonly int TextureRecord;
        internal readonly int SpawnWeight;
        internal readonly float BillboardHeight;
        internal readonly string[] TextureAssetNames;
        internal readonly float CruiseSpeedMultiplier;
        internal readonly float FleeSpeedMultiplier;
        internal readonly int MinSchoolSize;
        internal readonly int MaxSchoolSize;
        internal readonly float MinHeightMultiplier;
        internal readonly float MaxHeightMultiplier;
        internal readonly float FleeDartHoldMin;
        internal readonly float FleeDartHoldMax;
        // Biomes this species inhabits, and the band of the water column (as a
        // fraction of the location's max depth) it prefers. (issues 6 & 7)
        internal readonly WaterBiome Biomes;
        internal readonly float MinDepthFraction;
        internal readonly float MaxDepthFraction;

        internal Texture2D Texture;
        internal Texture2D IconTexture;

        // NOTE: biomes / minDepthFraction / maxDepthFraction are REQUIRED (no
        // defaults), placed before the remaining optional params. DFU's old
        // Mono.CSharp hard-crashes the whole mod compile on an enum-typed
        // optional parameter that has a default value (both `WaterBiome b = X`
        // and `WaterBiome? b = null` fail), so the biome arg must not be
        // optional. Every species entry already supplies all three by name, so
        // this needs no call-site changes.
        internal PassiveFishSpecies(
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
        internal float DepthWeight01(float depthFraction)
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
        internal const int LongnoseButterflyfishTemplateIndex = 9001;
        internal const int LargemouthBassTemplateIndex = 9002;
        internal const int CanaryRockfishTemplateIndex = 9003;
        internal const int CrucianCarpTemplateIndex = 9004;
        internal const int MackerelTemplateIndex = 9005;
        internal const int WhiteZebraAngelfishTemplateIndex = 9006;
        internal const int FinulonTemplateIndex = 9007;
        internal const ItemGroups FishItemGroup = ItemGroups.UselessItems2;

        // How far outside a species' depth band its weight tapers to zero, as a
        // fraction of max depth.
        internal const float DepthEdgeSoftness = 0.18f;

        internal static readonly int[] CustomItemTemplateIndices =
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
        internal static readonly PassiveFishSpecies[] All =
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

        internal static bool HasAnySpawnableSpecies()
        {
            BuildSpawnableCache();
            return totalSpawnWeight > 0;
        }

        internal static WaterBiome ClimateToBiome(int climateIndex)
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
        internal static PassiveFishSpecies PickRandom(int climateIndex, float depthFraction)
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

        internal static PassiveFishSpecies PickRandom()
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

	internal static class PassiveFishResources
	{
		private const float FishItemGroupMigrationInterval = 2f;

		private static FieldInfo itemImagesField;
		private static float nextFishItemGroupMigrationTime;

		internal static bool TryCreateFishItem(PassiveFishSpecies species, out DaggerfallUnityItem item)
		{
			if (species == null)
			{
				item = null;
				return false;
			}

			item = ItemBuilder.CreateItem(PassiveFishSpeciesCatalog.FishItemGroup, species.TemplateIndex);
			CacheInventoryIcons();
			return item != null && item.TemplateIndex == species.TemplateIndex;
		}

		private static bool IsFishTemplateIndex(int templateIndex)
		{
			int[] indices = PassiveFishSpeciesCatalog.CustomItemTemplateIndices;
			for (int i = 0; i < indices.Length; i++)
			{
				if (indices[i] == templateIndex)
					return true;
			}

			return false;
		}

		internal static void UpdateInventoryState()
		{
			if (Time.time < nextFishItemGroupMigrationTime)
				return;

			nextFishItemGroupMigrationTime = Time.time + FishItemGroupMigrationInterval;

			var gameManager = GameManager.Instance;
			if (gameManager == null || gameManager.PlayerEntity == null)
				return;

			NormalizeFishItemCollection(gameManager.PlayerEntity.Items);
			NormalizeFishItemCollection(gameManager.PlayerEntity.WagonItems);
		}

		internal static void CacheInventoryIcons()
		{
			var itemImages = GetItemImageCache();
			if (itemImages == null)
				return;

			PassiveFishSpecies[] speciesTable = PassiveFishSpeciesCatalog.All;
			for (int i = 0; i < speciesTable.Length; i++)
			{
				PassiveFishSpecies species = speciesTable[i];
				if (!LoadFishIconTexture(species))
					continue;

				ImageData imageData = CreateFishIconImageData(species);
				itemImages[MakeItemImageKey((int)DyeColors.Unchanged, species.TextureArchive, species.TextureRecord, true)] = imageData;
				itemImages[MakeItemImageKey((int)DyeColors.Unchanged, species.TextureArchive, species.TextureRecord, false)] = imageData;
			}
		}

		internal static bool LoadFishTexture(PassiveFishSpecies species)
		{
			if (species == null)
				return false;

			if (species.Texture != null)
				return true;

			for (int i = 0; i < species.TextureAssetNames.Length; i++)
			{
				species.Texture = TryLoadTexture(species.TextureAssetNames[i]);
				if (species.Texture != null)
					break;
			}

			// Deliberately NOT falling back to vanilla archive 216 via
			// TextureReader. Archive 216 records 42-48 hold misc inventory
			// icons in vanilla Daggerfall data, and when the mod-bundled
			// PNG replacements fail to resolve, that fallback path made
			// every spawned fish appear as a random Daggerfall loot icon
			// (longsword, potion bottle, ...). It is much better to skip
			// the spawn entirely than to render the wrong content.
			if (species.Texture == null)
			{
				Debug.LogWarning("[DeepWaters] Could not load fish texture for '" + species.ItemName +
					"'. Tried " + species.TextureAssetNames.Length +
					" asset names; the species will not spawn until the PNGs resolve.");
				return false;
			}

			species.Texture.filterMode = FilterMode.Point;
			return true;
		}

		private static bool LoadFishIconTexture(PassiveFishSpecies species)
		{
			if (species == null)
				return false;

			if (species.IconTexture != null)
				return true;

			string archiveName = "216_" + species.TextureRecord + "-0";
			species.IconTexture = TryLoadTexture(archiveName);
			if (species.IconTexture == null)
				species.IconTexture = TryLoadTexture(archiveName + ".png");
			if (species.IconTexture == null)
				species.IconTexture = TryLoadTexture("Assets/Game/Mods/deep-waters/Flats/" + archiveName + ".png");

			for (int i = 0; species.IconTexture == null && i < species.TextureAssetNames.Length; i++)
				species.IconTexture = TryLoadTexture(species.TextureAssetNames[i]);

			if (species.IconTexture == null)
				return false;

			species.IconTexture.filterMode = FilterMode.Point;
			return true;
		}

		internal static Texture2D GetFishIconTexture(PassiveFishSpecies species)
		{
			if (species == null)
				return null;

			if (LoadFishIconTexture(species))
				return species.IconTexture;

			return species.Texture;
		}

		private static Dictionary<int, ImageData> GetItemImageCache()
		{
			var dfUnity = DaggerfallUnity.Instance;
			if (dfUnity == null || dfUnity.ItemHelper == null)
				return null;

			if (itemImagesField == null)
				itemImagesField = typeof(ItemHelper).GetField("itemImages", BindingFlags.Instance | BindingFlags.NonPublic);

			return itemImagesField != null
				? itemImagesField.GetValue(dfUnity.ItemHelper) as Dictionary<int, ImageData>
				: null;
		}

		private static ImageData CreateFishIconImageData(PassiveFishSpecies species)
		{
			Texture2D texture = species.IconTexture != null ? species.IconTexture : species.Texture;
			ImageData data = new ImageData();
			data.type = ImageTypes.TEXTURE;
			data.filename = string.Format("TEXTURE.{0:000}", species.TextureArchive);
			data.record = species.TextureRecord;
			data.frame = 0;
			data.hasAlpha = true;
			data.alphaIndex = 0;
			data.width = texture.width;
			data.height = texture.height;
			data.texture = texture;
			data.size = new DFSize(texture.width, texture.height);
			data.scale = new DFSize(texture.width, texture.height);
			return data;
		}

		private static int MakeItemImageKey(int color, int archive, int record, bool removeMask)
		{
			int mask = removeMask ? 1 : 0;
			return (color << 27) + (archive << 18) + (record << 11) + (mask << 10);
		}

		private static void NormalizeFishItemCollection(ItemCollection items)
		{
			if (items == null)
				return;

			for (int i = 0; i < items.Count; i++)
			{
				DaggerfallUnityItem item = items.GetItem(i);
				if (item == null || item.ItemGroup == PassiveFishSpeciesCatalog.FishItemGroup || !IsFishTemplateIndex(item.TemplateIndex))
					continue;

				int stackCount = item.stackCount;
				int currentCondition = item.currentCondition;
				int maxCondition = item.maxCondition;
				item.SetItem(PassiveFishSpeciesCatalog.FishItemGroup, item.TemplateIndex);
				item.stackCount = stackCount;
				item.currentCondition = currentCondition;
				item.maxCondition = maxCondition;
			}
		}

		private static Texture2D TryLoadTexture(string assetName)
		{
			if (DeepWaters.Mod == null)
				return null;

			if (!DeepWaters.Mod.HasAsset(assetName))
			{
				if (DeepWaters.Mod.AssetBundle == null)
					DeepWaters.Mod.LoadAssetBundle();

				if (!DeepWaters.Mod.HasAsset(assetName))
					return null;
			}

			return DeepWaters.Mod.GetAsset<Texture2D>(assetName);
		}
	}
}
