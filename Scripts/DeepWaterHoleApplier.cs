// Project:         Iliac Puddle No More
// License:         MIT

using System;
using System.Collections;
using System.Collections.Generic;
using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Defers all Terrain hole writes out of the busy promotion frame.
    /// Calling the high-level <c>TerrainData.SetHoles</c> while DFU is
    /// streaming terrain native-crashes the player runtime — that path
    /// synchronously invalidates LOD/vegetation/collision, and concurrent
    /// rebuilds across adjacent tiles collide in Unity 2019.4. The fix is
    /// Unity's recommended two-phase flow:
    ///   1. <c>SetHolesDelayLOD</c> writes the mask without triggering the
    ///      LOD invalidation.
    ///   2. <c>SyncTexture(TerrainData.HolesTextureName)</c> pushes just the
    ///      holes texture to the GPU.
    /// Each tile is still paced through the queue, but the texture sync now
    /// follows its mask write immediately. That makes the local bay deepen as
    /// soon as its terrain has promoted instead of waiting for the whole
    /// streaming ring to drain.
    /// </summary>
    public class DeepWaterHoleApplier : MonoBehaviour
    {
        // Delay before applying the first queued hole. Keep this at zero so
        // loaded coastal tiles deepen immediately instead of visibly popping
        // in several seconds after the player enters the water.
        public static float PostStreamCooldownSeconds = 0f;

        // SetHolesDelayLOD avoids the per-call LOD/vegetation rebuilds that
        // made the old SetHoles + SyncHeightmap + Flush flow crash. These
        // frame spacers keep the player from doing every mask write and
        // every texture sync in the same rendered frame when a full
        // TerrainDistance=4 ring contributes dozens of water tiles.
        public static int FramesBetweenApplies = 1;
        public static int FramesBetweenSyncs = 2;

        // Cap total applies per drain session. 0 = unlimited. Set to 1 to
        // replicate the probe scenario exactly (one apply per stream cycle).
        public static int MaxAppliesPerDrain = 0;

        // Diagnostic logging.
        public static bool Verbose = true;

        public static DeepWaterHoleApplier Instance { get; private set; }

        private class PendingApply
        {
            public DaggerfallTerrain DfTerrain;
            public bool[,] Holes;
            public int MapPixelX;
            public int MapPixelY;
        }

        private readonly Queue<PendingApply> queue = new Queue<PendingApply>();
        private readonly Dictionary<DaggerfallTerrain, PendingApply> latestByTerrain =
            new Dictionary<DaggerfallTerrain, PendingApply>();
        private Coroutine drainCoroutine;
        private bool installed;
        private int totalApplied;
        private int totalSynced;

        void Awake()
        {
            Instance = this;
        }

        void OnEnable()
        {
            if (installed) return;
            StreamingWorld.OnUpdateTerrainsEnd += OnStreamEnd;
            installed = true;
            if (Verbose) Debug.Log("[DeepWaters.Applier] Subscribed to OnUpdateTerrainsEnd");
        }

        void OnDisable()
        {
            if (!installed) return;
            StreamingWorld.OnUpdateTerrainsEnd -= OnStreamEnd;
            installed = false;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Enqueue(DaggerfallTerrain dfTerrain, bool[,] holes)
        {
            if (dfTerrain == null || holes == null) return;
            var entry = new PendingApply
            {
                DfTerrain = dfTerrain,
                Holes = holes,
                MapPixelX = dfTerrain.MapPixelX,
                MapPixelY = dfTerrain.MapPixelY,
            };
            latestByTerrain[dfTerrain] = entry;
            queue.Enqueue(entry);
            if (Verbose) Debug.Log("[DeepWaters.Applier] Enqueued tile (" + dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + "); queue size=" + queue.Count);
            BeginDrain("enqueue");
        }

        public void ClearQueue()
        {
            queue.Clear();
            latestByTerrain.Clear();
        }

        public int PendingCount
        {
            get { return queue.Count; }
        }

        private void OnStreamEnd()
        {
            if (Verbose) Debug.Log("[DeepWaters.Applier] OnUpdateTerrainsEnd received; queue size=" + queue.Count + " drainActive=" + (drainCoroutine != null));
            BeginDrain("stream end");
        }

        private void BeginDrain(string reason)
        {
            if (drainCoroutine != null) return;
            if (queue.Count == 0) return;

            if (Verbose) Debug.Log("[DeepWaters.Applier] Starting drain from " + reason + "; queue size=" + queue.Count);
            drainCoroutine = StartCoroutine(DrainQueue());
        }

        private IEnumerator DrainQueue()
        {
            // Never mutate TerrainData holes in the same promotion call stack
            // that produced them. One frame is enough to leave DFU's terrain
            // promotion code while still feeling immediate in play.
            yield return null;

            if (PostStreamCooldownSeconds > 0f)
                yield return new WaitForSeconds(PostStreamCooldownSeconds);

            if (Verbose) Debug.Log("[DeepWaters.Applier] Beginning DelayLOD apply/sync pass");

            int appliesThisDrain = 0;
            while (queue.Count > 0)
            {
                if (MaxAppliesPerDrain > 0 && appliesThisDrain >= MaxAppliesPerDrain)
                {
                    if (Verbose) Debug.Log("[DeepWaters.Applier] Reached MaxAppliesPerDrain=" + MaxAppliesPerDrain + "; stopping (queue=" + queue.Count + ")");
                    break;
                }

                var entry = queue.Dequeue();
                if (Verbose) Debug.Log("[DeepWaters.Applier] >>> DelayLOD holes for tile (" + entry.MapPixelX + "," + entry.MapPixelY + "); remaining=" + queue.Count);
                if (ApplyOne(entry))
                {
                    appliesThisDrain++;
                    totalApplied++;
                    if (Verbose) Debug.Log("[DeepWaters.Applier] <<< DelayLOD holes queued for tile (" + entry.MapPixelX + "," + entry.MapPixelY + "); totalApplied=" + totalApplied);

                    for (int j = 0; j < FramesBetweenSyncs; j++)
                        yield return null;

                    if (SyncOne(entry))
                    {
                        totalSynced++;
                        if (Verbose) Debug.Log("[DeepWaters.Applier] <<< Sync holes done for tile (" + entry.MapPixelX + "," + entry.MapPixelY + "); totalSynced=" + totalSynced);
                    }

                    RemoveLatest(entry);
                }

                for (int i = 0; i < FramesBetweenApplies; i++)
                    yield return null;
            }

            if (Verbose) Debug.Log("[DeepWaters.Applier] Drain complete; totalApplied=" + totalApplied + " totalSynced=" + totalSynced);
            drainCoroutine = null;

            if (queue.Count > 0)
                BeginDrain("remaining queue");
        }

        private bool ApplyOne(PendingApply entry)
        {
            if (!IsEntryCurrent(entry, "apply"))
                return false;

            if (!IsLatestEntry(entry, "apply"))
                return false;

            var unityTerrain = entry.DfTerrain.GetComponent<Terrain>();
            if (unityTerrain == null || unityTerrain.terrainData == null)
            {
                if (Verbose) Debug.Log("[DeepWaters.Applier] Tile missing Terrain/TerrainData");
                return false;
            }

            try
            {
                // Unity's recommended hole flow: write the mask now without
                // triggering the synchronous LOD/vegetation/collision rebuild
                // that the high-level SetHoles does. The actual GPU texture
                // push happens in the separate SyncTexture pass below.
                unityTerrain.terrainData.SetHolesDelayLOD(0, 0, entry.Holes);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DeepWaters.Applier] Deferred SetHolesDelayLOD failed for tile (" +
                                 entry.MapPixelX + "," + entry.MapPixelY + "): " + ex.Message);
                return false;
            }
        }

        private bool SyncOne(PendingApply entry)
        {
            if (!IsEntryCurrent(entry, "sync"))
                return false;

            if (!IsLatestEntry(entry, "sync"))
                return false;

            var unityTerrain = entry.DfTerrain.GetComponent<Terrain>();
            if (unityTerrain == null || unityTerrain.terrainData == null)
            {
                if (Verbose) Debug.Log("[DeepWaters.Applier] Tile missing Terrain/TerrainData before sync");
                return false;
            }

            try
            {
                unityTerrain.terrainData.SyncTexture(TerrainData.HolesTextureName);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DeepWaters.Applier] Holes texture sync failed for tile (" +
                                 entry.MapPixelX + "," + entry.MapPixelY + "): " + ex.Message);
                return false;
            }
        }

        private bool IsEntryCurrent(PendingApply entry, string phase)
        {
            if (entry.DfTerrain == null)
            {
                if (Verbose) Debug.Log("[DeepWaters.Applier] Tile destroyed before " + phase);
                return false;
            }

            if (entry.DfTerrain.MapPixelX != entry.MapPixelX ||
                entry.DfTerrain.MapPixelY != entry.MapPixelY)
            {
                if (Verbose) Debug.Log("[DeepWaters.Applier] Tile recycled before " + phase + " (was " + entry.MapPixelX + "," + entry.MapPixelY +
                                       "; now " + entry.DfTerrain.MapPixelX + "," + entry.DfTerrain.MapPixelY + ")");
                return false;
            }

            return true;
        }

        private bool IsLatestEntry(PendingApply entry, string phase)
        {
            PendingApply latest;
            if (!latestByTerrain.TryGetValue(entry.DfTerrain, out latest) ||
                !object.ReferenceEquals(latest, entry))
            {
                if (Verbose) Debug.Log("[DeepWaters.Applier] Skipping stale " + phase + " for tile (" +
                                       entry.MapPixelX + "," + entry.MapPixelY + ")");
                return false;
            }

            return true;
        }

        private void RemoveLatest(PendingApply entry)
        {
            PendingApply latest;
            if (entry.DfTerrain != null &&
                latestByTerrain.TryGetValue(entry.DfTerrain, out latest) &&
                object.ReferenceEquals(latest, entry))
                latestByTerrain.Remove(entry.DfTerrain);
        }
    }
}
