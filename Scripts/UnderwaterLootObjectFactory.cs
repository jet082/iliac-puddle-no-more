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
                DeepWaterWorld.AlignObjectBottomToWorldY(loot.gameObject, worldPos.y);
            if (tracker != null)
                tracker.Add(loot != null ? loot.gameObject : null);

            return loot;
        }

        public static void QueueRubbleSprite(
            Vector3 worldPos,
            Transform parent,
            Dictionary<Transform, List<DaggerfallBillboardBatch.BasicInfo>> rubbleBatches)
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
            Dictionary<Transform, List<DaggerfallBillboardBatch.BasicInfo>> rubbleBatches,
            int record)
        {
            List<DaggerfallBillboardBatch.BasicInfo> batchItems;
            if (!rubbleBatches.TryGetValue(parent, out batchItems))
            {
                batchItems = new List<DaggerfallBillboardBatch.BasicInfo>();
                rubbleBatches.Add(parent, batchItems);
            }

            worldPos.y = UnderwaterDecorationPlacement.ResolveBillboardBaseWorldY(
                UnderwaterLootCatalog.RubbleArchive,
                record,
                worldPos.y);

            batchItems.Add(new DaggerfallBillboardBatch.BasicInfo(record, worldPos - parent.position));
        }

        public static int SpawnRubbleBatches(
            Dictionary<Transform, List<DaggerfallBillboardBatch.BasicInfo>> rubbleBatches,
            TransientObjectTracker tracker)
        {
            int placed = 0;
            foreach (KeyValuePair<Transform, List<DaggerfallBillboardBatch.BasicInfo>> pair in rubbleBatches)
            {
                if (pair.Key == null || pair.Value == null || pair.Value.Count == 0)
                    continue;

                if (SpawnRubbleBatch(pair.Key, pair.Value, tracker))
                    placed += pair.Value.Count;
            }

            return placed;
        }

        private static bool SpawnRubbleBatch(
            Transform parent,
            List<DaggerfallBillboardBatch.BasicInfo> items,
            TransientObjectTracker tracker)
        {
            DaggerfallBillboardBatch batch = GameObjectHelper.CreateBillboardBatchGameObject(
                UnderwaterLootCatalog.RubbleArchive,
                parent);
            if (batch == null)
                return false;

            batch.Clear();

#pragma warning disable 0618
            for (int i = 0; i < items.Count; i++)
                batch.AddItem(items[i]);
#pragma warning restore 0618

            batch.Apply();
            batch.gameObject.name = "DeepWaters_LootRubbleBatch";
            DeepWaterRendering.DisableShadows(batch.gameObject);
            if (tracker != null)
                tracker.Add(batch.gameObject);

            return true;
        }
    }
}
