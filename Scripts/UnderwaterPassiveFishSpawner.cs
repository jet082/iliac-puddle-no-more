// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Spawns passive underwater fish as lightweight lootable billboards.
    ///
    /// Population is per-MAP-PIXEL, not player-relative: when a water map pixel
    /// comes into range the spawner scatters base points uniformly across its
    /// whole area and seeds a small school at each that lands in valid water, so
    /// fish density tracks the pixel's water area (open ocean fills, shallow coves
    /// stay sparse) and nothing clumps along the player's path. The shared driver
    /// (UnderwaterEncounterPulse) decides which pixels to populate/despawn and
    /// meters the per-frame work; this class just owns the fish.
    /// </summary>
    public static class UnderwaterPassiveFishSpawner
    {
        public static readonly int[] CustomItemTemplateIndices = PassiveFishSpeciesCatalog.CustomItemTemplateIndices;
        public const ItemGroups FishItemGroup = PassiveFishSpeciesCatalog.FishItemGroup;

        // Base spawn attempts scattered uniformly across a map pixel. Each attempt
        // that lands in valid water seeds a SCHOOL (species school size), so the
        // realized fish count scales with the pixel's water area (open ocean fills,
        // shallow coves stay sparse). The hard ceiling on total live fish is the
        // MaxLiveFish setting; density below it is set by the frequency setting.
        private const int FishAttemptsPerPixel = 90;
        private const float PassiveFishFrequencyAtMidpoint = 3.0f;

        private sealed class PixelFishGroup
        {
            public readonly TransientObjectTracker Fish = new TransientObjectTracker();
            public int AttemptsRemaining;
        }

        private static readonly Dictionary<long, PixelFishGroup> pixelGroups = new Dictionary<long, PixelFishGroup>();
        private static readonly List<long> despawnScratch = new List<long>();
        private static GameObject iconBridgeObject;
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

        internal static void UpdateInventoryState()
        {
            PassiveFishResources.UpdateInventoryState();
        }

        internal static int LiveCount
        {
            get
            {
                int total = 0;
                foreach (var kv in pixelGroups)
                    total += kv.Value.Fish.Count;
                return total;
            }
        }

        // True when fish should exist at all right now (settings on, in an
        // exterior water context, species available). The driver gates on this.
        internal static bool CanPopulate()
        {
            return DeepWaters.Instance != null &&
                   DeepWaterRuntime.CanRunHeavyRuntimeWork &&
                   DeepWaters.Instance.PassiveFishFrequency > 0f &&
                   DeepWaterWorld.IsPlayerInExteriorWaterContext() &&
                   PassiveFishSpeciesCatalog.HasAnySpawnableSpecies();
        }

        private static void OnTransientReset()
        {
            ClearAll();
        }

        internal static void ClearAll()
        {
            foreach (var kv in pixelGroups)
                kv.Value.Fish.Clear();
            pixelGroups.Clear();
        }

        // Drop the fish of any populated pixel no longer in the keep set (out of
        // despawn range or unloaded). By the time a pixel leaves the keep range it
        // is far enough that all its fish are well past vision, so the cull is not
        // visible.
        internal static void TickDespawn(HashSet<long> keepKeys)
        {
            despawnScratch.Clear();
            foreach (var kv in pixelGroups)
            {
                if (!keepKeys.Contains(kv.Key))
                    despawnScratch.Add(kv.Key);
            }

            for (int i = 0; i < despawnScratch.Count; i++)
            {
                long key = despawnScratch[i];
                pixelGroups[key].Fish.Clear();
                pixelGroups.Remove(key);
            }
        }

        // Scatter up to attemptBudget base points across this pixel, seeding a
        // school at each that lands in valid water, until the pixel's attempt
        // allowance is spent. The driver caps attemptBudget per frame so a newly
        // entered pixel fills over a few ticks instead of hitching.
        internal static void TickPopulate(DaggerfallTerrain dfTerrain, ref int attemptBudget)
        {
            if (attemptBudget <= 0 || dfTerrain == null)
                return;

            long key = DeepWaterWorld.TileKey(dfTerrain.MapPixelX, dfTerrain.MapPixelY);
            PixelFishGroup group;
            if (!pixelGroups.TryGetValue(key, out group))
            {
                group = new PixelFishGroup { AttemptsRemaining = ScaledAttemptsPerPixel() };
                pixelGroups[key] = group;
            }

            if (group.AttemptsRemaining <= 0)
                return;

            int liveCap = EffectiveFishCap();
            Vector3 origin = dfTerrain.transform.position;
            float tileSize = DeepWaterWorld.TileWorldSize;

            while (attemptBudget > 0 && group.AttemptsRemaining > 0)
            {
                if (LiveCount >= liveCap)
                {
                    group.AttemptsRemaining = 0;
                    return;
                }

                attemptBudget--;
                group.AttemptsRemaining--;

                float worldX = origin.x + Random.value * tileSize;
                float worldZ = origin.z + Random.value * tileSize;

                int climateIndex;
                float depthFraction;
                ResolveSpeciesContext(worldX, worldZ, out climateIndex, out depthFraction);
                PassiveFishSpecies species = PassiveFishSpeciesCatalog.PickRandom(climateIndex, depthFraction);
                if (species == null)
                    continue;

                Vector3 worldPos;
                Transform parent;
                if (!PassiveFishPlacement.TryResolvePosition(worldX, worldZ, species, out worldPos, out parent))
                    continue;

				int remaining = liveCap - LiveCount;
				int schoolSize = Mathf.Min(Random.Range(species.MinSchoolSize, species.MaxSchoolSize + 1), remaining);
				if (schoolSize <= 0)
				{
					group.AttemptsRemaining = 0;
					return;
				}

				SpawnSchool(worldPos, parent, species, schoolSize, group);
            }
        }

		private static int SpawnSchool(Vector3 worldPos, Transform parent, PassiveFishSpecies species, int schoolSize, PixelFishGroup group)
		{
			float schoolRadius = PassiveFishPlacement.GetSchoolRadius(schoolSize);
			PassiveFishSchool school = schoolSize > 1
				? new PassiveFishSchool(worldPos, schoolRadius, species.CruiseSpeedMultiplier, species.FleeSpeedMultiplier)
				: null;
			List<Vector3> schoolPositions = schoolSize > 1 ? new List<Vector3>() : null;

			int spawned = SpawnFish(worldPos, parent, species, school, group);
			if (spawned == 0)
				return 0;

			if (schoolPositions != null)
				schoolPositions.Add(worldPos);

			for (int i = 1; i < schoolSize && LiveCount < EffectiveFishCap(); i++)
			{
				Vector3 schoolmatePos;
				Transform schoolmateParent;
				if (!PassiveFishPlacement.TryPickSchoolmatePosition(worldPos, schoolRadius, schoolPositions, out schoolmatePos, out schoolmateParent))
					continue;

				int added = SpawnFish(schoolmatePos, schoolmateParent, species, school, group);
				spawned += added;
				if (added > 0 && schoolPositions != null)
					schoolPositions.Add(schoolmatePos);
			}

			return spawned;
		}

		private static int SpawnFish(Vector3 worldPos, Transform parent, PassiveFishSpecies species, PassiveFishSchool school, PixelFishGroup group)
		{
			GameObject go = PassiveFishFactory.Spawn(worldPos, parent, species, school);
			if (go == null)
				return 0;

			group.Fish.Add(go);
			return 1;
		}

        // Hard ceiling on total live fish from the MaxLiveFish setting.
        internal static int EffectiveFishCap()
        {
            return DeepWaters.Instance != null ? DeepWaters.Instance.MaxLiveFish : 54;
        }

        private static int ScaledAttemptsPerPixel()
        {
            float scale = DeepWaters.Instance != null
                ? DeepWaters.Instance.PassiveFishFrequency / PassiveFishFrequencyAtMidpoint
                : 1f;

            return Mathf.Max(0, Mathf.RoundToInt(FishAttemptsPerPixel * scale));
        }

        // Resolve the owning tile's climate biome and the water-column depth
        // fraction (0..1 of max depth) at a candidate spawn point, so species
        // selection favours biome- and depth-appropriate fish.
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
                climateIndex = tile.BiomeClimateIndex;

            float maxDepth = DeepWaters.Instance != null ? Mathf.Max(1f, DeepWaters.Instance.WaterDepth) : 200f;
            depthFraction = Mathf.Clamp01(column.Depth / maxDepth);
        }
    }
}
