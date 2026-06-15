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

        // Empties the lookup caches. Called when DFU's streaming end-of-pass
        // signal arrives, so cached terrain refs for tiles that may have been
        // recycled don't outlive the streaming event.
        public static void Clear()
        {
            cache.Clear();
            frameSnapshot.Clear();
            frameSnapshotFrame = -1;
            lastHitIndex = -1;
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

        // Resolves a (worldX, worldZ) to the ACTIVE DaggerfallTerrain whose
        // current footprint contains that world position.
        //
        // Deliberately a bounds scan over live, active terrains rather than
        // map-pixel arithmetic through DFU's pixel registry: during a
        // map-pixel crossing, PlayerGPS's pixel, the registry, and the
        // recycled tiles' transforms/heightmaps are briefly mutually
        // inconsistent, and pixel math through that window resolved wrong or
        // mid-recycle tiles (player flung to the surface on crossings; jittery
        // shore exits). Matching by actual current origin — and only among
        // ACTIVE objects, which excludes tiles mid-recycle — stays correct
        // through the whole transition.
        //
        // FindObjectsOfType is expensive (~0.1 ms + array allocation), so the
        // live set is snapshotted at most once per rendered frame, with each
        // entry's Terrain component and origin prebaked behind a try/catch
        // for the destroy race. Each query is then a tight bounds scan over
        // plain structs, with the previous hit checked first (consecutive
        // probes are player-centric and almost always land on the same tile).
        private struct TerrainSnapshotEntry
        {
            public DaggerfallTerrain DfTerrain;
            public Terrain Terrain;
            public float OriginX;
            public float OriginZ;
        }

        private static readonly List<TerrainSnapshotEntry> frameSnapshot = new List<TerrainSnapshotEntry>(80);
        private static int frameSnapshotFrame = -1;
        private static int lastHitIndex = -1;

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

            List<TerrainSnapshotEntry> snapshot = GetFrameSnapshot();

            if (lastHitIndex >= 0 && lastHitIndex < snapshot.Count &&
                SnapshotEntryContains(snapshot[lastHitIndex], worldX, worldZ, tileWorldSize))
            {
                dfTerrain = snapshot[lastHitIndex].DfTerrain;
                terrain = snapshot[lastHitIndex].Terrain;
                return true;
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                if (!SnapshotEntryContains(snapshot[i], worldX, worldZ, tileWorldSize))
                    continue;

                lastHitIndex = i;
                dfTerrain = snapshot[i].DfTerrain;
                terrain = snapshot[i].Terrain;
                return true;
            }

            return false;
        }

        // Fills the parallel lists with this frame's live, active terrains
        // (the same snapshot TryGetByWorldPosition scans). Used by the swim
        // collider gate to enumerate candidate tiles by distance without any
        // per-tile world queries.
        public static void GetLoadedTerrains(List<DaggerfallTerrain> dfTerrains, List<Terrain> terrains)
        {
            dfTerrains.Clear();
            terrains.Clear();

            List<TerrainSnapshotEntry> snapshot = GetFrameSnapshot();
            for (int i = 0; i < snapshot.Count; i++)
            {
                dfTerrains.Add(snapshot[i].DfTerrain);
                terrains.Add(snapshot[i].Terrain);
            }
        }

        private static bool SnapshotEntryContains(TerrainSnapshotEntry entry, float worldX, float worldZ, float tileWorldSize)
        {
            const float edgeEpsilon = 0.25f;
            return worldX >= entry.OriginX - edgeEpsilon &&
                   worldX <= entry.OriginX + tileWorldSize + edgeEpsilon &&
                   worldZ >= entry.OriginZ - edgeEpsilon &&
                   worldZ <= entry.OriginZ + tileWorldSize + edgeEpsilon;
        }

        private static List<TerrainSnapshotEntry> GetFrameSnapshot()
        {
            int frame = Time.frameCount;
            if (frameSnapshotFrame == frame)
                return frameSnapshot;

            frameSnapshot.Clear();
            lastHitIndex = -1;
            frameSnapshotFrame = frame;

            Transform streamingTarget = null;
            try
            {
                GameManager gameManager = GameManager.Instance;
                StreamingWorld streamingWorld = gameManager != null ? gameManager.StreamingWorld : null;
                streamingTarget = streamingWorld != null ? streamingWorld.StreamingTarget : null;
            }
            catch
            {
                streamingTarget = null;
            }

            if (streamingTarget != null)
            {
                for (int i = 0; i < streamingTarget.childCount; i++)
                {
                    Transform child = streamingTarget.GetChild(i);
                    if (child == null || !child.gameObject.activeInHierarchy)
                        continue;

                    TryAddSnapshotEntry(child.GetComponent<DaggerfallTerrain>());
                }

                if (frameSnapshot.Count > 0)
                    return frameSnapshot;
            }

            // Fallback for early init or nonstandard scene layouts. This is
            // slower, but should now be rare.
            try
            {
                DaggerfallTerrain[] terrains = UnityEngine.Object.FindObjectsOfType<DaggerfallTerrain>();
                for (int i = 0; i < terrains.Length; i++)
                    TryAddSnapshotEntry(terrains[i]);
            }
            catch (System.Exception)
            {
                return frameSnapshot;
            }

            return frameSnapshot;
        }

        private static void TryAddSnapshotEntry(DaggerfallTerrain candidate)
        {
            if (candidate == null)
                return;

            try
            {
                Terrain candidateTerrain = candidate.GetComponent<Terrain>();
                if (candidateTerrain == null || candidateTerrain.terrainData == null)
                    return;

                Vector3 origin = candidate.transform.position;
                TerrainSnapshotEntry entry;
                entry.DfTerrain = candidate;
                entry.Terrain = candidateTerrain;
                entry.OriginX = origin.x;
                entry.OriginZ = origin.z;
                frameSnapshot.Add(entry);
            }
            catch
            {
                // Race: candidate destroyed between enumeration and member
                // access. Skip it; next frame re-snaps fresh.
            }
        }
    }
}
