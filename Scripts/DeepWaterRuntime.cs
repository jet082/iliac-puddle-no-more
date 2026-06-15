// Project:         Iliac Puddle No More
// License:         MIT

using System.Reflection;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
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
        public delegate void TransientResetHandler();
        public static event TransientResetHandler OnTransientReset;

        // Heavy-work grace period after save load / teleport. Spawners
        // (decorations, loot, encounters, fish) check
        // CanRunHeavyRuntimeWork before doing any work, so they pause
        // for this duration after a world transition while the streaming
        // ring re-promotes. Prevents a flurry of GameObject
        // instantiation while DFU is still rebuilding terrain.
        private const float PostLoadHeavyWorkGraceSeconds = 1.5f;
        private static float heavyWorkResumeTime;
        private static bool postTransitionRefreshPending;

        private static bool installed;
        private static bool terrainUpdateEventActive;
        private static bool terrainUpdateReflectionWarningLogged;
        private static FieldInfo terrainUpdateRunningField;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        public static bool DiagnosticProfiling;

        // === API consumed by newer subsystems (StreamingBuffer, spawners,
        // OutdoorSwimDriver, Settings). The working-backup version of
        // DeepWaterRuntime didn't have these; they were added with the
        // bake architecture and the various spawner systems. They're
        // pure read-only gates — they don't change the working-backup
        // lifecycle behavior, just expose state for other subsystems to
        // poll.

        public static bool CanRunLightRuntimeWork
        {
            get
            {
                GameManager gameManager = GameManager.Instance;
                return gameManager != null &&
                       gameManager.IsPlayingGame() &&
                       (SaveLoadManager.Instance == null || !SaveLoadManager.Instance.LoadInProgress);
            }
        }

        public static bool CanRunHeavyRuntimeWork
        {
            get
            {
                return CanRunLightRuntimeWork &&
                       Time.realtimeSinceStartup >= heavyWorkResumeTime;
            }
        }

        public static bool IsLoadGraceActive
        {
            get { return !CanRunHeavyRuntimeWork || DeepWaterLocationLoadGate.IsAnyLocationLoading; }
        }

        public static bool IsPostTransitionRefreshPending
        {
            get { return postTransitionRefreshPending; }
        }

        public static float HeavyWorkResumeInSeconds
        {
            get
            {
                if (float.IsPositiveInfinity(heavyWorkResumeTime))
                    return -1f;

                return Mathf.Max(0f, heavyWorkResumeTime - Time.realtimeSinceStartup);
            }
        }

        public static bool IsTerrainUpdateActive
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

        public static bool CanMutateTerrainData
        {
            get
            {
                return !IsLoadGraceActive && !IsTerrainUpdateActive;
            }
        }

        public static void Install()
        {
            if (installed)
                return;

            ResolveTerrainUpdateRunningField();
            StreamingWorld.OnUpdateTerrainsStart += OnUpdateTerrainsStart;
            StreamingWorld.OnUpdateTerrainsEnd += OnUpdateTerrainsEnd;
            SaveLoadManager.OnStartLoad += OnStartLoad;
            SaveLoadManager.OnLoad += OnLoad;
            StreamingWorld.OnTeleportToCoordinates += OnTeleportToCoordinates;
            installed = true;
        }

        private static void OnUpdateTerrainsStart()
        {
            terrainUpdateEventActive = true;
        }

        private static void OnUpdateTerrainsEnd()
        {
            terrainUpdateEventActive = false;
            DeepWaterTerrainLookup.Clear();
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
            heavyWorkResumeTime = float.PositiveInfinity;
            postTransitionRefreshPending = false;
            ResetTransientState();
        }

        private static void OnLoad(SaveData_v1 saveData)
        {
            heavyWorkResumeTime = Time.realtimeSinceStartup + PostLoadHeavyWorkGraceSeconds;
            postTransitionRefreshPending = true;
        }

        private static void OnTeleportToCoordinates(DFPosition worldPos)
        {
            heavyWorkResumeTime = Time.realtimeSinceStartup + PostLoadHeavyWorkGraceSeconds;
            postTransitionRefreshPending = true;
            ResetTransientState();
        }

        public static void ResetTransientState()
        {
            if (OnTransientReset != null)
                OnTransientReset();
        }

        public static System.Diagnostics.Stopwatch StartProfile()
        {
            return DiagnosticProfiling ? System.Diagnostics.Stopwatch.StartNew() : null;
        }

        public static void LogProfile(System.Diagnostics.Stopwatch stopwatch, string label, DaggerfallTerrain dfTerrain)
        {
            if (stopwatch == null)
                return;

            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds < 10)
                return;

            int mx = dfTerrain != null ? dfTerrain.MapPixelX : -1;
            int my = dfTerrain != null ? dfTerrain.MapPixelY : -1;
            Debug.Log("[DeepWaters.Profile] " + label + " tile=(" + mx + "," + my + ") " +
                      stopwatch.ElapsedMilliseconds + "ms");
        }

        public static void PumpPostTransitionRefresh()
        {
            if (!postTransitionRefreshPending)
                return;

            if (!CanMutateTerrainData)
                return;

            postTransitionRefreshPending = false;
            UnderwaterDecorations.RefreshPlayerArea();
            Debug.Log("[DeepWaters.Runtime] Post-transition decoration refresh queued.");
        }
    }
}
