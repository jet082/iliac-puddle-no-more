// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Mod bootstrap and settings holder.
    /// </summary>
    public partial class DeepWaters : MonoBehaviour
    {
        internal static DeepWaters Instance { get; private set; }
        internal static Mod Mod { get; private set; }

		private const float SliderMidpoint = 0.5f;
		private const float EnemyFrequencyAtMidpoint = 0.5f;
		internal const int MaxLiveFishLimit = 1080;
		private const float PassiveFishFrequencyAtMidpoint = 3.0f;
		private const float DecorationFrequencyAtMidpoint = 3.75f;
		private const float SeafloorLootRateAtMidpoint = 0.7f;
		private const float TreasureClusterRateAtMidpoint = 0.1f;
		private const float UnderwaterFogDensityMaxAtMidpoint = 0.014f;
		private const float UnderwaterVisionDistanceAtDefaultSetting = 95f;
		private const float MinimumUnderwaterVisionDistance = 22f;
		private const float MaximumUnderwaterVisionDistance = 360f;
		private const float MinFogDistanceMultiplier = 0.25f;
		private const float MaxFogDistanceMultiplier = 6.0f;
		private const float MinSwimSpeedMultiplier = 0.25f;
		private const float MaxSwimSpeedMultiplier = 30.0f;
		private static readonly Color NightUnderwaterFogTint = new Color(0.035f, 0.065f, 0.075f, 1f);

		internal float WaterDepth { get; private set; } = 200f;
		internal bool SpawnWaterSurfaces { get; private set; } = true;
		internal bool SpawnUnderwaterEnemies { get; private set; } = true;
		internal int MaxLiveEnemies { get; private set; } = 8;
		internal float EnemyFrequency { get; private set; } = EnemyFrequencyAtMidpoint;
		internal float PassiveFishFrequency { get; private set; } = PassiveFishFrequencyAtMidpoint;
		internal int MaxLiveFish { get; private set; } = MaxLiveFishLimit;
		internal bool SpawnUnderwaterDecorations { get; private set; } = true;
		internal int DecorationPopulateRadius { get; private set; } = 1;
		internal float DecorationFrequency { get; private set; } = DecorationFrequencyAtMidpoint;
		internal float SeafloorLootRate { get; private set; } = SeafloorLootRateAtMidpoint;
		internal int MaxLiveLootObjects { get; private set; } = 32;
		internal float TreasureClusterRate { get; private set; } = TreasureClusterRateAtMidpoint;
		internal int MaxLiveTreasureClusters { get; private set; } = 3;
		internal bool TreasureCove { get; private set; }
		private float WaterSurfaceTopTransparency { get; set; } = SliderMidpoint;
		private float WaterSurfaceBottomTransparency { get; set; } = SliderMidpoint;
		internal float UnderwaterFogStrength { get; private set; } = SliderMidpoint;
		internal float UnderwaterFogDistance { get; private set; } = SliderMidpoint;
		private bool ArgonianInfiniteBreath { get; set; } = true;
		internal float SwimSpeedMultiplier { get; private set; } = 1f;
		internal bool EnableSwimStroke { get; private set; }

		internal float WaterSurfaceTopAlpha
		{
			get { return SurfaceTransparencySliderToAlpha(WaterSurfaceTopTransparency); }
		}

		internal float WaterSurfaceBottomAlpha
		{
			get { return SurfaceTransparencySliderToAlpha(WaterSurfaceBottomTransparency); }
		}

		internal float UnderwaterFogDensityMax
		{
			get { return GetScaledSliderValue(UnderwaterFogStrength, UnderwaterFogDensityMaxAtMidpoint) / UnderwaterFogDistanceMultiplier; }
		}

		internal float UnderwaterFogDistanceMultiplier
		{
			get { return FogDistanceSliderToMultiplier(UnderwaterFogDistance); }
		}

		internal float UnderwaterVisionDistance
		{
			get
			{
				return Mathf.Clamp(
					UnderwaterVisionDistanceAtDefaultSetting * UnderwaterFogDistanceMultiplier,
					MinimumUnderwaterVisionDistance,
					MaximumUnderwaterVisionDistance);
			}
		}

		internal static Color GetUnderwaterFogColor()
		{
			Color fogColor = new Color32(14, 25, 21, 255);
			GameManager gameManager = GameManager.Instance;
			if (gameManager != null &&
				gameManager.PlayerEnterExit != null &&
				gameManager.PlayerEnterExit.UnderwaterFog != null)
			{
				fogColor = gameManager.PlayerEnterExit.UnderwaterFog.waterFogColor;
			}

			Color nightTint = NightUnderwaterFogTint;
			nightTint.a = fogColor.a;
			return Color.Lerp(nightTint, fogColor, GetDaylightFactor());
		}

		internal static float GetDaylightFactor()
		{
			DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
			if (dfUnity != null && dfUnity.WorldTime != null && dfUnity.WorldTime.Now.IsNight)
				return 0f;

			GameManager gameManager = GameManager.Instance;
			SunlightManager sunlightManager = gameManager != null ? gameManager.SunlightManager : null;
			return sunlightManager != null ? Mathf.Clamp01(sunlightManager.DaylightScale) : 1f;
		}

        [Invoke(StateManager.StateTypes.Start, 200)]
        public static void Init(InitParams initParams)
        {
            Mod = initParams.Mod;
            var go = new GameObject(Mod.Title);
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<DeepWaters>();

            Mod.LoadSettingsCallback = Instance.LoadSettings;
            Instance.LoadSettings();
			int[] templateIndices = PassiveFishSpeciesCatalog.CustomItemTemplateIndices;
			for (int i = 0; i < templateIndices.Length; i++)
			{
				DaggerfallUnity.Instance.ItemHelper.RegisterCustomItem(
					templateIndices[i],
					PassiveFishSpeciesCatalog.FishItemGroup);
			}
            Instance.LoadDistanceBake();
            InstallSubsystems(go);

            Mod.IsReady = true;
        }

		private void LoadSettings()
		{
			ApplySettings(Mod.GetSettings());
		}

		private void LoadSettings(ModSettings settings, ModSettingsChange change)
		{
			ApplySettings(settings);
			WaterSurfaceResources.ApplyMaterialSettings();
			WaterSurfaceManager.RefreshLoadedSurfaces();
			UnderwaterDecorations.RefreshPlayerArea();
			if (DeepWaterRuntime.CanRunHeavyRuntimeWork)
			{
				// Settings changed, but DFU has not re-promoted any tiles.
				// Force the rebuild so existing meshes reflect the new values.
				DeepWaterFloorBuilder.RefreshLoadedTiles(force: true);
			}
		}

		private void ApplySettings(ModSettings settings)
		{
			WaterDepth = GetFloatSetting(settings, "WaterDepth");
			SpawnWaterSurfaces = GetBoolSetting(settings, "SpawnWaterSurfaces");
			SpawnUnderwaterEnemies = GetBoolSetting(settings, "SpawnUnderwaterEnemies");
			EnemyFrequency = GetScaledSliderSetting(settings, "EnemyFrequency", EnemyFrequencyAtMidpoint);
			MaxLiveEnemies = Mathf.Max(0, GetIntSetting(settings, "MaxLiveEnemies"));
			PassiveFishFrequency = GetScaledSliderSetting(settings, "PassiveFishFrequency", PassiveFishFrequencyAtMidpoint);
			MaxLiveFish = Mathf.Clamp(GetIntSetting(settings, "MaxLiveFish"), 0, MaxLiveFishLimit);
			SpawnUnderwaterDecorations = GetBoolSetting(settings, "SpawnUnderwaterDecorations");
			DecorationPopulateRadius = Mathf.Clamp(GetIntSetting(settings, "DecorationPopulateRadius"), 1, 3);
			DecorationFrequency = GetScaledSliderSetting(settings, "DecorationFrequency", DecorationFrequencyAtMidpoint);
			SeafloorLootRate = GetScaledSliderSetting(settings, "SeafloorLootRate", SeafloorLootRateAtMidpoint);
			MaxLiveLootObjects = Mathf.Max(0, GetIntSetting(settings, "MaxLiveLootObjects"));
			TreasureClusterRate = GetScaledSliderSetting(settings, "TreasureClusterRate", TreasureClusterRateAtMidpoint);
			MaxLiveTreasureClusters = Mathf.Max(0, GetIntSetting(settings, "MaxLiveTreasureClusters"));
			TreasureCove = GetBoolSetting(settings, "TreasureCove");
			WaterSurfaceTopTransparency = GetFloatSetting(settings, "WaterSurfaceTopTransparency");
			WaterSurfaceBottomTransparency = GetFloatSetting(settings, "WaterSurfaceBottomTransparency");
			UnderwaterFogStrength = GetFloatSetting(settings, "UnderwaterFogStrength");
			UnderwaterFogDistance = GetFloatSetting(settings, "UnderwaterFogDistance");
			ArgonianInfiniteBreath = GetBoolSetting(settings, "ArgonianInfiniteBreath");
			SwimSpeedMultiplier = ClampSwimSpeedMultiplier(GetFloatSetting(settings, "SwimSpeedMultiplier"));
			EnableSwimStroke = GetBoolSetting(settings, "EnableSwimStroke");
		}

		private static float GetScaledSliderSetting(ModSettings settings, string key, float valueAtMidpoint)
		{
			return GetScaledSliderValue(GetFloatSetting(settings, key), valueAtMidpoint);
		}

		private static float GetScaledSliderValue(float sliderValue, float valueAtMidpoint)
		{
			return Mathf.Clamp01(sliderValue) * (valueAtMidpoint / SliderMidpoint);
		}

		private static float SurfaceTransparencySliderToAlpha(float sliderValue)
		{
			float slider = Mathf.Clamp01(sliderValue);
			return 1f - slider * slider;
		}

		private static float FogDistanceSliderToMultiplier(float sliderValue)
		{
			float t = Mathf.Clamp01(sliderValue);
			return t <= SliderMidpoint
				? Mathf.Lerp(MinFogDistanceMultiplier, 1f, t / SliderMidpoint)
				: Mathf.Lerp(1f, MaxFogDistanceMultiplier, (t - SliderMidpoint) / SliderMidpoint);
		}

		private static float ClampSwimSpeedMultiplier(float value)
		{
			return Mathf.Clamp(value, MinSwimSpeedMultiplier, MaxSwimSpeedMultiplier);
		}

		private static bool GetBoolSetting(ModSettings settings, string key)
		{
			return settings.GetBool("General", key);
		}

		private static float GetFloatSetting(ModSettings settings, string key)
		{
			return settings.GetFloat("General", key);
		}

		private static int GetIntSetting(ModSettings settings, string key)
		{
			return settings.GetInt("General", key);
		}

        // Load the pre-baked distance-to-coast field from the mod's bundled
        // Resources. Without this, DeepWaterTileData.HasDistanceField stays
        // false and every tile reports "NOT ocean-connected (distField=False)"
        // → no carving → can't swim. The working backup pre-dated the bake
        // architecture entirely and used per-tile BFS for distance, which is
        // why the working-backup DeepWaters.cs had no LoadDistanceBake call;
        // the current DeepWaterTileData requires the bake.
        private void LoadDistanceBake()
        {
            if (Mod == null)
            {
                Debug.LogError("[DeepWaters] Cannot load distance bake — Mod reference is null.");
                return;
            }

            // Pick the bake that matches the active terrain heightmap. Stock
            // DefaultTerrainSampler means no terrain overhaul (Interesting
            // Terrains / WoD replace the sampler with their own type), so the
            // vanilla bake lines the carve/shore data up with vanilla coasts.
            string assetName = DeepWaterDistanceBake.BakeAssetName;
            bool vanillaTerrain = DaggerfallUnity.Instance != null &&
                                  DaggerfallUnity.Instance.TerrainSampler is DefaultTerrainSampler;
            if (vanillaTerrain)
            {
                if (Mod.HasAsset(DeepWaterDistanceBake.VanillaBakeAssetName))
                {
                    assetName = DeepWaterDistanceBake.VanillaBakeAssetName;
                }
                else
                {
                    Debug.LogWarning("[DeepWaters] Vanilla terrain is active but no '" +
                                     DeepWaterDistanceBake.VanillaBakeAssetName +
                                     "' asset is bundled — falling back to the terrain-overhaul bake. " +
                                     "Shore depths may not match vanilla coastlines. Run Tools > Deep " +
                                     "Waters > Bake Distance Field (Vanilla Terrain) and rebuild the mod.");
                }
            }

            TextAsset bakeAsset = Mod.GetAsset<TextAsset>(assetName);
            if (bakeAsset == null || bakeAsset.bytes == null || bakeAsset.bytes.Length == 0)
            {
                Debug.LogError("[DeepWaters] Distance bake asset '" + assetName +
                               "' not found in mod bundle. Run Tools > Deep Waters > Bake " +
                               "Distance Field in the Unity editor, then rebuild the mod.");
                return;
            }

            if (!DeepWaterDistanceBake.TryLoadBytes(bakeAsset.bytes))
                Debug.LogError("[DeepWaters] Distance bake parse failed — seafloor will not build.");
        }

        void Update()
        {
            DeepWaterRuntime.Pump();
			DeepWaterFloorMaterial.UpdateLighting();
			SuppressVanillaWaterEncounters();
			UnderwaterDecorations.ProcessWorkQueue();
			UnderwaterEncounterPulse.Pump();
			UnderwaterLootSpawner.Pump();
        }

		void LateUpdate()
		{
			// ponytail: LateUpdate keeps this after DFU clears constant effects.
			ApplyArgonianInfiniteBreath();
			UnderwaterPassiveFishSpawner.UpdateFishLootIcon();
		}

		private void ApplyArgonianInfiniteBreath()
		{
			if (!ArgonianInfiniteBreath)
				return;

			GameManager gameManager = GameManager.Instance;
			if (gameManager == null || !gameManager.IsPlayingGame())
				return;

			PlayerEntity entity = gameManager.PlayerEntity;
			if (entity != null && entity.Race == Races.Argonian)
				entity.IsWaterBreathing = true;
		}

		private static void SuppressVanillaWaterEncounters()
		{
			GameManager gameManager = GameManager.Instance;
			if (gameManager == null ||
				!gameManager.IsPlayingGame() ||
				gameManager.PlayerEnterExit == null ||
				gameManager.PlayerEnterExit.IsPlayerInside ||
				gameManager.PlayerEntity == null)
			{
				return;
			}

			if (DeepWaterWorld.IsPlayerInOrAboveDeepWater(0.25f))
				gameManager.PlayerEntity.PreventEnemySpawns = true;
		}
    }

}
