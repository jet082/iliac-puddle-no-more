// Project:         Iliac Puddle No More
// License:         MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using Unity.Collections;
using Unity.Jobs;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepWaters.Editor
{
    /// <summary>
    /// One-tile comparison harness for deriving a CPU-side WOD water classifier.
    /// It samples WOD once, treats WOD's cached tilemap as ground truth, then
    /// compares candidate CPU classifications and writes mismatch maps.
    /// </summary>
    public class WodWaterClassifierDiagnostic : EditorWindow
    {
        private const string OutputDirectory = "Assets/Game/Mods/deep-waters/Diagnostics";
        private const string PrefMapPixelX = "DeepWaters.WodWaterClassifierDiagnostic.MapPixelX";
        private const string PrefMapPixelY = "DeepWaters.WodWaterClassifierDiagnostic.MapPixelY";
        private const string PrefWritePngs = "DeepWaters.WodWaterClassifierDiagnostic.WritePngs";

        private const byte InterestingTerrainWaterMapHeight = 6;
        private const float InterestingTerrainWaterMapThreshold = InterestingTerrainWaterMapHeight + 0.5f;
        private const float WodGpuWaterThresholdNormalized = 100.4f / 5000f;

        private int mapPixelX = TerrainHelper.defaultMapPixelX;
        private int mapPixelY = TerrainHelper.defaultMapPixelY;
        private bool writePngs = true;
        private string lastReport = string.Empty;

        [MenuItem("Tools/Deep Waters/Diagnostics/WOD Water Classifier Compare")]
        public static void ShowWindow()
        {
            GetWindow<WodWaterClassifierDiagnostic>("WOD Water Compare");
        }

        private void OnEnable()
        {
            mapPixelX = EditorPrefs.GetInt(PrefMapPixelX, TerrainHelper.defaultMapPixelX);
            mapPixelY = EditorPrefs.GetInt(PrefMapPixelY, TerrainHelper.defaultMapPixelY);
            writePngs = EditorPrefs.GetBool(PrefWritePngs, true);
            ClampMapPixelFields();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("WOD Water Classifier Compare", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUI.BeginChangeCheck();
            mapPixelX = EditorGUILayout.IntField("Map pixel X", mapPixelX);
            mapPixelY = EditorGUILayout.IntField("Map pixel Y", mapPixelY);
            writePngs = EditorGUILayout.Toggle("Write mismatch PNGs", writePngs);
            if (EditorGUI.EndChangeCheck())
            {
                ClampMapPixelFields();
                EditorPrefs.SetInt(PrefMapPixelX, mapPixelX);
                EditorPrefs.SetInt(PrefMapPixelY, mapPixelY);
                EditorPrefs.SetBool(PrefWritePngs, writePngs);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Use StreamingWorld Tile"))
                    UseStreamingWorldTile();

                if (GUILayout.Button("Compare Tile"))
                    CompareSelectedTile();
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Ground truth is the one tilemap WOD writes into Monobelisk.InterestingTerrains.tileDataCache. " +
                "Candidate 1 should match if the inferred WOD water threshold and indexing are correct.",
                MessageType.Info);

            if (!string.IsNullOrEmpty(lastReport))
            {
                EditorGUILayout.Space();
                EditorGUILayout.TextArea(lastReport, GUILayout.MinHeight(150f));
            }
        }

        private void ClampMapPixelFields()
        {
            mapPixelX = Mathf.Clamp(mapPixelX, TerrainHelper.minMapPixelX, TerrainHelper.maxMapPixelX);
            mapPixelY = Mathf.Clamp(mapPixelY, TerrainHelper.minMapPixelY, TerrainHelper.maxMapPixelY);
        }

        private void UseStreamingWorldTile()
        {
            if (!GameManager.HasInstance || GameManager.Instance.StreamingWorld == null)
            {
                EditorUtility.DisplayDialog("StreamingWorld unavailable",
                    "Start or load into the exterior world first, or enter a map pixel manually.",
                    "OK");
                return;
            }

            mapPixelX = GameManager.Instance.StreamingWorld.MapPixelX;
            mapPixelY = GameManager.Instance.StreamingWorld.MapPixelY;
            ClampMapPixelFields();
            EditorPrefs.SetInt(PrefMapPixelX, mapPixelX);
            EditorPrefs.SetInt(PrefMapPixelY, mapPixelY);
            Repaint();
        }

        private void CompareSelectedTile()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Deep Waters WOD compare",
                    "Generating WOD sample for (" + mapPixelX + "," + mapPixelY + ")...",
                    0.2f);

                DaggerfallUnity dfu = DaggerfallUnity.Instance;
                if (dfu == null || dfu.ContentReader == null || dfu.ContentReader.WoodsFileReader == null)
                    throw new Exception("DaggerfallUnity content reader is not initialized.");

                ITerrainSampler sampler = dfu.TerrainSampler;
                if (!IsMonobeliskInterestingTerrainSampler(sampler))
                {
                    string samplerName = sampler != null ? sampler.GetType().FullName : "<null>";
                    throw new Exception("Current terrain sampler is " + samplerName +
                                        ". This diagnostic expects Monobelisk.InterestingTerrainSampler.");
                }

                int hDim = sampler.HeightmapDimension;
                float[] wodHeights = new float[hDim * hDim];
                byte[] wodTileData = GenerateWodTileData(dfu, sampler, mapPixelX, mapPixelY, hDim, wodHeights);
                if (wodTileData == null)
                    throw new Exception("WOD did not return tileDataCache data for this tile.");
                if (wodTileData.Length < hDim * hDim)
                    throw new Exception("WOD tileData length " + wodTileData.Length +
                                        " is smaller than heightmap length " + (hDim * hDim) + ".");

                bool[] wodWater = BuildWodWaterTruth(wodTileData, hDim);
                bool[] gpuHeightThreshold = BuildGpuHeightThresholdCandidate(wodHeights, hDim);
                bool[] alteredWoods = BuildAlteredWoodsCandidate(dfu.ContentReader.WoodsFileReader.Buffer,
                    mapPixelX, mapPixelY, hDim, false);
                bool[] alteredWoodsFlipY = BuildAlteredWoodsCandidate(dfu.ContentReader.WoodsFileReader.Buffer,
                    mapPixelX, mapPixelY, hDim, true);
                bool[] cpuWodV0;
                float[] cpuWodV0Heights;
                string cpuWodV0Notes;
                bool hasCpuWodV0 = WodCpuWaterClassifierV0.TryBuildWaterMask(
                    mapPixelX, mapPixelY, hDim,
                    out cpuWodV0, out cpuWodV0Heights, out cpuWodV0Notes);
                bool[] cpuWodV1;
                float[] cpuWodV1Heights;
                string cpuWodV1Notes;
                bool hasCpuWodV1 = WodCpuWaterClassifierV0.TryBuildWaterMaskLocationPortV1(
                    mapPixelX, mapPixelY, hDim,
                    out cpuWodV1, out cpuWodV1Heights, out cpuWodV1Notes);
                bool[] cpuWodV2;
                float[] cpuWodV2Heights;
                string cpuWodV2Notes;
                bool hasCpuWodV2 = WodCpuWaterClassifierV0.TryBuildWaterMaskRoadsV2(
                    mapPixelX, mapPixelY, hDim,
                    out cpuWodV2, out cpuWodV2Heights, out cpuWodV2Notes);
                bool[] cpuWodV3;
                float[] cpuWodV3Heights;
                string cpuWodV3Notes;
                bool hasCpuWodV3 = WodCpuWaterClassifierV0.TryBuildWaterMaskGpuTextureV3(
                    mapPixelX, mapPixelY, hDim,
                    out cpuWodV3, out cpuWodV3Heights, out cpuWodV3Notes);

                ComparisonStats gpuHeightStats = Compare(wodWater, gpuHeightThreshold);
                ComparisonStats woodsStats = Compare(wodWater, alteredWoods);
                ComparisonStats woodsFlipYStats = Compare(wodWater, alteredWoodsFlipY);
                ComparisonStats cpuWodV0Stats = hasCpuWodV0 ? Compare(wodWater, cpuWodV0) : new ComparisonStats();
                HeightComparisonStats cpuWodV0HeightStats = hasCpuWodV0
                    ? CompareHeights(wodHeights, cpuWodV0Heights)
                    : new HeightComparisonStats();
                ComparisonStats cpuWodV1Stats = hasCpuWodV1 ? Compare(wodWater, cpuWodV1) : new ComparisonStats();
                HeightComparisonStats cpuWodV1HeightStats = hasCpuWodV1
                    ? CompareHeights(wodHeights, cpuWodV1Heights)
                    : new HeightComparisonStats();
                ComparisonStats cpuWodV2Stats = hasCpuWodV2 ? Compare(wodWater, cpuWodV2) : new ComparisonStats();
                HeightComparisonStats cpuWodV2HeightStats = hasCpuWodV2
                    ? CompareHeights(wodHeights, cpuWodV2Heights)
                    : new HeightComparisonStats();
                ThresholdSweepStats cpuWodV2SweepStats = hasCpuWodV2
                    ? FindBestThreshold(wodWater, cpuWodV2Heights, hDim, WodGpuWaterThresholdNormalized)
                    : new ThresholdSweepStats();
                ComparisonStats cpuWodV3Stats = hasCpuWodV3 ? Compare(wodWater, cpuWodV3) : new ComparisonStats();
                HeightComparisonStats cpuWodV3HeightStats = hasCpuWodV3
                    ? CompareHeights(wodHeights, cpuWodV3Heights)
                    : new HeightComparisonStats();
                ThresholdSweepStats cpuWodV3SweepStats = hasCpuWodV3
                    ? FindBestThreshold(wodWater, cpuWodV3Heights, hDim, WodGpuWaterThresholdNormalized)
                    : new ThresholdSweepStats();

                if (writePngs)
                {
                    EditorUtility.DisplayProgressBar("Deep Waters WOD compare", "Writing PNGs...", 0.85f);
                    WriteComparisonPng("GpuHeightThreshold", hDim, wodWater, gpuHeightThreshold);
                    WriteComparisonPng("AlteredWoodsBilinear", hDim, wodWater, alteredWoods);
                    WriteComparisonPng("AlteredWoodsBilinearFlipY", hDim, wodWater, alteredWoodsFlipY);
                    if (hasCpuWodV0)
                        WriteComparisonPng("CpuWodHeightV0", hDim, wodWater, cpuWodV0);
                    if (hasCpuWodV1)
                        WriteComparisonPng("CpuWodHeightV1", hDim, wodWater, cpuWodV1);
                    if (hasCpuWodV2)
                        WriteComparisonPng("CpuWodHeightV2", hDim, wodWater, cpuWodV2);
                    if (hasCpuWodV3)
                        WriteComparisonPng("CpuWodHeightV3GpuTex", hDim, wodWater, cpuWodV3);
                    AssetDatabase.Refresh();
                }

                lastReport = BuildReport(hDim, sampler,
                    gpuHeightStats,
                    woodsStats,
                    woodsFlipYStats,
                    hasCpuWodV0,
                    cpuWodV0Stats,
                    cpuWodV0HeightStats,
                    cpuWodV0Notes,
                    hasCpuWodV1,
                    cpuWodV1Stats,
                    cpuWodV1HeightStats,
                    cpuWodV1Notes,
                    hasCpuWodV2,
                    cpuWodV2Stats,
                    cpuWodV2HeightStats,
                    cpuWodV2Notes,
                    cpuWodV2SweepStats,
                    hasCpuWodV3,
                    cpuWodV3Stats,
                    cpuWodV3HeightStats,
                    cpuWodV3Notes,
                    cpuWodV3SweepStats);
                Debug.Log(lastReport);
                EditorUtility.DisplayDialog("WOD compare complete",
                    "Compared tile (" + mapPixelX + "," + mapPixelY + "). Details are in the Console" +
                    (writePngs ? " and Diagnostics PNGs." : "."),
                    "OK");
            }
            catch (Exception ex)
            {
                Debug.LogError("[DeepWaters.WODCompare] " + ex.Message + "\n" + ex.StackTrace);
                EditorUtility.DisplayDialog("WOD compare failed", ex.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static bool IsMonobeliskInterestingTerrainSampler(ITerrainSampler sampler)
        {
            return sampler != null && sampler.GetType().FullName == "Monobelisk.InterestingTerrainSampler";
        }

        private static byte[] GenerateWodTileData(
            DaggerfallUnity dfu,
            ITerrainSampler sampler,
            int mapPixelX,
            int mapPixelY,
            int hDim,
            float[] outHeights)
        {
            MapPixelData mapData = TerrainHelper.GetMapPixelData(dfu.ContentReader, mapPixelX, mapPixelY);
            mapData.heightmapData = new NativeArray<float>(hDim * hDim, Allocator.TempJob);
            mapData.nativeArrayList = new List<IDisposable>();

            object cacheInstance;
            MethodInfo getMethod;
            MethodInfo clearMethod;
            ResolveMonobeliskCache(out cacheInstance, out getMethod, out clearMethod);
            if (cacheInstance == null || getMethod == null)
                throw new Exception("Could not resolve Monobelisk.InterestingTerrains.tileDataCache.Get(int, int).");

            try
            {
                JobHandle handle = sampler.ScheduleGenerateSamplesJob(ref mapData);
                handle.Complete();
                AsyncGPUReadback.WaitAllRequests();
                mapData.heightmapData.CopyTo(outHeights);

                object[] getArgs = { mapPixelX, mapPixelY };
                byte[] tileData = getMethod.Invoke(cacheInstance, getArgs) as byte[];
                return tileData;
            }
            finally
            {
                if (clearMethod != null)
                {
                    try { clearMethod.Invoke(cacheInstance, null); }
                    catch { }
                }

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

        private static void ResolveMonobeliskCache(
            out object cacheInstance,
            out MethodInfo getMethod,
            out MethodInfo clearMethod)
        {
            cacheInstance = null;
            getMethod = null;
            clearMethod = null;

            FieldInfo cacheField = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType("Monobelisk.InterestingTerrains", false);
                if (t == null)
                    continue;

                cacheField = t.GetField("tileDataCache", BindingFlags.Public | BindingFlags.Static);
                if (cacheField != null)
                    break;
            }

            if (cacheField == null)
                return;

            cacheInstance = cacheField.GetValue(null);
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

        private static bool[] BuildWodWaterTruth(byte[] tileData, int hDim)
        {
            bool[] water = new bool[hDim * hDim];
            for (int y = 0; y < hDim; y++)
            {
                for (int x = 0; x < hDim; x++)
                {
                    int rowMajor = x + y * hDim;
                    water[rowMajor] = tileData[rowMajor] == 0;
                }
            }
            return water;
        }

        private static bool[] BuildGpuHeightThresholdCandidate(float[] wodHeights, int hDim)
        {
            bool[] water = new bool[hDim * hDim];
            for (int y = 0; y < hDim; y++)
            {
                for (int x = 0; x < hDim; x++)
                {
                    int rowMajor = x + y * hDim;
                    int wodHeightIndex = y + x * hDim;
                    water[rowMajor] = wodHeights[wodHeightIndex] <= WodGpuWaterThresholdNormalized;
                }
            }
            return water;
        }

        private static bool[] BuildAlteredWoodsCandidate(
            byte[] heightBuffer,
            int mapPixelX,
            int mapPixelY,
            int hDim,
            bool flipY)
        {
            if (heightBuffer == null || heightBuffer.Length < WoodsFile.MapWidth * WoodsFile.MapHeight)
                throw new Exception("WOODS.WLD height buffer is missing or truncated.");

            bool[] water = new bool[hDim * hDim];
            float denom = Mathf.Max(1, hDim - 1);

            for (int y = 0; y < hDim; y++)
            {
                float localY = y / denom;
                if (flipY)
                    localY = 1f - localY;

                for (int x = 0; x < hDim; x++)
                {
                    float localX = x / denom;
                    float globalX = mapPixelX + localX;
                    float globalY = mapPixelY + localY;
                    int rowMajor = x + y * hDim;
                    water[rowMajor] = SampleInterestingTerrainHeightBuffer(heightBuffer, globalX, globalY) <=
                                      InterestingTerrainWaterMapThreshold;
                }
            }

            return water;
        }

        private static float SampleInterestingTerrainHeightBuffer(byte[] heightBuffer, float globalX, float globalY)
        {
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

        private static ComparisonStats Compare(bool[] wodTruth, bool[] candidate)
        {
            ComparisonStats stats = new ComparisonStats();
            stats.Total = wodTruth.Length;

            for (int i = 0; i < wodTruth.Length; i++)
            {
                bool wod = wodTruth[i];
                bool cpu = candidate[i];

                if (wod)
                    stats.WodWater++;
                if (cpu)
                    stats.CandidateWater++;

                if (wod && cpu)
                    stats.MatchWater++;
                else if (!wod && !cpu)
                    stats.MatchLand++;
                else if (!wod && cpu)
                    stats.FalseWater++;
                else
                    stats.MissedWater++;
            }

            return stats;
        }

        private string BuildReport(
            int hDim,
            ITerrainSampler sampler,
            ComparisonStats gpuHeightStats,
            ComparisonStats woodsStats,
            ComparisonStats woodsFlipYStats,
            bool hasCpuWodV0,
            ComparisonStats cpuWodV0Stats,
            HeightComparisonStats cpuWodV0HeightStats,
            string cpuWodV0Notes,
            bool hasCpuWodV1,
            ComparisonStats cpuWodV1Stats,
            HeightComparisonStats cpuWodV1HeightStats,
            string cpuWodV1Notes,
            bool hasCpuWodV2,
            ComparisonStats cpuWodV2Stats,
            HeightComparisonStats cpuWodV2HeightStats,
            string cpuWodV2Notes,
            ThresholdSweepStats cpuWodV2SweepStats,
            bool hasCpuWodV3,
            ComparisonStats cpuWodV3Stats,
            HeightComparisonStats cpuWodV3HeightStats,
            string cpuWodV3Notes,
            ThresholdSweepStats cpuWodV3SweepStats)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[DeepWaters.WODCompare] tile=(" + mapPixelX + "," + mapPixelY + ")");
            sb.AppendLine("sampler=" + sampler.GetType().FullName +
                          " hDim=" + hDim +
                          " maxHeight=" + sampler.MaxTerrainHeight.ToString("F1") +
                          " ocean=" + sampler.OceanElevation.ToString("F2"));
            sb.AppendLine("WOD WATER threshold candidate: height <= " +
                          WodGpuWaterThresholdNormalized.ToString("F6") +
                          " normalized (" +
                          (WodGpuWaterThresholdNormalized * sampler.MaxTerrainHeight).ToString("F2") +
                          " m at sampler max height)");
            sb.AppendLine("Altered WOODS threshold candidate: byte <= " +
                          InterestingTerrainWaterMapThreshold.ToString("F1"));
            sb.AppendLine();
            AppendStats(sb, "GpuHeightThreshold", gpuHeightStats);
            AppendStats(sb, "AlteredWoodsBilinear", woodsStats);
            AppendStats(sb, "AlteredWoodsBilinearFlipY", woodsFlipYStats);
            AppendCpuStats(sb, sampler, "CpuWodHeightV0", hasCpuWodV0,
                cpuWodV0Stats, cpuWodV0HeightStats, cpuWodV0Notes);
            AppendCpuStats(sb, sampler, "CpuWodHeightV1", hasCpuWodV1,
                cpuWodV1Stats, cpuWodV1HeightStats, cpuWodV1Notes);
            AppendCpuStats(sb, sampler, "CpuWodHeightV2", hasCpuWodV2,
                cpuWodV2Stats, cpuWodV2HeightStats, cpuWodV2Notes);
            if (hasCpuWodV2)
                AppendThresholdSweep(sb, sampler, "CpuWodHeightV2", cpuWodV2SweepStats);
            AppendCpuStats(sb, sampler, "CpuWodHeightV3GpuTex", hasCpuWodV3,
                cpuWodV3Stats, cpuWodV3HeightStats, cpuWodV3Notes);
            if (hasCpuWodV3)
                AppendThresholdSweep(sb, sampler, "CpuWodHeightV3GpuTex", cpuWodV3SweepStats);
            if (writePngs)
                sb.AppendLine("PNGs written to " + OutputDirectory);
            return sb.ToString();
        }

        private static void AppendCpuStats(
            StringBuilder sb,
            ITerrainSampler sampler,
            string name,
            bool available,
            ComparisonStats stats,
            HeightComparisonStats heightStats,
            string notes)
        {
            if (!available)
            {
                sb.AppendLine(name + ": unavailable - " + notes);
                return;
            }

            AppendStats(sb, name, stats);
            sb.AppendLine(name + " height error: maxAbs=" +
                          heightStats.MaxAbs.ToString("F6") +
                          " normalized (" +
                          (heightStats.MaxAbs * sampler.MaxTerrainHeight).ToString("F2") +
                          " m), meanAbs=" +
                          heightStats.MeanAbs.ToString("F6") +
                          " normalized (" +
                          (heightStats.MeanAbs * sampler.MaxTerrainHeight).ToString("F2") +
                          " m)");
            sb.AppendLine(notes);
        }

        private static void AppendThresholdSweep(
            StringBuilder sb,
            ITerrainSampler sampler,
            string name,
            ThresholdSweepStats stats)
        {
            float mismatchPercent = stats.Total > 0
                ? (100f * stats.Mismatches / stats.Total)
                : 0f;
            sb.AppendLine(name + " best local threshold: height <= " +
                          stats.Threshold.ToString("F6") +
                          " normalized (" +
                          (stats.Threshold * sampler.MaxTerrainHeight).ToString("F2") +
                          " m), mismatches=" +
                          stats.Mismatches + "/" + stats.Total +
                          " (" + mismatchPercent.ToString("F2") + "%)" +
                          ", candidate water=" + stats.CandidateWater +
                          ", false water=" + stats.FalseWater +
                          ", missed water=" + stats.MissedWater);
        }

        private static void AppendStats(StringBuilder sb, string name, ComparisonStats stats)
        {
            int mismatches = stats.FalseWater + stats.MissedWater;
            float mismatchPercent = stats.Total > 0 ? (100f * mismatches / stats.Total) : 0f;

            sb.AppendLine(name + ": mismatches=" + mismatches + "/" + stats.Total +
                          " (" + mismatchPercent.ToString("F2") + "%)" +
                          ", WOD water=" + stats.WodWater +
                          ", candidate water=" + stats.CandidateWater +
                          ", false water=" + stats.FalseWater +
                          ", missed water=" + stats.MissedWater);
        }

        private static HeightComparisonStats CompareHeights(float[] wodHeights, float[] candidateHeights)
        {
            HeightComparisonStats stats = new HeightComparisonStats();
            stats.Total = Mathf.Min(wodHeights.Length, candidateHeights.Length);

            double sumAbs = 0.0;
            for (int i = 0; i < stats.Total; i++)
            {
                float abs = Mathf.Abs(wodHeights[i] - candidateHeights[i]);
                if (abs > stats.MaxAbs)
                    stats.MaxAbs = abs;
                sumAbs += abs;
            }

            stats.MeanAbs = stats.Total > 0 ? (float)(sumAbs / stats.Total) : 0f;
            return stats;
        }

        private static ThresholdSweepStats FindBestThreshold(
            bool[] wodTruth,
            float[] candidateHeights,
            int hDim,
            float defaultThreshold)
        {
            ThresholdSweepStats best = new ThresholdSweepStats();
            best.Total = wodTruth.Length;
            best.Mismatches = int.MaxValue;

            const float sweepRange = 0.0020f;
            const float sweepStep = 0.000005f;
            int steps = Mathf.CeilToInt((sweepRange * 2f) / sweepStep);

            for (int step = 0; step <= steps; step++)
            {
                float threshold = defaultThreshold - sweepRange + step * sweepStep;
                ThresholdSweepStats stats = CompareAtThreshold(
                    wodTruth, candidateHeights, hDim, threshold);

                if (stats.Mismatches < best.Mismatches ||
                    (stats.Mismatches == best.Mismatches &&
                     Mathf.Abs(stats.Threshold - defaultThreshold) <
                     Mathf.Abs(best.Threshold - defaultThreshold)))
                {
                    best = stats;
                }
            }

            return best;
        }

        private static ThresholdSweepStats CompareAtThreshold(
            bool[] wodTruth,
            float[] candidateHeights,
            int hDim,
            float threshold)
        {
            ThresholdSweepStats stats = new ThresholdSweepStats();
            stats.Total = wodTruth.Length;
            stats.Threshold = threshold;

            for (int y = 0; y < hDim; y++)
            {
                for (int x = 0; x < hDim; x++)
                {
                    int rowMajor = x + y * hDim;
                    int heightIndex = y + x * hDim;
                    bool wod = wodTruth[rowMajor];
                    bool cpu = candidateHeights[heightIndex] <= threshold;

                    if (cpu)
                        stats.CandidateWater++;

                    if (!wod && cpu)
                        stats.FalseWater++;
                    else if (wod && !cpu)
                        stats.MissedWater++;
                }
            }

            stats.Mismatches = stats.FalseWater + stats.MissedWater;
            return stats;
        }

        private void WriteComparisonPng(string name, int hDim, bool[] wodTruth, bool[] candidate)
        {
            string absoluteDir = Path.Combine(Directory.GetCurrentDirectory(), OutputDirectory);
            if (!Directory.Exists(absoluteDir))
                Directory.CreateDirectory(absoluteDir);

            Texture2D texture = new Texture2D(hDim, hDim, TextureFormat.RGBA32, false, true);
            Color32 matchLand = new Color32(24, 24, 24, 255);
            Color32 matchWater = new Color32(45, 120, 230, 255);
            Color32 falseWater = new Color32(235, 60, 105, 255);
            Color32 missedWater = new Color32(255, 210, 45, 255);

            for (int y = 0; y < hDim; y++)
            {
                int imageY = hDim - 1 - y;
                for (int x = 0; x < hDim; x++)
                {
                    int i = x + y * hDim;
                    bool wod = wodTruth[i];
                    bool cpu = candidate[i];

                    Color32 color;
                    if (wod && cpu)
                        color = matchWater;
                    else if (!wod && !cpu)
                        color = matchLand;
                    else if (!wod && cpu)
                        color = falseWater;
                    else
                        color = missedWater;

                    texture.SetPixel(x, imageY, color);
                }
            }

            texture.Apply();
            string fileName = "WodWaterCompare_" + mapPixelX + "_" + mapPixelY + "_" + name + ".png";
            string absolutePath = Path.Combine(absoluteDir, fileName);
            File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            DestroyImmediate(texture);
        }

        private struct ComparisonStats
        {
            public int Total;
            public int WodWater;
            public int CandidateWater;
            public int MatchWater;
            public int MatchLand;
            public int FalseWater;
            public int MissedWater;
        }

        private struct HeightComparisonStats
        {
            public int Total;
            public float MaxAbs;
            public float MeanAbs;
        }

        private struct ThresholdSweepStats
        {
            public int Total;
            public float Threshold;
            public int Mismatches;
            public int CandidateWater;
            public int FalseWater;
            public int MissedWater;
        }
    }
}
#endif
