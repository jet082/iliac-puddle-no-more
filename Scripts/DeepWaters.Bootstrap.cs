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
            // Physics catch-up clamp. After a frame longer than
            // Time.maximumDeltaTime, Unity runs maximumDeltaTime /
            // fixedDeltaTime catch-up physics steps EVERY frame; at the
            // default 0.333s cap that is ~15 steps, and in a town scene one
            // streaming hitch locked the game below 3fps permanently — each
            // 15-step frame exceeded the cap again (pausing "fixed" it
            // because timeScale=0 stops physics and clears the debt). 0.1s
            // (max ~5 steps) turns hitches into a brief slow-motion instead.
            if (Time.maximumDeltaTime > 0.1f)
            {
                Debug.Log("[DeepWaters] Clamping Time.maximumDeltaTime " +
                          Time.maximumDeltaTime.ToString("F2") + "s -> 0.10s (physics catch-up cap).");
                Time.maximumDeltaTime = 0.1f;
            }

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
