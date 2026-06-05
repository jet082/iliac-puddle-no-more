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
        private static readonly MobileTypes[] AquaticTypes =
        {
            MobileTypes.Slaughterfish,
            MobileTypes.Slaughterfish,
            MobileTypes.Slaughterfish,
            MobileTypes.Dreugh,
            MobileTypes.Lamia,
        };

        private static readonly MobileTypes[] RareTypes =
        {
            MobileTypes.Ghost,
            MobileTypes.SkeletalWarrior,
            MobileTypes.Wraith,
            MobileTypes.Zombie,
            MobileTypes.IceAtronach,
        };

        private const float RareSpawnChance = 0.05f;
        private const float SpawnViewportMargin = 0.08f;
        private const float MinEnemyPulseIntervalSeconds = 10f;
        private const float FailedEnemyRetrySeconds = 4f;
        private const float DespawnDistance = 160f;
        private const float MinimumColumnDepth = 4f;
        private const float EnemySeafloorClearance = 2.5f;
        private const float EnemySurfaceClearance = 3f;
        private const float EnemyColumnFractionMin = 0.28f;
        private const float EnemyColumnFractionMax = 0.78f;
        private const int CandidateAttemptsPerSpawn = 8;
        private const float OutdoorAquaticSwimSpeed = 3.8f;
        private const float OutdoorAquaticStopDistance = 2.25f;
        private const float TreasureGuardMinDistance = 8f;
        private const float TreasureGuardMaxDistance = 30f;
        private const int MaxTreasureGuardCount = 5;
        private const float EnemyFrequencyForMaxTreasureGuards = 0.6f;

        public const int FullSpawnCount = 8;
        public const float SpawnRateScale = 0.4f;
        private const int MaxLiveEnemies = 16;

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

        public static void Uninstall()
        {
            if (!installed)
                return;

            DeepWaterRuntime.OnTransientReset -= OnTransientReset;
            ResetRuntimeState(true);
            installed = false;
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
            if (liveEnemies.Count >= MaxLiveEnemies)
                return true;

            int targetCount = RollScaledSpawnCount(FullSpawnCount);
            targetCount = Mathf.Min(targetCount, MaxLiveEnemies - liveEnemies.Count);
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
                Vector3 spawnPoint = DeepWaterWorld.PickRingPoint(
                    playerPos,
                    DeepWaterWorld.EncounterSpawnMinDistance,
                    DeepWaterWorld.EncounterSpawnMaxDistance);

                Vector3 resolvedPos;
                Transform parent;
                if (!TryResolveSpawnPosition(spawnPoint.x, spawnPoint.z, out resolvedPos, out parent))
                    continue;

                if (!DeepWaterWorld.IsOutsideImmediateView(resolvedPos, playerPos, DeepWaterWorld.EncounterSpawnViewSafetyDistance, SpawnViewportMargin))
                    continue;

                MobileTypes[] pool = Random.value < RareSpawnChance ? RareTypes : AquaticTypes;
                if (SpawnEnemy(resolvedPos, parent, pool) != null)
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
                if (!TryResolveSpawnPosition(worldX, worldZ, out resolvedPos, out parent))
                    continue;

                if (SpawnEnemy(resolvedPos, parent, RareTypes) != null)
                    spawned++;
            }

            if (spawned == 0)
            {
                Vector3 resolvedPos;
                Transform parent;
                if (TryResolveSpawnPosition(centre.x, centre.z, out resolvedPos, out parent))
                {
                    spawned = SpawnEnemy(resolvedPos, parent, RareTypes) != null ? 1 : 0;
                }
            }

            return spawned;
        }

        private static bool TryResolveSpawnPosition(float worldX, float worldZ, out Vector3 worldPos, out Transform parent)
        {
            worldPos = Vector3.zero;
            parent = null;

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

            float t = Random.Range(EnemyColumnFractionMin, EnemyColumnFractionMax);
            worldPos = new Vector3(worldX, Mathf.Lerp(floorClearY, surfaceClearY, t), worldZ);
            parent = column.Parent;
            return parent != null;
        }

        private static GameObject SpawnEnemy(Vector3 worldPos, Transform parent, MobileTypes[] pool)
        {
            if (parent == null || pool == null || pool.Length == 0)
                return null;

            MobileTypes type = pool[Random.Range(0, pool.Length)];
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

            MobileUnit mobile = enemy.GetComponentInChildren<MobileUnit>();
            if (mobile != null && mobile.Enemy.Behaviour == MobileBehaviour.Aquatic &&
                enemy.GetComponent<OutdoorAquaticEnemyPilot>() == null)
            {
                enemy.AddComponent<OutdoorAquaticEnemyPilot>();
            }

            Physics.SyncTransforms();
        }

        public static int RollScaledSpawnCount(int fullSpawnCount)
        {
            float scaledCount = fullSpawnCount * DeepWaters.Instance.EnemyFrequency * SpawnRateScale;
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

        private static bool IsEnemyStillInWater(GameObject enemy)
        {
            DeepWaterColumn column;
            Vector3 position = enemy.transform.position;
            return DeepWaterWorld.TryGetWaterColumn(position.x, position.z, out column) &&
                   column.Depth >= MinimumColumnDepth &&
                   position.y <= column.OceanWorldY + 3f;
        }

        private class OutdoorAquaticEnemyPilot : MonoBehaviour
        {
            private CharacterController controller;

            void Awake()
            {
                controller = GetComponent<CharacterController>();
            }

            void Update()
            {
                if (!DeepWaterRuntime.CanRunHeavyRuntimeWork)
                    return;

                GameManager gameManager = GameManager.Instance;
                GameObject player = gameManager != null ? gameManager.PlayerObject : null;
                if (player == null)
                    return;

                Vector3 position = transform.position;
                DeepWaterColumn column;
                if (!DeepWaterWorld.TryGetWaterColumn(position.x, position.z, out column))
                    return;

                float seafloorWorldY;
                if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, position.x, position.z, out seafloorWorldY))
                    return;

                float minY = seafloorWorldY + EnemySeafloorClearance;
                float maxY = column.OceanWorldY - EnemySurfaceClearance;
                if (maxY <= minY)
                    return;

                position.y = Mathf.Clamp(position.y, minY, maxY);
                Vector3 target = player.transform.position;
                target.y = Mathf.Clamp(target.y, minY, maxY);

                Vector3 delta = target - position;
                if (delta.sqrMagnitude <= OutdoorAquaticStopDistance * OutdoorAquaticStopDistance)
                {
                    MoveTo(position);
                    return;
                }

                Vector3 motion = delta.normalized * OutdoorAquaticSwimSpeed * Time.deltaTime;
                Vector3 next = position + motion;
                next.y = Mathf.Clamp(next.y, minY, maxY);
                MoveTo(next);
            }

            private void MoveTo(Vector3 worldPosition)
            {
                if (controller != null && controller.enabled)
                    controller.Move(worldPosition - transform.position);
                else
                    transform.position = worldPosition;
            }
        }

    }
}
