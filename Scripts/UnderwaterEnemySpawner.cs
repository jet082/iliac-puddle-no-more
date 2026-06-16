// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Utility;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Spawns aquatic enemies near the player using the same pulse shape as
    /// passive fish: when nearby ocean exists, roll a small count and spawn in
    /// a ring just outside immediate view.
    /// </summary>
    public static class UnderwaterEnemySpawner
    {
        // Depth-banded aquatic pool (issue 7). Each entry prefers a band of the
        // water column (fraction of max depth). Shallow coves read as living
        // water (slaughterfish, lamia sirens); the deep and abyss turn menacing
        // as dreugh take over and the drowned dead / ice atronachs — which used
        // to be a flat 5% everywhere — surface only down deep.
        private struct DepthAquatic
        {
            public MobileTypes Type;
            public int Weight;
            public float MinDepthFraction;
            public float MaxDepthFraction;

            public DepthAquatic(MobileTypes type, int weight, float minDepthFraction, float maxDepthFraction)
            {
                Type = type;
                Weight = weight;
                MinDepthFraction = minDepthFraction;
                MaxDepthFraction = maxDepthFraction;
            }
        }

        private const float DepthEdgeSoftness = 0.18f;

        private static readonly DepthAquatic[] DepthAquaticTable =
        {
            new DepthAquatic(MobileTypes.Slaughterfish, 60, 0.00f, 0.70f),
            new DepthAquatic(MobileTypes.Lamia,         18, 0.00f, 0.45f),
            new DepthAquatic(MobileTypes.Dreugh,        25, 0.15f, 1.00f),
            new DepthAquatic(MobileTypes.Zombie,         6, 0.40f, 1.00f),
            new DepthAquatic(MobileTypes.SkeletalWarrior,7, 0.45f, 1.00f),
            new DepthAquatic(MobileTypes.Ghost,          7, 0.50f, 1.00f),
            new DepthAquatic(MobileTypes.Wraith,         5, 0.60f, 1.00f),
            new DepthAquatic(MobileTypes.IceAtronach,    4, 0.70f, 1.00f),
        };

        // Treasure-cluster guards are always the dangerous deep types regardless
        // of where the cluster sits.
        private static readonly MobileTypes[] RareTypes =
        {
            MobileTypes.Ghost,
            MobileTypes.SkeletalWarrior,
            MobileTypes.Wraith,
            MobileTypes.Zombie,
            MobileTypes.IceAtronach,
        };
        private const float SpawnViewportMargin = 0.08f;
        private const float MinEnemyPulseIntervalSeconds = 10f;
        private const float FailedEnemyRetrySeconds = 4f;
        private const float DespawnDistance = 120f;
        private const float MinimumColumnDepth = 4f;
        private const float EnemySeafloorClearance = 2.5f;
        private const float EnemySurfaceClearance = 3f;
        private const float EnemyColumnFractionMin = 0.28f;
        private const float EnemyColumnFractionMax = 0.78f;
        private const int CandidateAttemptsPerSpawn = 8;
        private const float TreasureGuardMinDistance = 8f;
        private const float TreasureGuardMaxDistance = 30f;
        private const int MaxTreasureGuardCount = 5;
        private const float EnemyFrequencyForMaxTreasureGuards = 0.6f;

        public const int FullSpawnCount = 4;
        public const float SpawnRateScale = 0.75f;

        private static readonly TransientObjectTracker liveEnemies = new TransientObjectTracker();
        private static float nextAllowedPulseTime;
        private static bool installed;

        public static void Install()
        {
            if (installed)
                return;

            DeepWaterRuntime.OnTransientReset += OnTransientReset;
            installed = true;
        }


        private static void OnTransientReset()
        {
            ResetRuntimeState(true);
        }

        internal static bool CanRunFromEncounterPulse()
        {
            return CanKeepEnemiesAlive();
        }

        internal static bool RunEncounterPulse(bool allowImmediate)
        {
            if (!CanKeepEnemiesAlive())
                return false;

            if (!allowImmediate && Time.time < nextAllowedPulseTime)
                return false;

            Vector3 playerPos;
            if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos))
                return false;

            liveEnemies.Prune(playerPos, DespawnDistance, IsEnemyStillInWater);
            int maxLiveEnemies = GetMaxLiveEnemies();
            if (liveEnemies.Count >= maxLiveEnemies)
                return true;

            int targetCount = RollScaledSpawnCount(FullSpawnCount);
            targetCount = Mathf.Min(targetCount, maxLiveEnemies - liveEnemies.Count);
            if (targetCount <= 0)
                return true;

            int spawned = 0;
            int attempts = 0;
            while (spawned < targetCount && attempts < 5 * targetCount + 10)
            {
                attempts++;
                if (TrySpawnNearPlayer())
                    spawned++;
            }

            nextAllowedPulseTime = Time.time + (spawned > 0 ? MinEnemyPulseIntervalSeconds : FailedEnemyRetrySeconds);
            return true;
        }

        internal static void PruneFromEncounterPulse(Vector3 playerPos)
        {
            liveEnemies.Prune(playerPos, DespawnDistance, IsEnemyStillInWater);
        }

        internal static void ClearEncounterPulseObjects()
        {
            liveEnemies.Clear();
        }

        private static bool CanKeepEnemiesAlive()
        {
            if (DeepWaters.Instance == null)
                return false;

            if (!DeepWaterRuntime.CanRunHeavyRuntimeWork)
                return false;

            if (!DeepWaters.Instance.SpawnUnderwaterEnemies)
                return false;

            if (DeepWaters.Instance.EnemyFrequency <= 0f)
                return false;

            if (!DeepWaterWorld.IsPlayerInExteriorWaterContext())
                return false;

            return true;
        }

        private static bool TrySpawnNearPlayer()
        {
            Vector3 playerPos;
            if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos))
                return false;

            for (int i = 0; i < CandidateAttemptsPerSpawn; i++)
            {
                Vector3 spawnPoint;
                if (Random.value >= DeepWaterWorld.FogAheadSpawnChance ||
                    !DeepWaterWorld.TryPickFogAheadPoint(playerPos, DespawnDistance, out spawnPoint))
                {
                    spawnPoint = DeepWaterWorld.PickFrontRingPoint(
                        playerPos,
                        DeepWaterWorld.EncounterSpawnMinDistance,
                        DeepWaterWorld.EncounterSpawnMaxDistance);
                }

                Vector3 resolvedPos;
                Transform parent;
                float depthFraction;
                if (!TryResolveSpawnPosition(spawnPoint.x, spawnPoint.z, out resolvedPos, out parent, out depthFraction))
                    continue;

                if (!DeepWaterWorld.IsOutsideImmediateView(resolvedPos, playerPos, DeepWaterWorld.EncounterSpawnViewSafetyDistance, SpawnViewportMargin))
                    continue;

                if (SpawnEnemy(resolvedPos, parent, PickAquaticForDepth(depthFraction)) != null)
                    return true;
            }

            return false;
        }

        public static int TrySpawnRareEnemiesNearTreasureCluster(Vector3 centre)
        {
            if (DeepWaters.Instance == null || !DeepWaters.Instance.SpawnUnderwaterEnemies)
                return 0;

            if (!DeepWaterRuntime.CanRunHeavyRuntimeWork)
                return 0;

            Vector3 playerPos;
            if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos))
                return 0;

            int targetCount = RollTreasureGuardCount();
            if (targetCount <= 0)
                return 0;

            int spawned = 0;
            int attempts = 0;
            while (spawned < targetCount && attempts < 8 * targetCount + 15)
            {
                attempts++;

                float angle = Random.Range(0f, Mathf.PI * 2f);
                float dist = DeepWaterWorld.PickRingDistance(TreasureGuardMinDistance, TreasureGuardMaxDistance);
                float worldX = centre.x + Mathf.Cos(angle) * dist;
                float worldZ = centre.z + Mathf.Sin(angle) * dist;

                Vector3 resolvedPos;
                Transform parent;
                float depthFraction;
                if (!TryResolveSpawnPosition(worldX, worldZ, out resolvedPos, out parent, out depthFraction))
                    continue;

                if (!DeepWaterWorld.IsOutsideImmediateView(resolvedPos, playerPos, DeepWaterWorld.UnderwaterVisionDistance, SpawnViewportMargin))
                    continue;

                if (SpawnEnemy(resolvedPos, parent, PickRare()) != null)
                    spawned++;
            }

            if (spawned == 0)
            {
                Vector3 resolvedPos;
                Transform parent;
                float depthFraction;
                if (TryResolveSpawnPosition(centre.x, centre.z, out resolvedPos, out parent, out depthFraction))
                {
                    if (DeepWaterWorld.IsOutsideImmediateView(resolvedPos, playerPos, DeepWaterWorld.UnderwaterVisionDistance, SpawnViewportMargin))
                        spawned = SpawnEnemy(resolvedPos, parent, PickRare()) != null ? 1 : 0;
                }
            }

            return spawned;
        }

        private static bool TryResolveSpawnPosition(float worldX, float worldZ, out Vector3 worldPos, out Transform parent, out float depthFraction)
        {
            worldPos = Vector3.zero;
            parent = null;
            depthFraction = 0f;

            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
                return false;

            float seafloorWorldY;
            if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, worldX, worldZ, out seafloorWorldY))
                return false;

            float floorClearY = seafloorWorldY + EnemySeafloorClearance;
            float surfaceClearY = column.OceanWorldY - EnemySurfaceClearance;
            if (surfaceClearY - floorClearY < MinimumColumnDepth)
                return false;

            float maxDepth = DeepWaters.Instance != null ? Mathf.Max(1f, DeepWaters.Instance.WaterDepth) : 200f;
            depthFraction = Mathf.Clamp01(column.Depth / maxDepth);

            float t = Random.Range(EnemyColumnFractionMin, EnemyColumnFractionMax);
            worldPos = new Vector3(worldX, Mathf.Lerp(floorClearY, surfaceClearY, t), worldZ);
            parent = column.Parent;
            return parent != null;
        }

        // Depth-weighted aquatic pick (issue 7). Each candidate's weight tapers
        // to zero outside its depth band, so shallow water stays slaughterfish/
        // lamia and the abyss fills with dreugh and the drowned dead.
        private static MobileTypes PickAquaticForDepth(float depthFraction)
        {
            depthFraction = Mathf.Clamp01(depthFraction);

            float total = 0f;
            for (int i = 0; i < DepthAquaticTable.Length; i++)
                total += DepthBandWeight(DepthAquaticTable[i], depthFraction);

            if (total <= 0f)
                return MobileTypes.Slaughterfish;

            float roll = Random.value * total;
            for (int i = 0; i < DepthAquaticTable.Length; i++)
            {
                float w = DepthBandWeight(DepthAquaticTable[i], depthFraction);
                if (roll < w)
                    return DepthAquaticTable[i].Type;

                roll -= w;
            }

            return DepthAquaticTable[0].Type;
        }

        private static float DepthBandWeight(DepthAquatic entry, float depthFraction)
        {
            float band01;
            if (depthFraction >= entry.MinDepthFraction && depthFraction <= entry.MaxDepthFraction)
            {
                band01 = 1f;
            }
            else
            {
                float distance = depthFraction < entry.MinDepthFraction
                    ? entry.MinDepthFraction - depthFraction
                    : depthFraction - entry.MaxDepthFraction;
                band01 = Mathf.Clamp01(1f - distance / DepthEdgeSoftness);
            }

            return entry.Weight * band01;
        }

        private static MobileTypes PickRare()
        {
            return RareTypes[Random.Range(0, RareTypes.Length)];
        }

        private static GameObject SpawnEnemy(Vector3 worldPos, Transform parent, MobileTypes type)
        {
            if (parent == null)
                return null;

            Vector3 localPos = worldPos - parent.position;
            GameObject enemy = GameObjectHelper.CreateEnemy(
                type.ToString(),
                type,
                localPos,
                MobileGender.Unspecified,
                parent);

            if (enemy != null)
            {
                ConfigureSpawnedEnemy(enemy, worldPos);
                liveEnemies.Add(enemy);
            }

            return enemy;
        }

        private static void ConfigureSpawnedEnemy(GameObject enemy, Vector3 worldPos)
        {
            if (enemy == null)
                return;

            // GameObjectHelper aligns non-flying enemies with raycast ground,
            // which is correct on land but unreliable over carved ocean holes.
            // Put the enemy back at the resolved underwater position.
            enemy.transform.position = worldPos;

            EnemyMotor motor = enemy.GetComponent<EnemyMotor>();
            GameManager gameManager = GameManager.Instance;
            if (motor != null && gameManager != null && gameManager.PlayerEntityBehaviour != null)
                motor.MakeEnemyHostileToAttacker(gameManager.PlayerEntityBehaviour);
        }

        public static int RollScaledSpawnCount(int fullSpawnCount)
        {
            float scaledCount = fullSpawnCount * DeepWaters.Instance.EnemyFrequency * SpawnRateScale *
                                DeepWaterWorld.DepthSpawnMultiplier();
            return DeepWaterWorld.RollCount(scaledCount);
        }

        private static int RollTreasureGuardCount()
        {
            float scaledCount = MaxTreasureGuardCount * Mathf.Clamp01(DeepWaters.Instance.EnemyFrequency / EnemyFrequencyForMaxTreasureGuards);
            return Mathf.Clamp(DeepWaterWorld.RollCount(scaledCount), 0, MaxTreasureGuardCount);
        }

        private static void ResetRuntimeState(bool destroyEnemies)
        {
            nextAllowedPulseTime = 0f;

            if (destroyEnemies)
                liveEnemies.Clear();
        }

        private static int GetMaxLiveEnemies()
        {
            return DeepWaters.Instance != null ? DeepWaters.Instance.MaxLiveEnemies : 8;
        }

        private static bool IsEnemyStillInWater(GameObject enemy)
        {
            DeepWaterColumn column;
            Vector3 position = enemy.transform.position;
            return DeepWaterWorld.TryGetWaterColumn(position.x, position.z, out column) &&
                   column.Depth >= MinimumColumnDepth &&
                   position.y <= column.OceanWorldY + 3f;
        }

    }
}
