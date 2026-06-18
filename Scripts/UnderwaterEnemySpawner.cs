// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Utility;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Spawns aquatic enemies per map pixel, scattered across the pixel's water
    /// area (same model as the passive fish). A rare boss roll seeds undead/vampire
    /// "bosses" only in the deep open ocean.
    /// </summary>
    public static class UnderwaterEnemySpawner
    {
        // Depth-banded aquatic pool (issue 7). Each entry prefers a band of the
        // water column (fraction of max depth). Shallow coves read as living
        // water (slaughterfish, lamia sirens); the deep and abyss turn menacing
        // as dreugh take over and the drowned dead / ice atronachs surface only
        // down deep.
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
			new DepthAquatic(MobileTypes.Nymph,         10, 0.00f, 0.55f),
            new DepthAquatic(MobileTypes.Dreugh,        25, 0.15f, 1.00f),
            new DepthAquatic(MobileTypes.Zombie,         6, 0.40f, 1.00f),
            new DepthAquatic(MobileTypes.SkeletalWarrior,7, 0.45f, 1.00f),
            new DepthAquatic(MobileTypes.Ghost,          7, 0.50f, 1.00f),
			new DepthAquatic(MobileTypes.Wraith,         5, 0.60f, 1.00f),
			new DepthAquatic(MobileTypes.IceAtronach,    4, 0.70f, 1.00f),
			new DepthAquatic(MobileTypes.Vampire,        3, 0.75f, 1.00f),
			new DepthAquatic(MobileTypes.Lich,           2, 0.85f, 1.00f),
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
			MobileTypes.Vampire,
			MobileTypes.Lich,
		};
		private const MobileTeams TreasureGuardTeam = MobileTeams.Undead;

        private const float SpawnViewportMargin = 0.08f;
        private const float MinimumColumnDepth = 4f;
        private const float EnemySeafloorClearance = 2.5f;
		private const float FloorEnemySeafloorClearance = 0.5f;
        private const float EnemySurfaceClearance = 3f;
		private const float DeepSwimmerFloorBiasStart = 0.55f;
		private const float DeepSwimmerFloorBandTop = 0.35f;
        private const float TreasureGuardMinDistance = 8f;
        private const float TreasureGuardMaxDistance = 30f;
        private const int MaxTreasureGuardCount = 5;
        private const float EnemyFrequencyForMaxTreasureGuards = 0.6f;

        // Per-map-pixel population (see UnderwaterPassiveFishSpawner for the model).
        // Total live enemies are capped by the MaxLiveEnemies setting.
        private const int EnemyAttemptsPerPixel = 96;
        private const float EnemyFrequencyAtMidpoint = 0.5f;

		// Rare "boss" roll: 1 in 100 of each deep-ocean enemy spawn becomes
		// an ancient lich or ancient vampire.
        private const float BossSpawnChance = 1f / 100f;
        private const float BossMinDepthFraction = 0.6f;
		private const float TreasureGuardBossChance = 1f / 50f;

        private sealed class PixelEnemyGroup
        {
            public readonly TransientObjectTracker Enemies = new TransientObjectTracker();
            public int AttemptsRemaining;
        }

        private static readonly Dictionary<long, PixelEnemyGroup> pixelGroups = new Dictionary<long, PixelEnemyGroup>();
        private static readonly List<long> despawnScratch = new List<long>();
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
            ClearAll();
        }

        internal static int LiveCount
        {
            get
            {
                int total = 0;
                foreach (var kv in pixelGroups)
                    total += kv.Value.Enemies.Count;
                return total;
            }
        }

        internal static bool CanPopulate()
        {
            return DeepWaters.Instance != null &&
                   DeepWaterRuntime.CanRunHeavyRuntimeWork &&
                   DeepWaters.Instance.SpawnUnderwaterEnemies &&
                   DeepWaters.Instance.EnemyFrequency > 0f &&
                   DeepWaterWorld.IsPlayerInExteriorWaterContext();
        }

        internal static void ClearAll()
        {
            foreach (var kv in pixelGroups)
                kv.Value.Enemies.Clear();
            pixelGroups.Clear();
        }

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
                pixelGroups[key].Enemies.Clear();
                pixelGroups.Remove(key);
            }
        }

        internal static void TickPopulate(DaggerfallTerrain dfTerrain, ref int attemptBudget)
        {
            if (attemptBudget <= 0 || dfTerrain == null)
                return;

            long key = DeepWaterWorld.TileKey(dfTerrain.MapPixelX, dfTerrain.MapPixelY);
            PixelEnemyGroup group;
            if (!pixelGroups.TryGetValue(key, out group))
            {
                group = new PixelEnemyGroup { AttemptsRemaining = ScaledAttemptsPerPixel() };
                pixelGroups[key] = group;
            }

            if (group.AttemptsRemaining <= 0)
                return;

            int liveCap = EffectiveEnemyCap();
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

                Transform parent;
				float floorY;
				float surfaceY;
                float depthFraction;
                if (!TryResolveSpawnColumn(worldX, worldZ, out floorY, out surfaceY, out parent, out depthFraction))
                    continue;

                MobileGender gender;
                bool boss;
                MobileTypes type = PickEnemyForDepth(depthFraction, out gender, out boss);
				Vector3 resolvedPos = PickEnemyPosition(worldX, worldZ, floorY, surfaceY, type, depthFraction);

                GameObject enemy = SpawnEnemy(resolvedPos, parent, type, gender);
                if (enemy != null)
                {
                    group.Enemies.Add(enemy);
                    if (boss)
                        LogBossSpawn(type, gender, depthFraction);
                }
            }
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

			bool includeBoss = Random.value < TreasureGuardBossChance;
            int targetCount = RollTreasureGuardCount();
			if (includeBoss)
				targetCount = Mathf.Max(1, targetCount);
            if (targetCount <= 0)
                return 0;

            int spawned = 0;
            int attempts = 0;
			bool bossSpawned = false;
            while (spawned < targetCount && attempts < 8 * targetCount + 15)
            {
                attempts++;

                float angle = Random.Range(0f, Mathf.PI * 2f);
                float dist = DeepWaterWorld.PickRingDistance(TreasureGuardMinDistance, TreasureGuardMaxDistance);
                float worldX = centre.x + Mathf.Cos(angle) * dist;
                float worldZ = centre.z + Mathf.Sin(angle) * dist;

                Transform parent;
				float floorY;
				float surfaceY;
				float depthFraction;
                if (!TryResolveSpawnColumn(worldX, worldZ, out floorY, out surfaceY, out parent, out depthFraction))
                    continue;
				MobileGender gender;
				MobileTypes type = PickTreasureGuardType(includeBoss, bossSpawned, out gender);
				Vector3 resolvedPos = PickEnemyPosition(worldX, worldZ, floorY, surfaceY, type, depthFraction);

                if (!DeepWaterWorld.IsOutsideImmediateView(resolvedPos, playerPos, DeepWaterWorld.UnderwaterVisionDistance, SpawnViewportMargin))
                    continue;

				if (SpawnTreasureGuardEnemy(resolvedPos, parent, type, gender) != null)
				{
					if (includeBoss && !bossSpawned)
						bossSpawned = true;
					spawned++;
				}
            }

            if (spawned == 0)
            {
                Transform parent;
				float floorY;
				float surfaceY;
                float depthFraction;
                if (TryResolveSpawnColumn(centre.x, centre.z, out floorY, out surfaceY, out parent, out depthFraction))
                {
					MobileGender gender;
                    MobileTypes type = PickTreasureGuardType(includeBoss, bossSpawned, out gender);
					Vector3 resolvedPos = PickEnemyPosition(centre.x, centre.z, floorY, surfaceY, type, depthFraction);
                    if (DeepWaterWorld.IsOutsideImmediateView(resolvedPos, playerPos, DeepWaterWorld.UnderwaterVisionDistance, SpawnViewportMargin))
                        spawned = SpawnTreasureGuardEnemy(resolvedPos, parent, type, gender) != null ? 1 : 0;
                }
            }

            return spawned;
        }

        private static bool TryResolveSpawnColumn(float worldX, float worldZ, out float floorY, out float surfaceY, out Transform parent, out float depthFraction)
        {
			floorY = 0f;
			surfaceY = 0f;
            parent = null;
            depthFraction = 0f;

            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
                return false;

            float seafloorWorldY;
            if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, worldX, worldZ, out seafloorWorldY))
                return false;

            floorY = seafloorWorldY + EnemySeafloorClearance;
            surfaceY = column.OceanWorldY - EnemySurfaceClearance;
            if (surfaceY - floorY < MinimumColumnDepth)
                return false;

            float maxDepth = DeepWaters.Instance != null ? Mathf.Max(1f, DeepWaters.Instance.WaterDepth) : 200f;
            depthFraction = Mathf.Clamp01(column.Depth / maxDepth);
            parent = column.Parent;
            return parent != null;
        }

		private static Vector3 PickEnemyPosition(float worldX, float worldZ, float floorY, float surfaceY, MobileTypes type, float depthFraction)
		{
			if (MustSpawnOnFloor(type))
				return new Vector3(worldX, floorY - EnemySeafloorClearance + FloorEnemySeafloorClearance, worldZ);

			float columnT = Random.value;
			float floorBias = Mathf.Clamp01((depthFraction - DeepSwimmerFloorBiasStart) / (1f - DeepSwimmerFloorBiasStart));
			if (floorBias > 0f)
				columnT = Mathf.Lerp(columnT, Random.Range(0f, DeepSwimmerFloorBandTop), floorBias);

			return new Vector3(worldX, Mathf.Lerp(floorY, surfaceY, columnT), worldZ);
		}

		private static bool MustSpawnOnFloor(MobileTypes type)
		{
			return type == MobileTypes.Zombie ||
				type == MobileTypes.SkeletalWarrior ||
				type == MobileTypes.IceAtronach ||
				type == MobileTypes.Nymph ||
				type == MobileTypes.Lich ||
				type == MobileTypes.AncientLich ||
				type == MobileTypes.Vampire ||
				type == MobileTypes.VampireAncient;
		}

        // Deep-ocean spawns roll for a boss; otherwise the depth-weighted
        // aquatic pick.
        private static MobileTypes PickEnemyForDepth(float depthFraction, out MobileGender gender, out bool boss)
        {
            gender = MobileGender.Unspecified;
            boss = false;

            if (depthFraction >= BossMinDepthFraction && Random.value < BossSpawnChance)
            {
                boss = true;
                return PickBoss(out gender);
            }

            return PickAquaticForDepth(depthFraction);
        }

        private static void LogBossSpawn(MobileTypes type, MobileGender gender, float depthFraction)
        {
            Debug.Log("[DeepWaters] Boss spawned: " + type + " (" + gender + ") at depthFraction " +
                      depthFraction.ToString("F2"));
        }

		private static MobileTypes PickBoss(out MobileGender gender)
		{
			gender = MobileGender.Unspecified;
			return Random.value < 0.5f ? MobileTypes.AncientLich : MobileTypes.VampireAncient;
		}

        // Depth-weighted aquatic pick (issue 7). Each candidate's weight tapers to
        // zero outside its depth band, so shallow water stays slaughterfish/lamia
        // and the abyss fills with dreugh and the drowned dead.
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

		private static MobileTypes PickTreasureGuardType(bool includeBoss, bool bossSpawned, out MobileGender gender)
		{
			if (includeBoss && !bossSpawned)
				return PickBoss(out gender);

			gender = MobileGender.Unspecified;
			return PickRare();
		}

        private static GameObject SpawnEnemy(Vector3 worldPos, Transform parent, MobileTypes type, MobileGender gender)
        {
            if (parent == null)
                return null;

            Vector3 localPos = worldPos - parent.position;
            GameObject enemy = GameObjectHelper.CreateEnemy(
                type.ToString(),
                type,
                localPos,
                gender,
                parent);

            if (enemy != null)
                ConfigureSpawnedEnemy(enemy, worldPos, type);

            return enemy;
        }

		private static GameObject SpawnTreasureGuardEnemy(Vector3 worldPos, Transform parent, MobileTypes type, MobileGender gender)
		{
			GameObject enemy = SpawnEnemy(worldPos, parent, type, gender);
			SetEnemyTeam(enemy, TreasureGuardTeam);
			return enemy;
		}

		private static void SetEnemyTeam(GameObject enemy, MobileTeams team)
		{
			DaggerfallEntityBehaviour behaviour = enemy != null ? enemy.GetComponent<DaggerfallEntityBehaviour>() : null;
			if (behaviour != null && behaviour.Entity != null)
				behaviour.Entity.Team = team;
		}

        private static void ConfigureSpawnedEnemy(GameObject enemy, Vector3 worldPos, MobileTypes type)
        {
            if (enemy == null)
                return;

            // GameObjectHelper aligns non-flying enemies with raycast ground,
            // which is correct on land but unreliable over carved ocean holes.
            // Put the enemy back at the resolved underwater position.
			if (MustSpawnOnFloor(type))
				worldPos = AlignFloorEnemyController(enemy, worldPos);

            enemy.transform.position = worldPos;

            EnemyMotor motor = enemy.GetComponent<EnemyMotor>();
            GameManager gameManager = GameManager.Instance;
            if (motor != null && gameManager != null && gameManager.PlayerEntityBehaviour != null)
                motor.MakeEnemyHostileToAttacker(gameManager.PlayerEntityBehaviour);
        }

		private static Vector3 AlignFloorEnemyController(GameObject enemy, Vector3 worldPos)
		{
			CharacterController controller = enemy != null ? enemy.GetComponent<CharacterController>() : null;
			if (controller == null)
				return worldPos;

			float seafloorY = worldPos.y - FloorEnemySeafloorClearance;
			worldPos.y = seafloorY + controller.height * 0.52f;
			return worldPos;
		}

        private static int ScaledAttemptsPerPixel()
        {
            float scale = DeepWaters.Instance != null
                ? DeepWaters.Instance.EnemyFrequency / EnemyFrequencyAtMidpoint
                : 1f;

            return Mathf.Max(0, Mathf.RoundToInt(EnemyAttemptsPerPixel * scale));
        }

		internal static int EffectiveEnemyCap()
		{
			return DeepWaters.Instance != null ? DeepWaters.Instance.MaxLiveEnemies : 8;
		}

        private static int RollTreasureGuardCount()
        {
            float scaledCount = MaxTreasureGuardCount * Mathf.Clamp01(DeepWaters.Instance.EnemyFrequency / EnemyFrequencyForMaxTreasureGuards);
            return Mathf.Clamp(DeepWaterWorld.RollCount(scaledCount), 0, MaxTreasureGuardCount);
        }
    }
}
