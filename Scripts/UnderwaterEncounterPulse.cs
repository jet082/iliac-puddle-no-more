// Project:         Iliac Puddle No More
// License:         MIT

using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// One shared pulse clock for moving underwater encounters.
    /// Fish and enemies use this same anchor and nearby-water gate so changing
    /// between shore, surface, and submerged states does not reroll one system
    /// while the other remains stable.
    /// </summary>
    internal static class UnderwaterEncounterPulse
    {
        private const float WaterProbeMinDistance = 35f;
        private const float PulseDistance = 35f;
        private const float MinimumColumnDepth = 4f;
        private const int NearbyOceanProbeDirections = 12;
        private const float NearbyOceanGateCheckInterval = 2f;
        private const float InactiveClearGraceSeconds = 1.5f;
        private const float MissingWaterGateGraceSeconds = 1.5f;

        private static GameObject driverObject;
        private static Vector3 lastPulseAnchor;
        private static bool hasPulseAnchor;
        private static readonly TimedGateCache oceanGate = new TimedGateCache(NearbyOceanGateCheckInterval);
        private static float enemiesInactiveSince = -1f;
        private static float fishInactiveSince = -1f;
        private static float missingWaterSince = -1f;
        private static bool installed;

        public static void Install()
        {
            if (installed)
                return;

            DeepWaterRuntime.OnTransientReset += ResetPulseState;

            if (driverObject == null)
            {
                driverObject = new GameObject("DeepWaters_EncounterPulseDriver");
                driverObject.AddComponent<EncounterPulseDriver>();
                Object.DontDestroyOnLoad(driverObject);
            }

            installed = true;
        }


        private class EncounterPulseDriver : MonoBehaviour
        {
            void Update()
            {
                if (!DeepWaterRuntime.CanRunHeavyRuntimeWork)
                {
                    ResetPulseState();
                    return;
                }

                UnderwaterPassiveFishSpawner.UpdateInventoryState();

                bool enemiesEnabled = UnderwaterEnemySpawner.CanRunFromEncounterPulse();
                bool fishEnabled = UnderwaterPassiveFishSpawner.CanRunFromEncounterPulse();

                bool clearEnemies = ShouldClearInactiveParticipant(enemiesEnabled, ref enemiesInactiveSince);
                bool clearFish = ShouldClearInactiveParticipant(fishEnabled, ref fishInactiveSince);

                if (clearEnemies)
                    UnderwaterEnemySpawner.ClearEncounterPulseObjects();
                if (clearFish)
                    UnderwaterPassiveFishSpawner.ClearEncounterPulseObjects();

                Vector3 playerPos;
                if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos))
                    return;

                if (!enemiesEnabled && !fishEnabled)
                {
                    if (clearEnemies && clearFish)
                        ResetPulseState();
                    return;
                }

                if (!HasNearbyEncounterWaterColumn(playerPos))
                {
                    if (missingWaterSince < 0f)
                        missingWaterSince = Time.time;
                    if (Time.time - missingWaterSince >= MissingWaterGateGraceSeconds)
                        hasPulseAnchor = false;

                    UnderwaterEnemySpawner.PruneFromEncounterPulse(playerPos);
                    UnderwaterPassiveFishSpawner.PruneFromEncounterPulse(playerPos);
                    return;
                }

                missingWaterSince = -1f;

                if (!hasPulseAnchor)
                {
                    lastPulseAnchor = playerPos;
                    hasPulseAnchor = true;
                    RunEncounterPulse(true, enemiesEnabled, fishEnabled);
                    return;
                }

                float dx = playerPos.x - lastPulseAnchor.x;
                float dz = playerPos.z - lastPulseAnchor.z;
                if (dx * dx + dz * dz < PulseDistance * PulseDistance)
                    return;

                bool pulseAccepted = RunEncounterPulse(false, enemiesEnabled, fishEnabled);
                if (pulseAccepted)
                    lastPulseAnchor = playerPos;
            }
        }

        private static bool RunEncounterPulse(bool allowImmediate, bool enemiesEnabled, bool fishEnabled)
        {
            bool pulseAccepted = false;

            if (enemiesEnabled)
                pulseAccepted |= UnderwaterEnemySpawner.RunEncounterPulse(allowImmediate);
            if (fishEnabled)
                pulseAccepted |= UnderwaterPassiveFishSpawner.RunEncounterPulse(allowImmediate);

            return pulseAccepted;
        }

        private static bool ShouldClearInactiveParticipant(bool enabled, ref float inactiveSince)
        {
            if (enabled)
            {
                inactiveSince = -1f;
                return false;
            }

            if (inactiveSince < 0f)
                inactiveSince = Time.time;

            return Time.time - inactiveSince >= InactiveClearGraceSeconds;
        }

        private static bool HasNearbyEncounterWaterColumn(Vector3 playerPos)
        {
            if (oceanGate.IsFresh)
                return oceanGate.Value;

            float depth;
            bool result = DeepWaterWorld.HasNearbyWaterColumn(
                playerPos,
                WaterProbeMinDistance,
                DeepWaterWorld.EncounterSpawnMaxDistance,
                NearbyOceanProbeDirections,
                MinimumColumnDepth,
                out depth);

            return oceanGate.Store(result);
        }

        private static void ResetPulseState()
        {
            hasPulseAnchor = false;
            oceanGate.Invalidate();
            enemiesInactiveSince = -1f;
            fishInactiveSince = -1f;
            missingWaterSince = -1f;
        }
    }
}

