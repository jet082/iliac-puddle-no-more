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
                // Always brighten: DFU's scene-lit billboard material renders dark
                // in the dim bay even when AssetInjection (HD/DREAM) is on, so the
                // treasure pile read much darker than the unlit decorations.
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
                {
                    // The batch mesh is baked relative to the tile origin, so the
                    // group's transform sits at the tile corner — often far from
                    // the wreck. Track an anchor at the rubble centroid instead so
                    // distance/count pruning measures from the actual wreck, not
                    // the corner (otherwise the immediate pulse fired when a loot
                    // menu closes prunes still-nearby rubble). Reparent the group
                    // under the anchor with a compensating offset so its baked
                    // billboards keep rendering at the original tile-relative spots.
                    Vector3 centroidLocal = ComputeCentroidLocal(pair.Value);
                    var anchor = new GameObject("DeepWaters_LootRubbleAnchor");
                    anchor.transform.SetParent(pair.Key, false);
                    anchor.transform.localPosition = centroidLocal;
                    group.transform.SetParent(anchor.transform, false);
                    group.transform.localPosition = -centroidLocal;
                    tracker.Add(anchor);
                }

                placed += pair.Value.Count;
            }

            return placed;
        }

        private static Vector3 ComputeCentroidLocal(List<UnderwaterDecorationPlacementInfo> items)
        {
            if (items == null || items.Count == 0)
                return Vector3.zero;

            Vector3 sum = Vector3.zero;
            for (int i = 0; i < items.Count; i++)
                sum += items[i].LocalPosition;

            return sum / items.Count;
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
