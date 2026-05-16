// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    public partial class DeepWaters
    {
        private static void InstallSubsystems(GameObject go)
        {
            // Must run before any terrain tile promotes.
            BoundaryReconciler.Install();
            DeepWaterRuntime.Install();
            WaterSurfaceManager.Install();
            UnderwaterEnemySpawner.Install();
            UnderwaterPassiveFishSpawner.Install();
            UnderwaterEncounterPulse.Install();
            UnderwaterDecorations.Install();
            UnderwaterLootSpawner.Install();

            OutdoorSwimDriver.Install(go);
            go.AddComponent<PlayerShipWaterlineFix>();
            go.AddComponent<UnderwaterDistanceFog>();
            go.AddComponent<UnderwaterWaveShadowFix>();
            go.AddComponent<ArgonianWaterBreathing>();
            go.AddComponent<SwimmingSfxBridge>();
            go.AddComponent<UnderwaterWeatherSuppressor>();
            go.AddComponent<UnderwaterAmbientMuter>();
        }

        private void RegisterCustomItems()
        {
            int[] templateIndices = UnderwaterPassiveFishSpawner.CustomItemTemplateIndices;
            for (int i = 0; i < templateIndices.Length; i++)
            {
                DaggerfallUnity.Instance.ItemHelper.RegisterCustomItem(
                    templateIndices[i],
                    UnderwaterPassiveFishSpawner.FishItemGroup);
            }
        }

        private void WrapTerrainTexturing()
        {
            var inner = DaggerfallUnity.Instance.TerrainTexturing;
            if (inner is DeepWaterTexturing)
                return;

            DaggerfallUnity.Instance.TerrainTexturing = new DeepWaterTexturing(inner);
        }
    }
}
