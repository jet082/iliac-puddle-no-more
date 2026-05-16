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
        private const float UnderwaterFogDensityMaxAtMidpoint = 0.08f;
        private const float OpaqueWaterSurfaceAlpha = 1.0f;
        private const float MostTransparentWaterSurfaceAlpha = 0.3f;
        private const float MinFogDistanceMultiplier = 0.25f;
        private const float MaxFogDistanceMultiplier = 6.0f;
        private const float DefaultEncounterMinSpawnDistance = 35f;
        private const float DefaultEncounterMaxSpawnDistance = 55f;
        private const float ClearWaterEncounterMinSpawnDistance = 90f;
        private const float ClearWaterEncounterMaxSpawnDistance = 180f;
        private const float EncounterImmediateViewDistance = 55f;

        public float WaterDepth { get; private set; } = 35f;
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

        public float WaterSurfaceTopAlpha
        {
            get { return TransparencySliderToAlpha(WaterSurfaceTopTransparency); }
        }

        public float WaterSurfaceBottomAlpha
        {
            get { return TransparencySliderToAlpha(WaterSurfaceBottomTransparency); }
        }

        public float UnderwaterFogDensityMax
        {
            get { return GetScaledSliderValue(UnderwaterFogStrength, UnderwaterFogDensityMaxAtMidpoint) / UnderwaterFogDistanceMultiplier; }
        }

        public float UnderwaterFogDistanceMultiplier
        {
            get { return FogDistanceSliderToMultiplier(UnderwaterFogDistance); }
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

        private static float TransparencySliderToAlpha(float sliderValue)
        {
            return Mathf.Lerp(OpaqueWaterSurfaceAlpha, MostTransparentWaterSurfaceAlpha, Mathf.Clamp01(sliderValue));
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
