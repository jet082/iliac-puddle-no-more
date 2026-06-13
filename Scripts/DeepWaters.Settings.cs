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
        private const float EnemyFrequencyAtMidpoint = 0.3f;
        private const float PassiveFishFrequencyAtMidpoint = 0.6f;
        private const float DecorationFrequencyAtMidpoint = 1.0f;
        private const float SeafloorLootRateAtMidpoint = 0.7f;
        private const float TreasureClusterRateAtMidpoint = 0.1f;
        private const float UnderwaterFogDensityMaxAtMidpoint = 0.014f;
        private const float UnderwaterVisionDistanceAtDefaultSetting = 70f;
        private const float MinimumUnderwaterVisionDistance = 22f;
        private const float MaximumUnderwaterVisionDistance = 260f;
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
        private const float EncounterImmediateViewDistance = 55f;

        public float WaterDepth { get; private set; } = 200f;
        public bool SpawnWaterSurfaces { get; private set; } = true;
        public bool SpawnUnderwaterEnemies { get; private set; } = true;
        public float EnemyFrequency { get; private set; } = EnemyFrequencyAtMidpoint;
        public float PassiveFishFrequency { get; private set; } = PassiveFishFrequencyAtMidpoint;
        public bool FishParadise { get; private set; }
        public bool SpawnUnderwaterDecorations { get; private set; } = true;
        public float DecorationFrequency { get; private set; } = DecorationFrequencyAtMidpoint;
        public float SeafloorLootRate { get; private set; } = SeafloorLootRateAtMidpoint;
        public float TreasureClusterRate { get; private set; } = TreasureClusterRateAtMidpoint;
        public bool TreasureCove { get; private set; }
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
            get { return DefaultEncounterMinSpawnDistance; }
        }

        public float EncounterSpawnMaxDistance
        {
            get { return DefaultEncounterMaxSpawnDistance; }
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
            PassiveFishFrequency = GetScaledSliderSetting(s, "PassiveFishFrequency", PassiveFishFrequencyAtMidpoint);
            FishParadise = GetBoolSetting(s, "FishParadise");
            SpawnUnderwaterDecorations = GetBoolSetting(s, "SpawnUnderwaterDecorations");
            DecorationFrequency = GetScaledSliderSetting(s, "DecorationFrequency", DecorationFrequencyAtMidpoint);
            SeafloorLootRate = GetScaledSliderSetting(s, "SeafloorLootRate", SeafloorLootRateAtMidpoint);
            TreasureClusterRate = GetScaledSliderSetting(s, "TreasureClusterRate", TreasureClusterRateAtMidpoint);
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
    }
}
