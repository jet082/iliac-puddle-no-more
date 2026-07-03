// Project:         Iliac Puddle No More
// License:         MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepWaters.Editor
{
    /// <summary>
    /// Experimental, paced exporter for WOD's exact GPU tilemap water.
    /// It does not build the final distance bake yet; it writes packed coarse
    /// and fine water masks that can later be consumed by DistanceBakeBuilder.
    /// </summary>
    public class WodExactTilemapMaskExporter : EditorWindow
    {
        private const string WodOutputPath =
            "Assets/Game/Mods/deep-waters/Diagnostics/WodExactWaterMasks.bytes";
		private const string VanillaOutputPath =
			"Assets/Game/Mods/deep-waters/Diagnostics/VanillaExactWaterMasks.bytes";
        private const uint Magic = 0x44574558; // "DWEX"
        private const ushort Version = 1;
        private const int CoarseSubCellsPerPixel = 8;
        private const int DefaultFineSubCellsPerPixel = 32;
        private const int MaxFineSubCellsPerPixel = 64;

		private enum ExportTarget { Wod, Vanilla }

		private ExportTarget target = ExportTarget.Wod;
        private int fineSubCellsPerPixel = DefaultFineSubCellsPerPixel;
        private int tilesPerUpdate = 1;
        private int rowsToExport = 1;
        private bool running;
        private int currentX;
        private int currentY;
        private int hDim;
        private int widthCellsCoarse;
        private int heightCellsCoarse;
        private int widthCellsFine;
        private int heightCellsFine;
        private byte[] coarseMaskBits;
        private byte[] fineMaskBits;
        private long coarseWaterCells;
        private long fineWaterCells;
        private int failures;
        private string lastFailure = string.Empty;

        private DaggerfallUnity dfu;
        private ITerrainSampler sampler;
		private DefaultTerrainTexturing vanillaTexturing;
		private float vanillaSeaThreshold;
		private float vanillaVisualThreshold;
        private object cacheInstance;
        private MethodInfo cacheGetMethod;
        private MethodInfo cacheClearMethod;

		private string OutputPath
		{
			get { return target == ExportTarget.Vanilla ? VanillaOutputPath : WodOutputPath; }
		}

		private string TargetLabel
		{
			get { return target == ExportTarget.Vanilla ? "vanilla" : "WOD"; }
		}

        private int TargetRows
        {
            get
            {
                return rowsToExport <= 0
                    ? MapsFile.MaxMapPixelY
                    : Mathf.Min(rowsToExport, MapsFile.MaxMapPixelY);
            }
        }

        [MenuItem("Tools/Deep Waters/Diagnostics/WOD Exact Tilemap Mask Exporter")]
        public static void ShowWindow()
        {
            var window = GetWindow<WodExactTilemapMaskExporter>("Exact Mask Export");
			window.target = ExportTarget.Wod;
        }

		[MenuItem("Tools/Deep Waters/Diagnostics/Vanilla Exact Tilemap Mask Exporter")]
		public static void ShowVanillaWindow()
		{
			var window = GetWindow<WodExactTilemapMaskExporter>("Exact Mask Export");
			window.target = ExportTarget.Vanilla;
		}

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Exact Tilemap Mask Exporter", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(running))
            {
				target = (ExportTarget)EditorGUILayout.EnumPopup("Target", target);
                fineSubCellsPerPixel = EditorGUILayout.IntSlider(
                    "Fine sub-cells", fineSubCellsPerPixel, 8, MaxFineSubCellsPerPixel);
                tilesPerUpdate = EditorGUILayout.IntSlider("Tiles per editor update", tilesPerUpdate, 1, 16);
                rowsToExport = EditorGUILayout.IntField("Rows to export (0 = full)", rowsToExport);
                if (rowsToExport < 0)
                    rowsToExport = 0;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", OutputPath);

            if (running)
            {
                float progress = currentY / (float)Mathf.Max(1, MapsFile.MaxMapPixelY);
                EditorGUILayout.LabelField("Progress",
                    "(" + currentX + "," + currentY + ") " + (100f * progress).ToString("F1") +
                    "% of world, target rows=" + TargetRows);
                EditorGUILayout.LabelField("Water cells",
                    "coarse=" + coarseWaterCells + ", fine=" + fineWaterCells);
                if (GUILayout.Button("Cancel Export"))
                    StopExport("Canceled by user.", false);
            }
            else
            {
                if (GUILayout.Button("Start Exact Mask Export"))
                    StartExport();
            }

            if (failures > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    "Failures: " + failures + "\nLast: " + lastFailure,
                    MessageType.Warning);
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "This exporter writes packed masks only; the final distance bake consumer is the next step. " +
				"WOD drives its GPU sampler, while vanilla forces DFU's stock sampler plus terrain texturing jobs.",
                MessageType.Info);
        }

        private void OnDestroy()
        {
            if (running)
                StopExport("Window closed.", false);
        }

        private void StartExport()
        {
            try
            {
                ValidateAndInitialize();
                running = true;
                currentX = 0;
                currentY = 0;
                coarseWaterCells = 0;
                fineWaterCells = 0;
                failures = 0;
                lastFailure = string.Empty;
                EditorApplication.update += TickExport;
                Debug.Log("[DeepWaters.ExactExport] Started exact " + TargetLabel + " tilemap export: coarse " +
                          widthCellsCoarse + "x" + heightCellsCoarse + ", fine " +
                          widthCellsFine + "x" + heightCellsFine + " (" +
                          fineSubCellsPerPixel + "x" + fineSubCellsPerPixel +
                          "), tilesPerUpdate=" + tilesPerUpdate +
                          ", targetRows=" + TargetRows + ".");
            }
            catch (Exception ex)
            {
                running = false;
                EditorApplication.update -= TickExport;
                EditorUtility.ClearProgressBar();
                Debug.LogError("[DeepWaters.ExactExport] Start failed: " + ex.Message + "\n" + ex.StackTrace);
                EditorUtility.DisplayDialog("Exact export failed", ex.Message, "OK");
            }
        }

        private void ValidateAndInitialize()
        {
            dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.ContentReader == null)
                throw new Exception("DaggerfallUnity is not initialized.");

			vanillaTexturing = null;
			cacheInstance = null;
			cacheGetMethod = null;
			cacheClearMethod = null;

			if (target == ExportTarget.Vanilla)
			{
				sampler = new DefaultTerrainSampler();
				vanillaTexturing = new DefaultTerrainTexturing();
				vanillaSeaThreshold = (sampler.OceanElevation + 0.5f) / sampler.MaxTerrainHeight;
				vanillaVisualThreshold = sampler.BeachElevation / sampler.MaxTerrainHeight;
			}
			else
			{
				sampler = dfu.TerrainSampler;
				if (sampler == null || sampler.GetType().FullName != "Monobelisk.InterestingTerrainSampler")
					throw new Exception("Runtime sampler is not Monobelisk.InterestingTerrainSampler.");

				ResolveMonobeliskCache(out cacheInstance, out cacheGetMethod, out cacheClearMethod);
				if (cacheInstance == null || cacheGetMethod == null)
					throw new Exception("Could not resolve Monobelisk.InterestingTerrains.tileDataCache.Get(int, int).");
			}

            hDim = sampler.HeightmapDimension;
            widthCellsCoarse = MapsFile.MaxMapPixelX * CoarseSubCellsPerPixel;
            heightCellsCoarse = MapsFile.MaxMapPixelY * CoarseSubCellsPerPixel;
            widthCellsFine = MapsFile.MaxMapPixelX * fineSubCellsPerPixel;
            heightCellsFine = MapsFile.MaxMapPixelY * fineSubCellsPerPixel;

            long coarseCells = (long)widthCellsCoarse * heightCellsCoarse;
            long fineCells = (long)widthCellsFine * heightCellsFine;
            long coarseBytes = (coarseCells + 7) / 8;
            long fineBytes = (fineCells + 7) / 8;
            if (coarseBytes > int.MaxValue || fineBytes > int.MaxValue)
                throw new Exception("Packed masks are too large for one byte array.");

            coarseMaskBits = new byte[(int)coarseBytes];
            fineMaskBits = new byte[(int)fineBytes];
        }

        private void TickExport()
        {
            try
            {
                int processed = 0;
                while (running && processed < tilesPerUpdate && currentY < TargetRows)
                {
                    ProcessCurrentTile();
                    AdvanceTile();
                    processed++;
                }

                GL.Flush();
                AsyncGPUReadback.WaitAllRequests();

                if ((currentY & 7) == 0 && currentX == 0)
                {
                    EditorUtility.UnloadUnusedAssetsImmediate();
                    GC.Collect();
                    Debug.Log("[DeepWaters.ExactExport] Exported through row " +
                              currentY + "/" + TargetRows + ".");
                }

                EditorUtility.DisplayProgressBar(TargetLabel + " exact mask export",
                    "Exporting tile (" + currentX + "," + currentY + ")",
                    currentY / (float)Mathf.Max(1, TargetRows));
                Repaint();

                if (currentY >= TargetRows)
                    CompleteExport();
            }
            catch (Exception ex)
            {
                failures++;
                lastFailure = ex.Message;
                StopExport("Failed at (" + currentX + "," + currentY + "): " + ex.Message, true);
            }
        }

        private void ProcessCurrentTile()
        {
			if (target == ExportTarget.Vanilla)
			{
				float[] heights;
				byte[] tilemap;
				GenerateVanillaTileData(currentX, currentY, out heights, out tilemap);
				AggregateVanillaCoarse(heights, tilemap);
				AggregateVanillaFine(heights, tilemap);
				return;
			}

            byte[] tileData = GenerateWodTileData(currentX, currentY);
			if (tileData == null || tileData.Length < hDim * hDim)
				throw new Exception("WOD tileData was missing or too small.");

            AggregateCoarse(tileData);
            AggregateFine(tileData);
        }

        private byte[] GenerateWodTileData(int mapPixelX, int mapPixelY)
        {
            MapPixelData mapData = TerrainHelper.GetMapPixelData(
                dfu.ContentReader, mapPixelX, mapPixelY);
            mapData.heightmapData = new NativeArray<float>(hDim * hDim, Allocator.TempJob);
            mapData.nativeArrayList = new List<IDisposable>();

            try
            {
                JobHandle handle = sampler.ScheduleGenerateSamplesJob(ref mapData);
                handle.Complete();
                AsyncGPUReadback.WaitAllRequests();

                object[] getArgs = { mapPixelX, mapPixelY };
                return cacheGetMethod.Invoke(cacheInstance, getArgs) as byte[];
            }
            finally
            {
                if (mapData.nativeArrayList != null)
                {
                    foreach (IDisposable nativeArray in mapData.nativeArrayList)
                    {
                        if (nativeArray != null)
                            nativeArray.Dispose();
                    }
                }
                if (mapData.heightmapData.IsCreated)
                    mapData.heightmapData.Dispose();
            }
        }

		private void GenerateVanillaTileData(int mapPixelX, int mapPixelY, out float[] heights, out byte[] tilemap)
		{
			int tilemapDim = MapsFile.WorldMapTileDim;
			MapPixelData mapData = TerrainHelper.GetMapPixelData(
				dfu.ContentReader, mapPixelX, mapPixelY);
			mapData.heightmapData = new NativeArray<float>(hDim * hDim, Allocator.TempJob);
			mapData.tilemapData = new NativeArray<byte>(tilemapDim * tilemapDim, Allocator.TempJob);
			mapData.avgMaxHeight = new NativeArray<float>(new float[] { 0f, float.MinValue }, Allocator.TempJob);
			mapData.nativeArrayList = new List<IDisposable>();

			try
			{
				JobHandle heightHandle = sampler.ScheduleGenerateSamplesJob(ref mapData);
				JobHandle terrainHandle = heightHandle;
				if (mapData.hasLocation)
				{
					JobHandle avgHandle = TerrainHelper.ScheduleCalcAvgMaxHeightJob(ref mapData, heightHandle);
					JobHandle.ScheduleBatchedJobs();
					TerrainHelper.SetLocationTiles(ref mapData);
					terrainHandle = sampler.IsLocationTerrainBlended()
						? avgHandle
						: TerrainHelper.ScheduleBlendLocationTerrainJob(ref mapData, avgHandle);
				}

				JobHandle tileHandle = vanillaTexturing.ScheduleAssignTilesJob(sampler, ref mapData, terrainHandle);
				tileHandle.Complete();

				heights = new float[hDim * hDim];
				mapData.heightmapData.CopyTo(heights);
				tilemap = new byte[tilemapDim * tilemapDim];
				mapData.tilemapData.CopyTo(tilemap);
				for (int i = 0; i < tilemap.Length; i++)
					if (tilemap[i] == byte.MaxValue)
						tilemap[i] = 0;
			}
			finally
			{
				if (mapData.nativeArrayList != null)
				{
					foreach (IDisposable nativeArray in mapData.nativeArrayList)
					{
						if (nativeArray != null)
							nativeArray.Dispose();
					}
				}
				if (mapData.heightmapData.IsCreated)
					mapData.heightmapData.Dispose();
				if (mapData.tilemapData.IsCreated)
					mapData.tilemapData.Dispose();
				if (mapData.avgMaxHeight.IsCreated)
					mapData.avgMaxHeight.Dispose();
			}
		}

        private void AggregateCoarse(byte[] tileData)
        {
            for (int subY = 0; subY < CoarseSubCellsPerPixel; subY++)
            {
                for (int subX = 0; subX < CoarseSubCellsPerPixel; subX++)
                {
                    int total;
                    int water;
                    CountWaterSamples(tileData, subX, subY, CoarseSubCellsPerPixel, out total, out water);
                    if (total <= 0 || water * 2 < total)
                        continue;

                    long bit = ((long)currentY * CoarseSubCellsPerPixel + subY) * widthCellsCoarse +
                               currentX * CoarseSubCellsPerPixel + subX;
                    SetPackedBit(coarseMaskBits, bit);
                    coarseWaterCells++;
                }
            }
        }

        private void AggregateFine(byte[] tileData)
        {
            for (int subY = 0; subY < fineSubCellsPerPixel; subY++)
            {
                for (int subX = 0; subX < fineSubCellsPerPixel; subX++)
                {
                    if (!HasAnyWaterSample(tileData, subX, subY, fineSubCellsPerPixel))
                        continue;

                    long bit = ((long)currentY * fineSubCellsPerPixel + subY) * widthCellsFine +
                               currentX * fineSubCellsPerPixel + subX;
                    SetPackedBit(fineMaskBits, bit);
                    fineWaterCells++;
                }
            }
        }

		private void AggregateVanillaCoarse(float[] heights, byte[] tilemap)
		{
			for (int subY = 0; subY < CoarseSubCellsPerPixel; subY++)
			{
				for (int subX = 0; subX < CoarseSubCellsPerPixel; subX++)
				{
					int total;
					int water;
					CountVanillaWaterSamples(heights, tilemap, subX, subY, CoarseSubCellsPerPixel, out total, out water);
					if (total <= 0 || water * 2 < total)
						continue;

					long bit = ((long)currentY * CoarseSubCellsPerPixel + subY) * widthCellsCoarse +
						currentX * CoarseSubCellsPerPixel + subX;
					SetPackedBit(coarseMaskBits, bit);
					coarseWaterCells++;
				}
			}
		}

		private void AggregateVanillaFine(float[] heights, byte[] tilemap)
		{
			for (int subY = 0; subY < fineSubCellsPerPixel; subY++)
			{
				for (int subX = 0; subX < fineSubCellsPerPixel; subX++)
				{
					if (!HasAnyVanillaWaterSample(heights, tilemap, subX, subY, fineSubCellsPerPixel))
						continue;

					long bit = ((long)currentY * fineSubCellsPerPixel + subY) * widthCellsFine +
						currentX * fineSubCellsPerPixel + subX;
					SetPackedBit(fineMaskBits, bit);
					fineWaterCells++;
				}
			}
		}

        private void CountWaterSamples(
            byte[] tileData,
            int subX,
            int subY,
            int subCellsPerPixel,
            out int total,
            out int water)
        {
            int rowStart;
            int rowEnd;
            int colStart;
            int colEnd;
            GetHeightmapSampleRange(subX, subY, subCellsPerPixel,
                out rowStart, out rowEnd, out colStart, out colEnd);

            total = 0;
            water = 0;
            for (int hy = rowStart; hy <= rowEnd; hy++)
            {
                int rowBase = hy * hDim;
                for (int hx = colStart; hx <= colEnd; hx++)
                {
                    int idx = rowBase + hx;
                    if (idx >= 0 && idx < tileData.Length && tileData[idx] == 0)
                        water++;
                    total++;
                }
            }
        }

        private bool HasAnyWaterSample(byte[] tileData, int subX, int subY, int subCellsPerPixel)
        {
            int rowStart;
            int rowEnd;
            int colStart;
            int colEnd;
            GetHeightmapSampleRange(subX, subY, subCellsPerPixel,
                out rowStart, out rowEnd, out colStart, out colEnd);

            for (int hy = rowStart; hy <= rowEnd; hy++)
            {
                int rowBase = hy * hDim;
                for (int hx = colStart; hx <= colEnd; hx++)
                {
                    int idx = rowBase + hx;
                    if (idx >= 0 && idx < tileData.Length && tileData[idx] == 0)
                        return true;
                }
            }

            return false;
        }

		private void CountVanillaWaterSamples(
			float[] heights,
			byte[] tilemap,
			int subX,
			int subY,
			int subCellsPerPixel,
			out int total,
			out int water)
		{
			int rowStart;
			int rowEnd;
			int colStart;
			int colEnd;
			GetHeightmapSampleRange(subX, subY, subCellsPerPixel,
				out rowStart, out rowEnd, out colStart, out colEnd);

			total = 0;
			water = 0;
			for (int hy = rowStart; hy <= rowEnd; hy++)
			{
				for (int hx = colStart; hx <= colEnd; hx++)
				{
					if (IsVanillaWaterSample(heights, tilemap, hy, hx))
						water++;
					total++;
				}
			}
		}

		private bool HasAnyVanillaWaterSample(float[] heights, byte[] tilemap, int subX, int subY, int subCellsPerPixel)
		{
			int rowStart;
			int rowEnd;
			int colStart;
			int colEnd;
			GetHeightmapSampleRange(subX, subY, subCellsPerPixel,
				out rowStart, out rowEnd, out colStart, out colEnd);

			for (int hy = rowStart; hy <= rowEnd; hy++)
			{
				for (int hx = colStart; hx <= colEnd; hx++)
				{
					if (IsVanillaWaterSample(heights, tilemap, hy, hx))
						return true;
				}
			}

			return false;
		}

		private bool IsVanillaWaterSample(float[] heights, byte[] tilemap, int hy, int hx)
		{
			float height = heights[hy + hx * hDim];
			if (height <= vanillaSeaThreshold)
				return true;

			int tilemapDim = MapsFile.WorldMapTileDim;
			int ty = Mathf.Clamp(hy * tilemapDim / Mathf.Max(1, hDim), 0, tilemapDim - 1);
			int tx = Mathf.Clamp(hx * tilemapDim / Mathf.Max(1, hDim), 0, tilemapDim - 1);
			byte tile = tilemap[ty + tx * tilemapDim];
			if (!TileValueContainsWater(tile))
				return false;

			return height <= vanillaVisualThreshold;
		}

		private static bool TileValueContainsWater(byte tile)
		{
			int index = tile & 0x3f;
			return index == 0 ||
				(index >= 5 && index <= 7) ||
				index == 48;
		}

        private void GetHeightmapSampleRange(
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

        private void AdvanceTile()
        {
            currentX++;
            if (currentX < MapsFile.MaxMapPixelX)
                return;

            currentX = 0;
            currentY++;
        }

        private void CompleteExport()
        {
            WriteExportFile();
            StopExport("Complete.", false);
            AssetDatabase.Refresh();
			string nextCommand = target == ExportTarget.Vanilla
				? "Tools > Deep Waters > Bake Distance Field from Vanilla Exact Masks"
				: "Tools > Deep Waters > Bake Distance Field from WOD Exact Masks";
            EditorUtility.DisplayDialog("Exact export complete",
				"Wrote " + OutputPath + ".\n\nNow run " + nextCommand + ".", "OK");
        }

        private void StopExport(string reason, bool logAsError)
        {
            running = false;
            EditorApplication.update -= TickExport;
            EditorUtility.ClearProgressBar();
            if (cacheClearMethod != null && cacheInstance != null)
            {
                try { cacheClearMethod.Invoke(cacheInstance, null); }
                catch { }
            }

            string message = "[DeepWaters.ExactExport] " + reason;
            if (logAsError)
                Debug.LogError(message);
            else
                Debug.Log(message);
            Repaint();
        }

        private void WriteExportFile()
        {
            string absolutePath = Path.Combine(Directory.GetCurrentDirectory(), OutputPath);
            string directory = Path.GetDirectoryName(absolutePath);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            using (FileStream fs = new FileStream(absolutePath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write(Magic);
                bw.Write(Version);
                bw.Write((ushort)CoarseSubCellsPerPixel);
                bw.Write((ushort)fineSubCellsPerPixel);
                bw.Write((ushort)MapsFile.MaxMapPixelX);
                bw.Write((ushort)MapsFile.MaxMapPixelY);
                bw.Write(widthCellsCoarse);
                bw.Write(heightCellsCoarse);
                bw.Write(widthCellsFine);
                bw.Write(heightCellsFine);
                bw.Write(TargetRows);
                bw.Write(coarseWaterCells);
                bw.Write(fineWaterCells);
                bw.Write(coarseMaskBits.Length);
                bw.Write(fineMaskBits.Length);
                bw.Write(coarseMaskBits);
                bw.Write(fineMaskBits);
            }
        }

        private static void ResolveMonobeliskCache(
            out object cacheInstance,
            out MethodInfo getMethod,
            out MethodInfo clearMethod)
        {
            cacheInstance = null;
            getMethod = null;
            clearMethod = null;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = asm.GetType("Monobelisk.InterestingTerrains", false);
                if (type == null)
                    continue;

                FieldInfo field = type.GetField("tileDataCache",
                    BindingFlags.Public | BindingFlags.Static);
                if (field == null)
                    continue;

                cacheInstance = field.GetValue(null);
                break;
            }

            if (cacheInstance == null)
                return;

            Type cacheType = cacheInstance.GetType();
            getMethod = cacheType.GetMethod("Get",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new Type[] { typeof(int), typeof(int) },
                null);
            clearMethod = cacheType.GetMethod("Clear",
                BindingFlags.Public | BindingFlags.Instance);
        }

        private static void SetPackedBit(byte[] packed, long bitIndex)
        {
            int byteIndex = (int)(bitIndex >> 3);
            int bit = (int)(bitIndex & 7);
            packed[byteIndex] = (byte)(packed[byteIndex] | (1 << bit));
        }
    }
}
#endif
