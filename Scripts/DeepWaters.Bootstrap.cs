// Project:         Iliac Puddle No More
// License:         MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;

namespace DeepWaters
{
    public partial class DeepWaters
    {
        private static void InstallSubsystems(GameObject go)
        {
            // Keep streaming hitches from turning into multi-step physics catch-up
            // frames that trip the swim motor spike guard.
            if (Time.maximumDeltaTime > 0.1f)
                Time.maximumDeltaTime = 0.1f;

            // === Core path ===
            // The floor builder must subscribe to OnPromoteTerrainData before
            // any tile promotes.
            DeepWaterRuntime.Install();
            DeepWaterFloorBuilder.Install();
            OutdoorSwimDriver.Install(go);
            // Swim extras (speed multiplier, strokes, anti-tunnel clamps)
            // layered on top of DFU's native swim movement.
            go.AddComponent<OutdoorSwimMovementController>();
            go.AddComponent<DeepWaterSwimWorldTracker>();

            // === Content and presentation ===
            WaterSurfaceManager.Install();
            // Latest stable path does not widen DFU's terrain ring here. The
            // regular promote hooks populate seafloor/decor as tiles stream in,
            // while avoiding a larger synchronous terrain load on pixel changes.
            UnderwaterEnemySpawner.Install();
            UnderwaterPassiveFishSpawner.Install();
            UnderwaterEncounterPulse.Install();
            UnderwaterDecorations.Install();
            UnderwaterLootSpawner.Install();
            go.AddComponent<PlayerShipWaterlineFix>();
            go.AddComponent<UnderwaterDistanceFog>();
            go.AddComponent<UnderwaterPresentationEffects>();
            DeepWaterDiagnosticsRunner.Install(go);
        }

    }
}
