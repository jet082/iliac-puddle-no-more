// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    internal static class UnderwaterTreasureClusterSpawner
    {
        private const float ClusterDebrisRadius = 22f;
        private const int ClusterDebrisCount = 24;

        public static bool TrySpawn(TransientObjectTracker trackedObjects)
        {
            Vector3 centre;
            Transform parent;
            long spawnCellKey;
            if (!UnderwaterLootPlacement.PickSpawnSpot(out centre, out parent, out spawnCellKey))
                return false;

            int debrisPlaced = SpawnDebris(centre, trackedObjects);
            int lootPlaced = SpawnTreasure(centre, trackedObjects);

            if (debrisPlaced == 0 && lootPlaced == 0)
                return false;

            UnderwaterLootPlacement.RememberSpawnCell(spawnCellKey);
            UnderwaterEnemySpawner.TrySpawnRareEnemiesNearTreasureCluster(centre);
            return true;
        }

        private static int SpawnDebris(Vector3 centre, TransientObjectTracker trackedObjects)
        {
            int debrisCount = DeepWaters.Instance.TreasureCove ? ClusterDebrisCount * 2 : ClusterDebrisCount;
            var rubbleBatches = new Dictionary<Transform, List<DaggerfallBillboardBatch.BasicInfo>>();

            for (int i = 0; i < debrisCount; i++)
            {
                float r = Mathf.Sqrt(Random.value) * ClusterDebrisRadius;
                float angle = Random.Range(0f, Mathf.PI * 2f);
                Vector3 spot = new Vector3(
                    centre.x + Mathf.Cos(angle) * r,
                    0f,
                    centre.z + Mathf.Sin(angle) * r);

                Transform debrisParent;
                if (UnderwaterLootPlacement.ResolveSeafloorAt(spot.x, spot.z, out spot.y, out debrisParent))
                    UnderwaterLootObjectFactory.QueueRubbleSprite(spot, debrisParent, rubbleBatches);
            }

            return UnderwaterLootObjectFactory.SpawnRubbleBatches(rubbleBatches, trackedObjects);
        }

        private static int SpawnTreasure(Vector3 centre, TransientObjectTracker trackedObjects)
        {
            int lootMin = DeepWaters.Instance.TreasureCove ? 6 : 3;
            int lootMax = DeepWaters.Instance.TreasureCove ? 11 : 6;
            int lootCount = Random.Range(lootMin, lootMax);
            int lootPlaced = 0;
            var placedLootSpots = new List<Vector3>();

            for (int i = 0; i < lootCount; i++)
            {
                Vector3 spot;
                if (!UnderwaterLootPlacement.TryPickClusterLootSpot(centre, placedLootSpots, out spot))
                    continue;

                Transform spotParent;
                if (!UnderwaterLootPlacement.ResolveSeafloorAt(spot.x, spot.z, out spot.y, out spotParent))
                    continue;

                DaggerfallLoot loot = UnderwaterLootObjectFactory.SpawnLootContainer(spot, spotParent, trackedObjects);
                if (loot == null)
                    continue;

                FillClusterContainer(loot);
                placedLootSpots.Add(spot);
                lootPlaced++;
            }

            return lootPlaced;
        }

        private static void FillClusterContainer(DaggerfallLoot loot)
        {
            int minItems = DeepWaters.Instance.TreasureCove ? 4 : 2;
            int maxItems = DeepWaters.Instance.TreasureCove ? 9 : 5;
            int items = Random.Range(minItems, maxItems);
            for (int i = 0; i < items; i++)
                UnderwaterLootCatalog.FillRandomItem(loot);
        }
    }
}
