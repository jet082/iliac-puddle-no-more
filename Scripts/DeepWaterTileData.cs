// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Per-DaggerfallTerrain cache. Holds the climate index, ocean-connectivity
    /// verdict, and convenience access to the pre-baked global distance-to-coast
    /// field. Other systems read this to call <see cref="DeepBathymetry.SampleDepthMeters"/>
    /// without having to know the bake format.
    ///
    /// Before the bake was introduced this class ran a per-tile chamfer BFS at
    /// promotion time and tried to reconcile cross-tile mismatches with seeding
    /// from neighbor edges plus a deferred neighbor-refresh cascade. The bake
    /// makes all of that unnecessary: distance values for the same world position
    /// are identical regardless of which tile is asking, so adjacent meshes
    /// automatically meet at the same depth.
    /// </summary>
    internal class DeepWaterTileData : MonoBehaviour
    {
		private const float LocalWaterFallbackDistanceMeters = DeepBathymetry.ShelfRampMeters * 0.30f;

        internal int ClimateIndex { get; private set; }
		internal int BiomeClimateIndex { get; private set; }
        internal bool IsOceanConnected { get; private set; }
		internal bool UsesLocalWaterFallback { get; private set; }
        internal int MapPixelX { get; private set; }
        internal int MapPixelY { get; private set; }

        private float tileWorldSize;
        private Vector3 cachedOrigin;
        private bool initialized;
		private MapPixelData mapData;

        internal bool HasDistanceField
        {
            get { return initialized && DeepWaterDistanceBake.IsLoaded; }
        }

        internal void Initialize(DaggerfallTerrain dfTerrain, int climateIndex)
        {
            ClimateIndex = climateIndex;
            MapPixelX = dfTerrain.MapPixelX;
            MapPixelY = dfTerrain.MapPixelY;
			BiomeClimateIndex = ResolveBiomeClimateIndex(climateIndex, MapPixelX, MapPixelY);
			mapData = dfTerrain.MapData;
			UsesLocalWaterFallback = false;
            tileWorldSize = MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale;
            cachedOrigin = dfTerrain.transform.position;
            IsOceanConnected = ComputeOceanConnectivity(dfTerrain);
            initialized = true;
        }

        internal float GetDistanceToCoastMeters(float worldX, float worldZ)
        {
            if (!DeepWaterDistanceBake.IsLoaded)
                return float.MaxValue;

            int mapPixelX;
            int mapPixelY;
            float fracX;
            float fracZ;
            GetGlobalMapFractions(worldX, worldZ, out mapPixelX, out mapPixelY, out fracX, out fracZ);
            return DeepWaterDistanceBake.SampleDistanceMeters(
                mapPixelX, mapPixelY, fracX, fracZ);
        }

        // Distance (meters) to the nearest CARVED SHORE EDGE — resolves small
        // islands the coast-distance field misses. Falls back to coast distance
        // on pre-v5 bakes (handled inside SampleEdgeDistanceMeters).
		internal float GetDistanceToEdgeMeters(float worldX, float worldZ)
		{
			if (UsesLocalWaterFallback)
				return LocalWaterFallbackDistanceMeters;

            if (!DeepWaterDistanceBake.IsLoaded)
                return float.MaxValue;

            int mapPixelX;
            int mapPixelY;
            float fracX;
            float fracZ;
			GetGlobalMapFractions(worldX, worldZ, out mapPixelX, out mapPixelY, out fracX, out fracZ);
			if (ShouldUseLocalEdgeDistance(mapPixelX, mapPixelY, fracX, fracZ))
			{
				float localDistance = DeepWaterDistanceBake.SampleLocalEdgeDistanceMeters(
					mapPixelX, mapPixelY, fracX, fracZ);
				return localDistance < float.MaxValue ? localDistance : LocalWaterFallbackDistanceMeters;
			}

			return DeepWaterDistanceBake.SampleEdgeDistanceMeters(
				mapPixelX, mapPixelY, fracX, fracZ);
		}

        internal bool IsBakedWater(float worldX, float worldZ)
        {
            if (!DeepWaterDistanceBake.IsLoaded)
                return false;

            int mapPixelX;
            int mapPixelY;
            float fracX;
            float fracZ;
            GetGlobalMapFractions(worldX, worldZ, out mapPixelX, out mapPixelY, out fracX, out fracZ);
            return DeepWaterDistanceBake.IsWaterAt(mapPixelX, mapPixelY, fracX, fracZ);
        }

        /// <summary>
        /// Phase B: per-cell carving query against the bake's FINE water
        /// mask. Adjacent tiles that share a boundary world position will
        /// read the same global bake value, so they agree on every
        /// boundary cell's carve decision — no per-tile heightmap
        /// reclassification, no map-pixel-transition seams. On pre-v4
        /// bakes (no fine mask), returns false; local water that the
		/// pruned fine mask missed is recovered from the loaded tile.
        /// </summary>
		internal bool IsCarvedWater(float worldX, float worldZ)
		{
			int mapPixelX;
			int mapPixelY;
			float fracX;
			float fracZ;
			GetGlobalMapFractions(worldX, worldZ, out mapPixelX, out mapPixelY, out fracX, out fracZ);
			return IsCarvedWater(mapPixelX, mapPixelY, fracX, fracZ);
		}

		internal bool IsCarvedWater(int mapPixelX, int mapPixelY, float fracX, float fracZ)
		{
			if (UsesLocalWaterFallback)
			{
				if (mapPixelX != MapPixelX || mapPixelY != MapPixelY)
					return false;

				return DeepWaterWaterClassification.IsLocalPointWater(mapData, fracX, fracZ);
			}

			if (!DeepWaterDistanceBake.IsLoaded || !DeepWaterDistanceBake.HasFineWaterMask)
				return false;

			if (DeepWaterDistanceBake.IsCarvedWater(mapPixelX, mapPixelY, fracX, fracZ))
				return true;

			return IsLocalWaterMissedByFineBake(mapPixelX, mapPixelY, fracX, fracZ);
		}

		private bool IsLocalWaterMissedByFineBake(int mapPixelX, int mapPixelY, float fracX, float fracZ)
		{
			// Some shipped bakes are ocean-pruned; the loaded tile is authoritative for local water.
			return mapPixelX == MapPixelX &&
				mapPixelY == MapPixelY &&
				DeepWaterDistanceBake.HasFineWaterMask &&
				!DeepWaterDistanceBake.IsCarvedWater(mapPixelX, mapPixelY, fracX, fracZ) &&
				DeepWaterWaterClassification.IsLocalPointWater(mapData, fracX, fracZ);
		}

		private bool ShouldUseLocalEdgeDistance(int mapPixelX, int mapPixelY, float fracX, float fracZ)
		{
			return mapPixelX == MapPixelX &&
				mapPixelY == MapPixelY &&
				DeepWaterDistanceBake.HasFineWaterMask &&
				DeepWaterWaterClassification.IsLocalPointWater(mapData, fracX, fracZ) &&
				(!DeepWaterDistanceBake.IsWaterAt(mapPixelX, mapPixelY, fracX, fracZ) ||
					IsLocalWaterMissedByFineBake(mapPixelX, mapPixelY, fracX, fracZ));
		}

        // Floating-origin-INDEPENDENT coordinate for the bathymetry noise.
        // Anchored to the global map-pixel grid, NOT the Unity origin (which
        // DFU's floating origin shifts as the player swims). Two tiles sampling
        // the same physical point return the same value regardless of when, or
        // at what origin offset, they were built — so the Perlin seabed hills
        // meet exactly at shared map-pixel edges instead of tearing into seams.
        internal void GetNoiseWorldCoords(float worldX, float worldZ, out float noiseX, out float noiseZ)
        {
            int mapPixelX;
            int mapPixelY;
            float fracX;
            float fracZ;
            GetGlobalMapFractions(worldX, worldZ, out mapPixelX, out mapPixelY, out fracX, out fracZ);
            noiseX = (mapPixelX + fracX) * tileWorldSize;
            noiseZ = (mapPixelY + (1f - fracZ)) * tileWorldSize;
        }

        // Bilinearly blend the per-map-pixel climate across the 4 surrounding
        // pixel CENTERS, returning both the blended seabed base depth and the
        // blended texture band. Climate is constant within a map pixel, so this
        // smooths the per-climate base/band across pixel boundaries — no hard
        // depth STEP (wall/seam) and no abrupt texture line where the climate
        // changes, while regional variety is preserved between boundaries.
        internal void GetBlendedClimate(float worldX, float worldZ, out float baseDepth, out float band)
        {
            int climateWN, climateEN, climateWS, climateES;
            float wx, wy;
            GetSurroundingClimates(worldX, worldZ, out climateWN, out climateEN, out climateWS, out climateES, out wx, out wy);

            float depthN = Mathf.Lerp(DeepBathymetry.ClimateBaseDepth(climateWN), DeepBathymetry.ClimateBaseDepth(climateEN), wx);
            float depthS = Mathf.Lerp(DeepBathymetry.ClimateBaseDepth(climateWS), DeepBathymetry.ClimateBaseDepth(climateES), wx);
            baseDepth = Mathf.Lerp(depthN, depthS, wy);

            float bandN = Mathf.Lerp(DeepBathymetry.ClimateBandSignal(climateWN), DeepBathymetry.ClimateBandSignal(climateEN), wx);
            float bandS = Mathf.Lerp(DeepBathymetry.ClimateBandSignal(climateWS), DeepBathymetry.ClimateBandSignal(climateES), wx);
            band = Mathf.Lerp(bandN, bandS, wy);
        }

        internal float GetBlendedClimateBaseDepth(float worldX, float worldZ)
        {
            float baseDepth, band;
            GetBlendedClimate(worldX, worldZ, out baseDepth, out band);
            return baseDepth;
        }

        // The 4 map-pixel climates whose CENTERS surround this world position,
        // plus the bilinear weights. Pixel centers sit at frac 0.5, so the -0.5
        // shift puts centers on integers; resolving through the same global
        // mapping as GetGlobalMapFractions keeps the result identical on both
        // sides of a shared edge.
        private void GetSurroundingClimates(
            float worldX, float worldZ,
            out int climateWN, out int climateEN, out int climateWS, out int climateES,
            out float wx, out float wy)
        {
            int mapPixelX;
            int mapPixelY;
            float fracX;
            float fracZ;
            GetGlobalMapFractions(worldX, worldZ, out mapPixelX, out mapPixelY, out fracX, out fracZ);

            // Global pixel-center space (south-growing Y matches the map pixel Y).
            float gx = mapPixelX + fracX - 0.5f;
            float gy = mapPixelY + (1f - fracZ) - 0.5f;
            int px = Mathf.FloorToInt(gx);
            int py = Mathf.FloorToInt(gy);
            wx = gx - px;
            wy = gy - py;

            climateWN = ClimateAtPixel(px, py);
            climateEN = ClimateAtPixel(px + 1, py);
            climateWS = ClimateAtPixel(px, py + 1);
            climateES = ClimateAtPixel(px + 1, py + 1);
        }

        private int ClimateAtPixel(int mapX, int mapY)
        {
            DaggerfallUnity dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.ContentReader == null || dfu.ContentReader.MapFileReader == null)
                return ClimateIndex;
            mapX = Mathf.Clamp(mapX, 0, MapsFile.MaxMapPixelX - 1);
            mapY = Mathf.Clamp(mapY, 0, MapsFile.MaxMapPixelY - 1);
            return dfu.ContentReader.MapFileReader.GetClimateIndex(mapX, mapY);
        }

		private static int ResolveBiomeClimateIndex(int climateIndex, int mapX, int mapY)
		{
			if (climateIndex != (int)MapsFile.Climates.Ocean)
				return climateIndex;
			if (!DeepWaterDistanceBake.MapPixelHasLandCells(mapX, mapY) && IsFullyOffshore(mapX, mapY))
				return climateIndex;

			DaggerfallUnity dfu = DaggerfallUnity.Instance;
			if (dfu == null || dfu.ContentReader == null || dfu.ContentReader.MapFileReader == null)
				return climateIndex;

			int bestClimate = climateIndex;
			int bestCount = 0;
			for (int dy = -1; dy <= 1; dy++)
			{
				for (int dx = -1; dx <= 1; dx++)
				{
					if (dx == 0 && dy == 0)
						continue;

					int c = GetClimateSafe(dfu, mapX + dx, mapY + dy, climateIndex);
					if (c == (int)MapsFile.Climates.Ocean)
						continue;

					int count = CountNeighborClimate(dfu, mapX, mapY, c, climateIndex);
					if (count > bestCount)
					{
						bestClimate = c;
						bestCount = count;
					}
				}
			}

			return bestClimate;
		}

		private static bool IsFullyOffshore(int mapX, int mapY)
		{
			for (int z = 0; z < 3; z++)
			{
				for (int x = 0; x < 3; x++)
				{
					float fracX = 0.125f + x * 0.375f;
					float fracZ = 0.125f + z * 0.375f;
					if (DeepWaterDistanceBake.SampleDistanceMeters(mapX, mapY, fracX, fracZ) <= DeepBathymetry.ShelfBreakDistance)
						return false;
				}
			}

			return true;
		}

		private static int CountNeighborClimate(DaggerfallUnity dfu, int mapX, int mapY, int climate, int fallback)
		{
			int count = 0;
			for (int dy = -1; dy <= 1; dy++)
				for (int dx = -1; dx <= 1; dx++)
					if ((dx != 0 || dy != 0) && GetClimateSafe(dfu, mapX + dx, mapY + dy, fallback) == climate)
						count++;

			return count;
		}

		private static int GetClimateSafe(DaggerfallUnity dfu, int mapX, int mapY, int fallback)
		{
			if (mapX < 0 || mapY < 0 || mapX >= MapsFile.MaxMapPixelX || mapY >= MapsFile.MaxMapPixelY)
				return fallback;

			return dfu.ContentReader.MapFileReader.GetClimateIndex(mapX, mapY);
		}

        private void GetTileFractions(float worldX, float worldZ, out float fracX, out float fracZ)
        {
            // Re-read the tile origin each query in case the streaming world
            // shifted us (FloatingOrigin can move terrains between updates,
            // so caching the position from Initialize would drift).
            Vector3 origin = transform != null ? transform.position : cachedOrigin;
            fracX = (worldX - origin.x) / tileWorldSize;
            fracZ = (worldZ - origin.z) / tileWorldSize;
        }

        private void GetGlobalMapFractions(
            float worldX,
            float worldZ,
            out int mapPixelX,
            out int mapPixelY,
            out float fracX,
            out float fracZ)
        {
            float localFracX;
            float localFracZ;
            GetTileFractions(worldX, worldZ, out localFracX, out localFracZ);

            float globalX = MapPixelX + localFracX;
            float globalSouthY = MapPixelY + (1f - localFracZ);

            mapPixelX = Mathf.FloorToInt(globalX);
            mapPixelY = Mathf.FloorToInt(globalSouthY);
            fracX = globalX - mapPixelX;
            float southFrac = globalSouthY - mapPixelY;
            fracZ = 1f - southFrac;

            NormalizeMapFractionX(ref mapPixelX, ref fracX, MapsFile.MaxMapPixelX);
            NormalizeMapFractionY(ref mapPixelY, ref fracZ, MapsFile.MaxMapPixelY);
        }

        private static void NormalizeMapFractionX(ref int mapPixel, ref float frac, int maxMapPixels)
        {
            if (mapPixel < 0)
            {
                mapPixel = 0;
                frac = 0f;
                return;
            }

            if (mapPixel >= maxMapPixels)
            {
                mapPixel = maxMapPixels - 1;
                frac = 1f;
                return;
            }

            frac = Mathf.Clamp01(frac);
        }

        private static void NormalizeMapFractionY(ref int mapPixel, ref float fracZ, int maxMapPixels)
        {
            if (mapPixel < 0)
            {
                mapPixel = 0;
                fracZ = 1f;
                return;
            }

            if (mapPixel >= maxMapPixels)
            {
                mapPixel = maxMapPixels - 1;
                fracZ = 0f;
                return;
            }

            fracZ = Mathf.Clamp01(fracZ);
        }

        // Water-generation gate. Normal ocean/coastline tiles use the
        // global fine bake so tile edges agree. Small inland/lake water
        // can be visible in DFU's promoted map data while absent from that
        // ocean-connected bake, so those tiles use a local shallow-water
        // fallback instead of being treated as dry land.
        private bool ComputeOceanConnectivity(DaggerfallTerrain dfTerrain)
        {
            if (!DeepWaterDistanceBake.IsLoaded) return false;
            if (dfTerrain == null) return false;

            int mx = dfTerrain.MapPixelX;
            int my = dfTerrain.MapPixelY;

            if (!DeepWaterWaterClassification.MapDataHasWater(dfTerrain.MapData))
                return false;

            if (DeepWaterDistanceBake.HasFineWaterMask)
			{
				if (DeepWaterDistanceBake.MapPixelHasFineWaterCells(mx, my))
				{
					UsesLocalWaterFallback = false;
					return true;
				}

				UsesLocalWaterFallback = true;
				return true;
			}

            // Pre-v4 (legacy) fallback: heightmap-based self-check
            // combined with coarse-mask neighbor check. Identical to
            // v0.52.4 behavior so old bakes still get the shoreline
            // ocean-connectivity improvement.
            return DeepWaterDistanceBake.MapPixelOrCardinalNeighborHasWaterCells(mx, my);
        }

    }
}
