// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Spawns passive underwater fish as lightweight lootable billboards.
    /// </summary>
    public static class UnderwaterPassiveFishSpawner
    {
        public static readonly int[] CustomItemTemplateIndices = PassiveFishSpeciesCatalog.CustomItemTemplateIndices;
        public const ItemGroups FishItemGroup = PassiveFishSpeciesCatalog.FishItemGroup;

        private const int FullFishCount = UnderwaterEnemySpawner.FullSpawnCount * 2;
        private const int NormalMaxLiveFish = 36;
        private const int FishParadiseMaxLiveFish = 72;
        private const int LocationMaxLiveFish = 24;
        private const int LocationFishParadiseMaxLiveFish = 36;
        private const float FishParadiseSpawnMultiplier = 4f;
        private const float FishParadisePulseIntervalMultiplier = 0.5f;
        private const float SpawnViewportMargin = 0.08f;
        private const float MinFishPulseIntervalSeconds = 10f;
        private const float FailedFishRetrySeconds = 4f;
        private const float DespawnDistance = 100f;
        private const float ShoreFishPulseMultiplier = 0.5f;

        private static readonly TransientObjectTracker liveFish = new TransientObjectTracker();

        internal static int LiveFishCount
        {
            get { return liveFish.Count; }
        }
        private static GameObject iconBridgeObject;
        private static float nextAllowedPulseTime;
        private static bool installed;

        public static void Install()
        {
            if (installed)
                return;

            DeepWaterRuntime.OnTransientReset += OnTransientReset;
            if (iconBridgeObject == null)
            {
                iconBridgeObject = new GameObject("DeepWaters_FishLootIconBridge");
                iconBridgeObject.AddComponent<FishLootIconBridge>();
                Object.DontDestroyOnLoad(iconBridgeObject);
            }

            PassiveFishResources.CacheInventoryIcons();
            installed = true;
        }


        public static DaggerfallUnityItem CreateLongnoseButterflyfishItem()
        {
            return PassiveFishResources.CreateLongnoseButterflyfishItem();
        }

        private static void OnTransientReset()
        {
            ResetRuntimeState(true);
        }

        internal static void UpdateInventoryState()
        {
            PassiveFishResources.UpdateInventoryState();
        }

        internal static bool CanRunFromEncounterPulse()
        {
            return CanKeepFishAlive();
        }

        internal static bool RunEncounterPulse(bool allowImmediate)
        {
            if (!CanKeepFishAlive())
                return false;

            if (!allowImmediate && Time.time < nextAllowedPulseTime)
                return false;

            Vector3 playerPos;
            if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos))
                return false;

            int maxLiveFish = GetMaxLiveFish();
            liveFish.PruneByDistance(playerPos, DespawnDistance);
            liveFish.PruneToCount(playerPos, maxLiveFish);
            if (liveFish.Count >= maxLiveFish)
                return true;

            int targetCount = RollScaledFishCount(FullFishCount);
            targetCount = Mathf.Min(targetCount, maxLiveFish - liveFish.Count);
            if (targetCount <= 0)
                return true;

            int spawned = 0;
            int attempts = 0;
            while (spawned < targetCount && attempts < 5 * targetCount + 10)
            {
                attempts++;
                int remainingPulseBudget = Mathf.Min(targetCount - spawned, maxLiveFish - liveFish.Count);
                int spawnedThisAttempt = TrySpawnNearPlayer(remainingPulseBudget);
                if (spawnedThisAttempt > 0)
                    spawned += spawnedThisAttempt;
            }

            bool spawnedAny = spawned > 0;
            nextAllowedPulseTime = Time.time + (spawnedAny ? GetFishPulseInterval() : FailedFishRetrySeconds);
            return true;
        }

        internal static void PruneFromEncounterPulse(Vector3 playerPos)
        {
            liveFish.PruneByDistance(playerPos, DespawnDistance);
            liveFish.PruneToCount(playerPos, GetMaxLiveFish());
        }

        internal static void ClearEncounterPulseObjects()
        {
            liveFish.Clear();
        }

        private static void ResetRuntimeState(bool destroyFish)
        {
            nextAllowedPulseTime = 0f;

            if (destroyFish)
                liveFish.Clear();
        }

        private static bool CanKeepFishAlive()
        {
            if (DeepWaters.Instance == null)
                return false;

            if (!DeepWaterRuntime.CanRunHeavyRuntimeWork)
                return false;

            if (DeepWaters.Instance.PassiveFishFrequency <= 0f)
                return false;

            if (!DeepWaterWorld.IsPlayerInExteriorWaterContext())
                return false;

            if (!PassiveFishSpeciesCatalog.HasAnySpawnableSpecies())
                return false;

            return true;
        }

        // Resolve the owning tile's climate biome and the water-column depth
        // fraction (0..1 of max depth) at a candidate spawn point, so species
        // selection can favour biome- and depth-appropriate fish.
        private static void ResolveSpeciesContext(float worldX, float worldZ, out int climateIndex, out float depthFraction)
        {
            climateIndex = 0;
            depthFraction = 0f;

            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
                return;

            DeepWaterTileData tile = column.DaggerfallTerrain != null
                ? column.DaggerfallTerrain.GetComponent<DeepWaterTileData>()
                : null;
            if (tile != null)
                climateIndex = tile.ClimateIndex;

            float maxDepth = DeepWaters.Instance != null ? Mathf.Max(1f, DeepWaters.Instance.WaterDepth) : 200f;
            depthFraction = Mathf.Clamp01(column.Depth / maxDepth);
        }

        private static int TrySpawnNearPlayer(int maxFishToSpawn)
        {
            if (maxFishToSpawn <= 0)
                return 0;

            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerObject == null)
                return 0;

            Vector3 playerPos = gameManager.PlayerObject.transform.position;
            Vector3 spawnPoint = DeepWaterWorld.PickRingPoint(
                playerPos,
                DeepWaterWorld.EncounterSpawnMinDistance,
                DeepWaterWorld.EncounterSpawnMaxDistance);

            Vector3 worldPos;
            Transform parent;
            if (!PassiveFishPlacement.TryResolvePosition(spawnPoint.x, spawnPoint.z, out worldPos, out parent))
                return 0;

            if (!DeepWaterWorld.IsOutsideImmediateView(worldPos, playerPos, DeepWaterWorld.EncounterSpawnViewSafetyDistance, SpawnViewportMargin))
                return 0;

            // Pick the species for THIS location's biome + depth so each region
            // reads distinctly: reef fish in tropical shallows, abyssal fish out
            // in the cold deep, etc. (issues 6 & 7)
            int climateIndex;
            float depthFraction;
            ResolveSpeciesContext(spawnPoint.x, spawnPoint.z, out climateIndex, out depthFraction);
            PassiveFishSpecies species = PassiveFishSpeciesCatalog.PickRandom(climateIndex, depthFraction);
            if (species == null)
                return 0;

            int schoolSize = Random.Range(species.MinSchoolSize, species.MaxSchoolSize + 1);
            schoolSize = Mathf.Min(schoolSize, maxFishToSpawn);
            float schoolRadius = PassiveFishPlacement.GetSchoolRadius(schoolSize);
            Vector3 schoolCenter = worldPos;
            PassiveFishSchool school = schoolSize > 1
                ? new PassiveFishSchool(schoolCenter, schoolRadius, species.CruiseSpeedMultiplier, species.FleeSpeedMultiplier)
                : null;

            List<Vector3> schoolPositions = schoolSize > 1 ? new List<Vector3>() : null;
            int spawned = SpawnFish(worldPos, parent, species, school);
            if (spawned > 0 && schoolPositions != null)
                schoolPositions.Add(worldPos);

            for (int i = 1; i < schoolSize; i++)
            {
                Vector3 schoolmatePos;
                Transform schoolmateParent;
                if (!PassiveFishPlacement.TryPickSchoolmatePosition(schoolCenter, schoolRadius, schoolPositions, out schoolmatePos, out schoolmateParent))
                    continue;

                if (SpawnFish(schoolmatePos, schoolmateParent, species, school) > 0)
                {
                    spawned++;
                    if (schoolPositions != null)
                        schoolPositions.Add(schoolmatePos);
                }
            }

            return spawned;
        }

        private static int SpawnFish(Vector3 worldPos, Transform parent, PassiveFishSpecies species, PassiveFishSchool school)
        {
            GameObject go = PassiveFishFactory.Spawn(worldPos, parent, species, school);
            if (go == null)
                return 0;

            liveFish.Add(go);
            return 1;
        }

        private static int RollScaledFishCount(int fullSpawnCount)
        {
            float scaledCount = fullSpawnCount *
                                DeepWaters.Instance.PassiveFishFrequency *
                                UnderwaterEnemySpawner.SpawnRateScale *
                                GetWaterContextSpawnMultiplier() *
                                GetFishParadiseMultiplier();
            return DeepWaterWorld.RollCount(scaledCount);
        }

        private static float GetWaterContextSpawnMultiplier()
        {
            return DeepWaterWorld.IsPlayerInOrAboveDeepWater(PassiveFishPlacement.MinimumColumnDepth)
                ? 1f
                : ShoreFishPulseMultiplier;
        }

        private static int GetMaxLiveFish()
        {
            bool fishParadise = DeepWaters.Instance != null && DeepWaters.Instance.FishParadise;
            bool inLocation = IsPlayerInLoadedLocation();
            if (fishParadise)
                return inLocation ? LocationFishParadiseMaxLiveFish : FishParadiseMaxLiveFish;

            return inLocation ? LocationMaxLiveFish : NormalMaxLiveFish;
        }

        private static bool IsPlayerInLoadedLocation()
        {
            GameManager gameManager = GameManager.Instance;
            return gameManager != null &&
                   gameManager.PlayerGPS != null &&
                   gameManager.PlayerGPS.IsPlayerInLocationRect;
        }

        private static float GetFishParadiseMultiplier()
        {
            return DeepWaters.Instance != null && DeepWaters.Instance.FishParadise
                ? FishParadiseSpawnMultiplier
                : 1f;
        }

        private static float GetFishPulseInterval()
        {
            return DeepWaters.Instance != null && DeepWaters.Instance.FishParadise
                ? MinFishPulseIntervalSeconds * FishParadisePulseIntervalMultiplier
                : MinFishPulseIntervalSeconds;
        }

    }

}
