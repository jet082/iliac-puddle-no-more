// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using System.Reflection;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Banking;
using DaggerfallWorkshop.Game.Serialization;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Small lifecycle hub for transient outdoor ocean content.
    /// DFU reuses terrain objects during world jumps, so systems that parent
    /// objects to terrain need one shared reset signal when the world is rebuilt.
    /// </summary>
    internal static class DeepWaterRuntime
    {
        internal static event System.Action OnTransientReset;

        // Heavy-work grace period after save load / teleport. Spawners
        // (decorations, loot, encounters, fish) check
        // CanRunHeavyRuntimeWork before doing any work, so they pause
        // for this duration after a world transition while the streaming
        // ring re-promotes. Prevents a flurry of GameObject
        // instantiation while DFU is still rebuilding terrain.
		private const float PostLoadHeavyWorkGraceSeconds = 1.5f;
		private const float LocationLoadStuckResetSeconds = 12f;
		private const float MinimumLocationSkipDepth = 4f;
		private const float DeferredLocationRestoreCheckInterval = 0.5f;
        private static float heavyWorkResumeTime;
        private static bool postTransitionRefreshPending;
		private static int activeLocationLoads;
		private static float lastLocationLoadIncrementTime;
		private static readonly HashSet<int> deferredLocationKeys = new HashSet<int>();
		private static FieldInfo terrainArrayField;
		private static FieldInfo updateLocationsField;
		private static float nextDeferredLocationRestoreCheckTime;

        private static bool installed;
        private static bool terrainUpdateEventActive;
        private static bool terrainUpdateReflectionWarningLogged;
        private static FieldInfo terrainUpdateRunningField;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        // Shared runtime gates for streaming, terrain mutation, and transient content.
        internal static bool CanRunLightRuntimeWork
        {
            get
            {
                GameManager gameManager = GameManager.Instance;
                return gameManager != null &&
                       gameManager.IsPlayingGame() &&
                       (SaveLoadManager.Instance == null || !SaveLoadManager.Instance.LoadInProgress);
            }
        }

        internal static bool CanRunHeavyRuntimeWork
        {
            get
            {
                return CanRunLightRuntimeWork &&
                       Time.realtimeSinceStartup >= heavyWorkResumeTime;
            }
        }

        internal static bool IsLoadGraceActive
        {
            get { return !CanRunHeavyRuntimeWork || IsAnyLocationLoading; }
        }

		internal static int ActiveLocationLoadCount
		{
			get { return activeLocationLoads; }
		}

		internal static float ActiveLocationLoadAgeSeconds
		{
			get { return activeLocationLoads > 0 ? Time.realtimeSinceStartup - lastLocationLoadIncrementTime : 0f; }
		}

		internal static int LastLocationSkippedCount { get; private set; }

		internal static int DeferredLocationCount
		{
			get { return deferredLocationKeys.Count; }
		}

        internal static bool IsPostTransitionRefreshPending
        {
            get { return postTransitionRefreshPending; }
        }

        internal static float HeavyWorkResumeInSeconds
        {
            get
            {
                if (float.IsPositiveInfinity(heavyWorkResumeTime))
                    return -1f;

                return Mathf.Max(0f, heavyWorkResumeTime - Time.realtimeSinceStartup);
            }
        }

        internal static bool IsTerrainUpdateActive
        {
            get
            {
                if (terrainUpdateEventActive)
                    return true;

                if (terrainUpdateRunningField == null)
                    ResolveTerrainUpdateRunningField();

                if (terrainUpdateRunningField == null)
                    return false;

                try
                {
                    GameManager gameManager = GameManager.Instance;
                    if (gameManager == null || gameManager.StreamingWorld == null)
                        return false;

                    object value = terrainUpdateRunningField.GetValue(gameManager.StreamingWorld);
                    return value is bool && (bool)value;
                }
                catch (System.Exception ex)
                {
                    if (!terrainUpdateReflectionWarningLogged)
                    {
                        Debug.LogWarning("[DeepWaters.Runtime] Could not read StreamingWorld.terrainUpdateRunning; blocking terrain holes until the next stable event. " + ex.Message);
                        terrainUpdateReflectionWarningLogged = true;
                    }
                    return true;
                }
            }
        }

        internal static bool CanMutateTerrainData
        {
            get
            {
                return !IsLoadGraceActive && !IsTerrainUpdateActive;
            }
        }

        internal static void Install()
        {
            if (installed)
                return;

            ResolveTerrainUpdateRunningField();
			terrainArrayField = typeof(StreamingWorld).GetField("terrainArray", PrivateInstance);
			updateLocationsField = typeof(StreamingWorld).GetField("updateLocations", PrivateInstance);
			if (terrainArrayField == null || updateLocationsField == null)
				Debug.LogWarning("[DeepWaters.Runtime] Could not reflect StreamingWorld location fields; peripheral location skip disabled.");

            StreamingWorld.OnCreateLocationGameObject += OnCreateLocationGameObject;
            StreamingWorld.OnUpdateLocationGameObject += OnUpdateLocationGameObject;
            StreamingWorld.OnUpdateTerrainsStart += OnUpdateTerrainsStart;
            StreamingWorld.OnUpdateTerrainsEnd += OnUpdateTerrainsEnd;
            SaveLoadManager.OnStartLoad += OnStartLoad;
            SaveLoadManager.OnLoad += OnLoad;
            StreamingWorld.OnTeleportToCoordinates += OnTeleportToCoordinates;
            installed = true;
        }

		private static bool IsAnyLocationLoading
		{
			get
			{
				if (activeLocationLoads <= 0)
					return false;

				if (Time.realtimeSinceStartup - lastLocationLoadIncrementTime > LocationLoadStuckResetSeconds)
				{
					Debug.LogWarning("[DeepWaters.Runtime] activeLocationLoads=" + activeLocationLoads +
						" stuck for >" + LocationLoadStuckResetSeconds +
						"s; resetting (Create/Update events out of sync).");
					activeLocationLoads = 0;
					return false;
				}

				return true;
			}
		}

		private static void OnCreateLocationGameObject(DaggerfallLocation dfLocation)
		{
			activeLocationLoads++;
			lastLocationLoadIncrementTime = Time.realtimeSinceStartup;
		}

		private static void OnUpdateLocationGameObject(GameObject locationObject, bool allowYield)
		{
			if (activeLocationLoads > 0)
				activeLocationLoads--;
		}

        private static void OnUpdateTerrainsStart()
        {
            terrainUpdateEventActive = true;
        }

        private static void OnUpdateTerrainsEnd()
        {
            terrainUpdateEventActive = false;
            DeepWaterTerrainLookup.Clear();
			SkipPeripheralLocationUpdates();
        }

        private static void ResolveTerrainUpdateRunningField()
        {
            if (terrainUpdateRunningField != null)
                return;

            terrainUpdateRunningField = typeof(StreamingWorld).GetField("terrainUpdateRunning", PrivateInstance);
            if (terrainUpdateRunningField == null && !terrainUpdateReflectionWarningLogged)
            {
                Debug.LogWarning("[DeepWaters.Runtime] StreamingWorld.terrainUpdateRunning reflection failed; using terrain start/end events only.");
                terrainUpdateReflectionWarningLogged = true;
            }
        }

        private static void OnStartLoad(SaveData_v1 saveData)
        {
            // Suspend heavy work until OnLoad re-arms the grace timer.
			ResetTransitionState(float.PositiveInfinity, false);
        }

        private static void OnLoad(SaveData_v1 saveData)
        {
            heavyWorkResumeTime = Time.realtimeSinceStartup + PostLoadHeavyWorkGraceSeconds;
            postTransitionRefreshPending = true;
        }

        private static void OnTeleportToCoordinates(DFPosition worldPos)
        {
			ResetTransitionState(Time.realtimeSinceStartup + PostLoadHeavyWorkGraceSeconds, true);
		}

		private static void ResetTransitionState(float heavyWorkResumeAt, bool refreshPending)
		{
			activeLocationLoads = 0;
			deferredLocationKeys.Clear();
			LastLocationSkippedCount = 0;
			heavyWorkResumeTime = heavyWorkResumeAt;
			postTransitionRefreshPending = refreshPending;
			OnTransientReset?.Invoke();
        }

        internal static void Pump()
		{
			PumpPostTransitionRefresh();
			PumpDeferredLocationRestore();
		}

		private static void PumpPostTransitionRefresh()
        {
            if (!postTransitionRefreshPending)
                return;

            if (!CanMutateTerrainData)
                return;

            postTransitionRefreshPending = false;
            UnderwaterDecorations.RefreshPlayerArea();
        }

		private static void PumpDeferredLocationRestore()
		{
			if (terrainArrayField == null || updateLocationsField == null ||
				deferredLocationKeys.Count == 0 ||
				Time.time < nextDeferredLocationRestoreCheckTime)
			{
				return;
			}

			nextDeferredLocationRestoreCheckTime = Time.time + DeferredLocationRestoreCheckInterval;
			GameManager gameManager = GameManager.Instance;
			if (gameManager == null || gameManager.StreamingWorld == null || gameManager.PlayerGPS == null)
				return;

			if (HasOceanConnectedActiveTerrain(gameManager.StreamingWorld))
				return;

			if (DeepWaterWorld.IsPlayerInOrAboveDeepWater(MinimumLocationSkipDepth) ||
				IsCurrentPixelOceanConnected(gameManager.StreamingWorld, gameManager.PlayerGPS.CurrentMapPixel))
			{
				return;
			}

			RestoreDeferredLocations(gameManager.StreamingWorld);
		}

		private static void SkipPeripheralLocationUpdates()
		{
			LastLocationSkippedCount = 0;

			GameManager gameManager = GameManager.Instance;
			if (terrainArrayField == null || updateLocationsField == null ||
				gameManager == null ||
				gameManager.StreamingWorld == null ||
				gameManager.PlayerGPS == null)
			{
				return;
			}

			StreamingWorld.TerrainDesc[] terrains = terrainArrayField.GetValue(gameManager.StreamingWorld) as StreamingWorld.TerrainDesc[];
			if (terrains == null)
				return;

			DFPosition current = gameManager.PlayerGPS.CurrentMapPixel;
			bool oceanNearby = HasOceanConnectedActiveTerrain(terrains);
			bool overDeepWater =
				DeepWaterWorld.IsPlayerInOrAboveDeepWater(MinimumLocationSkipDepth) ||
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
				LastLocationSkippedCount++;
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

			DFPosition shipCoords = DaggerfallBankManager.GetShipCoords();
			return shipCoords != null && shipCoords.X == mapPixelX && shipCoords.Y == mapPixelY;
		}

		private static bool IsCurrentPixelOceanConnected(StreamingWorld streamingWorld, DFPosition current)
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
