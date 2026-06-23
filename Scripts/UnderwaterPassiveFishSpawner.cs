// Project:         Iliac Puddle No More
// License:         MIT

using System.Reflection;
using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
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
    internal static class UnderwaterPassiveFishSpawner
    {
        // Base spawn attempts scattered uniformly across a map pixel. Each attempt
        // that lands in valid water seeds a SCHOOL (species school size), so the
        // realized fish count scales with the pixel's water area (open ocean fills,
        // shallow coves stay sparse). The hard ceiling on total live fish is the
        // MaxLiveFish setting; density below it is set by the frequency setting.
        private const int FishAttemptsPerPixel = 90;
        private const float PassiveFishFrequencyAtMidpoint = 3.0f;
		private const float MinimumFishColumnDepth = 8f;
		private const float FishSeafloorClearance = 1.2f;
		private const float FishSurfaceClearance = 1.4f;
		private const float SchoolMemberMinRadius = 1.2f;
		private const float SchoolMemberMaxRadius = 5f;
		private const float SchoolMemberMinSeparation = 2.2f;
		private const int SchoolPositionAttempts = 24;
		private const float DeepFishFloorBiasStart = 0.55f;
		private const float DeepFishFloorBandMeters = 35f;

        private sealed class PixelFishGroup
        {
            internal readonly TransientObjectTracker Fish = new TransientObjectTracker();
            internal int AttemptsRemaining;
        }

        private static readonly Dictionary<long, PixelFishGroup> pixelGroups = new Dictionary<long, PixelFishGroup>();
        private static readonly List<long> despawnScratch = new List<long>();
		private static readonly List<Vector3> schoolPositionsScratch = new List<Vector3>(32);
        private static bool installed;
		private static int liveCount;
		private static FieldInfo remoteTargetIconPanelField;

        internal static void Install()
        {
            if (installed)
                return;

            DeepWaterRuntime.OnTransientReset += ClearAll;
            PassiveFishResources.CacheInventoryIcons();
            installed = true;
        }

		internal static void UpdateFishLootIcon()
		{
			var inventoryWindow = DaggerfallUI.UIManager.TopWindow as DaggerfallInventoryWindow;
			if (inventoryWindow == null || inventoryWindow.LootTarget == null)
				return;

			FishLootIcon icon = inventoryWindow.LootTarget.GetComponent<FishLootIcon>();
			if (icon == null || icon.Texture == null)
				return;

			Panel panel = GetRemoteTargetIconPanel(inventoryWindow);
			if (panel != null)
				panel.BackgroundTexture = icon.Texture;
		}

        internal static int LiveCount
        {
			get { return liveCount; }
        }

        // True when fish are enabled and species exist. The shared pulse owns
        // runtime/context gates.
        internal static bool CanPopulate()
        {
            return DeepWaters.Instance != null &&
                   DeepWaters.Instance.PassiveFishFrequency > 0f &&
                   PassiveFishSpeciesCatalog.HasAnySpawnableSpecies();
        }

        internal static void ClearAll()
        {
            foreach (var kv in pixelGroups)
                kv.Value.Fish.Clear();
            pixelGroups.Clear();
			liveCount = 0;
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
				PixelFishGroup group = pixelGroups[key];
				liveCount = Mathf.Max(0, liveCount - group.Fish.Count);
				group.Fish.Clear();
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
                if (liveCount >= liveCap)
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
                if (!TryResolveFishPosition(worldX, worldZ, species, out worldPos, out parent))
                    continue;

				int remaining = liveCap - liveCount;
				int schoolSize = Mathf.Min(Random.Range(species.MinSchoolSize, species.MaxSchoolSize + 1), remaining);
				if (schoolSize <= 0)
				{
					group.AttemptsRemaining = 0;
					return;
				}

				SpawnSchool(worldPos, parent, species, schoolSize, liveCap, group);
            }
        }

		private static void SpawnSchool(Vector3 worldPos, Transform parent, PassiveFishSpecies species, int schoolSize, int liveCap, PixelFishGroup group)
		{
			float schoolRadius = GetSchoolRadius(schoolSize);
			PassiveFishSchool school = schoolSize > 1
				? new PassiveFishSchool(worldPos, schoolRadius, species.CruiseSpeedMultiplier, species.FleeSpeedMultiplier)
				: null;
			List<Vector3> schoolPositions = null;
			if (schoolSize > 1)
			{
				schoolPositionsScratch.Clear();
				schoolPositions = schoolPositionsScratch;
			}

			if (!SpawnFish(worldPos, parent, species, school, group))
				return;

			if (schoolPositions != null)
				schoolPositions.Add(worldPos);

			for (int i = 1; i < schoolSize && liveCount < liveCap; i++)
			{
				Vector3 schoolmatePos;
				Transform schoolmateParent;
				if (!TryPickSchoolmatePosition(worldPos, schoolRadius, schoolPositions, out schoolmatePos, out schoolmateParent))
					continue;

				if (SpawnFish(schoolmatePos, schoolmateParent, species, school, group) && schoolPositions != null)
					schoolPositions.Add(schoolmatePos);
			}
		}

		private static bool SpawnFish(Vector3 worldPos, Transform parent, PassiveFishSpecies species, PassiveFishSchool school, PixelFishGroup group)
		{
			GameObject go = SpawnPassiveFish(worldPos, parent, species, school);
			if (go == null)
				return false;

			group.Fish.Add(go);
			liveCount++;
			return true;
		}

		private static GameObject SpawnPassiveFish(Vector3 worldPos, Transform parent, PassiveFishSpecies species, PassiveFishSchool school)
		{
			if (species == null || !PassiveFishResources.LoadFishTexture(species))
				return null;

			DaggerfallUnityItem fishItem;
			if (!PassiveFishResources.TryCreateFishItem(species, out fishItem))
				return null;

			GameObject go = new GameObject("DeepWaters " + fishItem.shortName);
			if (parent != null)
				go.transform.parent = parent;

			float height = species.BillboardHeight * Random.Range(species.MinHeightMultiplier, species.MaxHeightMultiplier);
			Vector2 billboardSize = GetFishBillboardSize(species, height);

			DaggerfallBillboard billboard = go.AddComponent<DaggerfallBillboard>();
			billboard.FaceY = true;

			Material material = billboard.SetMaterial(species.Texture, billboardSize);
			if (material == null)
			{
				Object.Destroy(go);
				return null;
			}

			if (material.HasProperty("_Cutoff"))
				material.SetFloat("_Cutoff", 0.1f);

			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			UnderwaterDecorationBatchFactory.ApplyUnderwaterDecorationMaterial(renderer);

			go.transform.position = worldPos;
			DeepWaterRendering.FaceMainCamera(go.transform);
			AddFishClickCollider(go, billboardSize);
			DeepWaterRendering.DisableShadows(renderer);

			DaggerfallLoot loot = go.AddComponent<DaggerfallLoot>();
			loot.ContainerType = LootContainerTypes.DroppedLoot;
			loot.TextureArchive = species.TextureArchive;
			loot.TextureRecord = species.TextureRecord;
			loot.Items.AddItem(fishItem);

			FishLootIcon lootIcon = go.AddComponent<FishLootIcon>();
			lootIcon.Texture = PassiveFishResources.GetFishIconTexture(species);

			PassiveFishBehaviour behaviour = go.AddComponent<PassiveFishBehaviour>();
			behaviour.Initialize(
				loot,
				species.CruiseSpeedMultiplier,
				species.FleeSpeedMultiplier,
				school,
				species.FleeDartHoldMin,
				species.FleeDartHoldMax);

			return go;
		}

		private static Vector2 GetFishBillboardSize(PassiveFishSpecies species, float height)
		{
			float aspect = species.Texture != null && species.Texture.height > 0
				? (float)species.Texture.width / species.Texture.height
				: 1.8f;

			return new Vector2(height * aspect, height);
		}

		private static void AddFishClickCollider(GameObject go, Vector2 billboardSize)
		{
			BoxCollider clickCollider = go.AddComponent<BoxCollider>();
			clickCollider.isTrigger = true;
			clickCollider.size = new Vector3(
				billboardSize.x,
				billboardSize.y,
				Mathf.Max(0.35f, billboardSize.x * 0.25f));
		}

		private static float GetSchoolRadius(int schoolSize)
		{
			return Mathf.Clamp(2.5f + schoolSize * 0.45f, SchoolMemberMinRadius, SchoolMemberMaxRadius);
		}

		private static bool TryResolveFishPosition(float worldX, float worldZ, PassiveFishSpecies species, out Vector3 worldPos, out Transform parent)
		{
			worldPos = Vector3.zero;
			float billboardHalf = 0.5f * species.BillboardHeight * species.MaxHeightMultiplier;
			float floorClearance = Mathf.Max(FishSeafloorClearance, billboardHalf);
			float surfaceClearance = Mathf.Max(FishSurfaceClearance, billboardHalf);

			float minY, maxY, oceanY;
			if (!TryResolveColumnRange(worldX, worldZ, floorClearance, surfaceClearance, out minY, out maxY, out oceanY, out parent))
				return false;

			float y;
			if (!TryPickFishY(minY, maxY, oceanY, species, out y))
			{
				parent = null;
				return false;
			}

			worldPos = new Vector3(worldX, y, worldZ);
			return true;
		}

		private static bool TryResolveFishPosition(float worldX, float worldZ, out Vector3 worldPos, out Transform parent)
		{
			worldPos = Vector3.zero;

			float minY, maxY, oceanY;
			if (!TryResolveColumnRange(worldX, worldZ, FishSeafloorClearance, FishSurfaceClearance, out minY, out maxY, out oceanY, out parent))
				return false;

			worldPos = new Vector3(worldX, Random.Range(minY, maxY), worldZ);
			return true;
		}

		private static bool TryResolveColumnRange(
			float worldX,
			float worldZ,
			float floorClearance,
			float surfaceClearance,
			out float minY,
			out float maxY,
			out float oceanY,
			out Transform parent)
		{
			minY = 0f;
			maxY = 0f;
			oceanY = 0f;
			parent = null;

			DeepWaterColumn column;
			if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
				return false;

			if (column.Depth < MinimumFishColumnDepth)
				return false;

			float seafloorWorldY;
			if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, worldX, worldZ, out seafloorWorldY))
				return false;

			oceanY = column.OceanWorldY;
			minY = seafloorWorldY + floorClearance;
			maxY = oceanY - surfaceClearance;
			if (maxY <= minY)
				return false;

			parent = column.Parent;
			return true;
		}

		private static bool TryPickSchoolmatePosition(Vector3 schoolCenter, float schoolRadius, List<Vector3> existingPositions, out Vector3 worldPos, out Transform parent)
		{
			worldPos = Vector3.zero;
			parent = null;

			for (int attempt = 0; attempt < SchoolPositionAttempts; attempt++)
			{
				Vector2 offset = Random.insideUnitCircle;
				if (offset.sqrMagnitude < 0.01f)
					offset = Vector2.right;

				offset.Normalize();
				offset *= Random.Range(SchoolMemberMinRadius, schoolRadius);

				if (!TryResolveFishPosition(schoolCenter.x + offset.x, schoolCenter.z + offset.y, out worldPos, out parent))
					continue;

				ClampToSchoolDepth(schoolCenter, ref worldPos);

				if (IsFarEnoughFromSchoolmates(worldPos, existingPositions))
					return true;
			}

			return false;
		}

		private static void ClampToSchoolDepth(Vector3 schoolCenter, ref Vector3 worldPos)
		{
			DeepWaterColumn column;
			if (!DeepWaterWorld.TryGetWaterColumn(worldPos.x, worldPos.z, out column))
				return;

			float seafloorWorldY;
			if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, worldPos.x, worldPos.z, out seafloorWorldY))
				return;

			float minY = seafloorWorldY + FishSeafloorClearance;
			float maxY = column.OceanWorldY - FishSurfaceClearance;
			worldPos.y = Mathf.Clamp(schoolCenter.y + Random.Range(-1.0f, 1.0f), minY, maxY);
		}

		private static bool IsFarEnoughFromSchoolmates(Vector3 worldPos, List<Vector3> existingPositions)
		{
			if (existingPositions == null)
				return true;

			float minDistanceSq = SchoolMemberMinSeparation * SchoolMemberMinSeparation;
			for (int i = 0; i < existingPositions.Count; i++)
				if ((worldPos - existingPositions[i]).sqrMagnitude < minDistanceSq)
					return false;

			return true;
		}

		private static bool TryPickFishY(float minY, float maxY, float oceanY, PassiveFishSpecies species, out float y)
		{
			y = 0f;
			if (maxY <= minY)
				return false;

			float maxOceanDepth = DeepWaters.Instance != null
				? Mathf.Max(1f, DeepWaters.Instance.WaterDepth)
				: DeepBathymetry.MaxAbsoluteDepth;

			float bandShallow = species.MinDepthFraction * maxOceanDepth;
			float bandDeep = species.MaxDepthFraction * maxOceanDepth;
			float availShallow = oceanY - maxY;
			float availDeep = oceanY - minY;

			float lo = Mathf.Max(bandShallow, availShallow);
			float hi = Mathf.Min(bandDeep, availDeep);
			if (hi <= lo)
				return false;

			float depth = Random.Range(lo, hi);
			float columnDepthFraction = Mathf.Clamp01(availDeep / maxOceanDepth);
			float floorBias = Mathf.Clamp01((columnDepthFraction - DeepFishFloorBiasStart) / (1f - DeepFishFloorBiasStart));
			if (floorBias > 0f)
			{
				float deepLo = Mathf.Max(lo, hi - DeepFishFloorBandMeters);
				depth = Mathf.Lerp(depth, Random.Range(deepLo, hi), floorBias);
			}

			y = oceanY - depth;
			return true;
		}

        // Hard ceiling on total live fish from the MaxLiveFish setting.
        internal static int EffectiveFishCap()
        {
            return DeepWaters.Instance != null ? DeepWaters.Instance.MaxLiveFish : DeepWaters.MaxLiveFishLimit;
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

		private static Panel GetRemoteTargetIconPanel(DaggerfallInventoryWindow inventoryWindow)
		{
			if (inventoryWindow == null)
				return null;

			if (remoteTargetIconPanelField == null)
				remoteTargetIconPanelField = typeof(DaggerfallInventoryWindow).GetField(
					"remoteTargetIconPanel",
					BindingFlags.Instance | BindingFlags.NonPublic);

			return remoteTargetIconPanelField != null
				? remoteTargetIconPanelField.GetValue(inventoryWindow) as Panel
				: null;
		}
    }

	internal class FishLootIcon : MonoBehaviour
	{
		internal Texture2D Texture;
	}
}
