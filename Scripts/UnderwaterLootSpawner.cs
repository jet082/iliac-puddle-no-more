// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Utility;
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
    internal static class UnderwaterLootSpawner
    {
        // Distance-based top-up: re-roll loot after this much horizontal travel,
        // so finds track exploration rather than time spent idling.
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
		private const float ClusterDebrisRadius = 22f;
		private const int ClusterDebrisCount = 24;
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
		private const float DefaultLootMinSpawnDistance = 42f;
		private const float DefaultLootMaxSpawnDistance = 72f;
		private const int MaxStrayLootPerPulse = 12;
		private const int TreasureCoveMaxStrayLootPerPulse = 18;
		private const float ForwardSpawnArcDegrees = 110f;
		private const float ForwardBiasChance = 0.7f;
		private const float FogAheadMaxDistance = 130f;
		private const float SeafloorYClearance = 2f;
		private const float LootFloorLift = 0.08f;
		private const int SpawnSpotAttempts = 18;
		private const float SpawnCellSize = 48f;
		private const int MaxRememberedSpawnCells = 128;
		private const float ClusterLootRadius = 11f;
		private const float ClusterLootMinSpacing = 3.0f;
		private const int ClusterLootSpotAttempts = 8;
		private const float WreckMinimumDepthFraction = 0.5f;

        private static readonly TransientObjectTracker trackedObjects = new TransientObjectTracker();
		private static readonly HashSet<long> recentSpawnCells = new HashSet<long>();
		private static readonly Queue<long> recentSpawnCellOrder = new Queue<long>();
		private static readonly HashSet<UnderwaterDecorationRecord> usedRubbleRecordsScratch = new HashSet<UnderwaterDecorationRecord>();
		private static readonly List<Vector3> clusterLootSpotsScratch = new List<Vector3>(12);
		private static readonly int[] TreasurePileRecords = { 0, 1, 3, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 36, 37, 38, 39, 40 };
		private static readonly UnderwaterDecorationRecord[] RubbleRecords =
		{
			R(105, 0), R(105, 5), R(105, 6), R(105, 7), R(105, 8), R(105, 9), R(105, 10),
			R(400, 0), R(400, 1), R(400, 4), R(400, 6),
			R(380, 1),
			R(96, 0), R(96, 2), R(96, 3), R(96, 4), R(96, 5),
		};

        private static Vector3 lastPulseAnchor;
        private static bool hasPulseAnchor;
        private static float nextAllowedPulseTime;
        private static float nextNearbyWaterGateCheckTime;
        private static bool hasNearbyWaterGateCache;
        private static bool lastNearbyWaterGateResult;
        private static bool installed;

        internal static void Install()
        {
            if (installed)
                return;

            DeepWaterRuntime.OnTransientReset += ResetRuntimeState;

            installed = true;
        }

        private static void ResetRuntimeState()
        {
			recentSpawnCells.Clear();
			recentSpawnCellOrder.Clear();
            hasPulseAnchor = false;
            nextAllowedPulseTime = 0f;
            hasNearbyWaterGateCache = false;
            lastNearbyWaterGateResult = false;
            trackedObjects.Clear();
            liveClusterCentres.Clear();
			usedRubbleRecordsScratch.Clear();
			clusterLootSpotsScratch.Clear();
        }

		internal static void Pump()
		{
			Vector3 playerPos;
			if (!CanRunLootPulse(out playerPos))
			{
				hasPulseAnchor = false;
				return;
			}

			if (!hasPulseAnchor)
			{
				lastPulseAnchor = playerPos;
				hasPulseAnchor = true;
				TryRunLootPulse(true, playerPos);
				return;
			}

			float dx = playerPos.x - lastPulseAnchor.x;
			float dz = playerPos.z - lastPulseAnchor.z;
			if (dx * dx + dz * dz < LootPulseDistance * LootPulseDistance)
				return;

			lastPulseAnchor = playerPos;
			TryRunLootPulse(false, playerPos);
		}

        private static void TryRunLootPulse(bool allowImmediate, Vector3 playerPos)
        {
            if (!allowImmediate && Time.time < nextAllowedPulseTime)
                return;

			float spawnMinDistance = DefaultLootMinSpawnDistance;
			float spawnMaxDistance = DefaultLootMaxSpawnDistance;

            int maxLiveLoot = DeepWaters.Instance != null ? DeepWaters.Instance.MaxLiveLootObjects : 32;
            trackedObjects.Prune(playerPos, DespawnDistance, maxLiveLoot);
            PruneLiveClusters(playerPos);
            if (trackedObjects.Count >= maxLiveLoot)
                return;

            bool spawnedCluster = false;
            bool spawnedStray = false;
			float waterContextMultiplier = DeepWaterWorld.IsPlayerInOrAboveDeepWater(SurfaceLootOriginClearance)
				? 1f
				: ShoreLootPulseMultiplier;

            if (ShouldSpawnTreasureCluster(waterContextMultiplier))
                spawnedCluster = TrySpawnTreasureCluster(playerPos, spawnMinDistance, spawnMaxDistance);

            int strayTarget = RollStrayLootCount(spawnedCluster, waterContextMultiplier);
            for (int i = 0; i < strayTarget; i++)
            {
                if (TrySpawnStrayLoot(playerPos, spawnMinDistance, spawnMaxDistance))
                    spawnedStray = true;
            }

            bool spawnedAny = spawnedCluster || spawnedStray;
            nextAllowedPulseTime = Time.time + (spawnedAny ? MinPulseIntervalSeconds : FailedPulseRetrySeconds);
        }

        private static bool ShouldSpawnTreasureCluster(float waterContextMultiplier)
        {
            float rate = Mathf.Max(0f, DeepWaters.Instance.TreasureClusterRate);
            if (rate <= 0f)
                return false;

            if (liveClusterCentres.Count >= (DeepWaters.Instance != null ? DeepWaters.Instance.MaxLiveTreasureClusters : 3))
                return false;

            float multiplier = DeepWaters.Instance.TreasureCove ? TreasureCoveClusterChanceMultiplier : NormalLootMultiplier;
            float chance = Mathf.Min(
                rate * multiplier * waterContextMultiplier * DeepWaterWorld.DepthSpawnMultiplier(),
                MaxClusterChance);
            return Random.value < chance;
        }

        private static int RollStrayLootCount(bool spawnedCluster, float waterContextMultiplier)
        {
            // A normal treasure pulse is already a find; keep it readable and
            // cheap by not adding unrelated stray piles in the same pulse.
            if (spawnedCluster && !DeepWaters.Instance.TreasureCove)
                return 0;

            float rate = Mathf.Max(0f, DeepWaters.Instance.SeafloorLootRate);
            if (rate <= 0f) return 0;

            float multiplier = DeepWaters.Instance.TreasureCove ? TreasureCoveStrayMultiplier : NormalLootMultiplier;
            float scaledCount = FullStrayLootPerPulse * rate * multiplier *
                                waterContextMultiplier * DeepWaterWorld.DepthSpawnMultiplier();
            int count = DeepWaterWorld.RollCount(scaledCount);

            int max = DeepWaters.Instance.TreasureCove
                ? TreasureCoveMaxStrayLootPerPulse
                : MaxStrayLootPerPulse;
            return Mathf.Clamp(count, 0, max);
        }

        private static bool TrySpawnStrayLoot(Vector3 playerPos, float minSpawnDistance, float maxSpawnDistance)
        {
            Vector3 worldPos;
            Transform parent;
            long spawnCellKey;
			if (!PickSpawnSpot(playerPos, minSpawnDistance, maxSpawnDistance, out worldPos, out parent, out spawnCellKey))
                return false;

            DaggerfallLoot loot = SpawnLootContainer(worldPos, parent, trackedObjects);
            if (loot == null) return false;

			RememberSpawnCell(spawnCellKey);
            FillRandomItem(loot);
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
            usedRubbleRecordsScratch.Clear();

            for (int i = 0; i < targetCount; i++)
				QueueLooseLootDebris(lootPos, rubbleBatches, usedRubbleRecordsScratch);

            return SpawnRubbleBatches(rubbleBatches, tracker);
        }

        private static void QueueLooseLootDebris(
            Vector3 lootPos,
            Dictionary<Transform, List<UnderwaterDecorationPlacementInfo>> rubbleBatches,
            HashSet<UnderwaterDecorationRecord> usedRecords)
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
				if (!ResolveSeafloorAt(spot.x, spot.z, out spot.y, out debrisParent))
                    continue;

                UnderwaterDecorationRecord record = PickRubbleRecordExcept(usedRecords);
                usedRecords.Add(record);
                QueueRubbleSprite(spot, debrisParent, rubbleBatches, record);
                return;
            }
        }

        private static bool TrySpawnTreasureCluster(Vector3 playerPos, float minSpawnDistance, float maxSpawnDistance)
        {
			Vector3 centre;
			Transform parent;
			long spawnCellKey;
			if (!PickSpawnSpot(playerPos, minSpawnDistance, maxSpawnDistance, out centre, out parent, out spawnCellKey))
				return false;
			if (!IsDeepEnoughForWreck(centre.x, centre.z))
				return false;

			int debrisPlaced = SpawnClusterDebris(centre);
			int lootPlaced = SpawnClusterTreasure(centre);
			if (debrisPlaced == 0 && lootPlaced == 0)
				return false;

			RememberSpawnCell(spawnCellKey);
			UnderwaterEnemySpawner.TrySpawnRareEnemiesNearTreasureCluster(centre);
            liveClusterCentres.Add(centre);
            return true;
        }

		private static int SpawnClusterDebris(Vector3 centre)
		{
			int debrisCount = DeepWaters.Instance.TreasureCove ? ClusterDebrisCount * 2 : ClusterDebrisCount;
			var rubbleBatches = new Dictionary<Transform, List<UnderwaterDecorationPlacementInfo>>();

			for (int i = 0; i < debrisCount; i++)
			{
				float r = Mathf.Sqrt(Random.value) * ClusterDebrisRadius;
				float angle = Random.Range(0f, Mathf.PI * 2f);
				Vector3 spot = new Vector3(
					centre.x + Mathf.Cos(angle) * r,
					0f,
					centre.z + Mathf.Sin(angle) * r);

				Transform debrisParent;
				if (ResolveSeafloorAt(spot.x, spot.z, out spot.y, out debrisParent))
				{
					QueueRubbleSprite(
						spot,
						debrisParent,
						rubbleBatches,
						RubbleRecords[Random.Range(0, RubbleRecords.Length)]);
				}
			}

			return SpawnRubbleBatches(rubbleBatches, trackedObjects);
		}

		private static int SpawnClusterTreasure(Vector3 centre)
		{
			int lootMin = DeepWaters.Instance.TreasureCove ? 6 : 3;
			int lootMax = DeepWaters.Instance.TreasureCove ? 11 : 6;
			int lootCount = Random.Range(lootMin, lootMax);
			int lootPlaced = 0;
			clusterLootSpotsScratch.Clear();

			for (int i = 0; i < lootCount; i++)
			{
				Vector3 spot;
				if (!TryPickClusterLootSpot(centre, clusterLootSpotsScratch, out spot))
					continue;

				Transform spotParent;
				if (!ResolveSeafloorAt(spot.x, spot.z, out spot.y, out spotParent))
					continue;

				DaggerfallLoot loot = SpawnLootContainer(spot, spotParent, trackedObjects);
				if (loot == null)
					continue;

				FillClusterContainer(loot);
				clusterLootSpotsScratch.Add(spot);
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
				FillRandomItem(loot);
		}

		private static DaggerfallLoot SpawnLootContainer(
			Vector3 worldPos,
			Transform parent,
			TransientObjectTracker tracker)
		{
			int record = TreasurePileRecords[Random.Range(0, TreasurePileRecords.Length)];
			DaggerfallLoot loot = GameObjectHelper.CreateLootContainer(
				LootContainerTypes.RandomTreasure,
				InventoryContainerImages.Ground,
				worldPos,
				parent,
				DaggerfallLootDataTables.randomTreasureArchive,
				record,
				DaggerfallUnity.NextUID,
				null,
				true);

			DeepWaterRendering.DisableShadows(loot != null ? loot.gameObject : null);
			if (loot != null)
			{
				BrightenUnderwaterBillboards(loot.gameObject);
				DeepWaterWorld.AlignObjectBottomToWorldY(loot.gameObject, worldPos.y);
			}

			if (tracker != null)
				tracker.Add(loot != null ? loot.gameObject : null);

			return loot;
		}

		private static void QueueRubbleSprite(
			Vector3 worldPos,
			Transform parent,
			Dictionary<Transform, List<UnderwaterDecorationPlacementInfo>> rubbleBatches,
			UnderwaterDecorationRecord record)
		{
			List<UnderwaterDecorationPlacementInfo> batchItems;
			if (!rubbleBatches.TryGetValue(parent, out batchItems))
			{
				batchItems = new List<UnderwaterDecorationPlacementInfo>();
				rubbleBatches.Add(parent, batchItems);
			}

			batchItems.Add(new UnderwaterDecorationPlacementInfo(
				record,
				worldPos - parent.position));
		}

		private static int SpawnRubbleBatches(
			Dictionary<Transform, List<UnderwaterDecorationPlacementInfo>> rubbleBatches,
			TransientObjectTracker tracker)
		{
			int placed = 0;
			foreach (KeyValuePair<Transform, List<UnderwaterDecorationPlacementInfo>> pair in rubbleBatches)
			{
				if (pair.Key == null || pair.Value == null || pair.Value.Count == 0)
					continue;

				GameObject group = UnderwaterDecorationBatchFactory.Spawn(pair.Key, pair.Value);
				if (group == null)
					continue;

				group.name = "DeepWaters_LootRubbleBatch";
				if (tracker != null)
				{
					Vector3 centroidLocal = ComputeCentroidLocal(pair.Value);
					var anchor = new GameObject("DeepWaters_LootRubbleAnchor");
					anchor.transform.SetParent(pair.Key, false);
					anchor.transform.localPosition = centroidLocal;
					group.transform.SetParent(anchor.transform, false);
					group.transform.localPosition = -centroidLocal;
					tracker.Add(anchor);
				}

				placed += pair.Value.Count;
			}

			return placed;
		}

		private static Vector3 ComputeCentroidLocal(List<UnderwaterDecorationPlacementInfo> items)
		{
			if (items == null || items.Count == 0)
				return Vector3.zero;

			Vector3 sum = Vector3.zero;
			for (int i = 0; i < items.Count; i++)
				sum += items[i].LocalPosition;

			return sum / items.Count;
		}

		private static void BrightenUnderwaterBillboards(GameObject go)
		{
			if (go == null)
				return;

			MeshRenderer[] renderers = go.GetComponentsInChildren<MeshRenderer>(true);
			for (int i = 0; i < renderers.Length; i++)
				UnderwaterDecorationBatchFactory.ApplyUnderwaterDecorationMaterial(renderers[i]);
		}

		private static UnderwaterDecorationRecord PickRubbleRecordExcept(HashSet<UnderwaterDecorationRecord> excludedRecords)
		{
			if (excludedRecords == null || excludedRecords.Count >= RubbleRecords.Length)
				return RubbleRecords[Random.Range(0, RubbleRecords.Length)];

			UnderwaterDecorationRecord record;
			do
			{
				record = RubbleRecords[Random.Range(0, RubbleRecords.Length)];
			}
			while (excludedRecords.Contains(record));

			return record;
		}

		private static void FillRandomItem(DaggerfallLoot loot)
		{
			if (loot == null)
				return;

			var pe = GameManager.Instance.PlayerEntity;
			int level = pe != null ? pe.Level : 1;
			var gender = pe != null ? pe.Gender : Genders.Male;
			var race = pe != null ? pe.Race : Races.Breton;

			DaggerfallUnityItem item = null;
			float roll = Random.value;
			if (roll < 0.25f)       item = ItemBuilder.CreateRandomReligiousItem();
			else if (roll < 0.45f)  item = ItemBuilder.CreateRandomPotion();
			else if (roll < 0.60f)  item = ItemBuilder.CreateRandomJewellery();
			else if (roll < 0.75f)  item = ItemBuilder.CreateRandomGem();
			else if (roll < 0.85f)  item = ItemBuilder.CreateRandomClothing(gender, race);
			else if (roll < 0.95f)  item = ItemBuilder.CreateRandomWeapon(level);
			else                    item = ItemBuilder.CreateRandomArmor(level, gender, race);

			if (item != null)
				loot.Items.AddItem(item);
		}

		private static UnderwaterDecorationRecord R(int archive, int record)
		{
			return new UnderwaterDecorationRecord(archive, record);
		}

        private static bool CanRunLootPulse(out Vector3 playerPos)
        {
			playerPos = Vector3.zero;

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

            if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos))
                return false;

            if (hasNearbyWaterGateCache && Time.time < nextNearbyWaterGateCheckTime)
                return lastNearbyWaterGateResult;

            float depth;
            bool result = DeepWaterWorld.HasNearbyWaterColumn(
                playerPos,
                DefaultLootMinSpawnDistance,
                DefaultLootMaxSpawnDistance,
                NearbyWaterProbeDirections,
                SurfaceLootOriginClearance,
                out depth);

            hasNearbyWaterGateCache = true;
            lastNearbyWaterGateResult = result;
            nextNearbyWaterGateCheckTime = Time.time + NearbyWaterGateCheckInterval;
            return result;
        }

		private static bool PickSpawnSpot(
			Vector3 playerPos,
			float minDistance,
			float maxDistance,
			out Vector3 worldPos,
			out Transform parent,
			out long spawnCellKey)
		{
			worldPos = Vector3.zero;
			parent = null;
			spawnCellKey = 0L;

			minDistance = Mathf.Max(0f, minDistance);
			maxDistance = Mathf.Max(minDistance + 1f, maxDistance);

			for (int attempt = 0; attempt < SpawnSpotAttempts; attempt++)
			{
				float worldX, worldZ;
				Vector3 aheadPoint;
				if (Random.value < DeepWaterWorld.FogAheadSpawnChance &&
					DeepWaterWorld.TryPickFogAheadPoint(playerPos, FogAheadMaxDistance, out aheadPoint))
				{
					worldX = aheadPoint.x;
					worldZ = aheadPoint.z;
				}
				else
				{
					float angle = PickSpawnAngle();
					float dist = DeepWaterWorld.PickRingDistance(minDistance, maxDistance);
					worldX = playerPos.x + Mathf.Cos(angle) * dist;
					worldZ = playerPos.z + Mathf.Sin(angle) * dist;
				}

				long key = DeepWaterWorld.WorldCellKey(worldX, worldZ, SpawnCellSize);
				if (recentSpawnCells.Contains(key))
					continue;

				float worldY;
				Transform terrainParent;
				if (!ResolveSeafloorAt(worldX, worldZ, out worldY, out terrainParent))
					continue;

				worldPos = new Vector3(worldX, worldY, worldZ);
				if (!DeepWaterWorld.IsOutsideImmediateView(
					worldPos,
					playerPos,
					DeepWaterWorld.UnderwaterVisionDistance,
					0.12f))
				{
					continue;
				}

				parent = terrainParent;
				spawnCellKey = key;
				return true;
			}

			return false;
		}

		private static bool IsDeepEnoughForWreck(float worldX, float worldZ)
		{
			DeepWaterColumn column;
			if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
				return false;

			float maxDepth = DeepWaters.Instance != null ? Mathf.Max(1f, DeepWaters.Instance.WaterDepth) : 200f;
			return column.Depth >= maxDepth * WreckMinimumDepthFraction;
		}

		private static bool TryPickClusterLootSpot(Vector3 centre, List<Vector3> placedLootSpots, out Vector3 spot)
		{
			for (int attempt = 0; attempt < ClusterLootSpotAttempts; attempt++)
			{
				float r = Mathf.Sqrt(Random.value) * ClusterLootRadius;
				float angle = Random.Range(0f, Mathf.PI * 2f);
				spot = new Vector3(
					centre.x + Mathf.Cos(angle) * r,
					0f,
					centre.z + Mathf.Sin(angle) * r);

				if (IsFarEnoughFromClusterLoot(spot, placedLootSpots))
					return true;
			}

			spot = Vector3.zero;
			return false;
		}

		private static bool ResolveSeafloorAt(float worldX, float worldZ, out float seafloorWorldY, out Transform terrainTransform)
		{
			seafloorWorldY = 0f;
			terrainTransform = null;

			DeepWaterColumn column;
			if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
				return false;

			if (column.Parent == null || column.Depth < SeafloorYClearance)
				return false;

			float seafloorLocalY;
			if (!DeepWaterWorld.TryGetRenderedSeafloorLocalY(column, worldX, worldZ, out seafloorLocalY))
				return false;

			if (column.OceanLocalY - seafloorLocalY < SeafloorYClearance)
				return false;

			terrainTransform = column.Parent;
			seafloorWorldY = terrainTransform.position.y + seafloorLocalY + LootFloorLift;
			return true;
		}

		private static void RememberSpawnCell(long key)
		{
			if (recentSpawnCells.Contains(key))
				return;

			recentSpawnCells.Add(key);
			recentSpawnCellOrder.Enqueue(key);

			while (recentSpawnCellOrder.Count > MaxRememberedSpawnCells)
			{
				long oldKey = recentSpawnCellOrder.Dequeue();
				recentSpawnCells.Remove(oldKey);
			}
		}

		private static float PickSpawnAngle()
		{
			Vector3 forward = Vector3.zero;
			GameManager gameManager = GameManager.Instance;
			if (gameManager != null && gameManager.MainCamera != null)
				forward = gameManager.MainCamera.transform.forward;
			if (forward.sqrMagnitude < 0.001f && gameManager != null && gameManager.PlayerObject != null)
				forward = gameManager.PlayerObject.transform.forward;

			forward.y = 0f;
			if (forward.sqrMagnitude < 0.001f || Random.value > ForwardBiasChance)
				return Random.Range(0f, Mathf.PI * 2f);

			forward.Normalize();
			float baseAngle = Mathf.Atan2(forward.z, forward.x);
			float arc = ForwardSpawnArcDegrees * Mathf.Deg2Rad;
			return baseAngle + Random.Range(-arc, arc);
		}

		private static bool IsFarEnoughFromClusterLoot(Vector3 spot, List<Vector3> placedLootSpots)
		{
			float minSq = ClusterLootMinSpacing * ClusterLootMinSpacing;
			for (int i = 0; i < placedLootSpots.Count; i++)
			{
				float dx = spot.x - placedLootSpots[i].x;
				float dz = spot.z - placedLootSpots[i].z;
				if (dx * dx + dz * dz < minSq)
					return false;
			}

			return true;
		}

        // Live treasure clusters near the player, tracked by centre position so
        // 'max treasure' caps how many wreck sites exist around you at once
        // without splitting the shared loot tracker. Pruned by distance each
        // pulse and cleared on world rebuild.
        private static readonly List<Vector3> liveClusterCentres = new List<Vector3>();

        private static void PruneLiveClusters(Vector3 playerPos)
        {
            float maxDistance = DespawnDistance;
            float maxSq = maxDistance * maxDistance;
            for (int i = liveClusterCentres.Count - 1; i >= 0; i--)
            {
                Vector3 delta = liveClusterCentres[i] - playerPos;
                delta.y = 0f;
                if (delta.sqrMagnitude > maxSq)
                    liveClusterCentres.RemoveAt(i);
            }
        }

    }
}
