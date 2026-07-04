// Project:         Iliac Puddle No More
// License:         MIT

using System.IO;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Global pre-computed distance-to-coast field. Replaces the per-tile BFS
    /// that used to run at every tile promotion — the BFS could only see its
    /// own heightmap, so adjacent tiles disagreed on depth at the shared
    /// boundary and we had to paper over the seams with cross-tile seeding
    /// and a deferred neighbor-refresh cascade.
    ///
    /// The bake is built once by the editor tool (Tools > Deep Waters > Bake
    /// Distance Field) using DFU's WOODS.WLD world heightmap. Stored at a
    /// configurable sub-cell resolution (default 8 cells per map pixel ≈
    /// 102m/cell), one byte per cell, distance in `distanceScaleMeters`
    /// units (default 16, so the byte range 0..255 represents 0..4080m,
    /// comfortably past ShelfRampMeters so the shelf gradient never saturates).
    ///
    /// At runtime: tile passes its own MapPixelX/Y plus a (fracX, fracZ) in
    /// [0..1] and we bilinearly interpolate the four surrounding global
    /// cells. The result is identical for the same world position regardless
    /// of which tile is querying, so adjacent meshes agree at the seam.
    /// </summary>
    public static class DeepWaterDistanceBake
    {
        // File header magic + version. Bump version if the layout changes.
        // Only the current version loads: the mod ships its own bakes and the
        // editor baker always writes CurrentVersion, so there is no pre-v6
        // file to tolerate. Layout: distance grid + coarse water mask (8x8
        // per pixel), fine water mask (64x64 per pixel, any-water criterion,
        // drives cell-level carving), carved-edge distance field, and the
        // local-water edge distance field for inland lakes.
        private const uint MagicBytes = 0x44574442;   // 'DWDB'
		private const ushort CurrentVersion = 6;

        // Asset names (without extension) inside the mod's Resources folder.
        // The primary bake is built against the terrain-overhaul heightmap
        // (Interesting Terrains / WoD); the vanilla bake matches DFU's stock
        // DefaultTerrainSampler and is preferred when no overhaul is active.
        public const string BakeAssetName = "DistanceBake";
        public const string VanillaBakeAssetName = "DistanceBakeVanilla";

        private static bool loaded;
        private static byte[] data;
        private static byte[] waterMaskBits;        // coarse mask, 8x8 per pixel
        private static byte[] fineWaterMaskBits;    // fine mask, 64x64 per pixel by default (v4+)
		private static byte[] edgeData;             // distance-to-carved-edge, coarse (v5+)
		private static byte[] localEdgeData;        // distance-to-local-water-edge, coarse (v6+)
        private static int widthCells;
        private static int heightCells;
        private static int widthCellsFine;
        private static int heightCellsFine;
        private static int subCellsPerPixelX;
        private static int subCellsPerPixelY;
        private static int subCellsPerPixelFine;
        private static float distanceScaleMeters;
        private static bool hasWaterMask;
        private static bool hasFineWaterMask;
		private static bool hasEdgeField;
		private static bool hasLocalEdgeField;

        public static bool IsLoaded { get { return loaded; } }
        public static bool HasFineWaterMask { get { return hasFineWaterMask; } }

        /// <summary>
        /// Parse a bake file's bytes. Returns true if the load succeeded —
        /// existing data is preserved on failure so a corrupt file won't
        /// silently lose a previously valid bake. On success, callers can use
        /// SampleDistanceMeters().
        /// </summary>
        public static bool TryLoadBytes(byte[] fileBytes)
        {
            if (fileBytes == null || fileBytes.Length < HeaderByteSize)
            {
                Debug.LogError("[DeepWaters.Bake] Bake file is null or shorter than header (" +
                               (fileBytes != null ? fileBytes.Length : 0) + " bytes).");
                return false;
            }

            using (var ms = new MemoryStream(fileBytes))
            using (var br = new BinaryReader(ms))
            {
                uint magic = br.ReadUInt32();
                if (magic != MagicBytes)
                {
                    Debug.LogError("[DeepWaters.Bake] Bake file magic mismatch (got 0x" +
                                   magic.ToString("X8") + ", expected 0x" +
                                   MagicBytes.ToString("X8") + ").");
                    return false;
                }

                ushort version = br.ReadUInt16();
                if (version != CurrentVersion)
                {
                    Debug.LogError("[DeepWaters.Bake] Bake file version " + version +
                                   " unsupported (this build requires v" + CurrentVersion +
                                   " - re-run Tools > Deep Waters > Bake Distance Field).");
                    return false;
                }

                int sX = br.ReadUInt16();
                int sY = br.ReadUInt16();
                int pX = br.ReadUInt16();
                int pY = br.ReadUInt16();
                float scaleMeters = br.ReadUInt16();
                // v3 and earlier wrote `reserved`; v4 uses it for the fine
                // sub-cells-per-pixel value (zero in older bakes).
                int fineSubCellsRaw = br.ReadUInt16();

                int wCells = pX * sX;
                int hCells = pY * sY;
                long expectedDataBytes = (long)wCells * hCells;
                if (fileBytes.Length - HeaderByteSize < expectedDataBytes)
                {
                    Debug.LogError("[DeepWaters.Bake] Bake file truncated. Header says " +
                                   wCells + "x" + hCells + " cells (=" + expectedDataBytes +
                                   " bytes) but body is " + (fileBytes.Length - HeaderByteSize) + ".");
                    return false;
                }

                byte[] cells = new byte[expectedDataBytes];
                br.Read(cells, 0, cells.Length);

                long expectedMaskBytes = (expectedDataBytes + 7) / 8;
                if (fileBytes.Length - HeaderByteSize - expectedDataBytes < expectedMaskBytes)
                {
                    Debug.LogError("[DeepWaters.Bake] Bake water mask truncated. Header says " +
                                   expectedMaskBytes + " mask bytes but file only has " +
                                   (fileBytes.Length - HeaderByteSize - expectedDataBytes) + ".");
                    return false;
                }

                byte[] mask = new byte[expectedMaskBytes];
                br.Read(mask, 0, mask.Length);

                // Fine water mask follows the coarse mask. Resolution is
                // mapPixels × fineSubCells per axis.
                if (fineSubCellsRaw <= 0)
                {
                    Debug.LogError("[DeepWaters.Bake] Bake file has no fine water mask.");
                    return false;
                }

                int fineSubCells = fineSubCellsRaw;
                int wCellsFine = pX * fineSubCells;
                int hCellsFine = pY * fineSubCells;
                long expectedFineMaskBytes = ((long)wCellsFine * hCellsFine + 7) / 8;
                long remaining = fileBytes.Length - HeaderByteSize - expectedDataBytes - expectedMaskBytes;
                if (remaining < expectedFineMaskBytes)
                {
                    Debug.LogError("[DeepWaters.Bake] Fine water mask truncated. Header says " +
                                   expectedFineMaskBytes + " bytes (resolution " +
                                   wCellsFine + "x" + hCellsFine + ", " + fineSubCells +
                                   " sub-cells per pixel) but only " + remaining + " bytes remain.");
                    return false;
                }

                byte[] fineMask = new byte[expectedFineMaskBytes];
                br.Read(fineMask, 0, fineMask.Length);

                // Coarse distance-to-carved-edge field, appended after the
                // fine mask (same resolution + scale as the distance-to-coast grid).
                long remainingEdge = remaining - expectedFineMaskBytes;
                if (remainingEdge < expectedDataBytes)
                {
                    Debug.LogError("[DeepWaters.Bake] Edge-distance field truncated. Header implies " +
                                   expectedDataBytes + " edge bytes but only " + remainingEdge + " remain.");
                    return false;
                }
                byte[] edgeCells = new byte[expectedDataBytes];
                br.Read(edgeCells, 0, edgeCells.Length);

				// Coarse distance-to-local-water-edge field, appended after
				// the ocean-connected edge field. Used only by local fallback water.
				long remainingLocalEdge = remainingEdge - expectedDataBytes;
				if (remainingLocalEdge < expectedDataBytes)
				{
					Debug.LogError("[DeepWaters.Bake] Local edge-distance field truncated. Header implies " +
						expectedDataBytes + " local-edge bytes but only " + remainingLocalEdge + " remain.");
					return false;
				}
				byte[] localEdgeCells = new byte[expectedDataBytes];
				br.Read(localEdgeCells, 0, localEdgeCells.Length);

                widthCells = wCells;
                heightCells = hCells;
                widthCellsFine = wCellsFine;
                heightCellsFine = hCellsFine;
                subCellsPerPixelX = sX;
                subCellsPerPixelY = sY;
                subCellsPerPixelFine = fineSubCells;
                distanceScaleMeters = scaleMeters;
                data = cells;
                waterMaskBits = mask;
                fineWaterMaskBits = fineMask;
                edgeData = edgeCells;
				localEdgeData = localEdgeCells;
                hasWaterMask = true;
                hasFineWaterMask = true;
                hasEdgeField = true;
				hasLocalEdgeField = true;
                loaded = true;
            }

            // Count water vs land cells from the loaded distance grid so the
            // log line proves the runtime sees the version of the bake we
            // think it does. "Water" here = any cell whose stored distance
            // byte is 0 (i.e. the cell IS land in the bake — bake stores
            // 0 = on land, larger values = farther from land). So an
            // unhealthy number like ~100% would mean the file is missing
            // or the load got garbage. A typical Iliac Bay bake should
            // land in the 20–40% water range.
            int landCells = 0;
            for (int i = 0; i < data.Length; i++) if (data[i] == 0) landCells++;
            int totalCells = data.Length;
            int waterCells = totalCells - landCells;
            float waterPct = 100f * waterCells / totalCells;

            if (waterPct < 5f)
                Debug.LogWarning("[DeepWaters.Bake] Loaded bake contains very little ocean. " +
                                 "If WOD/Interesting Terrain is active, re-run Tools > Deep Waters > " +
                                 "Bake Distance Field with the patched WOD height-buffer path; a vanilla " +
                                 "sampler pass over WOD's altered height bytes can produce a nearly-dry bake.");
            return true;
        }

        /// <summary>
        /// Sample the bake's distance-to-coast value (in meters) for the world
        /// position described by (mapX, mapY, fracX, fracZ) where fracX/fracZ
        /// are in [0..1] within the tile (0 = south/west edge of tile, 1 =
        /// north/east edge). The caller computes the fractions from its
        /// tile's transform.position and the requested world coordinates.
        /// </summary>
        public static float SampleDistanceMeters(int mapPixelX, int mapPixelY, float fracX, float fracZ)
        {
            if (!loaded || data == null) return float.MaxValue;

            return BilinearSampleMeters(data, mapPixelX, mapPixelY, fracX, fracZ);
        }

		/// <summary>
		/// Sample the baked distance (meters) to the nearest CARVED SHORE EDGE
		/// (carved-water boundary). Falls back to distance-to-coast on pre-v5 bakes.
		/// </summary>
		public static float SampleEdgeDistanceMeters(int mapPixelX, int mapPixelY, float fracX, float fracZ)
		{
			if (!loaded || !hasEdgeField || edgeData == null)
				return SampleDistanceMeters(mapPixelX, mapPixelY, fracX, fracZ);

			return BilinearSampleMeters(edgeData, mapPixelX, mapPixelY, fracX, fracZ);
		}

		public static float SampleLocalEdgeDistanceMeters(int mapPixelX, int mapPixelY, float fracX, float fracZ)
		{
			if (!loaded || !hasLocalEdgeField || localEdgeData == null)
				return float.MaxValue;

			return BilinearSampleMeters(localEdgeData, mapPixelX, mapPixelY, fracX, fracZ);
		}

        // Convert tile-local fractions to global sub-cell coordinates.
        // DFU local Z grows north, while map pixel Y grows south; the baked
        // grid stores rows in map-pixel order (north-to-south), so fracZ
        // flips here. The half-cell offset puts global integer indices at
        // sub-cell CENTERS, so the interpolation between neighbors is
        // symmetric around the cell midpoint and matches bake-time placement.
        private static float BilinearSampleMeters(byte[] grid, int mapPixelX, int mapPixelY, float fracX, float fracZ)
        {
            float gx = mapPixelX * subCellsPerPixelX + Mathf.Clamp01(fracX) * subCellsPerPixelX - 0.5f;
            float gy = mapPixelY * subCellsPerPixelY + BakedSouthFraction(fracZ) * subCellsPerPixelY - 0.5f;

            int x0 = Mathf.Clamp(Mathf.FloorToInt(gx), 0, widthCells - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(gy), 0, heightCells - 1);
            int x1 = Mathf.Min(x0 + 1, widthCells - 1);
            int y1 = Mathf.Min(y0 + 1, heightCells - 1);
            float tx = Mathf.Clamp01(gx - x0);
            float ty = Mathf.Clamp01(gy - y0);

            float d00 = grid[y0 * widthCells + x0] * distanceScaleMeters;
            float d10 = grid[y0 * widthCells + x1] * distanceScaleMeters;
            float d01 = grid[y1 * widthCells + x0] * distanceScaleMeters;
            float d11 = grid[y1 * widthCells + x1] * distanceScaleMeters;

            float dx0 = Mathf.Lerp(d00, d10, tx);
            float dx1 = Mathf.Lerp(d01, d11, tx);
            return Mathf.Lerp(dx0, dx1, ty);
        }

        public static bool IsWaterAt(int mapPixelX, int mapPixelY, float fracX, float fracZ)
        {
            if (!loaded || data == null)
                return false;

            int x;
            int y;
            GetNearestCell(mapPixelX, mapPixelY, fracX, fracZ, out x, out y);
            return CellHasWater(x, y);
        }

        /// <summary>
        /// Phase B: bake-driven cell-level carving decision. Queries the
        /// FINE water mask (default 64 sub-cells per pixel ≈ 13 m / cell)
        /// at the given world position. Returns true if this cell should
        /// be carved. Because the mask is global and adjacent tiles
        /// sample the SAME data at their shared boundary world positions,
        /// they agree on every boundary cell by construction — no
        /// per-tile heightmap reclassification, no map-pixel transition
        /// seams. Returns false on pre-v4 bakes; callers should check
        /// HasFineWaterMask and fall back to the heightmap any-corner
        /// path if needed.
        /// </summary>
        public static bool IsCarvedWater(int mapPixelX, int mapPixelY, float fracX, float fracZ)
        {
            if (!loaded || !hasFineWaterMask || fineWaterMaskBits == null || subCellsPerPixelFine <= 0)
                return false;

            float gx = mapPixelX * subCellsPerPixelFine + Mathf.Clamp01(fracX) * subCellsPerPixelFine - 0.5f;
            float gy = mapPixelY * subCellsPerPixelFine + BakedSouthFraction(fracZ) * subCellsPerPixelFine - 0.5f;
            int x = Mathf.Clamp(Mathf.RoundToInt(gx), 0, widthCellsFine - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(gy), 0, heightCellsFine - 1);

            int index = y * widthCellsFine + x;
            return (fineWaterMaskBits[index >> 3] & (1 << (index & 7))) != 0;
        }

        /// <summary>
        /// True if the supplied map pixel has any FINE-mask water cell.
        /// Replaces MapPixelHasWaterCells (which queries the coarse mask)
        /// when callers need the bake-aligned, any-water-criterion answer
        /// — specifically the ocean-connectivity self-check for shoreline
        /// tiles where the coarse 30%/50% majority criterion would drop
        /// the tile but ANY water sample exists.
        /// </summary>
        public static bool MapPixelHasFineWaterCells(int mapPixelX, int mapPixelY)
        {
            if (!loaded || !hasFineWaterMask || fineWaterMaskBits == null || subCellsPerPixelFine <= 0)
                return false;
            if (!IsValidMapPixel(mapPixelX, mapPixelY))
                return false;

            int baseX = mapPixelX * subCellsPerPixelFine;
            int baseY = mapPixelY * subCellsPerPixelFine;
            for (int sy = 0; sy < subCellsPerPixelFine; sy++)
            {
                int rowBase = (baseY + sy) * widthCellsFine + baseX;
                for (int sx = 0; sx < subCellsPerPixelFine; sx++)
                {
                    int index = rowBase + sx;
                    if (index < 0 || index >= widthCellsFine * heightCellsFine)
                        continue;
                    if ((fineWaterMaskBits[index >> 3] & (1 << (index & 7))) != 0)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// True if the supplied map pixel has *any* water cell (distance > 0)
        /// in its 4-cell sub-grid. Used to short-circuit the ocean-connectivity
        /// check now that the BFS lives in the bake instead of the per-tile
        /// heightmap. Cheap because we already have the data in memory.
        /// </summary>
        public static bool MapPixelHasWaterCells(int mapPixelX, int mapPixelY)
        {
			return MapPixelHasCoarseCell(mapPixelX, mapPixelY, true);
        }

		public static bool MapPixelOrCardinalNeighborHasWaterCells(int mapPixelX, int mapPixelY)
		{
			return MapPixelHasWaterCells(mapPixelX, mapPixelY) ||
				MapPixelHasWaterCells(mapPixelX - 1, mapPixelY) ||
				MapPixelHasWaterCells(mapPixelX + 1, mapPixelY) ||
				MapPixelHasWaterCells(mapPixelX, mapPixelY - 1) ||
				MapPixelHasWaterCells(mapPixelX, mapPixelY + 1);
		}

        /// <summary>
        /// True if the supplied map pixel has *any* land cell (distance == 0)
        /// in its 4-cell sub-grid. Used to detect pure-ocean tiles cheaply.
        /// </summary>
        public static bool MapPixelHasLandCells(int mapPixelX, int mapPixelY)
        {
			return MapPixelHasCoarseCell(mapPixelX, mapPixelY, false);
        }

		private static bool MapPixelHasCoarseCell(int mapPixelX, int mapPixelY, bool water)
		{
			if (!loaded || data == null) return false;
			if (!IsValidMapPixel(mapPixelX, mapPixelY))
				return false;

			int baseX = mapPixelX * subCellsPerPixelX;
			int baseY = mapPixelY * subCellsPerPixelY;
			for (int sy = 0; sy < subCellsPerPixelY; sy++)
			{
				for (int sx = 0; sx < subCellsPerPixelX; sx++)
				{
					if (CellHasWater(baseX + sx, baseY + sy) == water)
						return true;
				}
			}

			return false;
		}

		private static bool IsValidMapPixel(int mapPixelX, int mapPixelY)
		{
			return mapPixelX >= 0 &&
				mapPixelY >= 0 &&
				mapPixelX < MapsFile.MaxMapPixelX &&
				mapPixelY < MapsFile.MaxMapPixelY;
		}

        private static void GetNearestCell(
            int mapPixelX,
            int mapPixelY,
            float fracX,
            float fracZ,
            out int x,
            out int y)
        {
            float gx = mapPixelX * subCellsPerPixelX + Mathf.Clamp01(fracX) * subCellsPerPixelX - 0.5f;
            float gy = mapPixelY * subCellsPerPixelY + BakedSouthFraction(fracZ) * subCellsPerPixelY - 0.5f;
            x = Mathf.Clamp(Mathf.RoundToInt(gx), 0, widthCells - 1);
            y = Mathf.Clamp(Mathf.RoundToInt(gy), 0, heightCells - 1);
        }

        private static float BakedSouthFraction(float fracZ)
        {
            return 1f - Mathf.Clamp01(fracZ);
        }

        private static bool CellHasWater(int x, int y)
        {
            if (x < 0 || y < 0 || x >= widthCells || y >= heightCells)
                return false;

            int index = y * widthCells + x;
            if (hasWaterMask && waterMaskBits != null)
                return (waterMaskBits[index >> 3] & (1 << (index & 7))) != 0;

            return data != null && data[index] > 0;
        }

        // --- helpers shared with the editor tool ------------------------------

        /// <summary>
        /// Number of header bytes the bake file format reserves. Layout is
        /// uint32 magic (4) + uint16 version (2) + uint16 subCellsX/Y (4) +
        /// uint16 mapPixelsX/Y (4) + uint16 distanceScale (2) +
        /// uint16 reserved (2) = 18 bytes. Keep this in sync with WriteHeaderV4().
        /// </summary>
        public const int HeaderByteSize = 18;

#if UNITY_EDITOR
        public static void WriteHeaderV4(BinaryWriter bw,
            int subCellsX, int subCellsY,
            int mapPixelsX, int mapPixelsY,
            ushort distanceScaleMetersToWrite,
            ushort fineSubCellsPerPixel)
        {
            // v4 header layout: identical to v3 except the formerly-reserved
            // ushort now carries fineSubCellsPerPixel. Body changes too: a
            // SECOND packed water mask follows the existing one, sized for
            // mapPixels × fineSubCellsPerPixel cells per axis.
            bw.Write(MagicBytes);
            bw.Write(CurrentVersion);
            bw.Write((ushort)subCellsX);
            bw.Write((ushort)subCellsY);
            bw.Write((ushort)mapPixelsX);
            bw.Write((ushort)mapPixelsY);
            bw.Write(distanceScaleMetersToWrite);
            bw.Write(fineSubCellsPerPixel);
        }
#endif
    }
}
