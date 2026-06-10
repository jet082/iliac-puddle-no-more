// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Tracks how many DFU location-update coroutines are currently in
    /// flight, so other subsystems can yield while DFU is laying out a
    /// town's RMB blocks. DeepWaterRuntime.IsLoadGraceActive folds this in,
    /// which pauses the spawners and forced tile refreshes while a location
    /// loads — heavy mod work concurrent with DFU's location layout caused
    /// load hitches and, in the era when terrain holes were written, native
    /// crashes.
    ///
    /// Counter pattern (not GameObject set) deliberately:
    ///   - Doesn't hold Unity references that go stale.
    ///   - Self-corrects via the stuck-counter watchdog in case the
    ///     Create/Update events aren't perfectly paired (rare but
    ///     possible across save/load or floating-origin shifts).
    /// </summary>
    public static class DeepWaterLocationLoadGate
    {
        // Number of location update coroutines currently in flight.
        private static int activeLoads;

        // Watchdog: if the counter has been stuck above zero for longer
        // than this, assume an event got dropped and reset to zero. Picks
        // a value comfortably larger than the slowest "Time to update
        // location" we see in logs (~4 s), with margin.
        private const float StuckCounterResetSeconds = 12f;
        private static float lastIncrementTime;

        public static bool IsAnyLocationLoading
        {
            get
            {
                if (activeLoads <= 0)
                    return false;

                // Self-healing watchdog. If we've been stuck above zero
                // for too long without any Create event resetting the
                // timer, an Update event must have been dropped. Reset
                // so gated work can resume.
                if (Time.realtimeSinceStartup - lastIncrementTime > StuckCounterResetSeconds)
                {
                    Debug.LogWarning("[DeepWaters.LoadGate] activeLoads=" + activeLoads +
                                     " stuck for >" + StuckCounterResetSeconds +
                                     "s — resetting (Create/Update events out of sync).");
                    activeLoads = 0;
                    return false;
                }

                return true;
            }
        }

        private static bool installed;

        public static void Install()
        {
            if (installed) return;
            StreamingWorld.OnCreateLocationGameObject += HandleCreate;
            StreamingWorld.OnUpdateLocationGameObject += HandleUpdate;
            // Save load and teleport reset the entire counter — events
            // from before the transition no longer make sense.
            SaveLoadManager.OnStartLoad += HandleReset;
            StreamingWorld.OnTeleportToCoordinates += HandleTeleport;
            installed = true;
        }

        private static void HandleCreate(DaggerfallLocation dfLocation)
        {
            // CreateLocationGameObject fired — DFU is about to lay out the
            // RMB blocks for this location inside its UpdateLocation
            // coroutine. Gate heavy work until UpdateLocationGameObject
            // fires (= layout complete).
            activeLoads++;
            lastIncrementTime = Time.realtimeSinceStartup;
        }

        private static void HandleUpdate(GameObject locationObject, bool allowYield)
        {
            if (activeLoads > 0)
                activeLoads--;
        }

        private static void HandleReset(SaveData_v1 saveData)
        {
            activeLoads = 0;
        }

        private static void HandleTeleport(DaggerfallConnect.Utility.DFPosition pos)
        {
            // Teleport spawns a fresh batch of location updates. Reset so
            // any straggler counts from the previous location can't hold
            // the gate forever.
            activeLoads = 0;
        }
    }
}
