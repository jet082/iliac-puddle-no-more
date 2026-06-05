// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    internal static class DeepWaterTerrainLookup
    {
        private const int MaxCachedLookups = 32;
        private static readonly Dictionary<long, CachedTerrainLookup> cache =
            new Dictionary<long, CachedTerrainLookup>();

        private struct CachedTerrainLookup
        {
            public DaggerfallTerrain DfTerrain;
            public Terrain Terrain;

            public CachedTerrainLookup(DaggerfallTerrain dfTerrain, Terrain terrain)
            {
                DfTerrain = dfTerrain;
                Terrain = terrain;
            }
        }

        // Empties the by-pixel lookup cache. Called from
        // DeepWaterStreamingBuffer / UnderwaterDecorations when DFU's
        // streaming end-of-pass signal arrives, so cached terrain refs
        // for tiles that may have been recycled don't outlive the
        // streaming event. Working backup didn't expose this — the
        // current StreamingBuffer needs it to keep its lookups fresh.
        public static void Clear()
        {
            cache.Clear();
        }

        public static bool TryGet(
            StreamingWorld streamingWorld,
            int mapPixelX,
            int mapPixelY,
            out DaggerfallTerrain dfTerrain,
            out Terrain terrain)
        {
            dfTerrain = null;
            terrain = null;

            long key = DeepWaterWorld.TileKey(mapPixelX, mapPixelY);
            CachedTerrainLookup cached;
            if (cache.TryGetValue(key, out cached) &&
                cached.DfTerrain != null &&
                cached.Terrain != null &&
                cached.DfTerrain.MapPixelX == mapPixelX &&
                cached.DfTerrain.MapPixelY == mapPixelY)
            {
                dfTerrain = cached.DfTerrain;
                terrain = cached.Terrain;
                return true;
            }

            GameObject terrainObject = streamingWorld.GetTerrainFromPixel(mapPixelX, mapPixelY);
            if (terrainObject == null)
                return false;

            dfTerrain = terrainObject.GetComponent<DaggerfallTerrain>();
            terrain = terrainObject.GetComponent<Terrain>();
            if (dfTerrain == null || terrain == null)
                return false;

            if (cache.Count >= MaxCachedLookups)
                cache.Clear();

            cache[key] = new CachedTerrainLookup(dfTerrain, terrain);
            return true;
        }

        // Resolves a (worldX, worldZ) to the DaggerfallTerrain whose
        // origin contains that world position. Added on top of the
        // working backup (which only exposed the by-pixel TryGet) because
        // DeepWaterWorld.TryGetWaterColumn and several LateUpdate
        // consumers query by world position when computing the local
        // water column.
        //
        // Per-frame snapshot caching: FindObjectsOfType is expensive
        // (~0.1 ms with allocation of a ~70-entry array per call). With
        // 3+ LateUpdate consumers calling this every frame plus the
        // image effect's OnRenderImage path, that's a thousand+
        // FindObjectsOfType calls per second and tanks the frame rate.
        // Cache by Time.frameCount so we do FindObjectsOfType at most
        // once per rendered frame, then iterate the cached snapshot.
        // Each iteration wraps Unity member access in try/catch so a
        // stale-reference race within the frame is skipped gracefully
        // — the next frame re-snaps fresh, so staleness can never
        // persist past one frame.
        private static DaggerfallTerrain[] cachedFrameSnapshot;
        private static int cachedFrameSnapshotFrame = -1;

        public static bool TryGetByWorldPosition(
            float worldX,
            float worldZ,
            out DaggerfallTerrain dfTerrain,
            out Terrain terrain)
        {
            dfTerrain = null;
            terrain = null;

            if (float.IsNaN(worldX) || float.IsNaN(worldZ) ||
                float.IsInfinity(worldX) || float.IsInfinity(worldZ))
            {
                return false;
            }

            float tileWorldSize = DeepWaterWorld.TileWorldSize;
            if (tileWorldSize <= 0f)
                return false;

            const float edgeEpsilon = 0.25f;

            DaggerfallTerrain[] terrains = GetFrameSnapshot();
            if (terrains == null) return false;

            for (int i = 0; i < terrains.Length; i++)
            {
                DaggerfallTerrain candidate = terrains[i];
                if (candidate == null) continue;

                Terrain candidateTerrain;
                Vector3 origin;
                try
                {
                    candidateTerrain = candidate.GetComponent<Terrain>();
                    if (candidateTerrain == null || candidateTerrain.terrainData == null)
                        continue;
                    Transform t = candidate.transform;
                    if (t == null) continue;
                    origin = t.position;
                }
                catch
                {
                    // Race: candidate destroyed between snapshot capture
                    // and our member access. Skip and try the next one.
                    continue;
                }

                if (worldX < origin.x - edgeEpsilon ||
                    worldX > origin.x + tileWorldSize + edgeEpsilon ||
                    worldZ < origin.z - edgeEpsilon ||
                    worldZ > origin.z + tileWorldSize + edgeEpsilon)
                {
                    continue;
                }

                dfTerrain = candidate;
                terrain = candidateTerrain;
                return true;
            }

            return false;
        }

        private static DaggerfallTerrain[] GetFrameSnapshot()
        {
            int frame = Time.frameCount;
            if (cachedFrameSnapshot != null && cachedFrameSnapshotFrame == frame)
                return cachedFrameSnapshot;

            try
            {
                cachedFrameSnapshot = UnityEngine.Object.FindObjectsOfType<DaggerfallTerrain>();
            }
            catch (System.Exception)
            {
                cachedFrameSnapshot = null;
            }
            cachedFrameSnapshotFrame = frame;
            return cachedFrameSnapshot;
        }
    }
}
