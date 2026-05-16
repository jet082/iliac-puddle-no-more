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
    }
}
