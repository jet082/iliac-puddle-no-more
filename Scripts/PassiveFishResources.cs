// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using System.Reflection;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Utility;
using UnityEngine;

namespace DeepWaters
{
    internal static class PassiveFishResources
    {
        private const float FishItemGroupMigrationInterval = 2f;

        private static FieldInfo itemImagesField;
        private static float nextFishItemGroupMigrationTime;

        public static DaggerfallUnityItem CreateLongnoseButterflyfishItem()
        {
            return CreateFishItem(PassiveFishSpeciesCatalog.All[0]);
        }

        public static DaggerfallUnityItem CreateFishItem(PassiveFishSpecies species)
        {
            DaggerfallUnityItem item;
            return TryCreateFishItem(species, out item) ? item : null;
        }

        public static bool TryCreateFishItem(PassiveFishSpecies species, out DaggerfallUnityItem item)
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

        public static bool IsFishTemplateIndex(int templateIndex)
        {
            int[] indices = PassiveFishSpeciesCatalog.CustomItemTemplateIndices;
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] == templateIndex)
                    return true;
            }

            return false;
        }

        public static void UpdateInventoryState()
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

        public static void CacheInventoryIcons()
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

        public static bool LoadFishTexture(PassiveFishSpecies species)
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

            if (species.Texture == null)
                species.Texture = TryLoadTextureFromArchive(species);

            if (species.Texture == null)
                return false;

            species.Texture.filterMode = FilterMode.Point;
            return true;
        }

        public static bool LoadFishIconTexture(PassiveFishSpecies species)
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

        public static Texture2D GetFishIconTexture(PassiveFishSpecies species)
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

        private static Texture2D TryLoadTextureFromArchive(PassiveFishSpecies species)
        {
            var textureReader = DaggerfallUnity.Instance.MaterialReader.TextureReader;
            return textureReader.GetTexture2D(species.TextureArchive, species.TextureRecord, 0);
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
