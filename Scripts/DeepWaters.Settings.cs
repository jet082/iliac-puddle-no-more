// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;
using UnityEngine;

namespace DeepWaters
{
    public partial class DeepWaters
    {
        private const float SliderMidpoint = 0.5f;
        private const float EnemyFrequencyAtMidpoint = 0.5f;
        private const float PassiveFishFrequencyAtMidpoint = 3.0f;
        private const float DecorationFrequencyAtMidpoint = 3.75f;
        private const float SeafloorLootRateAtMidpoint = 0.7f;
        private const float TreasureClusterRateAtMidpoint = 0.1f;
        private const float UnderwaterFogDensityMaxAtMidpoint = 0.014f;
        private const float UnderwaterVisionDistanceAtDefaultSetting = 95f;
        private const float MinimumUnderwaterVisionDistance = 22f;
        private const float MaximumUnderwaterVisionDistance = 360f;
        // Both surfaces map the slider linearly: alpha = 1 - transparency, so
        // 0.1 transparency = 90% opaque and 1.0 transparency = fully invisible.
        private const float TopOpaqueWaterSurfaceAlpha = 1.0f;
        private const float TopMostTransparentWaterSurfaceAlpha = 0.0f;
        private const float BottomOpaqueWaterSurfaceAlpha = 1.0f;
        private const float BottomMostTransparentWaterSurfaceAlpha = 0.0f;
        private const float MinFogDistanceMultiplier = 0.25f;
        private const float MaxFogDistanceMultiplier = 6.0f;
        private const float MinSwimSpeedMultiplier = 0.25f;
        private const float MaxSwimSpeedMultiplier = 30.0f;
        private const float DefaultEncounterMinSpawnDistance = 35f;
        private const float DefaultEncounterMaxSpawnDistance = 55f;
        private const float ClearWaterEncounterMinSpawnDistance = 90f;
        private const float ClearWaterEncounterMaxSpawnDistance = 180f;
        private const float EncounterImmediateViewDistance = 55f;

        public float WaterDepth { get; private set; } = 200f;
        public bool SpawnWaterSurfaces { get; private set; } = true;
        public bool SpawnUnderwaterEnemies { get; private set; } = true;
        public int MaxLiveEnemies { get; private set; } = 8;
        public float EnemyFrequency { get; private set; } = EnemyFrequencyAtMidpoint;
        public float PassiveFishFrequency { get; private set; } = PassiveFishFrequencyAtMidpoint;
        public int MaxLiveFish { get; private set; } = 54;
        public bool SpawnUnderwaterDecorations { get; private set; } = true;
        public int DecorationPopulateRadius { get; private set; } = 1;
        public float DecorationFrequency { get; private set; } = DecorationFrequencyAtMidpoint;
        public float SeafloorLootRate { get; private set; } = SeafloorLootRateAtMidpoint;
        public int MaxLiveLootObjects { get; private set; } = 32;
        public int MaxStrayLootPerPulse { get; private set; } = 12;
        public int TreasureCoveMaxStrayLootPerPulse { get; private set; } = 18;
        public float TreasureClusterRate { get; private set; } = TreasureClusterRateAtMidpoint;
        public int MaxLiveTreasureClusters { get; private set; } = 3;
        public bool TreasureCove { get; private set; }
        public int LootSpawnMinDistance { get; private set; } = 42;
        public int LootSpawnMaxDistance { get; private set; } = 72;
        public float WaterSurfaceTopTransparency { get; private set; } = SliderMidpoint;
        public float WaterSurfaceBottomTransparency { get; private set; } = SliderMidpoint;
        public float WaterSurfaceDistanceFalloff { get; private set; } = SliderMidpoint;
        public float UnderwaterFogStrength { get; private set; } = SliderMidpoint;
        public float UnderwaterFogDistance { get; private set; } = SliderMidpoint;
        public bool ArgonianInfiniteBreath { get; private set; } = true;
        public float SwimSpeedMultiplier { get; private set; } = 1f;
        public bool EnableSwimStroke { get; private set; }

        public float WaterSurfaceTopAlpha
        {
            get
            {
                return TransparencySliderToAlpha(
                    WaterSurfaceTopTransparency,
                    TopOpaqueWaterSurfaceAlpha,
                    TopMostTransparentWaterSurfaceAlpha);
            }
        }

        public float WaterSurfaceBottomAlpha
        {
            get
            {
                return TransparencySliderToAlpha(
                    WaterSurfaceBottomTransparency,
                    BottomOpaqueWaterSurfaceAlpha,
                    BottomMostTransparentWaterSurfaceAlpha);
            }
        }

        public float UnderwaterFogDensityMax
        {
            get { return GetScaledSliderValue(UnderwaterFogStrength, UnderwaterFogDensityMaxAtMidpoint) / UnderwaterFogDistanceMultiplier; }
        }

        public float UnderwaterFogDistanceMultiplier
        {
            get { return FogDistanceSliderToMultiplier(UnderwaterFogDistance); }
        }

        public float UnderwaterVisionDistance
        {
            get
            {
                return Mathf.Clamp(
                    UnderwaterVisionDistanceAtDefaultSetting * UnderwaterFogDistanceMultiplier,
                    MinimumUnderwaterVisionDistance,
                    MaximumUnderwaterVisionDistance);
            }
        }

        public float EncounterSpawnMinDistance
        {
            get { return Mathf.Lerp(DefaultEncounterMinSpawnDistance, ClearWaterEncounterMinSpawnDistance, EncounterVisibilityExpansion); }
        }

        public float EncounterSpawnMaxDistance
        {
            get { return Mathf.Lerp(DefaultEncounterMaxSpawnDistance, ClearWaterEncounterMaxSpawnDistance, EncounterVisibilityExpansion); }
        }

        public float EncounterSpawnViewSafetyDistance
        {
            get { return EncounterImmediateViewDistance; }
        }

        public static Color GetUnderwaterFogColor()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null &&
                gameManager.PlayerEnterExit != null &&
                gameManager.PlayerEnterExit.UnderwaterFog != null)
            {
                return gameManager.PlayerEnterExit.UnderwaterFog.waterFogColor;
            }

            return new Color32(14, 25, 21, 255);
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
                // Settings changed (e.g. WaterDepth, climate-band hue,
                // hole buffer) but DFU has not re-promoted any tiles —
                // heightmap arrays still match what we last built
                // against. Force the rebuild so the new settings
                // actually take effect on the existing meshes.
                DeepWaterFloorBuilder.RefreshLoadedTiles(force: true);
            }
        }

        private void ApplySettings(ModSettings s)
        {
            WaterDepth = GetFloatSetting(s, "WaterDepth");
            SpawnWaterSurfaces = GetBoolSetting(s, "SpawnWaterSurfaces");
            SpawnUnderwaterEnemies = GetBoolSetting(s, "SpawnUnderwaterEnemies");
            EnemyFrequency = GetScaledSliderSetting(s, "EnemyFrequency", EnemyFrequencyAtMidpoint);
            MaxLiveEnemies = Mathf.Max(0, GetIntSetting(s, "MaxLiveEnemies"));
            PassiveFishFrequency = GetScaledSliderSetting(s, "PassiveFishFrequency", PassiveFishFrequencyAtMidpoint);
            MaxLiveFish = Mathf.Clamp(GetIntSetting(s, "MaxLiveFish"), 0, 1080);
            SpawnUnderwaterDecorations = GetBoolSetting(s, "SpawnUnderwaterDecorations");
            DecorationPopulateRadius = Mathf.Clamp(GetIntSetting(s, "DecorationPopulateRadius"), 1, 3);
            DecorationFrequency = GetScaledSliderSetting(s, "DecorationFrequency", DecorationFrequencyAtMidpoint);
            SeafloorLootRate = GetScaledSliderSetting(s, "SeafloorLootRate", SeafloorLootRateAtMidpoint);
            MaxLiveLootObjects = Mathf.Max(0, GetIntSetting(s, "MaxLiveLootObjects"));
            TreasureClusterRate = GetScaledSliderSetting(s, "TreasureClusterRate", TreasureClusterRateAtMidpoint);
            MaxLiveTreasureClusters = Mathf.Max(0, GetIntSetting(s, "MaxLiveTreasureClusters"));
            TreasureCove = GetBoolSetting(s, "TreasureCove");
            WaterSurfaceTopTransparency = GetFloatSetting(s, "WaterSurfaceTopTransparency");
            WaterSurfaceBottomTransparency = GetFloatSetting(s, "WaterSurfaceBottomTransparency");
            WaterSurfaceDistanceFalloff = GetFloatSetting(s, "WaterSurfaceDistanceFalloff");
            UnderwaterFogStrength = GetFloatSetting(s, "UnderwaterFogStrength");
            UnderwaterFogDistance = GetFloatSetting(s, "UnderwaterFogDistance");
            ArgonianInfiniteBreath = GetBoolSetting(s, "ArgonianInfiniteBreath");
            SwimSpeedMultiplier = ClampSwimSpeedMultiplier(GetFloatSetting(s, "SwimSpeedMultiplier"));
            EnableSwimStroke = GetBoolSetting(s, "EnableSwimStroke");
        }

        private float EncounterVisibilityExpansion
        {
            get
            {
                float surfaceClarity = Mathf.Max(
                    SurfaceClarity01(WaterSurfaceTopTransparency),
                    SurfaceClarity01(WaterSurfaceBottomTransparency));
                float fogRange = AboveMidpoint01(UnderwaterFogDistance);
                float fogThinness = Mathf.Clamp01((SliderMidpoint - UnderwaterFogStrength) / SliderMidpoint);
                return Mathf.Clamp01(Mathf.Max(Mathf.Max(surfaceClarity, fogRange), fogThinness));
            }
        }

        private static float GetScaledSliderSetting(ModSettings settings, string key, float valueAtMidpoint)
        {
            return GetScaledSliderValue(GetFloatSetting(settings, key), valueAtMidpoint);
        }

        private static float GetScaledSliderValue(float sliderValue, float valueAtMidpoint)
        {
            return Mathf.Clamp01(sliderValue) * (valueAtMidpoint / SliderMidpoint);
        }

        private static float TransparencySliderToAlpha(float sliderValue, float opaqueAlpha, float transparentAlpha)
        {
            // Piecewise mapping: low settings stay nearly opaque (0.1 -> 98%
            // opaque), then opacity falls linearly to fully invisible at 1.0.
            float slider = Mathf.Clamp01(sliderValue);
            float opacity01 = slider <= 0.1f
                ? Mathf.Lerp(1f, 0.98f, slider / 0.1f)
                : Mathf.Lerp(0.98f, 0f, (slider - 0.1f) / 0.9f);
            return Mathf.Lerp(transparentAlpha, opaqueAlpha, opacity01);
        }

        private static float SurfaceClarity01(float sliderValue)
        {
            return Mathf.Clamp01(sliderValue);
        }

        private static float AboveMidpoint01(float sliderValue)
        {
            return Mathf.Clamp01((Mathf.Clamp01(sliderValue) - SliderMidpoint) / SliderMidpoint);
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
    }
}
