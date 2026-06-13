// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;
using UnityEngine;

namespace DeepWaters
{
    internal static class UnderwaterLootObjectFactory
    {
        public static DaggerfallLoot SpawnLootContainer(
            Vector3 worldPos,
            Transform parent,
            TransientObjectTracker tracker)
        {
            int record = UnderwaterLootCatalog.PickTreasurePileRecord();
            DaggerfallLoot loot = GameObjectHelper.CreateLootContainer(
                LootContainerTypes.RandomTreasure,
                InventoryContainerImages.Ground,
                worldPos,
                parent,
                DaggerfallLootDataTables.randomTreasureArchive,
                record,
                DaggerfallUnity.NextUID,
                null,
                true);

            DeepWaterRendering.DisableShadows(loot != null ? loot.gameObject : null);
            if (loot != null)
            {
                if (!DaggerfallUnity.Settings.AssetInjection)
                    BrightenUnderwaterBillboards(loot.gameObject);

                DeepWaterWorld.AlignObjectBottomToWorldY(loot.gameObject, worldPos.y);
            }
            if (tracker != null)
                tracker.Add(loot != null ? loot.gameObject : null);

            return loot;
        }

        public static void QueueRubbleSprite(
            Vector3 worldPos,
            Transform parent,
            Dictionary<Transform, List<UnderwaterDecorationPlacementInfo>> rubbleBatches)
        {
            QueueRubbleSprite(
                worldPos,
                parent,
                rubbleBatches,
                UnderwaterLootCatalog.PickRubbleRecord());
        }

        public static void QueueRubbleSprite(
            Vector3 worldPos,
            Transform parent,
            Dictionary<Transform, List<UnderwaterDecorationPlacementInfo>> rubbleBatches,
            int record)
        {
            List<UnderwaterDecorationPlacementInfo> batchItems;
            if (!rubbleBatches.TryGetValue(parent, out batchItems))
            {
                batchItems = new List<UnderwaterDecorationPlacementInfo>();
                rubbleBatches.Add(parent, batchItems);
            }

            worldPos.y = UnderwaterDecorationPlacement.ResolveBillboardBaseWorldY(
                UnderwaterLootCatalog.RubbleArchive,
                record,
                worldPos.y);

            batchItems.Add(new UnderwaterDecorationPlacementInfo(
                new UnderwaterDecorationRecord(UnderwaterLootCatalog.RubbleArchive, record),
                worldPos - parent.position));
        }

        public static int SpawnRubbleBatches(
            Dictionary<Transform, List<UnderwaterDecorationPlacementInfo>> rubbleBatches,
            TransientObjectTracker tracker)
        {
            int placed = 0;
            foreach (KeyValuePair<Transform, List<UnderwaterDecorationPlacementInfo>> pair in rubbleBatches)
            {
                if (pair.Key == null || pair.Value == null || pair.Value.Count == 0)
                    continue;

                GameObject group = UnderwaterDecorationBatchFactory.Spawn(pair.Key, pair.Value);
                if (group == null)
                    continue;

                group.name = "DeepWaters_LootRubbleBatch";
                if (tracker != null)
                    tracker.Add(group);

                placed += pair.Value.Count;
            }

            return placed;
        }

        // Loot piles and wreck rubble are dark underwater because DFU's billboard
        // material is scene-lit and the bay is dim. Decorations avoid this with an
        // unlit, brightened material; apply the same to loot/rubble so they read
        // as clearly as the surrounding flora. (issue 12)
        private static void BrightenUnderwaterBillboards(GameObject go)
        {
            if (go == null)
                return;

            MeshRenderer[] renderers = go.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                UnderwaterDecorationBatchFactory.ApplyUnderwaterDecorationMaterial(renderers[i]);
        }
    }
}
