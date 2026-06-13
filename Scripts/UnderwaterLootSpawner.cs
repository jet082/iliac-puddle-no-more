// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using System.Collections.Generic;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Spawns underwater loot as exploration encounters near the player.
    ///
    /// Loot is not pre-populated across whole terrain tiles. Instead, a
    /// lightweight driver runs only while the player is outdoors and either
    /// underwater or over deep ocean. Every so often, after enough horizontal
    /// travel, it rolls for a stray find and a rarer treasure cluster, then
    /// resolves just a few candidate points against the loaded heightmaps.
    ///
    /// This keeps the work proportional to exploration rather than to loaded
    /// terrain area, avoids visible pop-in by spawning beyond underwater fog
    /// distance, and clears transient objects when DFU rebuilds the world.
    /// </summary>
    public static class UnderwaterLootSpawner
    {
        // Distance-based top-up. Tile boundaries are 819m apart; this keeps loot
        // rhythm responsive while searching a bay or circling a wreck site.
        private const float LootPulseDistance = 90f;
        private const float MinPulseIntervalSeconds = 8f;
        private const float FailedPulseRetrySeconds = 3f;
        private const float DespawnDistance = 140f;

        // Rate 1.0 means up to this many stray finds per pulse. Fractional
        // counts are rolled, matching the enemy spawner's smooth scaling style.
        private const int FullStrayLootPerPulse = 2;
        private const float NormalLootMultiplier = 2f;
        private const float TreasureCoveStrayMultiplier = 3f;
        // Treasure clusters are event-like, so the setting maps directly to a
        // per-pulse chance rather than a count.
        private const float TreasureCoveClusterChanceMultiplier = 3f;
        private const float MaxClusterChance = 0.85f;
        private const float ShoreLootPulseMultiplier = 0.125f;
        private const float LooseLootDebrisChance = 0.75f;
        private const float TreasureCoveLooseLootDebrisChance = 0.95f;
        private const float LooseLootDebrisMinRadius = 1.5f;
        private const float LooseLootDebrisMaxRadius = 5.0f;
        private const int LooseLootDebrisSpotAttempts = 4;

        // Height and placement checks.
        private const float SurfaceLootOriginClearance = 8f;
        private const int NearbyWaterProbeDirections = 12;
        private const float NearbyWaterGateCheckInterval = 2f;

        private static readonly TransientObjectTracker trackedObjects = new TransientObjectTracker();

        private static GameObject pulseDriverObject;
        private static Vector3 lastPulseAnchor;
        private static bool hasPulseAnchor;
        private static float nextAllowedPulseTime;
        private static readonly TimedGateCache nearbyWaterGate = new TimedGateCache(NearbyWaterGateCheckInterval);
        private static bool installed;

        public static void Install()
        {
            if (installed)
                return;

            DeepWaterRuntime.OnTransientReset += OnTransientReset;

            if (pulseDriverObject == null)
            {
                pulseDriverObject = new GameObject("DeepWaters_LootPulseDriver");
                pulseDriverObject.AddComponent<LootPulseDriver>();
                Object.DontDestroyOnLoad(pulseDriverObject);
            }

            installed = true;
        }


        private static void OnTransientReset()
        {
            ResetRuntimeState();
        }

        private static void ResetRuntimeState()
        {
            UnderwaterLootPlacement.Reset();
            hasPulseAnchor = false;
            nextAllowedPulseTime = 0f;
            nearbyWaterGate.Invalidate();
            trackedObjects.Clear();
        }

        private class LootPulseDriver : MonoBehaviour
        {
            void Update()
            {
                if (!CanRunLootPulse())
                {
                    hasPulseAnchor = false;
                    return;
                }

                Vector3 playerPos;
                if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos)) return;

                if (!hasPulseAnchor)
                {
                    lastPulseAnchor = playerPos;
                    hasPulseAnchor = true;
                    TryRunLootPulse(true);
                    return;
                }

                float dx = playerPos.x - lastPulseAnchor.x;
                float dz = playerPos.z - lastPulseAnchor.z;
                if (dx * dx + dz * dz < LootPulseDistance * LootPulseDistance)
                    return;

                lastPulseAnchor = playerPos;
                TryRunLootPulse(false);
            }
        }

        private static void TryRunLootPulse(bool allowImmediate)
        {
            if (DeepWaters.Instance.SeafloorLootRate <= 0f && DeepWaters.Instance.TreasureClusterRate <= 0f)
                return;

            if (!allowImmediate && Time.time < nextAllowedPulseTime)
                return;

            Vector3 playerPos;
            if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos))
                return;

            float spawnMinDistance;
            float spawnMaxDistance;
            GetLootSpawnDistanceRange(out spawnMinDistance, out spawnMaxDistance);

            trackedObjects.PruneByDistance(playerPos, GetLootDespawnDistance());
            int maxLiveLoot = GetMaxLiveLootObjects();
            trackedObjects.PruneToCount(playerPos, maxLiveLoot);
            if (trackedObjects.Count >= maxLiveLoot)
                return;

            bool spawnedCluster = false;
            int spawnedStrays = 0;

            if (ShouldSpawnTreasureCluster())
                spawnedCluster = TrySpawnTreasureCluster(spawnMinDistance, spawnMaxDistance);

            int strayTarget = RollStrayLootCount(spawnedCluster);
            for (int i = 0; i < strayTarget; i++)
            {
                if (TrySpawnStrayLoot(spawnMinDistance, spawnMaxDistance))
                    spawnedStrays++;
            }

            bool spawnedAny = spawnedCluster || spawnedStrays > 0;
            nextAllowedPulseTime = Time.time + (spawnedAny ? MinPulseIntervalSeconds : FailedPulseRetrySeconds);
        }

        private static bool ShouldSpawnTreasureCluster()
        {
            float rate = Mathf.Max(0f, DeepWaters.Instance.TreasureClusterRate);
            if (rate <= 0f)
                return false;

            float multiplier = DeepWaters.Instance.TreasureCove ? TreasureCoveClusterChanceMultiplier : NormalLootMultiplier;
            float chance = Mathf.Min(rate * multiplier * GetWaterContextSpawnMultiplier(), MaxClusterChance);
            return Random.value < chance;
        }

        private static int RollStrayLootCount(bool spawnedCluster)
        {
            // A normal treasure pulse is already a find; keep it readable and
            // cheap by not adding unrelated stray piles in the same pulse.
            if (spawnedCluster && !DeepWaters.Instance.TreasureCove)
                return 0;

            float rate = Mathf.Max(0f, DeepWaters.Instance.SeafloorLootRate);
            if (rate <= 0f) return 0;

            float multiplier = DeepWaters.Instance.TreasureCove ? TreasureCoveStrayMultiplier : NormalLootMultiplier;
            float scaledCount = FullStrayLootPerPulse * rate * multiplier * GetWaterContextSpawnMultiplier();
            int count = DeepWaterWorld.RollCount(scaledCount);

            int max = DeepWaters.Instance.TreasureCove
                ? DeepWaters.Instance.TreasureCoveMaxStrayLootPerPulse
                : DeepWaters.Instance.MaxStrayLootPerPulse;
            return Mathf.Clamp(count, 0, max);
        }

        private static bool TrySpawnStrayLoot(float minSpawnDistance, float maxSpawnDistance)
        {
            Vector3 worldPos;
            Transform parent;
            long spawnCellKey;
            if (!UnderwaterLootPlacement.PickSpawnSpot(minSpawnDistance, maxSpawnDistance, out worldPos, out parent, out spawnCellKey))
                return false;

            DaggerfallLoot loot = UnderwaterLootObjectFactory.SpawnLootContainer(worldPos, parent, trackedObjects);
            if (loot == null) return false;

            UnderwaterLootPlacement.RememberSpawnCell(spawnCellKey);
            UnderwaterLootCatalog.FillRandomItem(loot);
            SpawnLooseLootDebris(worldPos, trackedObjects);
            return true;
        }

        private static int SpawnLooseLootDebris(Vector3 lootPos, TransientObjectTracker tracker)
        {
            float chance = DeepWaters.Instance.TreasureCove
                ? TreasureCoveLooseLootDebrisChance
                : LooseLootDebrisChance;
            if (Random.value >= chance)
                return 0;

            int targetCount = Random.Range(1, 3);
            var rubbleBatches = new Dictionary<Transform, List<UnderwaterDecorationPlacementInfo>>();
            var usedRecords = new HashSet<int>();

            for (int i = 0; i < targetCount; i++)
                TryQueueLooseLootDebris(lootPos, rubbleBatches, usedRecords);

            return UnderwaterLootObjectFactory.SpawnRubbleBatches(rubbleBatches, tracker);
        }

        private static bool TryQueueLooseLootDebris(
            Vector3 lootPos,
            Dictionary<Transform, List<UnderwaterDecorationPlacementInfo>> rubbleBatches,
            HashSet<int> usedRecords)
        {
            for (int attempt = 0; attempt < LooseLootDebrisSpotAttempts; attempt++)
            {
                float dist = Random.Range(LooseLootDebrisMinRadius, LooseLootDebrisMaxRadius);
                float angle = Random.Range(0f, Mathf.PI * 2f);
                Vector3 spot = new Vector3(
                    lootPos.x + Mathf.Cos(angle) * dist,
                    lootPos.y,
                    lootPos.z + Mathf.Sin(angle) * dist);

                Transform debrisParent;
                if (!UnderwaterLootPlacement.ResolveSeafloorAt(spot.x, spot.z, out spot.y, out debrisParent))
                    continue;

                int record = UnderwaterLootCatalog.PickRubbleRecordExcept(usedRecords);
                usedRecords.Add(record);
                UnderwaterLootObjectFactory.QueueRubbleSprite(spot, debrisParent, rubbleBatches, record);
                return true;
            }

            return false;
        }

        private static bool TrySpawnTreasureCluster(float minSpawnDistance, float maxSpawnDistance)
        {
            return UnderwaterTreasureClusterSpawner.TrySpawn(trackedObjects, minSpawnDistance, maxSpawnDistance);
        }

        private static bool CanRunLootPulse()
        {
            if (DeepWaters.Instance == null)
                return false;

            if (!DeepWaterRuntime.CanRunHeavyRuntimeWork)
                return false;

            if (DeepWaters.Instance.SeafloorLootRate <= 0f && DeepWaters.Instance.TreasureClusterRate <= 0f)
                return false;

            if (!DeepWaterWorld.IsPlayerInExteriorWaterContext())
                return false;

            var gameManager = GameManager.Instance;
            if (gameManager == null)
                return false;

            if (gameManager.StreamingWorld == null || gameManager.MainCamera == null)
                return false;

            Vector3 playerPos;
            if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos))
                return false;

            if (nearbyWaterGate.IsFresh)
                return nearbyWaterGate.Value;

            float minSpawnDistance;
            float maxSpawnDistance;
            GetLootSpawnDistanceRange(out minSpawnDistance, out maxSpawnDistance);

            float depth;
            bool result = DeepWaterWorld.HasNearbyWaterColumn(
                playerPos,
                minSpawnDistance,
                maxSpawnDistance,
                NearbyWaterProbeDirections,
                SurfaceLootOriginClearance,
                out depth);

            return nearbyWaterGate.Store(result);
        }

        private static float GetWaterContextSpawnMultiplier()
        {
            return DeepWaterWorld.IsPlayerInOrAboveDeepWater(SurfaceLootOriginClearance)
                ? 1f
                : ShoreLootPulseMultiplier;
        }

        private static void GetLootSpawnDistanceRange(out float minDistance, out float maxDistance)
        {
            if (DeepWaters.Instance == null)
            {
                minDistance = UnderwaterLootPlacement.MinSpawnDistance;
                maxDistance = UnderwaterLootPlacement.MaxSpawnDistance;
                return;
            }

            minDistance = DeepWaters.Instance.LootSpawnMinDistance;
            maxDistance = Mathf.Max(minDistance + 1f, DeepWaters.Instance.LootSpawnMaxDistance);
        }

        private static float GetLootDespawnDistance()
        {
            return DespawnDistance;
        }

        private static int GetMaxLiveLootObjects()
        {
            return DeepWaters.Instance != null ? DeepWaters.Instance.MaxLiveLootObjects : 32;
        }

    }
}
