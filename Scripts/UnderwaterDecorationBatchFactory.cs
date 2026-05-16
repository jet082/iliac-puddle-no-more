// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;
using UnityEngine;

namespace DeepWaters
{
    internal static class UnderwaterDecorationBatchFactory
    {
        public const string GroupName = "DeepWaters_DecorationBatch";

        // Per-decoration size variation. DaggerfallBillboardBatch's
        // customScale field is in Daggerfall "twips" where divisor = 256,
        // so for a target size multiplier m, scale = (m - 1) * 256.
        //   m = 0.70 -> scale = -76.8
        //   m = 1.00 -> scale = 0  (vanilla size)
        //   m = 1.20 -> scale = +51.2
        // Only the replacement-aware code path supports per-item scale
        // (CustomInfo overload); the vanilla archive batch path stays at
        // the material's authored size.
        private const float DecorationScaleMin = 0.70f;
        private const float DecorationScaleMax = 1.20f;

        public static void Spawn(Transform terrainParent, List<UnderwaterDecorationPlacementInfo> positions)
        {
            if (terrainParent == null || positions == null || positions.Count == 0)
                return;

            Transform group = CreateDecorationGroup(terrainParent).transform;
            if (DaggerfallUnity.Settings.AssetInjection)
                SpawnReplacementAwareBillboards(group, positions);
            else
                SpawnArchiveBillboards(group, positions);
        }

        private static GameObject CreateDecorationGroup(Transform parent)
        {
            var group = new GameObject(GroupName);
            group.transform.parent = parent;
            group.transform.localPosition = Vector3.zero;
            group.transform.localRotation = Quaternion.identity;
            group.transform.localScale = Vector3.one;
            return group;
        }

        private static void SpawnArchiveBillboards(Transform parent, List<UnderwaterDecorationPlacementInfo> positions)
        {
            var archivePositions = new Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>>();
            for (int i = 0; i < positions.Count; i++)
                AddArchivePosition(archivePositions, positions[i]);

            SpawnArchiveBillboards(parent, archivePositions);
        }

        private static void SpawnArchiveBillboards(
            Transform parent,
            Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>> archivePositions)
        {
            foreach (KeyValuePair<int, List<DaggerfallBillboardBatch.BasicInfo>> pair in archivePositions)
                SpawnArchiveBillboards(parent, pair.Key, pair.Value);
        }

        private static void SpawnArchiveBillboards(
            Transform parent,
            int archive,
            List<DaggerfallBillboardBatch.BasicInfo> positions)
        {
            DaggerfallBillboardBatch batch = GameObjectHelper.CreateBillboardBatchGameObject(
                archive,
                parent);
            if (batch == null)
                return;

            batch.Clear();
            batch.gameObject.name = "DeepWaters_DecorationArchiveBatch_" + archive;
            ApplyArchiveAnimationSpeed(batch, archive);

#pragma warning disable 0618
            for (int i = 0; i < positions.Count; i++)
                batch.AddItem(positions[i]);
#pragma warning restore 0618

            batch.Apply();
            DeepWaterRendering.DisableShadows(batch.gameObject);
        }

        private static void SpawnReplacementAwareBillboards(Transform parent, List<UnderwaterDecorationPlacementInfo> positions)
        {
            var archivePositions = new Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>>();
            var replacementPositions = new Dictionary<UnderwaterDecorationRecord, List<Vector3>>();

            for (int i = 0; i < positions.Count; i++)
            {
                UnderwaterDecorationPlacementInfo item = positions[i];
                UnderwaterDecorationRecord record = item.ToRecord();
                UnderwaterDecorationReplacementInfo replacementInfo;
                if (UnderwaterDecorationCatalog.UsesArchiveAnimation(record) ||
                    !UnderwaterDecorationReplacementCache.TryGet(record, out replacementInfo))
                {
                    AddArchivePosition(archivePositions, item);
                    continue;
                }

                List<Vector3> recordPositions;
                if (!replacementPositions.TryGetValue(record, out recordPositions))
                {
                    recordPositions = new List<Vector3>();
                    replacementPositions.Add(record, recordPositions);
                }

                recordPositions.Add(item.LocalPosition);
            }

            if (archivePositions.Count > 0)
                SpawnArchiveBillboards(parent, archivePositions);

            foreach (KeyValuePair<UnderwaterDecorationRecord, List<Vector3>> pair in replacementPositions)
            {
                UnderwaterDecorationReplacementInfo replacementInfo;
                if (UnderwaterDecorationReplacementCache.TryGet(pair.Key, out replacementInfo))
                    SpawnReplacementBatch(parent, pair.Key, pair.Value, replacementInfo);
            }
        }

        private static void SpawnReplacementBatch(
            Transform parent,
            UnderwaterDecorationRecord record,
            List<Vector3> positions,
            UnderwaterDecorationReplacementInfo info)
        {
            DaggerfallBillboardBatch batch = GameObjectHelper.CreateBillboardBatchGameObject(
                record.Archive,
                parent);
            if (batch == null)
                return;

            batch.Clear();
            batch.gameObject.name =
                "DeepWaters_DecorationReplacementBatch_" + record.Archive + "_" + record.Record;
            batch.SetMaterial(info.Material);

#pragma warning disable 0618
            for (int i = 0; i < positions.Count; i++)
            {
                float sizeFactor = Random.Range(DecorationScaleMin, DecorationScaleMax);
                float scaleValue = (sizeFactor - 1f) * BlocksFile.ScaleDivisor;
                Vector2 scale = new Vector2(scaleValue, scaleValue);
                batch.AddItem(info.Rect, info.BatchSize, scale, positions[i]);
            }
#pragma warning restore 0618

            batch.Apply();
            DeepWaterRendering.DisableShadows(batch.gameObject);
        }

        private static void AddArchivePosition(
            Dictionary<int, List<DaggerfallBillboardBatch.BasicInfo>> archivePositions,
            UnderwaterDecorationPlacementInfo item)
        {
            List<DaggerfallBillboardBatch.BasicInfo> positions;
            if (!archivePositions.TryGetValue(item.Archive, out positions))
            {
                positions = new List<DaggerfallBillboardBatch.BasicInfo>();
                archivePositions.Add(item.Archive, positions);
            }

            positions.Add(new DaggerfallBillboardBatch.BasicInfo(item.Record, item.LocalPosition));
        }

        private static void ApplyArchiveAnimationSpeed(DaggerfallBillboardBatch batch, int archive)
        {
            float framesPerSecond;
            if (UnderwaterDecorationCatalog.TryGetFramesPerSecond(archive, out framesPerSecond))
                batch.FramesPerSecond = framesPerSecond;
        }
    }
}
