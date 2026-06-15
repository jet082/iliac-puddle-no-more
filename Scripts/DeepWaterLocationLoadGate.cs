// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using System.Reflection;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Banking;
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

        public static int ActiveLoadCount
        {
            get { return activeLoads; }
        }

        public static float ActiveLoadAgeSeconds
        {
            get { return activeLoads > 0 ? Time.realtimeSinceStartup - lastIncrementTime : 0f; }
        }

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

    internal static class DeepWaterLocationUpdateSkipper
    {
        private const float MinimumSkipDepth = 4f;
        private const float DeferredRestoreCheckInterval = 0.5f;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly HashSet<int> deferredLocationKeys = new HashSet<int>();
        private static FieldInfo terrainArrayField;
        private static FieldInfo updateLocationsField;
        private static bool installed;
        private static float nextDeferredRestoreCheckTime;

        public static int LastSkippedCount { get; private set; }

        public static int DeferredLocationCount
        {
            get { return deferredLocationKeys.Count; }
        }

        public static void Install()
        {
            if (installed)
                return;

            terrainArrayField = typeof(StreamingWorld).GetField("terrainArray", PrivateInstance);
            updateLocationsField = typeof(StreamingWorld).GetField("updateLocations", PrivateInstance);
            if (terrainArrayField == null || updateLocationsField == null)
            {
                Debug.LogWarning("[DeepWaters.LocationSkip] Could not reflect StreamingWorld location fields; peripheral location skip disabled.");
                installed = true;
                return;
            }

            StreamingWorld.OnUpdateTerrainsEnd += OnUpdateTerrainsEnd;
            SaveLoadManager.OnStartLoad += OnStartLoad;
            StreamingWorld.OnTeleportToCoordinates += OnTeleport;
            installed = true;
        }

        public static void PumpDeferredRestore()
        {
            if (deferredLocationKeys.Count == 0 || Time.time < nextDeferredRestoreCheckTime)
                return;

            nextDeferredRestoreCheckTime = Time.time + DeferredRestoreCheckInterval;
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.StreamingWorld == null || gameManager.PlayerGPS == null)
                return;

            if (HasOceanConnectedActiveTerrain(gameManager.StreamingWorld))
                return;

            if (DeepWaterWorld.IsPlayerInOrAboveDeepWater(MinimumSkipDepth) ||
                IsCurrentPixelOceanConnected(gameManager.StreamingWorld, gameManager.PlayerGPS.CurrentMapPixel))
            {
                return;
            }

            RestoreDeferredLocations(gameManager.StreamingWorld);
        }

        private static void OnStartLoad(SaveData_v1 saveData)
        {
            deferredLocationKeys.Clear();
            LastSkippedCount = 0;
        }

        private static void OnTeleport(DaggerfallConnect.Utility.DFPosition pos)
        {
            deferredLocationKeys.Clear();
            LastSkippedCount = 0;
        }

        private static void OnUpdateTerrainsEnd()
        {
            LastSkippedCount = 0;

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.StreamingWorld == null || gameManager.PlayerGPS == null)
                return;

            StreamingWorld.TerrainDesc[] terrains = terrainArrayField.GetValue(gameManager.StreamingWorld) as StreamingWorld.TerrainDesc[];
            if (terrains == null)
                return;

            DaggerfallConnect.Utility.DFPosition current = gameManager.PlayerGPS.CurrentMapPixel;
            bool oceanNearby = HasOceanConnectedActiveTerrain(terrains);
            bool overDeepWater =
                DeepWaterWorld.IsPlayerInOrAboveDeepWater(MinimumSkipDepth) ||
                IsCurrentPixelOceanConnected(gameManager.StreamingWorld, current) ||
                oceanNearby;

            for (int i = 0; i < terrains.Length; i++)
            {
                StreamingWorld.TerrainDesc desc = terrains[i];
                if (!desc.active || !desc.hasLocation)
                    continue;

                int key = TerrainHelper.MakeTerrainKey(desc.mapPixelX, desc.mapPixelY);
                bool isCurrentPixel = desc.mapPixelX == current.X && desc.mapPixelY == current.Y;
                if (isCurrentPixel && deferredLocationKeys.Remove(key))
                {
                    desc.updateLocation = true;
                    terrains[i] = desc;
                    updateLocationsField.SetValue(gameManager.StreamingWorld, true);
                    continue;
                }

                if (!overDeepWater || isCurrentPixel || IsOwnedShipPixel(desc.mapPixelX, desc.mapPixelY) || !desc.updateLocation)
                    continue;

                desc.updateLocation = false;
                terrains[i] = desc;
                deferredLocationKeys.Add(key);
                LastSkippedCount++;
            }
        }

        private static void RestoreDeferredLocations(StreamingWorld streamingWorld)
        {
            StreamingWorld.TerrainDesc[] terrains = terrainArrayField.GetValue(streamingWorld) as StreamingWorld.TerrainDesc[];
            if (terrains == null)
                return;

            bool restoredAny = false;
            for (int i = 0; i < terrains.Length; i++)
            {
                StreamingWorld.TerrainDesc desc = terrains[i];
                if (!desc.active || !desc.hasLocation)
                    continue;

                int key = TerrainHelper.MakeTerrainKey(desc.mapPixelX, desc.mapPixelY);
                if (!deferredLocationKeys.Remove(key))
                    continue;

                desc.updateLocation = true;
                terrains[i] = desc;
                restoredAny = true;
            }

            if (restoredAny)
                updateLocationsField.SetValue(streamingWorld, true);
        }

        private static bool IsOwnedShipPixel(int mapPixelX, int mapPixelY)
        {
            if (!DaggerfallBankManager.OwnsShip)
                return false;

            DaggerfallConnect.Utility.DFPosition shipCoords = DaggerfallBankManager.GetShipCoords();
            return shipCoords != null && shipCoords.X == mapPixelX && shipCoords.Y == mapPixelY;
        }

        private static bool IsCurrentPixelOceanConnected(StreamingWorld streamingWorld, DaggerfallConnect.Utility.DFPosition current)
        {
            DaggerfallTerrain dfTerrain;
            Terrain terrain;
            if (!DeepWaterTerrainLookup.TryGet(streamingWorld, current.X, current.Y, out dfTerrain, out terrain))
                return false;

            DeepWaterTileData tile = dfTerrain != null ? dfTerrain.GetComponent<DeepWaterTileData>() : null;
            return tile != null &&
                   tile.IsOceanConnected &&
                   tile.HasDistanceField &&
                   DeepWaterWaterClassification.MapDataHasWater(dfTerrain.MapData);
        }

        private static bool HasOceanConnectedActiveTerrain(StreamingWorld streamingWorld)
        {
            StreamingWorld.TerrainDesc[] terrains = terrainArrayField.GetValue(streamingWorld) as StreamingWorld.TerrainDesc[];
            return terrains != null && HasOceanConnectedActiveTerrain(terrains);
        }

        private static bool HasOceanConnectedActiveTerrain(StreamingWorld.TerrainDesc[] terrains)
        {
            for (int i = 0; i < terrains.Length; i++)
            {
                StreamingWorld.TerrainDesc desc = terrains[i];
                if (!desc.active || desc.terrainObject == null)
                    continue;

                DaggerfallTerrain dfTerrain = desc.terrainObject.GetComponent<DaggerfallTerrain>();
                if (dfTerrain == null)
                    continue;

                DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();
                if (tile != null &&
                    tile.IsOceanConnected &&
                    tile.HasDistanceField &&
                    DeepWaterWaterClassification.MapDataHasWater(dfTerrain.MapData))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
