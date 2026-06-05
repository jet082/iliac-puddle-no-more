// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    public partial class DeepWaters
    {
        // Temporary load-crash isolation switch (v0.55.19). When true, only the
        // core hole-carve + seafloor + swim path is installed; every spawner,
        // decoration, loot, cosmetic, and terrain-texturing subsystem is skipped
        // (see WrapTerrainTexturing). The deferred carve surfaces near shore
        // without crashing but crashes on LOAD in the full build — the minimal
        // working build, which has none of these extras, does not. If lean mode
        // LOADS, an extra is the culprit (bisect by re-enabling groups); if it
        // still crashes, the core carve/mesh path is the cause.
        public static bool LeanMode = false;

        private static void InstallSubsystems(GameObject go)
        {
            // === Core path (always installed) ===
            // Hole applier must exist before the floor builder enqueues its
            // first hole mask. The floor builder itself must subscribe to
            // OnPromoteTerrainData before any tile promotes.
            DeepWaterLocationLoadGate.Install();
            go.AddComponent<DeepWaterHoleApplier>();
            DeepWaterFloorBuilder.Install();
            DeepWaterRuntime.Install();
            OutdoorSwimDriver.Install(go);
            // OutdoorSwimMovementController owns the underwater swim-direction
            // logic — without it, OutdoorSwimDriver detects we're underwater but
            // the player motor never receives swim-direction updates and the
            // character is frozen in place.
            go.AddComponent<OutdoorSwimMovementController>();

            if (LeanMode)
            {
                Debug.Log("[DeepWaters] LEAN MODE active — WaterSurfaceManager (stenciled " +
                          "water surface) + all spawners/decorations/loot/texturing/cosmetics " +
                          "OFF. Only applier+builder+runtime+swim installed. Testing whether the " +
                          "custom water surface is the deferred load-crash culprit.");
                return;
            }

            // === Extra subsystems (skipped in lean mode) ===
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
            // Skipped in lean mode: the custom terrain texturing changes how the
            // (holed) terrain renders and is a prime suspect for the deferred
            // load crash, so the isolation build runs vanilla terrain texturing.
            if (LeanMode)
                return;

            var inner = DaggerfallUnity.Instance.TerrainTexturing;
            if (inner is DeepWaterTexturing)
                return;

            DaggerfallUnity.Instance.TerrainTexturing = new DeepWaterTexturing(inner);
        }
    }
}
