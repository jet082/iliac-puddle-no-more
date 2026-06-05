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
    /// town's RMB blocks. The crash signature we chased through builds
    /// v0.40-v0.48 was always:
    ///
    ///   1. Our applier calls SetHolesDelayLOD + SyncTexture on tile X.
    ///   2. SyncTexture invalidates / re-uploads tile X's holes texture.
    ///   3. CONCURRENTLY, DFU's StreamingWorld.UpdateLocation coroutine
    ///      calls CreateRMBBlockGameObject for tile Y (a town tile, often
    ///      Fonthope End / Grimton in our test seed).
    ///   4. CreateRMBBlockGameObject instantiates building prefabs with
    ///      colliders. Unity's physics setup queries terrain colliders.
    ///   5. Some terrain in the loaded ring is in a transitional state
    ///      because SyncTexture just re-uploaded its holes mask.
    ///   6. Native crash. Log truncates after the "Location GameObject
    ///      Created: ..." line, with no managed exception.
    ///
    /// IT/WoD never hit this because IT never carves holes — its water
    /// tiles are flat at sea level, no SetHoles ever called. We DO need
    /// holes (for swimmable depth past Unity's 100 m heightmap limit),
    /// so we keep the SetHoles path but gate it on this tracker: while
    /// any location load is in flight, the applier yields and waits.
    /// Once DFU fires OnUpdateLocationGameObject (the END of the
    /// UpdateLocation coroutine, after all RMB blocks placed and
    /// billboards applied), the counter drops and the applier resumes.
    ///
    /// Counter pattern (not GameObject set) deliberately:
    ///   - Doesn't hold Unity references that go stale.
    ///   - Self-corrects via the periodic safety reset below in case the
    ///     Create/Update events aren't perfectly paired (rare but
    ///     possible across save/load or floating-origin shifts).
    /// </summary>
    public static class DeepWaterLocationLoadGate
    {
        // Number of location update coroutines currently in flight. The
        // applier yields while this is non-zero.
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
                // so the applier can resume.
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

        public static int ActiveLoads { get { return activeLoads; } }

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

        public static void Uninstall()
        {
            if (!installed) return;
            StreamingWorld.OnCreateLocationGameObject -= HandleCreate;
            StreamingWorld.OnUpdateLocationGameObject -= HandleUpdate;
            SaveLoadManager.OnStartLoad -= HandleReset;
            StreamingWorld.OnTeleportToCoordinates -= HandleTeleport;
            installed = false;
        }

        private static void HandleCreate(DaggerfallLocation dfLocation)
        {
            // CreateLocationGameObject fired — DFU is about to lay out the
            // RMB blocks for this location inside its UpdateLocation
            // coroutine. Block the applier until UpdateLocationGameObject
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
            // any straggler counts from the previous location can't gate
            // the post-teleport drain forever.
            activeLoads = 0;
        }
    }
}
