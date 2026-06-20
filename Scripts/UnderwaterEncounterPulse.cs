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
		private const float PopulateRadiusSq = PopulateRadius * PopulateRadius;
		private const float DespawnRadiusSq = DespawnRadius * DespawnRadius;
        // Per-pixel, per-tick attempt caps. Every in-range pixel gets a few
		// attempts each tick so the live-cap budget fills all of them in parallel
		// (even spread) rather than the nearest pixel eating the whole cap.
		private const int FishAttemptsPerPixelPerTick = 6;
		private const int EnemyAttemptsPerPixelPerTick = 12;
		private const float DisableClearGraceSeconds = 2f;

        private static float nextTickTime;
        private static float fishDisabledSince = -1f;
        private static float enemyDisabledSince = -1f;
        private static bool installed;

        private static readonly List<DaggerfallTerrain> loadedDfTerrains = new List<DaggerfallTerrain>();
        private static readonly HashSet<long> keepKeys = new HashSet<long>();
        private static readonly List<PopulateCandidate> populateCandidates = new List<PopulateCandidate>();

        private struct PopulateCandidate
        {
            internal DaggerfallTerrain Terrain;
            internal float EdgeDistanceSq;
        }

        internal static void Install()
        {
			if (installed)
				return;

			DeepWaterRuntime.OnTransientReset += ResetState;

			installed = true;
		}

		internal static void Pump()
		{
			if (Time.time < nextTickTime)
				return;
			nextTickTime = Time.time + TickInterval;

			if (!DeepWaterRuntime.CanRunHeavyRuntimeWork)
			{
				ClearEverything();
				return;
			}

			PassiveFishResources.UpdateInventoryState();

			bool exteriorWater = DeepWaterWorld.IsPlayerInExteriorWaterContext();
			bool fishEnabled = exteriorWater && UnderwaterPassiveFishSpawner.CanPopulate();
			bool enemiesEnabled = exteriorWater && UnderwaterEnemySpawner.CanPopulate();

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

        // Build this tick's keep set (loaded pixels within DespawnRadius) and the
        // populate candidates (loaded water pixels within PopulateRadius), sorted
        // nearest-first so the pixel the player is in or approaching fills first.
        private static void CollectPixels(Vector3 playerPos)
        {
            keepKeys.Clear();
            populateCandidates.Clear();

            DeepWaterTerrainLookup.GetLoadedTerrains(loadedDfTerrains, null);
            float tileSize = DeepWaterWorld.TileWorldSize;

            for (int i = 0; i < loadedDfTerrains.Count; i++)
            {
                DaggerfallTerrain dfTerrain = loadedDfTerrains[i];
                if (dfTerrain == null)
                    continue;

                Vector3 origin = dfTerrain.transform.position;
                float edgeDistanceSq = NearestEdgeDistanceSq(playerPos, origin.x, origin.z, tileSize);
                if (edgeDistanceSq > DespawnRadiusSq)
                    continue;

                keepKeys.Add(DeepWaterWorld.TileKey(dfTerrain.MapPixelX, dfTerrain.MapPixelY));

                if (edgeDistanceSq <= PopulateRadiusSq && IsWaterPixel(dfTerrain))
                {
                    PopulateCandidate candidate;
                    candidate.Terrain = dfTerrain;
                    candidate.EdgeDistanceSq = edgeDistanceSq;
                    populateCandidates.Add(candidate);
                }
            }

            populateCandidates.Sort((a, b) => a.EdgeDistanceSq.CompareTo(b.EdgeDistanceSq));
        }

		private static bool IsWaterPixel(DaggerfallTerrain dfTerrain)
		{
			DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();
			return tile != null && tile.IsOceanConnected && tile.HasDistanceField;
		}

		private static float NearestEdgeDistanceSq(Vector3 playerPos, float originX, float originZ, float size)
        {
            float dx = Mathf.Max(originX - playerPos.x, 0f, playerPos.x - (originX + size));
            float dz = Mathf.Max(originZ - playerPos.z, 0f, playerPos.z - (originZ + size));
            return dx * dx + dz * dz;
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

	internal sealed class TransientObjectTracker
	{
		private readonly List<GameObject> objects = new List<GameObject>();

		internal int Count
		{
			get { return objects.Count; }
		}

		internal void Add(GameObject go)
		{
			if (go != null)
				objects.Add(go);
		}

		internal void Clear()
		{
			for (int i = objects.Count - 1; i >= 0; i--)
			{
				if (objects[i] != null)
					UnityEngine.Object.Destroy(objects[i]);
			}

			objects.Clear();
		}

		internal void Prune(Vector3 playerPos, float maxFlatDistance, int maxCount)
		{
			maxCount = Mathf.Max(0, maxCount);
			float maxFlatDistanceSq = maxFlatDistance * maxFlatDistance;

			for (int i = objects.Count - 1; i >= 0; i--)
			{
				GameObject go = objects[i];
				if (go == null)
				{
					objects.RemoveAt(i);
					continue;
				}

				Vector3 delta = go.transform.position - playerPos;
				delta.y = 0f;
				if (delta.sqrMagnitude > maxFlatDistanceSq)
				{
					UnityEngine.Object.Destroy(go);
					objects.RemoveAt(i);
				}
			}

			while (objects.Count > maxCount)
			{
				int farthestIndex = -1;
				float farthestDistanceSq = -1f;

				for (int i = 0; i < objects.Count; i++)
				{
					GameObject go = objects[i];
					if (go == null)
					{
						farthestIndex = i;
						break;
					}

					Vector3 delta = go.transform.position - playerPos;
					delta.y = 0f;
					float distanceSq = delta.sqrMagnitude;
					if (distanceSq > farthestDistanceSq)
					{
						farthestDistanceSq = distanceSq;
						farthestIndex = i;
					}
				}

				if (farthestIndex < 0)
					return;

				GameObject remove = objects[farthestIndex];
				if (remove != null)
					UnityEngine.Object.Destroy(remove);

				objects.RemoveAt(farthestIndex);
			}
		}
	}
}
