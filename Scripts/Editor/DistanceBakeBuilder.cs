// Project:         Iliac Puddle No More
// License:         MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;

namespace DeepWaters.Editor
{
    /// <summary>
    /// Offline tool that pre-computes the world-wide distance-to-coast field.
    /// Replaces the per-tile BFS that used to run at every tile promotion —
    /// the old approach could never agree at tile boundaries because each
    /// tile only saw its own heightmap. With one global BFS done offline, the
    /// runtime is a simple lookup and adjacent meshes automatically match.
    ///
    /// Run once via the menu (Tools > Deep Waters > Bake Distance Field).
    /// Re-run if you install a terrain-changing mod that changes the
    /// WOODS.WLD height buffer before the bake. Interesting Terrains does
    /// exactly that, so this tool can capture its altered large-scale
    /// coastline without driving its per-pixel compute-shader sampler across
    /// the entire world.
    /// </summary>
    public static class DistanceBakeBuilder
    {
        // Tunables. Bumping resolution gives finer shoreline detail at the
        // cost of file size (cells = mapPixels * SubCellsPerPixel² ; storage
        // is one byte per cell plus one bit per cell for the carve mask).
        // 8 × 8 ≈ 32 MB distance + 4 MB mask for the full world.
        private const int SubCellsPerPixel = 8;

        // Fine carve mask — Phase B. The COARSE 8x8 mask (above) drives
        // the distance-field BFS and the inland-lake filter; it uses a
        // STRICT majority criterion so shoreline cells stay LAND and
        // depths near the coast stay shallow. The FINE mask drives the
        // per-cell carving decision in DeepWaterFloorBuilder.ComputeHoleMask
        // and uses a PERMISSIVE "any water sample in sub-cell" criterion
        // so shoreline cells correctly classify as carve-worthy. Because
        // both adjacent tiles read the SAME global fine mask at their
        // shared boundary world positions, they agree on carve decisions
        // by construction — no more per-tile heightmap interpretation
        // mismatches creating 1-pixel walls / cross-pixel seams.
        //
        // 64 sub-cells per pixel ≈ 13 m per cell on an 819 m tile. The fine
        // mask is packed as bits while it is built, so editor memory stays
        // bounded instead of holding a 2 GB bool[] before packing. The packed
        // fine mask is still large (~256 MB), so keep a 32x32 menu item below
        // for quicker diagnostic bakes.
        private const int SubCellsPerPixelFine = 64;
        private const int SubCellsPerPixelFineDiagnostic = 32;
        private const int FineOceanConnectionCoarseRadius = 1;
        // MUST stay FALSE. Driving IT's GPU compute sampler from the bake removes
        // the D3D device (DXGI_DEVICE_REMOVED) on the FIRST dispatch — proven
        // twice, including with per-pixel readback drains + per-row GPU/resource
        // flushes (died on row 0 regardless). It's a driver/context
        // incompatibility, not resource pile-up, so pacing/batching/frame-yields
        // cannot fix it. IT's fine coastline therefore can't be captured offline;
        // it can only be read per-tile at runtime as IT streams it.
        private const bool UseInterestingTerrainGpuSamplerForBake = false;
        private const byte InterestingTerrainWaterMapHeight = 6;
        private const float InterestingTerrainWaterMapThreshold = InterestingTerrainWaterMapHeight + 0.5f;
        private const bool UseHybridCpuWodClassifierForBake = true;
        private const float HybridCpuWodWaterThresholdNormalized = 0.020000f;
        private const int HybridCpuSourceShoreRadius = 4;
        private const int HybridCpuLocationInfluenceRadius = 16;
        private const int HybridCpuLocationWaterProbeRadius = 16;
        // Lowered from 0.85 → 0.30 to capture shoreline boundary sub-cells.
        // The previous 85% threshold meant a sub-cell needed 85% of its
        // ~256 samples to be at-or-below ocean elevation to be classified
        // as water. At the visible shoreline, bicubic interpolation in
        // DFU's DefaultTerrainSampler pulls samples toward neighboring
        // pixel heights — water-pixel samples adjacent to a land pixel
        // creep above the clamp, and land-pixel samples adjacent to a
        // water pixel get pulled below it. A typical boundary sub-cell
        // ends up with 20–50% of samples at the ocean clamp and the
        // rest gradient-above. With 85% coverage these all classified
        // as LAND, leaving a visible gap between the carved water and
        // the actual rendered shore. 30% catches the gradient cells
        // where any meaningful chunk of the sub-cell is water, which is
        // what the user sees as "where water is" visually.
        private const float RequiredWaterCoverage = 0.30f;

        // How tightly a sample has to hug sea level to count as "water".
        // DFU's DefaultTerrainSampler clamps below-ocean values *to* the
        // ocean threshold, so true-water samples land exactly at it. IT
        // clamps to BASEHEIGHT_MIN (100) / MaxTerrainHeight (5000) = 0.02,
        // a hair below its OceanElevation (100.01), and classifies tiles as
        // water when w.land < 0.01 → height < 0.02008.
        //
        // Raised from 0.0008 (~4 cm above clamp) to 0.0040 (~20 cm above)
        // for the same shoreline-precision reason as RequiredWaterCoverage:
        // bicubic interpolation can push a sample a few centimeters
        // above the clamp even though the sample is visually still
        // water. 20 cm covers most boundary cases without false-
        // positives on actual beach (which is at least 1 m above
        // sea level in IT's classification — w.land >= 0.01 starts the
        // beach band).
        private const float WaterSampleHeadroomNormalized = 0.0040f;

        // Distance encoding: 1 byte per cell × this scale = metres. 16 m gives
        // a byte range of 0..4080 m, comfortably past DeepBathymetry's
        // ShelfRampMeters (2700 m) so the shelf gradient reaches full depth
        // before saturating — and leaves headroom to lengthen the ramp at
        // runtime without rebaking. Keep this >= ShelfRampMeters / 255.
        private const ushort DistanceScaleMeters = 16;

        // Output relative to the project root. The Resources folder gets
        // bundled into the .dfmod, so ship the bake just by rebuilding.
        private const string OutputPath =
            "Assets/Game/Mods/deep-waters/Resources/DistanceBake.bytes";
        private const string VanillaOutputPath =
            "Assets/Game/Mods/deep-waters/Resources/DistanceBakeVanilla.bytes";

        [MenuItem("Tools/Deep Waters/Bake Distance Field")]
        public static void BakeMenuItem()
        {
            try
            {
                Bake();
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError("[DeepWaters.Bake] Build failed: " + ex.Message + "\n" + ex.StackTrace);
                EditorUtility.DisplayDialog("Bake failed", ex.Message, "OK");
            }
        }

        // Bakes against DFU's stock DefaultTerrainSampler regardless of which
        // sampler is currently active, so the mod can ship a second bake that
        // matches vanilla terrain. At runtime the mod loads this one when no
        // terrain-overhaul sampler (Interesting Terrains / WoD) is detected.
        [MenuItem("Tools/Deep Waters/Bake Distance Field (Vanilla Terrain)")]
        public static void BakeVanillaMenuItem()
        {
            try
            {
                Bake(SubCellsPerPixelFine, new DefaultTerrainSampler(), VanillaOutputPath);
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError("[DeepWaters.Bake] Build failed: " + ex.Message + "\n" + ex.StackTrace);
                EditorUtility.DisplayDialog("Bake failed", ex.Message, "OK");
            }
        }

        [MenuItem("Tools/Deep Waters/Bake Distance Field (Diagnostic 32x32 Fine Mask)")]
        public static void BakeDiagnosticMenuItem()
        {
            try
            {
                Bake(SubCellsPerPixelFineDiagnostic);
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError("[DeepWaters.Bake] Build failed: " + ex.Message + "\n" + ex.StackTrace);
                EditorUtility.DisplayDialog("Bake failed", ex.Message, "OK");
            }
        }

        public static void Bake()
        {
            Bake(SubCellsPerPixelFine);
        }

        private static void Bake(int fineSubCellsPerPixel, ITerrainSampler forcedSampler = null, string outputPath = OutputPath)
        {
            var dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.ContentReader == null)
            {
                throw new System.Exception(
                    "DaggerfallUnity not initialized. Open a scene with DaggerfallUnity " +
                    "before running this tool so its TerrainSampler and WOODS.WLD reader " +
                    "are available.");
            }

            // DaggerfallUnity.TerrainSampler returns ITerrainSampler — leave
            // it at the interface so any third-party sampler (Interesting
            // Terrains' compute-shader sampler, Wilderness Overhaul, custom
            // mods) plugs in directly without an unsafe cast. A forced sampler
            // (the vanilla bake) bypasses the active one entirely.
            ITerrainSampler runtimeSampler = forcedSampler ?? dfu.TerrainSampler;
            if (runtimeSampler == null)
                throw new System.Exception("DaggerfallUnity.TerrainSampler is null.");
            bool useInterestingTerrainHeightBuffer =
                IsMonobeliskInterestingTerrainSampler(runtimeSampler) &&
                !UseInterestingTerrainGpuSamplerForBake;
            ITerrainSampler sampler = useInterestingTerrainHeightBuffer ? null : runtimeSampler;

            // Log the bake source so the user can tell when a mod sampler
            // needed a safer offline path.
            int mapPixelsX = MapsFile.MaxMapPixelX;
            int mapPixelsY = MapsFile.MaxMapPixelY;
            int widthCells = mapPixelsX * SubCellsPerPixel;
            int heightCells = mapPixelsY * SubCellsPerPixel;
            ValidateFineResolution(fineSubCellsPerPixel);
            int widthCellsFine = mapPixelsX * fineSubCellsPerPixel;
            int heightCellsFine = mapPixelsY * fineSubCellsPerPixel;

            Debug.Log("[DeepWaters.Bake] Building bake: distance " +
                      widthCells + "x" + heightCells + " (" + SubCellsPerPixel + "x" + SubCellsPerPixel +
                      " per pixel) + fine mask " + widthCellsFine + "x" + heightCellsFine +
                      " (" + fineSubCellsPerPixel + "x" + fineSubCellsPerPixel + " per pixel) using " +
                      (useInterestingTerrainHeightBuffer
                          ? "current WOODS.WLD height buffer, WOD water <= " + InterestingTerrainWaterMapHeight +
                            " (runtime sampler: " + runtimeSampler.GetType().FullName + ")"
                          : sampler.GetType().FullName) +
                      "...");

            // 1) One pass over every map pixel: aggregate the selected bake
            //    source into BOTH masks. Coarse mask drives the distance-field
            //    BFS and inland-lake filter. Fine mask drives runtime carving
            //    decisions so shoreline sub-cells become carve-eligible.
            EditorUtility.DisplayProgressBar("Bake distance field",
                useInterestingTerrainHeightBuffer
                    ? "Classifying WOD water from current WOODS.WLD height buffer..."
                    : "Generating heightmaps via " + sampler.GetType().Name + "...",
                0.02f);
            bool[] rawCoarseMask;
            byte[] packedFineMask;
            long rawFineWater;
            if (useInterestingTerrainHeightBuffer)
            {
                SampleWaterGridsFromInterestingTerrainHeightBuffer(
                    runtimeSampler,
                    widthCells, heightCells, widthCellsFine, heightCellsFine, fineSubCellsPerPixel,
                    out rawCoarseMask, out packedFineMask, out rawFineWater);
            }
            else
            {
                SampleWaterGridsUsingSampler(
                    sampler, widthCells, heightCells, widthCellsFine, heightCellsFine, fineSubCellsPerPixel,
                    out rawCoarseMask, out packedFineMask, out rawFineWater);
            }

            int rawCoarseWater = 0;
            for (int i = 0; i < rawCoarseMask.Length; i++) if (rawCoarseMask[i]) rawCoarseWater++;
            Debug.Log("[DeepWaters.Bake] Raw classification: coarse=" + rawCoarseWater + "/" +
                      rawCoarseMask.Length + " (" + (100.0 * rawCoarseWater / rawCoarseMask.Length).ToString("F1") +
                      "%), fine=" + rawFineWater + "/" + (long)widthCellsFine * heightCellsFine + " (" +
                      (100.0 * rawFineWater / ((long)widthCellsFine * heightCellsFine)).ToString("F1") + "%).");

            // 2) Ocean-connectivity BFS on the COARSE mask only, then prune
            //    the packed fine mask to coarse ocean-connected cells plus a
            //    one-cell shore neighborhood. This keeps the detailed shore
            //    carve mask without paying for a 512M-cell fine-mask BFS.
            bool[] coarseMask = BuildOceanConnectedWaterMask(rawCoarseMask, widthCells, heightCells);
            rawCoarseMask = null;
            long connectedFine = PruneFineMaskToCoarseOcean(
                packedFineMask,
                coarseMask,
                widthCells,
                heightCells,
                widthCellsFine,
                heightCellsFine);

            int connectedCoarse = 0;
            for (int i = 0; i < coarseMask.Length; i++) if (coarseMask[i]) connectedCoarse++;
            Debug.Log("[DeepWaters.Bake] Ocean-connected coarse=" + connectedCoarse +
                      " (" + (100.0 * connectedCoarse / coarseMask.Length).ToString("F1") +
                      "%); pruned fine=" + connectedFine + " (" +
                      (100.0 * connectedFine / ((long)widthCellsFine * heightCellsFine)).ToString("F1") + "%).");

            // 3) Global chamfer BFS on the COARSE mask only. Distance field
            //    stays at 8x8 — it just feeds smooth depth interpolation,
            //    so the coarser grid is fine. Cell width in metres equals
            //    one tile (~819 m) divided by SubCellsPerPixel.
            float tileWorldSize = MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale;
            float cellWidth = tileWorldSize / SubCellsPerPixel;
            EditorUtility.DisplayProgressBar("Bake distance field",
                "Running global chamfer BFS...", 0.40f);
            float[] distance = SeedDistanceGrid(coarseMask);
            ChamferDistance(distance, widthCells, heightCells, cellWidth);

            // 3b) Distance-to-carved-EDGE field (coarse, seeded from the FINE
            //     mask so small islands the coarse mask misses still register as
            //     shore). Drives the seabed shelf so the floor descends gradually
            //     from EVERY edge (coast + islands), not just the coarse
            //     coastline. Global, so adjacent tiles agree — no seams.
            EditorUtility.DisplayProgressBar("Bake distance field",
                "Building shore-edge distance...", 0.62f);
            float[] edgeDistance = BuildEdgeDistance(packedFineMask,
                widthCells, heightCells, widthCellsFine, heightCellsFine, cellWidth);

            // 4) Quantize + pack.
            EditorUtility.DisplayProgressBar("Bake distance field",
                "Quantizing to bytes...", 0.85f);
            byte[] distanceBytes = Quantize(distance, DistanceScaleMeters);
            byte[] edgeBytes = Quantize(edgeDistance, DistanceScaleMeters);
            byte[] packedCoarseMask = PackWaterMask(coarseMask);

            // 5) Write file.
            EditorUtility.DisplayProgressBar("Bake distance field",
                "Writing " + outputPath + "...", 0.95f);
            WriteBakeFile(outputPath, distanceBytes, packedCoarseMask, packedFineMask, edgeBytes,
                SubCellsPerPixel, SubCellsPerPixel,
                fineSubCellsPerPixel,
                mapPixelsX, mapPixelsY,
                DistanceScaleMeters);

            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
            Debug.Log("[DeepWaters.Bake] Wrote " + distanceBytes.Length + " distance bytes + " +
                      packedCoarseMask.Length + " coarse-mask bytes + " +
                      packedFineMask.Length + " fine-mask bytes + " +
                       edgeBytes.Length + " shore-edge bytes to " + outputPath +
                       " (coarse cell " + cellWidth.ToString("F1") + " m, fine cell " +
                       (tileWorldSize / fineSubCellsPerPixel).ToString("F1") + " m).");
        }

        private const string ExactMaskInputPath =
            "Assets/Game/Mods/deep-waters/Diagnostics/WodExactWaterMasks.bytes";

        // Build the shipped DistanceBake.bytes from the WOD Exact Tilemap Mask
        // Exporter's output. That exporter (Diagnostics menu) paces WOD's GPU
        // sampler one tile per editor update and writes the EXACT IT water masks,
        // but does NOT build a distance field — so the runtime never loads it.
        // This consumes those exact masks through the SAME ocean-connectivity ->
        // chamfer-distance -> shore-edge pipeline as Bake(), producing a bake that
        // uses IT's true coastline instead of the CPU-hybrid approximation. Run
        // after a fresh FULL-WORLD export (exporter "Rows to export" = 0).
        [MenuItem("Tools/Deep Waters/Bake Distance Field from WOD Exact Masks")]
        public static void BakeFromExactMasksMenuItem()
        {
            try
            {
                BakeFromExactMasks();
            }
            catch (System.Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError("[DeepWaters.Bake] Build from exact masks failed: " + ex.Message + "\n" + ex.StackTrace);
                EditorUtility.DisplayDialog("Bake from exact masks failed", ex.Message, "OK");
            }
        }

        private static void BakeFromExactMasks()
        {
            string absolute = System.IO.Path.Combine(
                System.IO.Directory.GetCurrentDirectory(), ExactMaskInputPath);
            if (!System.IO.File.Exists(absolute))
                throw new System.Exception("Exact mask file not found at " + ExactMaskInputPath +
                    ". Run Tools > Deep Waters > Diagnostics > WOD Exact Tilemap Mask Exporter " +
                    "with 'Rows to export' = 0 (full world) first.");

            int mapPixelsX = MapsFile.MaxMapPixelX;
            int mapPixelsY = MapsFile.MaxMapPixelY;
            int widthCells = mapPixelsX * SubCellsPerPixel;
            int heightCells = mapPixelsY * SubCellsPerPixel;

            EditorUtility.DisplayProgressBar("Bake from exact masks", "Reading exact WOD masks...", 0.05f);

            int fineSub;
            int widthCellsFine;
            int heightCellsFine;
            bool[] rawCoarse;
            byte[] packedFineMask;
            using (var fs = new System.IO.FileStream(absolute, System.IO.FileMode.Open, System.IO.FileAccess.Read))
            using (var br = new System.IO.BinaryReader(fs))
            {
                // Matches WodExactTilemapMaskExporter.WriteExportFile byte-for-byte.
                if (br.ReadUInt32() != 0x44574558)   // "DWEX"
                    throw new System.Exception("Not a WOD exact mask file (magic mismatch).");
                br.ReadUInt16();                      // version
                int coarseSub = br.ReadUInt16();
                fineSub = br.ReadUInt16();
                int fileMapX = br.ReadUInt16();
                int fileMapY = br.ReadUInt16();
                int fileWidthCoarse = br.ReadInt32();
                int fileHeightCoarse = br.ReadInt32();
                widthCellsFine = br.ReadInt32();
                heightCellsFine = br.ReadInt32();
                int targetRows = br.ReadInt32();
                long coarseWater = br.ReadInt64();
                long fineWater = br.ReadInt64();
                int coarseLen = br.ReadInt32();
                int fineLen = br.ReadInt32();
                byte[] coarseBits = br.ReadBytes(coarseLen);
                packedFineMask = br.ReadBytes(fineLen);

                if (coarseSub != SubCellsPerPixel)
                    throw new System.Exception("Exact mask coarse sub-cells (" + coarseSub +
                        ") != builder SubCellsPerPixel (" + SubCellsPerPixel + ").");
                if (fileMapX != mapPixelsX || fileMapY != mapPixelsY ||
                    fileWidthCoarse != widthCells || fileHeightCoarse != heightCells)
                    throw new System.Exception("Exact mask world dimensions do not match this builder.");
                if (targetRows < mapPixelsY)
                    throw new System.Exception("Exact mask export covers only " + targetRows + "/" +
                        mapPixelsY + " rows — a partial (smoke-test) export. Re-run the exporter with " +
                        "'Rows to export' = 0 for the full world.");

                rawCoarse = new bool[widthCells * heightCells];
                for (int i = 0; i < rawCoarse.Length; i++)
                    rawCoarse[i] = GetPackedBit(coarseBits, i);

                Debug.Log("[DeepWaters.Bake] Loaded EXACT WOD masks: coarse " + widthCells + "x" +
                          heightCells + " (water=" + coarseWater + "), fine " + widthCellsFine + "x" +
                          heightCellsFine + " (" + fineSub + "/pixel, water=" + fineWater + ").");
            }

            // Same pipeline as Bake(): ocean connectivity -> prune fine -> chamfer
            // distance -> shore-edge distance -> quantize -> write.
            EditorUtility.DisplayProgressBar("Bake from exact masks", "Ocean connectivity...", 0.30f);
            bool[] coarseMask = BuildOceanConnectedWaterMask(rawCoarse, widthCells, heightCells);
            PruneFineMaskToCoarseOcean(packedFineMask, coarseMask,
                widthCells, heightCells, widthCellsFine, heightCellsFine);

            float tileWorldSize = MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale;
            float cellWidth = tileWorldSize / SubCellsPerPixel;

            EditorUtility.DisplayProgressBar("Bake from exact masks", "Distance transform...", 0.55f);
            float[] distance = SeedDistanceGrid(coarseMask);
            ChamferDistance(distance, widthCells, heightCells, cellWidth);

            EditorUtility.DisplayProgressBar("Bake from exact masks", "Shore-edge distance...", 0.75f);
            float[] edgeDistance = BuildEdgeDistance(packedFineMask,
                widthCells, heightCells, widthCellsFine, heightCellsFine, cellWidth);

            EditorUtility.DisplayProgressBar("Bake from exact masks", "Quantize + write...", 0.90f);
            byte[] distanceBytes = Quantize(distance, DistanceScaleMeters);
            byte[] edgeBytes = Quantize(edgeDistance, DistanceScaleMeters);
            byte[] packedCoarseMask = PackWaterMask(coarseMask);

            WriteBakeFile(OutputPath, distanceBytes, packedCoarseMask, packedFineMask, edgeBytes,
                SubCellsPerPixel, SubCellsPerPixel, fineSub, mapPixelsX, mapPixelsY, DistanceScaleMeters);

            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
            Debug.Log("[DeepWaters.Bake] Wrote " + OutputPath + " from EXACT WOD masks (fine " +
                      fineSub + "/pixel). Rebuild the .dfmod to ship it.");
            EditorUtility.DisplayDialog("Bake from exact masks complete",
                "Wrote DistanceBake.bytes from the exact WOD tilemap masks (fine " + fineSub +
                "/pixel). Rebuild the .dfmod to ship it.", "OK");
        }

        private static void ValidateFineResolution(int fineSubCellsPerPixel)
        {
            if (fineSubCellsPerPixel <= 0)
                throw new System.Exception("Fine mask resolution must be positive.");

            long fineCells = (long)MapsFile.MaxMapPixelX * MapsFile.MaxMapPixelY *
                             fineSubCellsPerPixel * fineSubCellsPerPixel;
            long fineMaskBytes = (fineCells + 7) / 8;
            if (fineMaskBytes > int.MaxValue)
            {
                throw new System.Exception("Fine mask resolution " + fineSubCellsPerPixel + "x" +
                                           fineSubCellsPerPixel + " is too large for a single packed byte array (" +
                                           fineMaskBytes + " bytes).");
            }
        }

        private static bool IsMonobeliskInterestingTerrainSampler(ITerrainSampler sampler)
        {
            return sampler != null && sampler.GetType().FullName == "Monobelisk.InterestingTerrainSampler";
        }

        // WOD's startup compute pass replaces WoodsFileReader.Buffer with a
        // 0..255 encoding of normalized 0..5000m terrain. Feeding that buffer
        // through DFU's DefaultTerrainSampler misreads most water as dry land
        // because the vanilla sampler expects original WOODS.WLD byte scales.
        // Instead, classify the current buffer directly using the same map-
        // height water threshold other WOD-aware water systems use (<= 6).
        private static void SampleWaterGridsFromInterestingTerrainHeightBuffer(
            ITerrainSampler runtimeSampler,
            int widthCells, int heightCells,
            int widthCellsFine, int heightCellsFine,
            int fineSubCellsPerPixel,
            out bool[] waterCoarse,
            out byte[] packedWaterFine,
            out long fineWaterCount)
        {
            DaggerfallUnity dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.ContentReader == null || dfu.ContentReader.WoodsFileReader == null)
                throw new System.Exception("DaggerfallUnity WOODS.WLD reader is not initialized.");

            byte[] heightBuffer = dfu.ContentReader.WoodsFileReader.Buffer;
            int expectedLength = WoodsFile.MapWidth * WoodsFile.MapHeight;
            if (heightBuffer == null || heightBuffer.Length < expectedLength)
                throw new System.Exception("WOODS.WLD height buffer is missing or truncated.");

            int mapPixelsX = MapsFile.MaxMapPixelX;
            int mapPixelsY = MapsFile.MaxMapPixelY;
            waterCoarse = new bool[widthCells * heightCells];
            long fineCells = (long)widthCellsFine * heightCellsFine;
            packedWaterFine = new byte[(int)((fineCells + 7) / 8)];
            fineWaterCount = 0;

            int sourceWaterPixels = 0;
            for (int y = 0; y < mapPixelsY; y++)
            {
                int row = y * WoodsFile.MapWidth;
                for (int x = 0; x < mapPixelsX; x++)
                {
                    if (heightBuffer[row + x] <= InterestingTerrainWaterMapHeight)
                        sourceWaterPixels++;
                }
            }

            Debug.Log("[DeepWaters.Bake] WOD height-buffer classification: source water pixels=" +
                      sourceWaterPixels + "/" + (mapPixelsX * mapPixelsY) + " (" +
                      (100.0 * sourceWaterPixels / (mapPixelsX * mapPixelsY)).ToString("F1") +
                      "%), threshold <= " + InterestingTerrainWaterMapHeight + ".");

            int hybridCandidateCount = 0;
            int hybridSourceShoreCount = 0;
            int hybridLocationCount = 0;
            bool[] hybridCpuCandidates = null;
            int hDim = runtimeSampler != null ? runtimeSampler.HeightmapDimension : 0;
            float[] hybridCpuHeights = null;
            if (UseHybridCpuWodClassifierForBake && hDim > 0)
            {
                hybridCpuCandidates = BuildHybridCpuWodCandidateMap(
                    dfu,
                    heightBuffer,
                    mapPixelsX,
                    mapPixelsY,
                    out hybridCandidateCount,
                    out hybridSourceShoreCount,
                    out hybridLocationCount);
                hybridCpuHeights = new float[hDim * hDim];
                Debug.Log("[DeepWaters.Bake] Hybrid CPU WOD candidate tiles=" +
                          hybridCandidateCount + "/" + (mapPixelsX * mapPixelsY) +
                          " (source shoreline=" + hybridSourceShoreCount +
                          ", water-adjacent locations added=" + hybridLocationCount +
                          "), CPU water threshold=" +
                          HybridCpuWodWaterThresholdNormalized.ToString("F6") + ".");
            }

            int hybridCpuTiles = 0;
            int hybridHeightBufferTiles = 0;
            int hybridCpuFailures = 0;
            string lastHybridCpuFailure = string.Empty;

            for (int my = 0; my < mapPixelsY; my++)
            {
                if ((my & 3) == 0)
                {
                    EditorUtility.DisplayProgressBar("Bake distance field",
                        "Classifying WOD water row " + my + "/" + mapPixelsY,
                        0.02f + 0.55f * my / mapPixelsY);
                }

                for (int mx = 0; mx < mapPixelsX; mx++)
                {
                    bool useHybridCpu = false;
                    int mapIndex = my * mapPixelsX + mx;
                    if (hybridCpuCandidates != null && hybridCpuCandidates[mapIndex])
                    {
                        string cpuNotes;
                        useHybridCpu = WodCpuWaterClassifierV0.TryBuildHeightsGpuTextureV3(
                            mx, my, hDim, hybridCpuHeights, out cpuNotes);
                        if (useHybridCpu)
                        {
                            hybridCpuTiles++;
                        }
                        else
                        {
                            hybridCpuFailures++;
                            lastHybridCpuFailure = cpuNotes;
                            if (hybridCpuFailures <= 5)
                            {
                                Debug.LogWarning("[DeepWaters.Bake] Hybrid CPU WOD failed for (" +
                                                 mx + "," + my + "): " + cpuNotes +
                                                 ". Falling back to WOD height buffer.");
                            }
                        }
                    }

                    if (!useHybridCpu)
                        hybridHeightBufferTiles++;

                    for (int subY = 0; subY < SubCellsPerPixel; subY++)
                    for (int subX = 0; subX < SubCellsPerPixel; subX++)
                    {
                        bool isWater = useHybridCpu
                            ? IsCpuWodCoarseSubCellWater(
                                hybridCpuHeights, hDim, subX, subY, SubCellsPerPixel)
                            : IsHeightBufferSubCellWater(
                                heightBuffer, mx, my, subX, subY, SubCellsPerPixel);

                        int coarseIdx = (my * SubCellsPerPixel + subY) * widthCells +
                                        (mx * SubCellsPerPixel + subX);
                        waterCoarse[coarseIdx] = isWater;
                    }

                    for (int subY = 0; subY < fineSubCellsPerPixel; subY++)
                    for (int subX = 0; subX < fineSubCellsPerPixel; subX++)
                    {
                        bool isWater = useHybridCpu
                            ? IsCpuWodFineSubCellWater(
                                hybridCpuHeights, hDim, subX, subY, fineSubCellsPerPixel)
                            : IsHeightBufferSubCellWater(
                                heightBuffer, mx, my, subX, subY, fineSubCellsPerPixel);

                        if (!isWater)
                            continue;

                        int fineIdx = (my * fineSubCellsPerPixel + subY) * widthCellsFine +
                                      (mx * fineSubCellsPerPixel + subX);
                        SetPackedBit(packedWaterFine, fineIdx);
                        fineWaterCount++;
                    }
                }
            }

            if (hybridCpuCandidates != null)
            {
                Debug.Log("[DeepWaters.Bake] Hybrid CPU WOD classification: cpuTiles=" +
                          hybridCpuTiles + ", heightBufferTiles=" + hybridHeightBufferTiles +
                          ", cpuFailures=" + hybridCpuFailures +
                          (hybridCpuFailures > 0 ? ", lastFailure=" + lastHybridCpuFailure : string.Empty) +
                          ".");
            }
        }

        private static bool IsHeightBufferSubCellWater(
            byte[] heightBuffer,
            int mapPixelX,
            int mapPixelY,
            int subX,
            int subY,
            int subCellsPerPixel)
        {
            float globalX = mapPixelX + (subX + 0.5f) / subCellsPerPixel;
            float globalY = mapPixelY + (subY + 0.5f) / subCellsPerPixel;
            return SampleInterestingTerrainHeightBuffer(heightBuffer, globalX, globalY) <=
                   InterestingTerrainWaterMapThreshold;
        }

        private static bool IsCpuWodCoarseSubCellWater(
            float[] heights,
            int hDim,
            int subX,
            int subY,
            int subCellsPerPixel)
        {
            int rowStart;
            int rowEnd;
            int colStart;
            int colEnd;
            GetHeightmapSampleRange(hDim, subX, subY, subCellsPerPixel,
                out rowStart, out rowEnd, out colStart, out colEnd);

            int total = 0;
            int waterSamples = 0;
            for (int hy = rowStart; hy <= rowEnd; hy++)
            {
                for (int hx = colStart; hx <= colEnd; hx++)
                {
                    if (heights[hy + hx * hDim] <= HybridCpuWodWaterThresholdNormalized)
                        waterSamples++;
                    total++;
                }
            }

            return total > 0 && waterSamples * 2 >= total;
        }

        private static bool IsCpuWodFineSubCellWater(
            float[] heights,
            int hDim,
            int subX,
            int subY,
            int subCellsPerPixel)
        {
            int rowStart;
            int rowEnd;
            int colStart;
            int colEnd;
            GetHeightmapSampleRange(hDim, subX, subY, subCellsPerPixel,
                out rowStart, out rowEnd, out colStart, out colEnd);

            for (int hy = rowStart; hy <= rowEnd; hy++)
            {
                for (int hx = colStart; hx <= colEnd; hx++)
                {
                    if (heights[hy + hx * hDim] <= HybridCpuWodWaterThresholdNormalized)
                        return true;
                }
            }

            return false;
        }

        private static void GetHeightmapSampleRange(
            int hDim,
            int subX,
            int subY,
            int subCellsPerPixel,
            out int rowStart,
            out int rowEnd,
            out int colStart,
            out int colEnd)
        {
            int invSubY = subCellsPerPixel - 1 - subY;
            rowStart = invSubY * (hDim - 1) / subCellsPerPixel;
            rowEnd = (invSubY + 1) * (hDim - 1) / subCellsPerPixel;
            colStart = subX * (hDim - 1) / subCellsPerPixel;
            colEnd = (subX + 1) * (hDim - 1) / subCellsPerPixel;
            if (rowEnd > hDim - 1) rowEnd = hDim - 1;
            if (colEnd > hDim - 1) colEnd = hDim - 1;
            if (rowEnd < rowStart) rowEnd = rowStart;
            if (colEnd < colStart) colEnd = colStart;
        }

        private static bool[] BuildHybridCpuWodCandidateMap(
            DaggerfallUnity dfu,
            byte[] heightBuffer,
            int mapPixelsX,
            int mapPixelsY,
            out int totalCandidates,
            out int sourceShoreCandidates,
            out int locationAddedCandidates)
        {
            bool[] sourceWater = new bool[mapPixelsX * mapPixelsY];
            for (int y = 0; y < mapPixelsY; y++)
            {
                int sourceRow = y * WoodsFile.MapWidth;
                int row = y * mapPixelsX;
                for (int x = 0; x < mapPixelsX; x++)
                    sourceWater[row + x] = heightBuffer[sourceRow + x] <= InterestingTerrainWaterMapHeight;
            }

            bool[] candidates = new bool[sourceWater.Length];
            for (int y = 0; y < mapPixelsY; y++)
            {
                for (int x = 0; x < mapPixelsX; x++)
                {
                    int i = y * mapPixelsX + x;
                    if (x + 1 < mapPixelsX && sourceWater[i] != sourceWater[i + 1])
                    {
                        MarkCandidateRect(candidates, mapPixelsX, mapPixelsY,
                            x - HybridCpuSourceShoreRadius,
                            y - HybridCpuSourceShoreRadius,
                            x + 1 + HybridCpuSourceShoreRadius,
                            y + HybridCpuSourceShoreRadius);
                    }
                    if (y + 1 < mapPixelsY && sourceWater[i] != sourceWater[i + mapPixelsX])
                    {
                        MarkCandidateRect(candidates, mapPixelsX, mapPixelsY,
                            x - HybridCpuSourceShoreRadius,
                            y - HybridCpuSourceShoreRadius,
                            x + HybridCpuSourceShoreRadius,
                            y + 1 + HybridCpuSourceShoreRadius);
                    }
                }
            }

            sourceShoreCandidates = CountTrue(candidates);
            locationAddedCandidates = 0;

            if (dfu != null && dfu.ContentReader != null)
            {
                int diffWidth = mapPixelsX + 1;
                int[] locationDiff = new int[(mapPixelsX + 1) * (mapPixelsY + 1)];
                int waterAdjacentLocations = 0;

                for (int y = 0; y < mapPixelsY; y++)
                {
                    if ((y & 7) == 0)
                    {
                        EditorUtility.DisplayProgressBar("Bake distance field",
                            "Building hybrid WOD CPU candidate map row " + y + "/" + mapPixelsY,
                            0.01f * y / mapPixelsY);
                    }

                    for (int x = 0; x < mapPixelsX; x++)
                    {
                        try
                        {
                            MapPixelData mapPixelData = TerrainHelper.GetMapPixelData(
                                dfu.ContentReader, x, y);
                            if (!mapPixelData.hasLocation)
                                continue;
                            if (!HasSourceWaterInRadius(sourceWater, mapPixelsX, mapPixelsY,
                                    x, y, HybridCpuLocationWaterProbeRadius))
                                continue;

                            waterAdjacentLocations++;
                            AddCandidateDiffRect(locationDiff, diffWidth, mapPixelsX, mapPixelsY,
                                x - HybridCpuLocationInfluenceRadius,
                                y - HybridCpuLocationInfluenceRadius,
                                x + HybridCpuLocationInfluenceRadius + 1,
                                y + HybridCpuLocationInfluenceRadius + 1);
                        }
                        catch
                        {
                            // TerrainHelper can be fussy at world edges in some
                            // modded setups. A missed candidate just falls back
                            // to the cheap height-buffer path.
                        }
                    }
                }

                locationAddedCandidates = ApplyCandidateDiff(locationDiff, diffWidth,
                    candidates, mapPixelsX, mapPixelsY);
                Debug.Log("[DeepWaters.Bake] Hybrid CPU WOD water-adjacent locations=" +
                          waterAdjacentLocations + ".");
            }

            totalCandidates = CountTrue(candidates);
            return candidates;
        }

        private static bool HasSourceWaterInRadius(
            bool[] sourceWater,
            int width,
            int height,
            int centerX,
            int centerY,
            int radius)
        {
            int minX = Mathf.Max(0, centerX - radius);
            int minY = Mathf.Max(0, centerY - radius);
            int maxX = Mathf.Min(width - 1, centerX + radius);
            int maxY = Mathf.Min(height - 1, centerY + radius);
            for (int y = minY; y <= maxY; y++)
            {
                int row = y * width;
                for (int x = minX; x <= maxX; x++)
                {
                    if (sourceWater[row + x])
                        return true;
                }
            }

            return false;
        }

        private static void MarkCandidateRect(
            bool[] candidates,
            int width,
            int height,
            int minX,
            int minY,
            int maxXInclusive,
            int maxYInclusive)
        {
            minX = Mathf.Max(0, minX);
            minY = Mathf.Max(0, minY);
            maxXInclusive = Mathf.Min(width - 1, maxXInclusive);
            maxYInclusive = Mathf.Min(height - 1, maxYInclusive);
            for (int y = minY; y <= maxYInclusive; y++)
            {
                int row = y * width;
                for (int x = minX; x <= maxXInclusive; x++)
                    candidates[row + x] = true;
            }
        }

        private static void AddCandidateDiffRect(
            int[] diff,
            int diffWidth,
            int width,
            int height,
            int minX,
            int minY,
            int maxXExclusive,
            int maxYExclusive)
        {
            minX = Mathf.Clamp(minX, 0, width);
            minY = Mathf.Clamp(minY, 0, height);
            maxXExclusive = Mathf.Clamp(maxXExclusive, 0, width);
            maxYExclusive = Mathf.Clamp(maxYExclusive, 0, height);
            if (minX >= maxXExclusive || minY >= maxYExclusive)
                return;

            diff[minY * diffWidth + minX]++;
            diff[minY * diffWidth + maxXExclusive]--;
            diff[maxYExclusive * diffWidth + minX]--;
            diff[maxYExclusive * diffWidth + maxXExclusive]++;
        }

        private static int ApplyCandidateDiff(
            int[] diff,
            int diffWidth,
            bool[] candidates,
            int width,
            int height)
        {
            int added = 0;
            int[] columnSums = new int[width];
            for (int y = 0; y < height; y++)
            {
                int rowRunning = 0;
                int row = y * width;
                int diffRow = y * diffWidth;
                for (int x = 0; x < width; x++)
                {
                    rowRunning += diff[diffRow + x];
                    columnSums[x] += rowRunning;
                    if (columnSums[x] <= 0)
                        continue;

                    int i = row + x;
                    if (candidates[i])
                        continue;

                    candidates[i] = true;
                    added++;
                }
            }

            return added;
        }

        private static int CountTrue(bool[] values)
        {
            int count = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i])
                    count++;
            }
            return count;
        }

        private static float SampleInterestingTerrainHeightBuffer(byte[] heightBuffer, float globalX, float globalY)
        {
            // Treat each WOODS byte as the centre of its map pixel. This keeps
            // the middle of a water map pixel anchored to that byte value while
            // still giving sub-cell shoreline interpolation at the edges.
            float px = Mathf.Clamp(globalX - 0.5f, 0f, WoodsFile.MapWidth - 1f);
            float py = Mathf.Clamp(globalY - 0.5f, 0f, WoodsFile.MapHeight - 1f);

            int x0 = Mathf.FloorToInt(px);
            int y0 = Mathf.FloorToInt(py);
            int x1 = Mathf.Min(x0 + 1, WoodsFile.MapWidth - 1);
            int y1 = Mathf.Min(y0 + 1, WoodsFile.MapHeight - 1);
            float tx = px - x0;
            float ty = py - y0;

            int row0 = y0 * WoodsFile.MapWidth;
            int row1 = y1 * WoodsFile.MapWidth;
            float h00 = heightBuffer[row0 + x0];
            float h10 = heightBuffer[row0 + x1];
            float h01 = heightBuffer[row1 + x0];
            float h11 = heightBuffer[row1 + x1];

            float hx0 = Mathf.Lerp(h00, h10, tx);
            float hx1 = Mathf.Lerp(h01, h11, tx);
            return Mathf.Lerp(hx0, hx1, ty);
        }

        // For each map pixel, run the selected bake TerrainSampler to
        // get the heightmap used for water classification. Aggregate the
        // heightmap into BOTH bake masks in one pass:
        //   - waterCoarse (SubCellsPerPixel): strict majority criterion;
        //     drives distance-field BFS and inland-lake filtering.
        //   - waterFine (SubCellsPerPixelFine): permissive any-water criterion;
        //     drives runtime cell-level carving so adjacent tiles agree at
        //     map-pixel boundaries by reading the same global data.
        // Slow (one full sampler invocation per of the world's 1000×500 map
        // pixels), so GPU-backed runtime samplers are intentionally avoided.
        private static void SampleWaterGridsUsingSampler(
            ITerrainSampler sampler,
            int widthCells, int heightCells,
            int widthCellsFine, int heightCellsFine,
            int fineSubCellsPerPixel,
            out bool[] waterCoarse,
            out byte[] packedWaterFine,
            out long fineWaterCount)
        {
            int hDim = sampler.HeightmapDimension;
            float oceanThresholdNormalized = sampler.OceanElevation / sampler.MaxTerrainHeight;
            float waterThresholdNormalized = oceanThresholdNormalized + WaterSampleHeadroomNormalized;

            int mapPixelsX = MapsFile.MaxMapPixelX;
            int mapPixelsY = MapsFile.MaxMapPixelY;
            waterCoarse = new bool[widthCells * heightCells];
            long fineCells = (long)widthCellsFine * heightCellsFine;
            packedWaterFine = new byte[(int)((fineCells + 7) / 8)];
            fineWaterCount = 0;
            float[] heightsBuffer = new float[hDim * hDim];
            DaggerfallUnity dfu = DaggerfallUnity.Instance;

            // Monobelisk's Interesting Terrains compute shader produces TWO
            // outputs per map pixel: a heightmap AND a per-sample tilemap
            // that classifies each cell as WATER / DIRT / GRASS / STONE based
            // on `w.land` (which IT also uses to drive the texture painter).
            // The tilemap is exactly the visual-water source-of-truth the
            // player sees; deriving water from the heightmap drops every
            // shoreline beach because IT clamps beach heights ~4 m above
            // ocean threshold which exceeds our height epsilon. Reading the
            // tilemap directly aligns the bake with what IT paints.
            //
            // We resolve `tileDataCache` plus its `Get(int, int)` method via
            // reflection so the editor binary stays compileable on a vanilla
            // DFU project (no compile-time dependency on wod-terrain). Both
            // resolved fields stay null on vanilla DFU — the loop then falls
            // back to the height-based classification path below.
            //
            // `Get` REMOVES the entry from the cache as it returns it, so we
            // get the drain-on-read behaviour the old `Clear()` cycle was
            // simulating for free. The old Clear path is still resolved as
            // a safety net in case Get is missing on an older wod-terrain
            // build.
            object monobeliskCacheInstance = null;
            System.Reflection.MethodInfo monobeliskCacheGet = null;
            System.Reflection.MethodInfo monobeliskCacheClear = null;
            if (IsMonobeliskInterestingTerrainSampler(sampler))
            {
                ResolveMonobeliskCache(out monobeliskCacheInstance,
                    out monobeliskCacheGet, out monobeliskCacheClear);
            }
            int hRes = MapsFile.WorldMapTileDim + 1;
            object[] getArgs = new object[2];

            Debug.Log("[DeepWaters.Bake] Classification path: " +
                      (monobeliskCacheGet != null
                          ? "IT tilemap (WATER cells)"
                          : "heightmap threshold (" + waterThresholdNormalized.ToString("F5") +
                            " normalized = " + (waterThresholdNormalized * sampler.MaxTerrainHeight).ToString("F2") + " m)"));
            int pixelsWithTilemap = 0;
            int pixelsWithoutTilemap = 0;

            for (int my = 0; my < mapPixelsY; my++)
            {
                if ((my & 3) == 0)
                {
                    // Pace the tight IT-GPU dispatch loop so it doesn't exhaust /
                    // remove the device (the v0.55.52 DXGI_DEVICE_REMOVED crash):
                    // flush the GPU command queue every few rows, and do a heavier
                    // GPU+managed resource sweep (and a log line, so a crash shows
                    // exactly how far it got) less often.
                    GL.Flush();
                    if ((my & 31) == 0)
                    {
                        EditorUtility.UnloadUnusedAssetsImmediate();
                        System.GC.Collect();
                        Debug.Log("[DeepWaters.Bake] Sampled to row " + my + "/" + mapPixelsY + "...");
                    }
                    if (EditorUtility.DisplayCancelableProgressBar("Bake distance field",
                        "Sampling map pixels row " + my + "/" + mapPixelsY,
                        0.02f + 0.55f * my / mapPixelsY))
                        throw new System.OperationCanceledException("Bake canceled by user.");
                }

                for (int mx = 0; mx < mapPixelsX; mx++)
                {
                    GenerateHeightmapForMapPixel(dfu, sampler, mx, my, hDim, heightsBuffer);

                    // Pull IT's per-sample tilemap for this map pixel right
                    // after the sampler finishes. The same Dispatch that
                    // populated heightsBuffer also wrote `tilemapData[]`,
                    // and BufferIO.ProcessBufferValuesAndDispose copied that
                    // into tileDataCache keyed by (mx, my). On vanilla DFU
                    // (no reflection target), tileData stays null and we
                    // fall back to the height-only classification.
                    byte[] tileData = null;
                    if (monobeliskCacheGet != null && monobeliskCacheInstance != null)
                    {
                        try
                        {
                            getArgs[0] = mx;
                            getArgs[1] = my;
                            tileData = monobeliskCacheGet.Invoke(monobeliskCacheInstance, getArgs) as byte[];
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning("[DeepWaters.Bake] tileDataCache.Get failed for (" +
                                             mx + "," + my + "): " + ex.Message + ". Falling back to heights.");
                            monobeliskCacheGet = null; // stop trying
                        }
                    }

                    if (tileData != null) pixelsWithTilemap++;
                    else pixelsWithoutTilemap++;

                    // Aggregate into COARSE mask (strict majority criterion).
                    for (int subY = 0; subY < SubCellsPerPixel; subY++)
                    for (int subX = 0; subX < SubCellsPerPixel; subX++)
                    {
                        // Bake row 0 is world NORTH while the DFU heightmap's
                        // row 0 is world SOUTH (rows increase with worldZ).
                        // Flip subY before mapping to heightmap rows so a
                        // bake cell ends up over the same patch of ground
                        // that the streamed mesh renders.
                        int invSubY = SubCellsPerPixel - 1 - subY;
                        int dfuRowStart = invSubY * (hDim - 1) / SubCellsPerPixel;
                        int dfuRowEnd = (invSubY + 1) * (hDim - 1) / SubCellsPerPixel;
                        int dfuColStart = subX * (hDim - 1) / SubCellsPerPixel;
                        int dfuColEnd = (subX + 1) * (hDim - 1) / SubCellsPerPixel;
                        if (dfuRowEnd > hDim - 1) dfuRowEnd = hDim - 1;
                        if (dfuColEnd > hDim - 1) dfuColEnd = hDim - 1;
                        if (dfuRowEnd < dfuRowStart) dfuRowEnd = dfuRowStart;
                        if (dfuColEnd < dfuColStart) dfuColEnd = dfuColStart;

                        bool isWater;
                        if (tileData != null)
                        {
                            int total = 0;
                            int waterSamples = 0;
                            for (int hy = dfuRowStart; hy <= dfuRowEnd; hy++)
                            {
                                int rowBase = hy * hRes;
                                for (int hx = dfuColStart; hx <= dfuColEnd; hx++)
                                {
                                    int idx = rowBase + hx;
                                    if (idx >= 0 && idx < tileData.Length && tileData[idx] == 0)
                                        waterSamples++;
                                    total++;
                                }
                            }
                            // 50% majority on IT WATER samples.
                            isWater = total > 0 && waterSamples * 2 >= total;
                        }
                        else
                        {
                            int total = 0;
                            int waterSamples = 0;
                            for (int hy = dfuRowStart; hy <= dfuRowEnd; hy++)
                            {
                                for (int hx = dfuColStart; hx <= dfuColEnd; hx++)
                                {
                                    float h = heightsBuffer[hy + hx * hDim];
                                    if (h <= waterThresholdNormalized)
                                        waterSamples++;
                                    total++;
                                }
                            }
                            isWater = total > 0 &&
                                waterSamples >= Mathf.CeilToInt(total * RequiredWaterCoverage);
                        }

                        int coarseIdx = (my * SubCellsPerPixel + subY) * widthCells +
                                        (mx * SubCellsPerPixel + subX);
                        waterCoarse[coarseIdx] = isWater;
                    }

                    // Aggregate into FINE mask (any-water criterion). One
                    // water sample anywhere in the sub-cell is enough — this
                    // is the criterion that matches what WaterSurfaceManager
                    // and ComputeHoleMask's per-corner check both look for.
                    // The fine resolution is menu-selectable. At the default
                    // 64x64, each fine sub-cell covers ~2x2 of the 129x129
                    // heightmap; at the diagnostic 32x32 setting it covers
                    // ~4x4. Either way, one water sample anywhere in that
                    // sub-cell is enough to mark it carved.
                    for (int subY = 0; subY < fineSubCellsPerPixel; subY++)
                    for (int subX = 0; subX < fineSubCellsPerPixel; subX++)
                    {
                        int invSubY = fineSubCellsPerPixel - 1 - subY;
                        int dfuRowStart = invSubY * (hDim - 1) / fineSubCellsPerPixel;
                        int dfuRowEnd = (invSubY + 1) * (hDim - 1) / fineSubCellsPerPixel;
                        int dfuColStart = subX * (hDim - 1) / fineSubCellsPerPixel;
                        int dfuColEnd = (subX + 1) * (hDim - 1) / fineSubCellsPerPixel;
                        if (dfuRowEnd > hDim - 1) dfuRowEnd = hDim - 1;
                        if (dfuColEnd > hDim - 1) dfuColEnd = hDim - 1;
                        if (dfuRowEnd < dfuRowStart) dfuRowEnd = dfuRowStart;
                        if (dfuColEnd < dfuColStart) dfuColEnd = dfuColStart;

                        bool isWater = false;
                        if (tileData != null)
                        {
                            for (int hy = dfuRowStart; hy <= dfuRowEnd && !isWater; hy++)
                            {
                                int rowBase = hy * hRes;
                                for (int hx = dfuColStart; hx <= dfuColEnd; hx++)
                                {
                                    int idx = rowBase + hx;
                                    if (idx >= 0 && idx < tileData.Length && tileData[idx] == 0)
                                    {
                                        isWater = true;
                                        break;
                                    }
                                }
                            }
                        }
                        else
                        {
                            for (int hy = dfuRowStart; hy <= dfuRowEnd && !isWater; hy++)
                            {
                                for (int hx = dfuColStart; hx <= dfuColEnd; hx++)
                                {
                                    float h = heightsBuffer[hy + hx * hDim];
                                    if (h <= waterThresholdNormalized)
                                    {
                                        isWater = true;
                                        break;
                                    }
                                }
                            }
                        }

                        int fineIdx = (my * fineSubCellsPerPixel + subY) * widthCellsFine +
                                      (mx * fineSubCellsPerPixel + subX);
                        if (isWater)
                        {
                            SetPackedBit(packedWaterFine, fineIdx);
                            fineWaterCount++;
                        }
                    }
                }

                // If we don't have a Get path the cache still grows per
                // dispatch — drain it once per row via Clear() so it never
                // holds more than a stripe of byte[] payloads in memory at
                // once. With Get the cache self-drains as we read, so this
                // is a no-op safety belt only.
                if (monobeliskCacheGet == null &&
                    monobeliskCacheClear != null && monobeliskCacheInstance != null)
                {
                    try { monobeliskCacheClear.Invoke(monobeliskCacheInstance, null); }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning("[DeepWaters.Bake] Failed to drain Monobelisk cache: " + ex.Message);
                        monobeliskCacheClear = null; // stop trying for the rest of the bake
                    }
                }
            }

            Debug.Log("[DeepWaters.Bake] Tilemap coverage: " + pixelsWithTilemap +
                      " map pixels classified via IT tilemap, " + pixelsWithoutTilemap +
                      " via heightmap fallback.");
        }

        // Resolve Monobelisk.InterestingTerrains.tileDataCache plus its
        // Get(int, int) and Clear() instance methods via reflection so the
        // bake stays compileable on a vanilla DFU project (no compile-time
        // dependency on wod-terrain). All three out parameters stay null on
        // vanilla DFU — the caller falls back to the height-only path and
        // skips both the tilemap classification and the cache drain.
        private static void ResolveMonobeliskCache(
            out object cacheInstance,
            out System.Reflection.MethodInfo getMethod,
            out System.Reflection.MethodInfo clearMethod)
        {
            cacheInstance = null;
            getMethod = null;
            clearMethod = null;

            System.Reflection.FieldInfo cacheField = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = asm.GetType("Monobelisk.InterestingTerrains", false);
                if (t == null) continue;
                var field = t.GetField("tileDataCache",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (field == null) continue;
                cacheField = field;
                break;
            }

            if (cacheField == null) return;

            object instance = cacheField.GetValue(null);
            if (instance == null) return;
            cacheInstance = instance;

            // Resolve Get(int, int). Used by the bake to pull the per-pixel
            // tilemap byte[] right after the sampler dispatch — IT's Get
            // also removes the entry from the cache as it returns, so we
            // don't need a separate Clear() drain when this method is
            // available.
            getMethod = instance.GetType().GetMethod("Get",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null,
                new System.Type[] { typeof(int), typeof(int) },
                null);
            if (getMethod == null)
            {
                Debug.LogWarning("[DeepWaters.Bake] Monobelisk.InterestingTerrains.tileDataCache " +
                                 "is missing a Get(int, int) method — falling back to height-only " +
                                 "classification (beach tiles will be land). Update wod-terrain to " +
                                 "expose Get().");
            }

            // Resolve Clear() as a fallback drain for old wod-terrain builds
            // that don't have Get. With Get the cache self-drains on read,
            // so Clear is only invoked when Get is missing.
            clearMethod = instance.GetType().GetMethod("Clear",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (clearMethod == null)
            {
                Debug.LogWarning("[DeepWaters.Bake] Monobelisk.InterestingTerrains.tileDataCache " +
                                 "does not expose a public Clear() method — bake will not drain " +
                                 "its cache between rows. Update wod-terrain to the patched " +
                                 "build to add it.");
            }
        }

        // Invoke the sampler's schedule API the way DaggerfallTerrain does at
        // runtime, then copy the resulting NativeArray into a managed buffer
        // we can index without disposal hassle.
        private static void GenerateHeightmapForMapPixel(
            DaggerfallUnity dfu, ITerrainSampler sampler,
            int mapPixelX, int mapPixelY, int hDim, float[] outHeights)
        {
            MapPixelData mapData = TerrainHelper.GetMapPixelData(
                dfu.ContentReader, mapPixelX, mapPixelY);
            mapData.heightmapData = new NativeArray<float>(hDim * hDim, Allocator.TempJob);
            mapData.nativeArrayList = new List<IDisposable>();

            try
            {
                JobHandle handle = sampler.ScheduleGenerateSamplesJob(ref mapData);
                handle.Complete();
                // Drain any pending GPU readbacks before the next dispatch so
                // IT's compute sampler can't pile them up across the tight bake
                // loop and remove the device (DXGI_DEVICE_REMOVED, v0.55.52).
                UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();
                mapData.heightmapData.CopyTo(outHeights);
            }
            finally
            {
                if (mapData.nativeArrayList != null)
                {
                    foreach (IDisposable nativeArray in mapData.nativeArrayList)
                        nativeArray.Dispose();
                }
                if (mapData.heightmapData.IsCreated)
                    mapData.heightmapData.Dispose();
            }
        }

        private static bool[] BuildOceanConnectedWaterMask(bool[] rawWater, int widthCells, int heightCells)
        {
            bool[] connected = new bool[rawWater.Length];
            int[] queue = new int[rawWater.Length];
            int head = 0;
            int tail = 0;
            int rawWaterCount = 0;

            for (int i = 0; i < rawWater.Length; i++)
            {
                if (rawWater[i])
                    rawWaterCount++;
            }

            for (int x = 0; x < widthCells; x++)
            {
                EnqueueOceanSeed(rawWater, connected, queue, ref tail, x);
                EnqueueOceanSeed(rawWater, connected, queue, ref tail, (heightCells - 1) * widthCells + x);
            }

            for (int y = 1; y < heightCells - 1; y++)
            {
                EnqueueOceanSeed(rawWater, connected, queue, ref tail, y * widthCells);
                EnqueueOceanSeed(rawWater, connected, queue, ref tail, y * widthCells + widthCells - 1);
            }

            while (head < tail)
            {
                int index = queue[head++];
                int x = index % widthCells;
                int y = index / widthCells;

                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    int nx = x + dx;
                    int ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= widthCells || ny >= heightCells)
                        continue;

                    EnqueueOceanSeed(rawWater, connected, queue, ref tail, ny * widthCells + nx);
                }

                if ((head & 65535) == 0)
                {
                    EditorUtility.DisplayProgressBar("Bake distance field",
                        "Resolving ocean-connected water " + head + "/" + rawWaterCount,
                        0.35f + 0.05f * Mathf.Clamp01(head / (float)Mathf.Max(1, rawWaterCount)));
                }
            }

            Debug.Log("[DeepWaters.Bake] Ocean connectivity kept " + tail + " of " +
                      rawWaterCount + " water cells; removed " +
                      (rawWaterCount - tail) + " disconnected cells.");
            return connected;
        }

        private static void EnqueueOceanSeed(
            bool[] rawWater,
            bool[] connected,
            int[] queue,
            ref int tail,
            int index)
        {
            if (!rawWater[index] || connected[index])
                return;

            connected[index] = true;
            queue[tail++] = index;
        }

        private static float[] SeedDistanceGrid(bool[] waterMask)
        {
            const float InfiniteDistance = 1e9f;
            float[] dist = new float[waterMask.Length];
            for (int i = 0; i < dist.Length; i++)
                dist[i] = waterMask[i] ? InfiniteDistance : 0f;
            return dist;
        }

        // (Previous releases shipped a hand-rolled DefaultTerrainSampler
        // formula here for direct WoodsFile sampling. The current baker uses
        // a real TerrainSampler for the CPU-safe path, which still benefits
        // from terrain mods that mutate the WOODS.WLD buffer before the bake.)

        // Standard 8-direction chamfer distance transform. Two passes (forward
        // and backward) propagate the minimum land-cell distance through the
        // grid. Land cells already sit at 0; water cells start at +∞ and get
        // pulled down. The chamfer metric (1, √2) approximates Euclidean
        // distance closely enough for our needs at no extra cost.
        private static void ChamferDistance(float[] dist, int w, int h, float cellWidth)
        {
            float straight = cellWidth;
            float diagonal = cellWidth * 1.41421356f;

            // Forward pass: top-left to bottom-right.
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    float cur = dist[idx];
                    if (y > 0)
                    {
                        if (x > 0)
                            cur = Mathf.Min(cur, dist[(y - 1) * w + (x - 1)] + diagonal);
                        cur = Mathf.Min(cur, dist[(y - 1) * w + x] + straight);
                        if (x < w - 1)
                            cur = Mathf.Min(cur, dist[(y - 1) * w + (x + 1)] + diagonal);
                    }
                    if (x > 0)
                        cur = Mathf.Min(cur, dist[idx - 1] + straight);
                    dist[idx] = cur;
                }

                if ((y & 63) == 0)
                {
                    EditorUtility.DisplayProgressBar("Bake distance field",
                        "Forward BFS row " + y + "/" + h,
                        0.40f + 0.20f * y / (float)h);
                }
            }

            // Backward pass: bottom-right to top-left.
            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = w - 1; x >= 0; x--)
                {
                    int idx = y * w + x;
                    float cur = dist[idx];
                    if (y < h - 1)
                    {
                        if (x < w - 1)
                            cur = Mathf.Min(cur, dist[(y + 1) * w + (x + 1)] + diagonal);
                        cur = Mathf.Min(cur, dist[(y + 1) * w + x] + straight);
                        if (x > 0)
                            cur = Mathf.Min(cur, dist[(y + 1) * w + (x - 1)] + diagonal);
                    }
                    if (x < w - 1)
                        cur = Mathf.Min(cur, dist[idx + 1] + straight);
                    dist[idx] = cur;
                }

                if ((y & 63) == 0)
                {
                    EditorUtility.DisplayProgressBar("Bake distance field",
                        "Backward BFS row " + (h - y) + "/" + h,
                        0.60f + 0.25f * (h - y) / (float)h);
                }
            }
        }

        private static byte[] Quantize(float[] distMeters, ushort scaleMeters)
        {
            byte[] bytes = new byte[distMeters.Length];
            float scaleInv = 1f / scaleMeters;
            for (int i = 0; i < distMeters.Length; i++)
            {
                int quant = Mathf.RoundToInt(distMeters[i] * scaleInv);
                if (quant < 0) quant = 0;
                if (quant > 255) quant = 255;
                bytes[i] = (byte)quant;
            }
            return bytes;
        }

        private static byte[] PackWaterMask(bool[] waterMask)
        {
            byte[] packed = new byte[(waterMask.Length + 7) / 8];
            for (int i = 0; i < waterMask.Length; i++)
            {
                if (waterMask[i])
                    packed[i >> 3] |= (byte)(1 << (i & 7));
            }
            return packed;
        }

        private static long PruneFineMaskToCoarseOcean(
            byte[] packedFineMask,
            bool[] coarseOceanMask,
            int coarseWidth,
            int coarseHeight,
            int fineWidth,
            int fineHeight)
        {
            int ratioX = Mathf.Max(1, fineWidth / coarseWidth);
            int ratioY = Mathf.Max(1, fineHeight / coarseHeight);
            long fineCells = (long)fineWidth * fineHeight;
            long kept = 0;

            EditorUtility.DisplayProgressBar("Bake distance field",
                "Pruning fine shoreline mask to ocean-connected water...", 0.38f);

            for (long index = 0; index < fineCells; index++)
            {
                if (!GetPackedBit(packedFineMask, index))
                    continue;

                int x = (int)(index % fineWidth);
                int y = (int)(index / fineWidth);
                int coarseX = Mathf.Clamp(x / ratioX, 0, coarseWidth - 1);
                int coarseY = Mathf.Clamp(y / ratioY, 0, coarseHeight - 1);
                if (!CoarseOceanNear(coarseOceanMask, coarseWidth, coarseHeight, coarseX, coarseY))
                    ClearPackedBit(packedFineMask, index);
                else
                    kept++;

                if ((index & 0xfffff) == 0)
                {
                    EditorUtility.DisplayProgressBar("Bake distance field",
                        "Pruning fine shoreline mask " + index + "/" + fineCells,
                        0.35f + 0.03f * (float)(index / (double)fineCells));
                }
            }

            return kept;
        }

        private static bool CoarseOceanNear(
            bool[] coarseOceanMask,
            int width,
            int height,
            int x,
            int y)
        {
            int r = FineOceanConnectionCoarseRadius;
            for (int cy = y - r; cy <= y + r; cy++)
            {
                if (cy < 0 || cy >= height)
                    continue;

                int row = cy * width;
                for (int cx = x - r; cx <= x + r; cx++)
                {
                    if (cx < 0 || cx >= width)
                        continue;

                    if (coarseOceanMask[row + cx])
                        return true;
                }
            }

            return false;
        }

        private static bool GetPackedBit(byte[] packed, long bitIndex)
        {
            int byteIndex = (int)(bitIndex >> 3);
            return (packed[byteIndex] & (1 << (int)(bitIndex & 7))) != 0;
        }

        private static void SetPackedBit(byte[] packed, long bitIndex)
        {
            int byteIndex = (int)(bitIndex >> 3);
            packed[byteIndex] |= (byte)(1 << (int)(bitIndex & 7));
        }

        private static void ClearPackedBit(byte[] packed, long bitIndex)
        {
            int byteIndex = (int)(bitIndex >> 3);
            packed[byteIndex] &= (byte)~(1 << (int)(bitIndex & 7));
        }

        // Distance-to-carved-edge (coarse). A coarse cell is "interior water"
        // only if ALL of its fine sub-cells are carved water; otherwise it
        // touches a shore edge (or land) — including any small island the
        // coarse majority mask drops — and seeds the chamfer at distance 0.
        // Result: interior-ocean cells get their distance to the nearest shore
        // edge, so the seabed shelf can rise to meet every edge.
        private static float[] BuildEdgeDistance(byte[] packedFineMask,
            int widthCells, int heightCells, int widthCellsFine, int heightCellsFine, float cellWidth)
        {
            int ratioX = Mathf.Max(1, widthCellsFine / widthCells);
            int ratioY = Mathf.Max(1, heightCellsFine / heightCells);
            const float infinite = 1e9f;
            float[] dist = new float[widthCells * heightCells];

            for (int cy = 0; cy < heightCells; cy++)
            {
                for (int cx = 0; cx < widthCells; cx++)
                {
                    bool allFineWater = true;
                    for (int sy = 0; sy < ratioY && allFineWater; sy++)
                    {
                        long fy = (long)cy * ratioY + sy;
                        for (int sx = 0; sx < ratioX; sx++)
                        {
                            long fx = (long)cx * ratioX + sx;
                            long fineIdx = fy * widthCellsFine + fx;
                            if (!GetPackedBit(packedFineMask, fineIdx))
                            {
                                allFineWater = false;
                                break;
                            }
                        }
                    }
                    dist[cy * widthCells + cx] = allFineWater ? infinite : 0f;
                }
            }

            ChamferDistance(dist, widthCells, heightCells, cellWidth);
            return dist;
        }

        private static void WriteBakeFile(string path,
            byte[] cellBytes,
            byte[] coarseMaskBytes,
            byte[] fineMaskBytes,
            byte[] edgeBytes,
            int subCellsX, int subCellsY,
            int fineSubCellsPerPixel,
            int mapPixelsX, int mapPixelsY,
            ushort distanceScaleMeters)
        {
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                DeepWaterDistanceBake.WriteHeaderV4(bw,
                    subCellsX, subCellsY, mapPixelsX, mapPixelsY,
                    distanceScaleMeters,
                    (ushort)fineSubCellsPerPixel);
                bw.Write(cellBytes);
                bw.Write(coarseMaskBytes);
                bw.Write(fineMaskBytes);
                bw.Write(edgeBytes);
            }
        }
    }
}
#endif
