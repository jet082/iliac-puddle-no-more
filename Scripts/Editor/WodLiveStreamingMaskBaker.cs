// Project:         Iliac Puddle No More
// License:         MIT

#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using UnityEditor;
using UnityEngine;

namespace DeepWaters.Editor
{
    /// <summary>
    /// Whole-world water-mask baker driven by the LIVE streaming world.
    ///
    /// The offline exporter (WodExactTilemapMaskExporter) runs IT's sampler per
    /// map pixel in isolation, which can disagree with the live game's terrain at
    /// tile seams (the live game's settled heightmap is the source of truth the
    /// carve/column/depth all read). This baker instead TELEPORTS the player
    /// across the world, lets each region fully load + settle, and reads the live
    /// DaggerfallTerrain heightmaps + tilemaps — so the bake matches exactly what
    /// the player walks on, everywhere, including seams.
    ///
    /// It writes the SAME WodExactWaterMasks.bytes format the offline exporter
    /// does, so the existing pipeline is unchanged:
    ///   1. (Play mode, game running) Tools > Deep Waters > Diagnostics > Live
    ///      Streaming Mask Baker > Start.  (Long — hours. Checkpoints to disk so a
    ///      crash resumes instead of restarting.)
    ///   2. Tools > Deep Waters > Bake Distance Field from WOD Exact Masks.
    ///   3. Deep Waters > Build Windows Mod.
    ///
    /// Must run in PLAY MODE with a save loaded (streaming world active).
    /// </summary>
    public class WodLiveStreamingMaskBaker : EditorWindow
    {
        private const string OutputPath =
            "Assets/Game/Mods/deep-waters/Diagnostics/WodExactWaterMasks.bytes";
        private const string CheckpointPath =
            "Assets/Game/Mods/deep-waters/Diagnostics/WodLiveBakeCheckpoint.bytes";
        private const uint Magic = 0x44574558;       // "DWEX" — matches the offline exporter
        private const uint CheckpointMagic = 0x44574C42; // "DWLB"
        private const ushort Version = 1;
        private const int CoarseSubCellsPerPixel = 8;

        public enum BakeMode { FullWorld, ShorelineTargeted }

        // Settings.
        private BakeMode mode = BakeMode.FullWorld;
        private int fineSubCellsPerPixel = 64;
        // How many tiles in from the loaded-ring edge to trust as fully settled.
        private int readMarginTiles = 1;
        // Reduce to teleport less often (bigger reads) at the cost of more memory
        // and longer per-stop settles. Applied to StreamingWorld.TerrainDistance.
        private int terrainDistance = 5;
        private int settleStableFrames = 8;
        // Each checkpoint rewrites the full ~256 MB mask, so keep it infrequent;
        // resume skips done pixels, so a lost checkpoint only re-teleports (fast).
        private int checkpointEveryStops = 300;

        // State.
        private bool running;
        private int mapPixelsX;
        private int mapPixelsY;
        private int widthCellsCoarse, heightCellsCoarse, widthCellsFine, heightCellsFine;
        private byte[] coarseMaskBits, fineMaskBits;
        private bool[] pixelDone;
        private long coarseWaterCells, fineWaterCells;
        private int processedPixels;
        private int totalPixels;

        private int gridX, gridY, gridStep, gridStart;
        private int stopsSinceCheckpoint;
        private float waterThresholdNormalized;

        private enum Phase { Idle, Teleporting, Settling, Reading }
        private Phase phase;
        private int teleportCX, teleportCY;
        private int settleStable;
        private int framesSinceTeleport;
        private const int MinFramesAfterTeleport = 10;
        private int passCount;
        private const int MaxPasses = 4;
        private bool terrainUpdating;
        private int savedTerrainDistance = -1;
        private string statusLine = string.Empty;

        [MenuItem("Tools/Deep Waters/Diagnostics/Live Streaming Mask Baker")]
        public static void ShowWindow()
        {
            GetWindow<WodLiveStreamingMaskBaker>("Live Mask Bake");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Live Streaming Water-Mask Baker", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Run in PLAY MODE with a save loaded. Teleports across the whole world, " +
                "reads the settled live terrain, and writes WodExactWaterMasks.bytes (then run " +
                "'Bake Distance Field from WOD Exact Masks' + 'Build Windows Mod'). Long run; " +
                "checkpoints so a crash resumes. Don't recompile scripts while it runs.",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(running))
            {
                mode = (BakeMode)EditorGUILayout.EnumPopup("Mode", mode);
                fineSubCellsPerPixel = EditorGUILayout.IntSlider("Fine sub-cells", fineSubCellsPerPixel, 8, 64);
                terrainDistance = EditorGUILayout.IntSlider("Terrain distance (ring)", terrainDistance, 3, 8);
                readMarginTiles = EditorGUILayout.IntSlider("Settled-edge margin", readMarginTiles, 1, 3);
            }
            if (mode == BakeMode.ShorelineTargeted)
                EditorGUILayout.HelpBox("Shoreline-targeted: loads the existing WodExactWaterMasks.bytes as a base " +
                    "(run the offline exporter or a full live bake first) and live-rebakes ONLY coast pixels — much " +
                    "faster. Fine sub-cells must match the base file.", MessageType.None);

            EditorGUILayout.Space();
            if (running)
            {
                EditorGUILayout.LabelField("Progress",
                    processedPixels + "/" + totalPixels + " pixels (" +
                    (100f * processedPixels / Mathf.Max(1, totalPixels)).ToString("F1") + "%)");
                EditorGUILayout.LabelField("Status", statusLine);
                EditorGUILayout.LabelField("Water cells", "coarse=" + coarseWaterCells + " fine=" + fineWaterCells);
                if (GUILayout.Button("Stop (keeps checkpoint)"))
                    StopBake("Stopped by user.", false);
            }
            else
            {
                bool hasCheckpoint = File.Exists(Path.Combine(Directory.GetCurrentDirectory(), CheckpointPath));
                if (GUILayout.Button(hasCheckpoint ? "Resume Live Bake (checkpoint found)" : "Start Live Bake"))
                    StartBake();
                if (hasCheckpoint && GUILayout.Button("Delete checkpoint (start fresh next time)"))
                    File.Delete(Path.Combine(Directory.GetCurrentDirectory(), CheckpointPath));
            }
        }

        private void OnDestroy()
        {
            if (running)
                StopBake("Window closed.", false);
        }

        private void StartBake()
        {
            try
            {
                if (!Application.isPlaying)
                    throw new Exception("Enter PLAY MODE with a save loaded first (streaming world must be active).");
                var gm = DaggerfallWorkshop.Game.GameManager.Instance;
                if (gm == null || gm.StreamingWorld == null || !gm.StreamingWorld.IsReady)
                    throw new Exception("StreamingWorld not ready. Load a save and let the world spawn first.");
                var sampler = DaggerfallUnity.Instance != null ? DaggerfallUnity.Instance.TerrainSampler : null;
                if (sampler == null)
                    throw new Exception("DaggerfallUnity.TerrainSampler is null.");

                mapPixelsX = MapsFile.MaxMapPixelX;
                mapPixelsY = MapsFile.MaxMapPixelY;
                widthCellsCoarse = mapPixelsX * CoarseSubCellsPerPixel;
                heightCellsCoarse = mapPixelsY * CoarseSubCellsPerPixel;
                widthCellsFine = mapPixelsX * fineSubCellsPerPixel;
                heightCellsFine = mapPixelsY * fineSubCellsPerPixel;
                totalPixels = mapPixelsX * mapPixelsY;
                waterThresholdNormalized = sampler.OceanElevation / sampler.MaxTerrainHeight +
                    0.5f / Mathf.Max(1f, sampler.MaxTerrainHeight);

                if (!TryLoadCheckpoint())
                {
                    if (mode == BakeMode.ShorelineTargeted)
                    {
                        LoadExistingMasks();      // base masks + counts from the offline / full bake
                        pixelDone = new bool[totalPixels];
                        processedPixels = 0;
                        ComputeShorelinePixels(); // mark non-coast pixels done; only re-bake the coast
                    }
                    else
                    {
                        coarseMaskBits = new byte[((long)widthCellsCoarse * heightCellsCoarse + 7) / 8];
                        fineMaskBits = new byte[(int)(((long)widthCellsFine * heightCellsFine + 7) / 8)];
                        pixelDone = new bool[totalPixels];
                        coarseWaterCells = fineWaterCells = 0;
                        processedPixels = 0;
                    }
                }

                int innerRadius = Mathf.Max(0, terrainDistance - readMarginTiles);
                gridStep = 2 * innerRadius + 1;
                gridStart = innerRadius;
                gridX = gridStart;
                gridY = gridStart;

                savedTerrainDistance = gm.StreamingWorld.TerrainDistance;
                gm.StreamingWorld.TerrainDistance = terrainDistance;

                DaggerfallWorkshop.StreamingWorld.OnUpdateTerrainsStart += OnTerrainsStart;
                DaggerfallWorkshop.StreamingWorld.OnUpdateTerrainsEnd += OnTerrainsEnd;

                running = true;
                phase = Phase.Teleporting;
                stopsSinceCheckpoint = 0;
                statusLine = "Starting...";
                EditorApplication.update += Tick;
                Debug.Log("[DeepWaters.LiveBake] Started. fine=" + fineSubCellsPerPixel +
                          ", terrainDistance=" + terrainDistance + ", innerRadius=" + innerRadius +
                          ", resume processed=" + processedPixels + "/" + totalPixels + ".");
            }
            catch (Exception ex)
            {
                running = false;
                EditorApplication.update -= Tick;
                Debug.LogError("[DeepWaters.LiveBake] Start failed: " + ex.Message + "\n" + ex.StackTrace);
                EditorUtility.DisplayDialog("Live bake failed to start", ex.Message, "OK");
            }
        }

        private void OnTerrainsStart() { terrainUpdating = true; }
        private void OnTerrainsEnd() { terrainUpdating = false; }

        private void Tick()
        {
            try
            {
                if (!running)
                    return;
                if (!Application.isPlaying)
                {
                    StopBake("Left play mode — checkpoint kept; resume later.", true);
                    return;
                }

                var gm = DaggerfallWorkshop.Game.GameManager.Instance;
                var sw = gm != null ? gm.StreamingWorld : null;
                if (sw == null)
                    return;

                switch (phase)
                {
                    case Phase.Teleporting:
                        AdvanceToNextUnfinishedStop();
                        if (!running)
                            return;
                        sw.TeleportToCoordinates(teleportCX, teleportCY,
                            DaggerfallWorkshop.StreamingWorld.RepositionMethods.Origin);
                        settleStable = 0;
                        framesSinceTeleport = 0;
                        phase = Phase.Settling;
                        statusLine = "Teleporting to " + teleportCX + ":" + teleportCY;
                        break;

                    case Phase.Settling:
                        framesSinceTeleport++;
                        bool busy = terrainUpdating || sw.IsRepositioningPlayer ||
                                    sw.MapPixelX != teleportCX || sw.MapPixelY != teleportCY ||
                                    !IsTileLoaded(sw, teleportCX, teleportCY) ||
                                    framesSinceTeleport < MinFramesAfterTeleport;
                        if (busy)
                            settleStable = 0;
                        else
                            settleStable++;
                        statusLine = "Settling " + teleportCX + ":" + teleportCY + " (" + settleStable + ")";
                        if (settleStable >= settleStableFrames)
                            phase = Phase.Reading;
                        break;

                    case Phase.Reading:
                        ReadCurrentStop(sw);
                        if (++stopsSinceCheckpoint >= checkpointEveryStops)
                        {
                            SaveCheckpoint();
                            stopsSinceCheckpoint = 0;
                        }
                        AdvanceGrid();
                        phase = running ? Phase.Teleporting : phase;
                        break;
                }

                Repaint();
            }
            catch (Exception ex)
            {
                Debug.LogError("[DeepWaters.LiveBake] Tick error: " + ex.Message + "\n" + ex.StackTrace);
                SaveCheckpoint();
                StopBake("Errored (checkpoint saved): " + ex.Message, true);
            }
        }

        // Skip grid stops whose inner block is already fully processed (resume).
        private void AdvanceToNextUnfinishedStop()
        {
            int innerRadius = Mathf.Max(0, terrainDistance - readMarginTiles);
            while (running)
            {
                if (BlockHasUnfinished(gridX, gridY, innerRadius))
                {
                    teleportCX = Mathf.Clamp(gridX, 0, mapPixelsX - 1);
                    teleportCY = Mathf.Clamp(gridY, 0, mapPixelsY - 1);
                    return;
                }
                AdvanceGrid();
            }
        }

        private bool BlockHasUnfinished(int cx, int cy, int innerRadius)
        {
            for (int my = cy - innerRadius; my <= cy + innerRadius; my++)
            {
                if (my < 0 || my >= mapPixelsY) continue;
                for (int mx = cx - innerRadius; mx <= cx + innerRadius; mx++)
                {
                    if (mx < 0 || mx >= mapPixelsX) continue;
                    if (!pixelDone[my * mapPixelsX + mx])
                        return true;
                }
            }
            return false;
        }

        private void AdvanceGrid()
        {
            gridX += gridStep;
            if (gridX < mapPixelsX)
                return;
            gridX = gridStart;
            gridY += gridStep;
            if (gridY < mapPixelsY)
                return;
            // Grid pass done. Re-pass to mop up tiles skipped while still loading
            // (AdvanceToNextUnfinishedStop skips already-done blocks, so re-passes
            // are cheap). Stop when everything's done or after a few passes —
            // world-edge pixels may never load.
            gridY = gridStart;
            if (processedPixels >= totalPixels || ++passCount >= MaxPasses)
                CompleteBake();
            else
                Debug.Log("[DeepWaters.LiveBake] Pass " + passCount + " done; " +
                          (totalPixels - processedPixels) + " pixels remain — re-passing.");
        }

        private bool IsTileLoaded(DaggerfallWorkshop.StreamingWorld sw, int mx, int my)
        {
            if (mx < 0 || my < 0 || mx >= mapPixelsX || my >= mapPixelsY)
                return true; // off-map edge: don't block settle on it
            GameObject go = sw.GetTerrainFromPixel(mx, my);
            DaggerfallTerrain dft = go != null ? go.GetComponent<DaggerfallTerrain>() : null;
            return dft != null && dft.MapData.heightmapSamples != null;
        }

        private void ReadCurrentStop(DaggerfallWorkshop.StreamingWorld sw)
        {
            int innerRadius = Mathf.Max(0, terrainDistance - readMarginTiles);
            int read = 0;
            for (int my = teleportCY - innerRadius; my <= teleportCY + innerRadius; my++)
            {
                if (my < 0 || my >= mapPixelsY) continue;
                for (int mx = teleportCX - innerRadius; mx <= teleportCX + innerRadius; mx++)
                {
                    if (mx < 0 || mx >= mapPixelsX) continue;
                    if (pixelDone[my * mapPixelsX + mx]) continue;

                    GameObject go = sw.GetTerrainFromPixel(mx, my);
                    DaggerfallTerrain dft = go != null ? go.GetComponent<DaggerfallTerrain>() : null;
                    if (dft == null || dft.MapData.heightmapSamples == null)
                        continue; // not loaded — leave for a later stop

                    if (mode == BakeMode.ShorelineTargeted)
                        ClearPixel(mx, my);   // overwrite the offline base for this coast pixel
                    AggregatePixel(mx, my, dft.MapData.heightmapSamples, dft.MapData.tilemapSamples);
                    pixelDone[my * mapPixelsX + mx] = true;
                    processedPixels++;
                    read++;
                }
            }
            statusLine = "Read " + read + " tiles at " + teleportCX + ":" + teleportCY;
        }

        // Water = terrain height at/below ocean (+~0.5 m) OR a water tilemap tile —
        // matching DeepWaterWaterClassification.IsLocalPointWater at runtime.
        private bool IsSampleWater(float[,] heights, byte[,] tilemap, int hy, int hx, int hRows, int hCols)
        {
            if (heights[hy, hx] <= waterThresholdNormalized)
                return true;
            if (tilemap == null)
                return false;
            int tRows = tilemap.GetLength(0);
            int tCols = tilemap.GetLength(1);
            int ty = Mathf.Clamp(hy * tRows / Mathf.Max(1, hRows), 0, tRows - 1);
            int tx = Mathf.Clamp(hx * tCols / Mathf.Max(1, hCols), 0, tCols - 1);
            int index = tilemap[ty, tx] & 0x3f;
            return index == 0 || (index >= 5 && index <= 7) || index == 48;
        }

        // Same sub-cell aggregation + bit layout (incl. the north-row-0 subY flip)
        // as WodExactTilemapMaskExporter, so BakeFromExactMasks reads it identically
        // — only the data source (live heightmap+tilemap) differs.
        private void AggregatePixel(int mx, int my, float[,] heights, byte[,] tilemap)
        {
            int hRows = heights.GetLength(0);
            int hCols = heights.GetLength(1);
            int hDim = Mathf.Min(hRows, hCols);

            for (int subY = 0; subY < CoarseSubCellsPerPixel; subY++)
            for (int subX = 0; subX < CoarseSubCellsPerPixel; subX++)
            {
                int total, water;
                CountSubCell(heights, tilemap, hDim, hRows, hCols, subX, subY, CoarseSubCellsPerPixel, out total, out water);
                if (total <= 0 || water * 2 < total)
                    continue;
                long bit = ((long)my * CoarseSubCellsPerPixel + subY) * widthCellsCoarse +
                           (long)mx * CoarseSubCellsPerPixel + subX;
                if (SetPackedBit(coarseMaskBits, bit))
                    coarseWaterCells++;
            }

            for (int subY = 0; subY < fineSubCellsPerPixel; subY++)
            for (int subX = 0; subX < fineSubCellsPerPixel; subX++)
            {
                if (!AnySubCellWater(heights, tilemap, hDim, hRows, hCols, subX, subY, fineSubCellsPerPixel))
                    continue;
                long bit = ((long)my * fineSubCellsPerPixel + subY) * widthCellsFine +
                           (long)mx * fineSubCellsPerPixel + subX;
                if (SetPackedBit(fineMaskBits, bit))
                    fineWaterCells++;
            }
        }

        private void GetRange(int hDim, int subX, int subY, int subCells,
            out int rowStart, out int rowEnd, out int colStart, out int colEnd)
        {
            int invSubY = subCells - 1 - subY;   // bake row 0 = north, heightmap row 0 = south
            rowStart = invSubY * (hDim - 1) / subCells;
            rowEnd = (invSubY + 1) * (hDim - 1) / subCells;
            colStart = subX * (hDim - 1) / subCells;
            colEnd = (subX + 1) * (hDim - 1) / subCells;
            if (rowEnd > hDim - 1) rowEnd = hDim - 1;
            if (colEnd > hDim - 1) colEnd = hDim - 1;
            if (rowEnd < rowStart) rowEnd = rowStart;
            if (colEnd < colStart) colEnd = colStart;
        }

        private void CountSubCell(float[,] heights, byte[,] tilemap, int hDim, int hRows, int hCols,
            int subX, int subY, int subCells, out int total, out int water)
        {
            int rs, re, cs, ce;
            GetRange(hDim, subX, subY, subCells, out rs, out re, out cs, out ce);
            total = 0; water = 0;
            for (int hy = rs; hy <= re; hy++)
            for (int hx = cs; hx <= ce; hx++)
            {
                if (IsSampleWater(heights, tilemap, hy, hx, hRows, hCols)) water++;
                total++;
            }
        }

        private bool AnySubCellWater(float[,] heights, byte[,] tilemap, int hDim, int hRows, int hCols,
            int subX, int subY, int subCells)
        {
            int rs, re, cs, ce;
            GetRange(hDim, subX, subY, subCells, out rs, out re, out cs, out ce);
            for (int hy = rs; hy <= re; hy++)
            for (int hx = cs; hx <= ce; hx++)
                if (IsSampleWater(heights, tilemap, hy, hx, hRows, hCols))
                    return true;
            return false;
        }

        private static bool SetPackedBit(byte[] packed, long bitIndex)
        {
            int byteIndex = (int)(bitIndex >> 3);
            byte mask = (byte)(1 << (int)(bitIndex & 7));
            if ((packed[byteIndex] & mask) != 0)
                return false;
            packed[byteIndex] |= mask;
            return true;
        }

        private static bool ClearPackedBit(byte[] packed, long bitIndex)
        {
            int byteIndex = (int)(bitIndex >> 3);
            byte mask = (byte)(1 << (int)(bitIndex & 7));
            if ((packed[byteIndex] & mask) == 0)
                return false;
            packed[byteIndex] &= (byte)~mask;
            return true;
        }

        private static bool GetPackedBit(byte[] packed, long bitIndex)
        {
            return (packed[(int)(bitIndex >> 3)] & (1 << (int)(bitIndex & 7))) != 0;
        }

        // Shoreline mode: wipe a pixel's coarse + fine bits (decrementing counts)
        // so the live read overwrites the offline base instead of unioning with it.
        private void ClearPixel(int mx, int my)
        {
            for (int subY = 0; subY < CoarseSubCellsPerPixel; subY++)
            for (int subX = 0; subX < CoarseSubCellsPerPixel; subX++)
            {
                long bit = ((long)my * CoarseSubCellsPerPixel + subY) * widthCellsCoarse +
                           (long)mx * CoarseSubCellsPerPixel + subX;
                if (ClearPackedBit(coarseMaskBits, bit)) coarseWaterCells--;
            }
            for (int subY = 0; subY < fineSubCellsPerPixel; subY++)
            for (int subX = 0; subX < fineSubCellsPerPixel; subX++)
            {
                long bit = ((long)my * fineSubCellsPerPixel + subY) * widthCellsFine +
                           (long)mx * fineSubCellsPerPixel + subX;
                if (ClearPackedBit(fineMaskBits, bit)) fineWaterCells--;
            }
        }

        // Load the offline / full-bake WodExactWaterMasks.bytes as the base for
        // shoreline mode (only coast pixels get re-baked live over it).
        private void LoadExistingMasks()
        {
            string abs = Path.Combine(Directory.GetCurrentDirectory(), OutputPath);
            if (!File.Exists(abs))
                throw new Exception("Shoreline mode needs an existing " + OutputPath +
                    ". Run the offline exporter or a full live bake first.");
            using (var fs = new FileStream(abs, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                if (br.ReadUInt32() != Magic)
                    throw new Exception("Base mask file magic mismatch.");
                br.ReadUInt16();                 // version
                int coarseSub = br.ReadUInt16();
                int fineSub = br.ReadUInt16();
                int fmx = br.ReadUInt16();
                int fmy = br.ReadUInt16();
                br.ReadInt32(); br.ReadInt32();  // coarse width/height
                br.ReadInt32(); br.ReadInt32();  // fine width/height
                br.ReadInt32();                  // targetRows
                coarseWaterCells = br.ReadInt64();
                fineWaterCells = br.ReadInt64();
                int cl = br.ReadInt32();
                int fl = br.ReadInt32();
                if (coarseSub != CoarseSubCellsPerPixel || fineSub != fineSubCellsPerPixel ||
                    fmx != mapPixelsX || fmy != mapPixelsY)
                    throw new Exception("Base mask dims/fine-sub mismatch (file fine=" + fineSub +
                        " vs setting " + fineSubCellsPerPixel + "). Set Fine sub-cells to match the base file.");
                coarseMaskBits = br.ReadBytes(cl);
                fineMaskBits = br.ReadBytes(fl);
            }
            Debug.Log("[DeepWaters.LiveBake] Shoreline base loaded: coarse water=" + coarseWaterCells +
                      ", fine water=" + fineWaterCells + ".");
        }

        // A pixel needs a live re-bake if it straddles a water/land boundary: its
        // own coarse cells are mixed, OR a neighbour differs in water presence (the
        // seam is at the shared edge). Interior ocean/land keep the offline base.
        private void ComputeShorelinePixels()
        {
            bool[] hasWater = new bool[totalPixels];
            bool[] hasLand = new bool[totalPixels];
            for (int my = 0; my < mapPixelsY; my++)
            for (int mx = 0; mx < mapPixelsX; mx++)
            {
                bool w = false, l = false;
                for (int subY = 0; subY < CoarseSubCellsPerPixel && !(w && l); subY++)
                for (int subX = 0; subX < CoarseSubCellsPerPixel; subX++)
                {
                    long bit = ((long)my * CoarseSubCellsPerPixel + subY) * widthCellsCoarse +
                               (long)mx * CoarseSubCellsPerPixel + subX;
                    if (GetPackedBit(coarseMaskBits, bit)) w = true; else l = true;
                    if (w && l) break;
                }
                hasWater[my * mapPixelsX + mx] = w;
                hasLand[my * mapPixelsX + mx] = l;
            }

            int shorelineCount = 0;
            for (int my = 0; my < mapPixelsY; my++)
            for (int mx = 0; mx < mapPixelsX; mx++)
            {
                int p = my * mapPixelsX + mx;
                bool shore = hasWater[p] && hasLand[p];
                for (int dy = -1; dy <= 1 && !shore; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = mx + dx, ny = my + dy;
                    if (nx < 0 || ny < 0 || nx >= mapPixelsX || ny >= mapPixelsY) continue;
                    if (hasWater[ny * mapPixelsX + nx] != hasWater[p]) { shore = true; break; }
                }
                if (shore)
                {
                    shorelineCount++;
                }
                else
                {
                    pixelDone[p] = true;     // keep offline classification, skip
                    processedPixels++;
                }
            }
            Debug.Log("[DeepWaters.LiveBake] Shoreline pixels to re-bake: " + shorelineCount + "/" + totalPixels +
                      " (" + (100f * shorelineCount / Mathf.Max(1, totalPixels)).ToString("F1") + "%).");
        }

        private void CompleteBake()
        {
            WriteExportFile();
            string cp = Path.Combine(Directory.GetCurrentDirectory(), CheckpointPath);
            if (File.Exists(cp)) File.Delete(cp);
            StopBake("Complete — wrote " + OutputPath + ".", false);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Live bake complete",
                "Wrote " + OutputPath + " from live terrain (" + fineWaterCells + " fine water cells).\n\n" +
                "Now run 'Bake Distance Field from WOD Exact Masks' then 'Build Windows Mod'.", "OK");
        }

        private void StopBake(string reason, bool error)
        {
            running = false;
            EditorApplication.update -= Tick;
            DaggerfallWorkshop.StreamingWorld.OnUpdateTerrainsStart -= OnTerrainsStart;
            DaggerfallWorkshop.StreamingWorld.OnUpdateTerrainsEnd -= OnTerrainsEnd;
            var gm = DaggerfallWorkshop.Game.GameManager.Instance;
            if (gm != null && gm.StreamingWorld != null && savedTerrainDistance > 0)
                gm.StreamingWorld.TerrainDistance = savedTerrainDistance;
            if (error) Debug.LogError("[DeepWaters.LiveBake] " + reason);
            else Debug.Log("[DeepWaters.LiveBake] " + reason);
            Repaint();
        }

        private void WriteExportFile()
        {
            string abs = Path.Combine(Directory.GetCurrentDirectory(), OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(abs));
            using (var fs = new FileStream(abs, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Magic);
                bw.Write(Version);
                bw.Write((ushort)CoarseSubCellsPerPixel);
                bw.Write((ushort)fineSubCellsPerPixel);
                bw.Write((ushort)mapPixelsX);
                bw.Write((ushort)mapPixelsY);
                bw.Write(widthCellsCoarse);
                bw.Write(heightCellsCoarse);
                bw.Write(widthCellsFine);
                bw.Write(heightCellsFine);
                bw.Write(mapPixelsY);            // targetRows = full world
                bw.Write(coarseWaterCells);
                bw.Write(fineWaterCells);
                bw.Write(coarseMaskBits.Length);
                bw.Write(fineMaskBits.Length);
                bw.Write(coarseMaskBits);
                bw.Write(fineMaskBits);
            }
        }

        private void SaveCheckpoint()
        {
            try
            {
                string abs = Path.Combine(Directory.GetCurrentDirectory(), CheckpointPath);
                Directory.CreateDirectory(Path.GetDirectoryName(abs));
                using (var fs = new FileStream(abs, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    bw.Write(CheckpointMagic);
                    bw.Write((byte)mode);
                    bw.Write((ushort)fineSubCellsPerPixel);
                    bw.Write(mapPixelsX);
                    bw.Write(mapPixelsY);
                    bw.Write(coarseWaterCells);
                    bw.Write(fineWaterCells);
                    bw.Write(processedPixels);
                    bw.Write(coarseMaskBits.Length);
                    bw.Write(fineMaskBits.Length);
                    bw.Write(coarseMaskBits);
                    bw.Write(fineMaskBits);
                    for (int i = 0; i < pixelDone.Length; i++)
                        bw.Write(pixelDone[i]);
                }
                Debug.Log("[DeepWaters.LiveBake] Checkpoint at " + processedPixels + "/" + totalPixels + ".");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DeepWaters.LiveBake] Checkpoint write failed: " + ex.Message);
            }
        }

        private bool TryLoadCheckpoint()
        {
            string abs = Path.Combine(Directory.GetCurrentDirectory(), CheckpointPath);
            if (!File.Exists(abs))
                return false;
            try
            {
                using (var fs = new FileStream(abs, FileMode.Open, FileAccess.Read))
                using (var br = new BinaryReader(fs))
                {
                    if (br.ReadUInt32() != CheckpointMagic) return false;
                    if (br.ReadByte() != (byte)mode) return false;   // a different mode's checkpoint
                    int fine = br.ReadUInt16();
                    int mx = br.ReadInt32(), myy = br.ReadInt32();
                    if (fine != fineSubCellsPerPixel || mx != mapPixelsX || myy != mapPixelsY)
                    {
                        Debug.LogWarning("[DeepWaters.LiveBake] Checkpoint dimensions differ from current settings; starting fresh.");
                        return false;
                    }
                    coarseWaterCells = br.ReadInt64();
                    fineWaterCells = br.ReadInt64();
                    processedPixels = br.ReadInt32();
                    int cl = br.ReadInt32(), fl = br.ReadInt32();
                    coarseMaskBits = br.ReadBytes(cl);
                    fineMaskBits = br.ReadBytes(fl);
                    pixelDone = new bool[totalPixels];
                    for (int i = 0; i < pixelDone.Length; i++)
                        pixelDone[i] = br.ReadBoolean();
                }
                Debug.Log("[DeepWaters.LiveBake] Resumed checkpoint: " + processedPixels + "/" + totalPixels + " pixels done.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[DeepWaters.LiveBake] Checkpoint read failed (" + ex.Message + "); starting fresh.");
                return false;
            }
        }
    }
}
#endif
