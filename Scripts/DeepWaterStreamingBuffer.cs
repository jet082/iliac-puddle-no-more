// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections;
using System.Reflection;
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
    /// </summary>
    public sealed class DeepWaterStreamingBuffer : MonoBehaviour
    {
        private const int ExtraTerrainRings = 1;
        private const int MaxBufferedTerrainDistance = 7;
        private const float ExitLingerSeconds = 3f;
        private const float SurfaceActivationMargin = 2f;
        private const float MinimumBufferedDepth = 1f;
        // v0.55.29: forced refresh DISABLED. Forcing DFU's private UpdateWorld
        // out-of-band stack-overflows when the Interesting Terrains mod is doing
        // its heavy terrain generation (caught, but the forced stream silently
        // fails — that was the underwater "void"). The buffer still widens the
        // loaded ring while submerged; a non-crashing way to actually warm it
        // underwater is still TBD (DFU doesn't auto-stream below water).
        public static bool ForceImmediateStreamingRefresh = false;
        public static bool SwimWorldPositionDiagnostics = false;

        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static MethodInfo updateWorldMethod;
        private static MethodInfo updateTerrainsMethod;
        private static FieldInfo terrainUpdateRunningField;
        private static FieldInfo updateLocationsField;
        private static FieldInfo initField;
        private static bool reflectionResolved;
        private static bool reflectionWarningLogged;
        private static bool refreshWarningLogged;
        private static bool interestingTerrainCacheResolved;
        private static bool interestingTerrainCacheWarningLogged;
        private static object interestingTerrainCacheInstance;
        private static MethodInfo interestingTerrainCacheClearMethod;
        private static FieldInfo interestingTerrainCacheDictionaryField;

        private StreamingWorld activeWorld;
        private int baselineTerrainDistance = -1;
        private int appliedTerrainDistance = -1;
        private bool pendingRefresh;
        private float keepBufferUntil;
        // Last map pixel we forced a stream for. While submerged we re-warm the
        // ring each time the player crosses into a new pixel (DFU's own
        // streaming only fires above water). int.MinValue = none yet.
        private int lastStreamedPixelX = int.MinValue;
        private int lastStreamedPixelY = int.MinValue;
        private float lastNudgeTime;
        private const float NudgeIntervalSeconds = 1.5f;
        private Vector3 lastTrackPos;
        private bool swimTrackInitialized;
        private const float RecenterJumpThreshold = 300f;
        private const float RecenterJumpThresholdSq = RecenterJumpThreshold * RecenterJumpThreshold;

        private void OnEnable()
        {
            ResolveStreamingWorldReflection();
            SaveLoadManager.OnStartLoad += OnStartLoad;
            StreamingWorld.OnTeleportToCoordinates += OnTeleportToCoordinates;
            StreamingWorld.OnUpdateTerrainsEnd += OnUpdateTerrainsEnd;
        }

        private void OnDisable()
        {
            SaveLoadManager.OnStartLoad -= OnStartLoad;
            StreamingWorld.OnTeleportToCoordinates -= OnTeleportToCoordinates;
            StreamingWorld.OnUpdateTerrainsEnd -= OnUpdateTerrainsEnd;
            RestoreOriginalDistance(false);
        }

        private void LateUpdate()
        {
            // Whole-body try/catch. v0.49.1's crash log showed NRE
            // floods from DeepWaterTerrainLookup.TryGetByWorldPosition
            // propagating up through ShouldBufferStreaming →
            // LateUpdate, fired every frame from every LateUpdate
            // consumer. The lookup has its own defensive catches now,
            // but a belt-and-suspenders catch here ensures a transient
            // lookup throw can't poison the buffer state or knock
            // Unity into a "MonoBehaviour disabled due to exception"
            // loop.
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
                    // v0.55.33: manually advance the player's world coordinate
                    // from the swim movement so the map pixel tracks the player
                    // and the terrain streamer follows underwater.
                    OverrideSwimWorldPosition();
                }
                else
                {
                    swimTrackInitialized = false;
                    RestoreOriginalDistance(true);
                }

                TryRunPendingRefresh();
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

        private static bool loggedLateUpdateException;

        // v0.55.33: manually advance the player's world coordinate from swim
        // movement. The diagnostic proved the player moves (gpsPos changes) but
        // DFU's WorldX/WorldZ stay frozen at the dive spot underwater — stock DFU
        // would advance them, so Interesting Terrains' streaming overhaul must
        // not follow the custom swim's MoveWithMovingPlatform. Replicate DFU's
        // own advance (delta * SceneMapRatio) so the map pixel tracks the player
        // and the terrain streamer follows. Large single-frame jumps
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

            // Skip floating-origin recenter / teleport jumps (not real swimming).
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
            pendingRefresh = false;
            lastStreamedPixelX = int.MinValue;
            lastStreamedPixelY = int.MinValue;
            swimTrackInitialized = false;
            RestoreOriginalDistance(false);
        }

        private void OnTeleportToCoordinates(DFPosition worldPos)
        {
            keepBufferUntil = 0f;
            pendingRefresh = false;
            lastStreamedPixelX = int.MinValue;
            lastStreamedPixelY = int.MinValue;
            swimTrackInitialized = false;
            RestoreOriginalDistance(false);
        }

        private void OnUpdateTerrainsEnd()
        {
            DeepWaterTerrainLookup.Clear();
            // Do NOT re-refresh the whole player area here. Each NEW tile a
            // stream promotes already carves + builds its seafloor in the
            // promote event, so re-promoting the entire loaded ring on every
            // terrain update is redundant — and with the buffer forcing frequent
            // streams it re-carved ~75 tiles per update, flooding the carve drain
            // (10k+ carves) and hanging the game (v0.55.27). The promote event
            // handles new tiles; seam/decoration refresh is a separate, throttled
            // concern handled elsewhere.
            TryRunPendingRefresh();
        }

        private void ApplyBuffer(StreamingWorld streamingWorld)
        {
            if (streamingWorld == null)
                return;

            if (activeWorld != null && activeWorld != streamingWorld)
                RestoreOriginalDistance(false);

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
            pendingRefresh = ForceImmediateStreamingRefresh;
            DeepWaterTerrainLookup.Clear();
            Debug.Log("[DeepWaters.StreamingBuffer] TerrainDistance buffered from " +
                      baselineTerrainDistance + " to " + desiredDistance +
                      (ForceImmediateStreamingRefresh ? "." : " (deferred until natural DFU stream)."));
        }

        private int DesiredBufferedDistance(int baselineDistance)
        {
            if (baselineDistance >= MaxBufferedTerrainDistance)
                return baselineDistance;

            return Mathf.Min(MaxBufferedTerrainDistance, baselineDistance + ExtraTerrainRings);
        }

        private void RestoreOriginalDistance(bool refreshWorld)
        {
            if (activeWorld == null)
                return;

            StreamingWorld streamingWorld = activeWorld;
            int restoreDistance = baselineTerrainDistance > 0 ? baselineTerrainDistance : streamingWorld.TerrainDistance;
            bool changed = streamingWorld != null &&
                           appliedTerrainDistance > 0 &&
                           streamingWorld.TerrainDistance == appliedTerrainDistance &&
                           streamingWorld.TerrainDistance != restoreDistance;

            if (changed)
            {
                streamingWorld.TerrainDistance = restoreDistance;
                Debug.Log("[DeepWaters.StreamingBuffer] TerrainDistance restored to " + restoreDistance + ".");
            }

            activeWorld = null;
            baselineTerrainDistance = -1;
            appliedTerrainDistance = -1;
            pendingRefresh = changed && refreshWorld && ForceImmediateStreamingRefresh;
            if (pendingRefresh)
            {
                activeWorld = streamingWorld;
                TryRunPendingRefresh();
                if (pendingRefresh)
                    pendingRefresh = false;

                activeWorld = null;
            }
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

        private void TryRunPendingRefresh()
        {
            if (!ForceImmediateStreamingRefresh)
            {
                pendingRefresh = false;
                return;
            }

            if (!pendingRefresh || activeWorld == null || !DeepWaterRuntime.CanRunLightRuntimeWork)
                return;

            if (DeepWaterLocationLoadGate.IsAnyLocationLoading)
                return;

            if (IsTerrainUpdateRunning(activeWorld))
                return;

            if (!CanInvokeStreamingRefresh())
            {
                pendingRefresh = false;
                return;
            }

            try
            {
                ClearInterestingTerrainTileDataCache();
                updateWorldMethod.Invoke(activeWorld, null);
                SetUpdateLocations(activeWorld, true);
                IEnumerator routine = updateTerrainsMethod.Invoke(activeWorld, null) as IEnumerator;
                if (routine != null)
                    StartCoroutine(routine);

                pendingRefresh = false;
            }
            catch (System.Exception ex)
            {
                if (!refreshWarningLogged)
                {
                    refreshWarningLogged = true;
                    Debug.LogWarning("[DeepWaters.StreamingBuffer] Could not warm buffered terrain ring. " + ex.Message);
                }
                pendingRefresh = false;
            }
        }

        private static bool IsTerrainUpdateRunning(StreamingWorld streamingWorld)
        {
            if (streamingWorld == null || terrainUpdateRunningField == null)
                return true;

            try
            {
                return (bool)terrainUpdateRunningField.GetValue(streamingWorld);
            }
            catch
            {
                return true;
            }
        }

        private static void SetUpdateLocations(StreamingWorld streamingWorld, bool value)
        {
            if (streamingWorld == null || updateLocationsField == null)
                return;

            try
            {
                updateLocationsField.SetValue(streamingWorld, value);
            }
            catch
            {
                // Non-critical: ocean terrain and seafloor generation do not rely on location objects.
            }
        }

        private static bool CanInvokeStreamingRefresh()
        {
            ResolveStreamingWorldReflection();
            bool canInvoke = updateWorldMethod != null &&
                             updateTerrainsMethod != null &&
                             terrainUpdateRunningField != null;

            if (!canInvoke && !reflectionWarningLogged)
            {
                reflectionWarningLogged = true;
                Debug.LogWarning("[DeepWaters.StreamingBuffer] DFU streaming internals not found; buffered radius will apply on the next natural terrain transition.");
            }

            return canInvoke;
        }

        private static void ResolveStreamingWorldReflection()
        {
            if (reflectionResolved)
                return;

            updateWorldMethod = typeof(StreamingWorld).GetMethod("UpdateWorld", PrivateInstance);
            updateTerrainsMethod = typeof(StreamingWorld).GetMethod("UpdateTerrains", PrivateInstance);
            terrainUpdateRunningField = typeof(StreamingWorld).GetField("terrainUpdateRunning", PrivateInstance);
            updateLocationsField = typeof(StreamingWorld).GetField("updateLocations", PrivateInstance);
            initField = typeof(StreamingWorld).GetField("init", PrivateInstance);
            reflectionResolved = true;
        }

        private static void ClearInterestingTerrainTileDataCache()
        {
            ResolveInterestingTerrainCache();
            if (interestingTerrainCacheInstance == null)
                return;

            try
            {
                if (interestingTerrainCacheClearMethod != null)
                {
                    interestingTerrainCacheClearMethod.Invoke(interestingTerrainCacheInstance, null);
                    return;
                }

                if (interestingTerrainCacheDictionaryField != null)
                {
                    IDictionary cache = interestingTerrainCacheDictionaryField.GetValue(interestingTerrainCacheInstance) as IDictionary;
                    if (cache != null)
                        cache.Clear();
                }
            }
            catch (System.Exception ex)
            {
                if (!interestingTerrainCacheWarningLogged)
                {
                    interestingTerrainCacheWarningLogged = true;
                    Debug.LogWarning("[DeepWaters.StreamingBuffer] Could not clear Interesting Terrain tile-data cache before forced refresh. " + ex.Message);
                }
            }
        }

        private static void ResolveInterestingTerrainCache()
        {
            if (interestingTerrainCacheResolved)
                return;

            interestingTerrainCacheResolved = true;

            System.Reflection.Assembly[] assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                System.Type owner = assemblies[i].GetType("Monobelisk.InterestingTerrains", false);
                if (owner == null)
                    continue;

                FieldInfo cacheField = owner.GetField("tileDataCache", BindingFlags.Public | BindingFlags.Static);
                if (cacheField == null)
                    return;

                interestingTerrainCacheInstance = cacheField.GetValue(null);
                if (interestingTerrainCacheInstance == null)
                    return;

                System.Type cacheType = interestingTerrainCacheInstance.GetType();
                interestingTerrainCacheClearMethod = cacheType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance);
                interestingTerrainCacheDictionaryField = cacheType.GetField("tileDataCache", BindingFlags.NonPublic | BindingFlags.Instance);
                return;
            }
        }
    }
}
