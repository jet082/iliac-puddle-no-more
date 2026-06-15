// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Keeps one extra DFU terrain ring loaded while the player is using the
    /// outdoor ocean as playable space. DFU normally streams just enough land
    /// for walking speed; underwater sprint/stroke movement can expose the edge
    /// of that ring before deep-water floor/decor refreshes have time to settle.
    ///
    /// The ring is only WIDENED (TerrainDistance bumped); the actual streaming
    /// still happens on DFU's own schedule. Forcing DFU's private UpdateWorld
    /// out-of-band stack-overflowed under Interesting Terrains' heavy terrain
    /// generation, so that path was removed.
    ///
    /// It also advances the player's DFU world coordinate from swim movement:
    /// WorldX/WorldZ stay frozen at the dive spot underwater (Interesting
    /// Terrains' streaming overhaul doesn't follow the swim controller's
    /// MoveWithMovingPlatform), so without the override the map pixel never
    /// changes and the terrain streamer never follows the swimmer.
    /// </summary>
    public sealed class DeepWaterStreamingBuffer : MonoBehaviour
    {
        private const int ExtraTerrainRings = 1;
        private const int MaxBufferedTerrainDistance = 7;
        private const float ExitLingerSeconds = 3f;
        private const float SurfaceActivationMargin = 2f;
        private const float MinimumBufferedDepth = 1f;
        public static bool SwimWorldPositionDiagnostics = false;

        private StreamingWorld activeWorld;
        private int baselineTerrainDistance = -1;
        private int appliedTerrainDistance = -1;
        private float keepBufferUntil;
        private float lastNudgeTime;
        private Vector3 lastTrackPos;
        private bool swimTrackInitialized;
        private const float RecenterJumpThreshold = 300f;
        private const float RecenterJumpThresholdSq = RecenterJumpThreshold * RecenterJumpThreshold;

        private static bool loggedLateUpdateException;

        private void OnEnable()
        {
            SaveLoadManager.OnStartLoad += OnStartLoad;
            StreamingWorld.OnTeleportToCoordinates += OnTeleportToCoordinates;
            StreamingWorld.OnUpdateTerrainsEnd += OnUpdateTerrainsEnd;
        }

        private void OnDisable()
        {
            SaveLoadManager.OnStartLoad -= OnStartLoad;
            StreamingWorld.OnTeleportToCoordinates -= OnTeleportToCoordinates;
            StreamingWorld.OnUpdateTerrainsEnd -= OnUpdateTerrainsEnd;
            RestoreOriginalDistance();
        }

        private void LateUpdate()
        {
            // Whole-body try/catch: a transient lookup throw must not poison
            // the buffer state or knock Unity into a "MonoBehaviour disabled
            // due to exception" loop.
            try
            {
                if (!DeepWaterRuntime.CanRunLightRuntimeWork)
                    return;

                StreamingWorld streamingWorld = GameManager.Instance != null ? GameManager.Instance.StreamingWorld : null;
                bool shouldBuffer = ShouldBufferStreaming(streamingWorld);
                if (shouldBuffer)
                    keepBufferUntil = Time.realtimeSinceStartup + ExitLingerSeconds;

                if (streamingWorld != null && (shouldBuffer || Time.realtimeSinceStartup < keepBufferUntil))
                {
                    ApplyBuffer(streamingWorld);
                    OverrideSwimWorldPosition();
                }
                else
                {
                    swimTrackInitialized = false;
                    RestoreOriginalDistance();
                }
            }
            catch (System.Exception ex)
            {
                if (!loggedLateUpdateException)
                {
                    Debug.LogWarning("[DeepWaters.StreamingBuffer] LateUpdate threw — continuing without buffer adjust. " + ex.Message);
                    loggedLateUpdateException = true;
                }
            }
        }

        // Replicate DFU's own world-coordinate advance (delta * SceneMapRatio)
        // so the map pixel tracks the swimming player. Large single-frame jumps
        // (floating-origin recenter / teleport) are skipped so they don't
        // corrupt the coordinate; if DFU's own absolute recenter ever fires it
        // re-grounds WorldX, keeping us consistent.
        private void OverrideSwimWorldPosition()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerGPS == null)
                return;

            Vector3 gpsPos = gameManager.PlayerGPS.transform.position;

            if (!swimTrackInitialized)
            {
                lastTrackPos = gpsPos;
                swimTrackInitialized = true;
                return;
            }

            Vector3 delta = gpsPos - lastTrackPos;
            lastTrackPos = gpsPos;

            if (delta.sqrMagnitude > RecenterJumpThresholdSq)
                return;

            float ratio = StreamingWorld.SceneMapRatio;
            gameManager.PlayerGPS.WorldX += (int)(delta.x * ratio);
            gameManager.PlayerGPS.WorldZ += (int)(delta.z * ratio);

            if (SwimWorldPositionDiagnostics &&
                Time.realtimeSinceStartup - lastNudgeTime >= 1f)
            {
                lastNudgeTime = Time.realtimeSinceStartup;
                DFPosition pixel = gameManager.PlayerGPS.CurrentMapPixel;
                Debug.Log("[DeepWaters.SwimDiag] override WorldX=" + gameManager.PlayerGPS.WorldX +
                          " WorldZ=" + gameManager.PlayerGPS.WorldZ + " pixel=(" + pixel.X + "," + pixel.Y +
                          ") gpsPos=(" + gpsPos.x.ToString("F1") + "," + gpsPos.z.ToString("F1") + ")");
            }
        }

        private void OnStartLoad(SaveData_v1 saveData)
        {
            keepBufferUntil = 0f;
            swimTrackInitialized = false;
            RestoreOriginalDistance();
        }

        private void OnTeleportToCoordinates(DFPosition worldPos)
        {
            keepBufferUntil = 0f;
            swimTrackInitialized = false;
            RestoreOriginalDistance();
        }

        private void OnUpdateTerrainsEnd()
        {
            // Streaming may have recycled tiles; drop cached terrain lookups.
            DeepWaterTerrainLookup.Clear();
        }

        private void ApplyBuffer(StreamingWorld streamingWorld)
        {
            if (streamingWorld == null)
                return;

            if (activeWorld != null && activeWorld != streamingWorld)
                RestoreOriginalDistance();

            if (activeWorld == null)
            {
                activeWorld = streamingWorld;
                baselineTerrainDistance = Mathf.Max(1, streamingWorld.TerrainDistance);
            }
            else if (streamingWorld.TerrainDistance != appliedTerrainDistance &&
                     streamingWorld.TerrainDistance != baselineTerrainDistance)
            {
                // Respect runtime changes made by DFU/settings while the buffer is active.
                baselineTerrainDistance = Mathf.Max(1, streamingWorld.TerrainDistance);
            }

            int desiredDistance = DesiredBufferedDistance(baselineTerrainDistance);
            if (streamingWorld.TerrainDistance == desiredDistance &&
                appliedTerrainDistance == desiredDistance)
            {
                return;
            }

            streamingWorld.TerrainDistance = desiredDistance;
            appliedTerrainDistance = desiredDistance;
            DeepWaterTerrainLookup.Clear();
            Debug.Log("[DeepWaters.StreamingBuffer] TerrainDistance buffered from " +
                      baselineTerrainDistance + " to " + desiredDistance +
                      " (applies on the next natural DFU stream).");
        }

        private int DesiredBufferedDistance(int baselineDistance)
        {
            if (baselineDistance >= MaxBufferedTerrainDistance)
                return baselineDistance;

            return Mathf.Min(MaxBufferedTerrainDistance, baselineDistance + ExtraTerrainRings);
        }

        private void RestoreOriginalDistance()
        {
            if (activeWorld == null)
                return;

            StreamingWorld streamingWorld = activeWorld;
            int restoreDistance = baselineTerrainDistance > 0 ? baselineTerrainDistance : streamingWorld.TerrainDistance;
            if (appliedTerrainDistance > 0 &&
                streamingWorld.TerrainDistance == appliedTerrainDistance &&
                streamingWorld.TerrainDistance != restoreDistance)
            {
                streamingWorld.TerrainDistance = restoreDistance;
                Debug.Log("[DeepWaters.StreamingBuffer] TerrainDistance restored to " + restoreDistance + ".");
            }

            activeWorld = null;
            baselineTerrainDistance = -1;
            appliedTerrainDistance = -1;
        }

        private bool ShouldBufferStreaming(StreamingWorld streamingWorld)
        {
            if (DeepWaterRuntime.IsLoadGraceActive)
                return false;

            if (streamingWorld == null || streamingWorld.LocalPlayerGPS == null)
                return false;

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame() || gameManager.PlayerObject == null)
                return false;

            PlayerEnterExit playerEnterExit = gameManager.PlayerEnterExit;
            if (playerEnterExit == null || playerEnterExit.IsPlayerInside)
                return false;

            float oceanSurfaceY;
            if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY))
                return false;

            if (gameManager.PlayerObject.transform.position.y > oceanSurfaceY + SurfaceActivationMargin)
                return false;

            DeepWaterColumn column;
            if (!OutdoorSwimDriver.TryGetPlayerWaterColumn(out column))
                return false;

            return column.Depth >= MinimumBufferedDepth;
        }
    }

    internal sealed class DeepWaterSwimWorldTracker : MonoBehaviour
    {
        private const float SurfaceActivationMargin = 2f;
        private const float RecenterJumpThreshold = 300f;
        private const float RecenterJumpThresholdSq = RecenterJumpThreshold * RecenterJumpThreshold;

        private Vector3 lastTrackPos;
        private int lastWorldX;
        private int lastWorldZ;
        private double pendingWorldX;
        private double pendingWorldZ;
        private bool tracking;

        private void OnEnable()
        {
            SaveLoadManager.OnStartLoad += OnStartLoad;
            StreamingWorld.OnTeleportToCoordinates += OnTeleportToCoordinates;
        }

        private void OnDisable()
        {
            SaveLoadManager.OnStartLoad -= OnStartLoad;
            StreamingWorld.OnTeleportToCoordinates -= OnTeleportToCoordinates;
            ResetTracking();
        }

        private void LateUpdate()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame() ||
                gameManager.PlayerGPS == null || gameManager.PlayerObject == null)
            {
                ResetTracking();
                return;
            }

            if (!ShouldTrack(gameManager))
            {
                ResetTracking();
                return;
            }

            Vector3 position = gameManager.PlayerGPS.transform.position;
            if (!tracking)
            {
                BeginTracking(gameManager, position);
                return;
            }

            Vector3 delta = position - lastTrackPos;
            if (delta.sqrMagnitude > RecenterJumpThresholdSq)
            {
                BeginTracking(gameManager, position);
                return;
            }

            float ratio = StreamingWorld.SceneMapRatio;
            pendingWorldX += delta.x * ratio - (gameManager.PlayerGPS.WorldX - lastWorldX);
            pendingWorldZ += delta.z * ratio - (gameManager.PlayerGPS.WorldZ - lastWorldZ);

            int nudgeX = (int)pendingWorldX;
            int nudgeZ = (int)pendingWorldZ;
            if (nudgeX != 0)
            {
                gameManager.PlayerGPS.WorldX += nudgeX;
                pendingWorldX -= nudgeX;
            }

            if (nudgeZ != 0)
            {
                gameManager.PlayerGPS.WorldZ += nudgeZ;
                pendingWorldZ -= nudgeZ;
            }

            lastTrackPos = position;
            lastWorldX = gameManager.PlayerGPS.WorldX;
            lastWorldZ = gameManager.PlayerGPS.WorldZ;
        }

        private static bool ShouldTrack(GameManager gameManager)
        {
            if (gameManager.PlayerEnterExit != null && gameManager.PlayerEnterExit.IsPlayerInside)
                return false;

            if (!DeepWaterWorld.IsPlayerInExteriorWaterContext())
                return false;

            float oceanSurfaceY;
            if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY))
                return false;

            return gameManager.PlayerObject.transform.position.y <= oceanSurfaceY + SurfaceActivationMargin;
        }

        private void BeginTracking(GameManager gameManager, Vector3 position)
        {
            lastTrackPos = position;
            lastWorldX = gameManager.PlayerGPS.WorldX;
            lastWorldZ = gameManager.PlayerGPS.WorldZ;
            pendingWorldX = 0d;
            pendingWorldZ = 0d;
            tracking = true;
        }

        private void ResetTracking()
        {
            tracking = false;
            pendingWorldX = 0d;
            pendingWorldZ = 0d;
        }

        private void OnStartLoad(SaveData_v1 saveData)
        {
            ResetTracking();
        }

        private void OnTeleportToCoordinates(DFPosition worldPos)
        {
            ResetTracking();
        }
    }
}
