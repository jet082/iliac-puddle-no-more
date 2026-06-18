// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Per-map-pixel population driver for moving underwater encounters.
    ///
    /// Instead of pulsing spawns in front of the player on a distance clock, this
    /// keeps every loaded water map pixel within range populated: a pixel inside
    /// PopulateRadius is filled (fish + enemies scattered across its water area),
    /// and a populated pixel that drops past DespawnRadius — or unloads — is culled
    /// whole. Because the cull only happens once a pixel is well past vision, it is
    /// never visible. Per-frame spawn work is metered so a freshly entered pixel
    /// fills over a few ticks instead of hitching.
    /// </summary>
    internal static class UnderwaterEncounterPulse
    {
        private const float TickInterval = 0.3f;
        private const float PopulateRadius = 200f;
        private const float DespawnRadius = 300f;
        // Per-pixel, per-tick attempt caps. Every in-range pixel gets a few
        // attempts each tick so the live-cap budget fills all of them in parallel
        // (even spread) rather than the nearest pixel eating the whole cap.
        private const int FishAttemptsPerPixelPerTick = 6;
        private const int EnemyAttemptsPerPixelPerTick = 12;
        private const float DisableClearGraceSeconds = 2f;

        private static GameObject driverObject;
        private static float nextTickTime;
        private static float fishDisabledSince = -1f;
        private static float enemyDisabledSince = -1f;
        private static bool installed;

        private static readonly List<DaggerfallTerrain> loadedDfTerrains = new List<DaggerfallTerrain>();
        private static readonly List<Terrain> loadedTerrains = new List<Terrain>();
        private static readonly HashSet<long> keepKeys = new HashSet<long>();
        private static readonly List<PopulateCandidate> populateCandidates = new List<PopulateCandidate>();

        private struct PopulateCandidate
        {
            public DaggerfallTerrain Terrain;
            public float EdgeDistance;
        }

        public static void Install()
        {
            if (installed)
                return;

            DeepWaterRuntime.OnTransientReset += ResetState;

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
                if (Time.time < nextTickTime)
                    return;
                nextTickTime = Time.time + TickInterval;

                if (!DeepWaterRuntime.CanRunHeavyRuntimeWork)
                {
                    ClearEverything();
                    return;
                }

                UnderwaterPassiveFishSpawner.UpdateInventoryState();

                bool fishEnabled = UnderwaterPassiveFishSpawner.CanPopulate();
                bool enemiesEnabled = UnderwaterEnemySpawner.CanPopulate();

                HandleDisable(fishEnabled, ref fishDisabledSince, UnderwaterPassiveFishSpawner.ClearAll);
                HandleDisable(enemiesEnabled, ref enemyDisabledSince, UnderwaterEnemySpawner.ClearAll);

                if (!fishEnabled && !enemiesEnabled)
                    return;

                Vector3 playerPos;
                if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos))
                    return;

                CollectPixels(playerPos);

                if (fishEnabled)
                    UnderwaterPassiveFishSpawner.TickDespawn(keepKeys);
                if (enemiesEnabled)
                    UnderwaterEnemySpawner.TickDespawn(keepKeys);

                for (int i = 0; i < populateCandidates.Count; i++)
                {
                    DaggerfallTerrain terrain = populateCandidates[i].Terrain;
                    if (fishEnabled)
                    {
                        int fishBudget = FishAttemptsPerPixelPerTick;
                        UnderwaterPassiveFishSpawner.TickPopulate(terrain, ref fishBudget);
                    }
                    if (enemiesEnabled)
                    {
                        int enemyBudget = EnemyAttemptsPerPixelPerTick;
                        UnderwaterEnemySpawner.TickPopulate(terrain, ref enemyBudget);
                    }
                }
            }
        }

        // Build this tick's keep set (loaded pixels within DespawnRadius) and the
        // populate candidates (loaded water pixels within PopulateRadius), sorted
        // nearest-first so the pixel the player is in or approaching fills first.
        private static void CollectPixels(Vector3 playerPos)
        {
            keepKeys.Clear();
            populateCandidates.Clear();

            DeepWaterTerrainLookup.GetLoadedTerrains(loadedDfTerrains, loadedTerrains);
            float tileSize = DeepWaterWorld.TileWorldSize;

            for (int i = 0; i < loadedDfTerrains.Count; i++)
            {
                DaggerfallTerrain dfTerrain = loadedDfTerrains[i];
                if (dfTerrain == null)
                    continue;

                Vector3 origin = dfTerrain.transform.position;
                float edgeDistance = NearestEdgeDistance(playerPos, origin.x, origin.z, tileSize);
                if (edgeDistance > DespawnRadius)
                    continue;

                keepKeys.Add(DeepWaterWorld.TileKey(dfTerrain.MapPixelX, dfTerrain.MapPixelY));

                if (edgeDistance <= PopulateRadius && IsWaterPixel(dfTerrain))
                {
                    PopulateCandidate candidate;
                    candidate.Terrain = dfTerrain;
                    candidate.EdgeDistance = edgeDistance;
                    populateCandidates.Add(candidate);
                }
            }

            populateCandidates.Sort(CompareCandidateDistance);
        }

        private static int CompareCandidateDistance(PopulateCandidate a, PopulateCandidate b)
        {
            return a.EdgeDistance.CompareTo(b.EdgeDistance);
        }

        private static bool IsWaterPixel(DaggerfallTerrain dfTerrain)
        {
            DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();
            return tile != null && tile.IsOceanConnected && tile.HasDistanceField;
        }

        private static float NearestEdgeDistance(Vector3 playerPos, float originX, float originZ, float size)
        {
            float dx = Mathf.Max(originX - playerPos.x, 0f, playerPos.x - (originX + size));
            float dz = Mathf.Max(originZ - playerPos.z, 0f, playerPos.z - (originZ + size));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static void HandleDisable(bool enabled, ref float disabledSince, System.Action clearAll)
        {
            if (enabled)
            {
                disabledSince = -1f;
                return;
            }

            if (disabledSince < 0f)
                disabledSince = Time.time;
            else if (Time.time - disabledSince >= DisableClearGraceSeconds)
                clearAll();
        }

        private static void ClearEverything()
        {
            UnderwaterPassiveFishSpawner.ClearAll();
            UnderwaterEnemySpawner.ClearAll();
        }

        private static void ResetState()
        {
            nextTickTime = 0f;
            fishDisabledSince = -1f;
            enemyDisabledSince = -1f;
            keepKeys.Clear();
            populateCandidates.Clear();
            ClearEverything();
        }
    }
}
