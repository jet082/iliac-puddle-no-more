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
            // === Core path ===
            // The floor builder must subscribe to OnPromoteTerrainData before
            // any tile promotes.
            DeepWaterLocationLoadGate.Install();
            DeepWaterFloorBuilder.Install();
            DeepWaterRuntime.Install();
            OutdoorSwimDriver.Install(go);
            // Swim extras (speed multiplier, strokes, anti-tunnel clamps)
            // layered on top of DFU's native swim movement.
            go.AddComponent<OutdoorSwimMovementController>();

            // === Content and presentation ===
            WaterSurfaceManager.Install();
            // Underwater terrain-streaming buffer: keeps a wider terrain ring
            // loaded and forces a stream while swimming, so the seafloor/land
            // appears as you move instead of only when you surface. Shelved
            // during the crash hunt (expanding TerrainDistance mid-promote
            // "matched crash timing") — but that crash was the holes-texture
            // compression line (v0.55.24), now fixed, so it's safe to run again.
            go.AddComponent<DeepWaterStreamingBuffer>();
            UnderwaterEnemySpawner.Install();
            UnderwaterPassiveFishSpawner.Install();
            UnderwaterEncounterPulse.Install();
            UnderwaterDecorations.Install();
            UnderwaterLootSpawner.Install();
            go.AddComponent<PlayerShipWaterlineFix>();
            go.AddComponent<CutoutDepthQueueFix>();
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
