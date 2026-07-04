// Project:         Iliac Puddle No More
// License:         MIT

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using DaggerfallConnect;
using DaggerfallWorkshop;
using UnityEngine;

namespace DeepWaters.Editor
{
    /// <summary>
    /// CPU-side first pass at WOD's TerrainComputer height path.
    /// This intentionally ports the wilderness height generation only:
    /// biome/deriv maps + procedural terrain noise. Location flattening,
    /// port shaping, and BasicRoads smoothing are left out for now so the
    /// diagnostic can tell us exactly how much they matter.
    /// </summary>
    internal static class WodCpuWaterClassifierV0
    {
        private const float TerrainX = 999f;
        private const float TerrainY = 499f;
        private const float CoastlineVar = 40f;
        private const float BaseHeightMin = 100f;
        private const float BaseHeightMax = 800f;
        private const float BaseHeightHill = 1000f;
        private const float BaseHeightMountain = 1200f;
        private const float NewHeight = 5000f;
        private const float MaxTerrainHeight = 2308.5f;
        private const float BaseHeightScale = 8f;
        private const float NoiseMapScale = 4f;
        private const float ScaledOceanElevation = 27.2f;

        public static bool TryBuildHeightsGpuTextureV3(
            int mapPixelX,
            int mapPixelY,
            int hDim,
            float[] heights,
            out string notes)
        {
            return TryBuildHeightsInternal(mapPixelX, mapPixelY, hDim, true, true,
                TextureSamplingMode.GpuLinearClamp,
                heights, out notes);
        }

        private static bool TryBuildHeightsInternal(
            int mapPixelX,
            int mapPixelY,
            int hDim,
            bool includeLocationPort,
            bool includeRoadSmoothing,
            TextureSamplingMode textureSamplingMode,
            float[] heights,
            out string notes)
        {
            notes = string.Empty;
            if (heights == null || heights.Length < hDim * hDim)
            {
                notes = "CPU WOD height buffer is null or too small.";
                return false;
            }

            CpuConfig config;
            if (!CpuConfig.TryCreate(mapPixelX, mapPixelY, includeLocationPort, includeRoadSmoothing,
                    textureSamplingMode,
                    out config, out notes))
                return false;

            Vector2 terrainSize = new Vector2(hDim, hDim);
            Vector2 terrainPosition = new Vector2(mapPixelX, TerrainY - mapPixelY) * hDim;
            CpuComputer computer = new CpuComputer(config, terrainPosition, terrainSize, hDim,
                includeLocationPort, includeRoadSmoothing);

            for (int y = 0; y < hDim; y++)
            {
                for (int x = 0; x < hDim; x++)
                {
                    int heightIndex = y + x * hDim;
                    heights[heightIndex] = Mathf.Max(
                        BaseHeightMin / NewHeight,
                        computer.GetHeightSample(new Vector2(x, y)));
                }
            }

            if (textureSamplingMode == TextureSamplingMode.GpuLinearClamp)
                notes = "CPU WOD v3: v2 terrain path with GPU-style linear clamp texture sampling.";
            else if (includeRoadSmoothing)
                notes = "CPU WOD v2: wilderness height path plus nearby-location, port shaping, and BasicRoads road smoothing.";
            else if (includeLocationPort)
                notes = "CPU WOD v1: wilderness height path plus nearby-location and port shaping; BasicRoads smoothing omitted.";
            else
                notes = "CPU WOD v0: wilderness height path only; location, port, and road shaping omitted.";
            return true;
        }

        private enum TextureSamplingMode
        {
            LegacyUnitInterval,
            GpuLinearClamp,
        }

        private sealed class CpuComputer
        {
            private readonly CpuConfig config;
            private readonly Vector2 terrainPosition;
            private readonly Vector2 terrainSize;
            private readonly int hDim;
            private readonly bool includeLocationPort;
            private readonly bool includeRoadSmoothing;

            public CpuComputer(
                CpuConfig config,
                Vector2 terrainPosition,
                Vector2 terrainSize,
                int hDim,
                bool includeLocationPort,
                bool includeRoadSmoothing)
            {
                this.config = config;
                this.terrainPosition = terrainPosition;
                this.terrainSize = terrainSize;
                this.hDim = hDim;
                this.includeLocationPort = includeLocationPort;
                this.includeRoadSmoothing = includeRoadSmoothing;
            }

            public float GetHeightSample(Vector2 id)
            {
                if (!includeLocationPort)
                {
                    float extraHeightV0;
                    float bumpsV0;
                    float baseHeightV0 = GetBaseHeight(id, out extraHeightV0, out bumpsV0);

                    // In WOD's shader, wilderness tiles receive no location
                    // bump weight, and BasicRoads is effectively zero for this
                    // v0 port. That leaves the generated terrain contribution
                    // at 1.3x.
                    baseHeightV0 += extraHeightV0 * 1.3f;
                    return baseHeightV0;
                }

                float heightmapMaxIndex = hDim - 1f;
                Vector2 tileUv = id / heightmapMaxIndex;
                Vector2 pos = terrainPosition + tileUv * hDim;
                Vector2 worldSize = Vector2.Scale(terrainSize, new Vector2(TerrainX, TerrainY));
                Vector2 uvOffset = Divide(new Vector2(-0.25f, -1.1f), new Vector2(TerrainX, TerrainY));
                Vector2 portUv = Clamp01(Divide(pos, worldSize)) + uvOffset;

                Color portSample = config.portMap.SampleBilinear(portUv);
                Color roadSample = config.roadMap != null
                    ? config.roadMap.SampleBilinear(portUv)
                    : Color.black;
                float portHeight = portSample.r;
                float seaHeight = portSample.b;
                float altHeight = portSample.g;
                float smoothRoads = roadSample.r;

                float locationHeight;
                float bumpWeight;
                float locationWeight = LocationWeight(pos, portHeight, out locationHeight, out bumpWeight);
                float wildernessWeight = bumpWeight;
                bumpWeight *= Mathf.Pow(locationWeight, 0.25f);

                locationHeight *= 0.75f;

                float extraHeight;
                float bumps;
                float baseHeight = GetBaseHeight(id, out extraHeight, out bumps);
                bumps *= bumpWeight;

                float roadWeight = includeRoadSmoothing ? GetRoadWeight(pos) : 0f;

                PerlinParams transitionParams = new PerlinParams
                {
                    pos = pos,
                    octaves = 16,
                    frequency = 0.01f,
                    amplitude = 1f,
                    lacunarity = 1.6f,
                    persistence = 0.6f,
                    offset = Vector2.zero,
                    maxHeight = NewHeight,
                };
                float transition = Mathf.Abs(SimplePerlin(transitionParams));

                float transitionWeight = Mathf.Pow(locationWeight, 0.5f) * wildernessWeight;
                locationWeight = Saturate(locationWeight - 0.5f) / 0.5f;

                float portLerp = Saturate(portHeight * 10f);
                float portLocationWeight = PortLocationWeight(pos, portHeight, out locationHeight, out bumpWeight);

                float portLocationHeight = 0.021f;
                float regLocationHeight = Mathf.Max(
                    locationHeight + (BaseHeightMin / 10000f) + (altHeight * 0.1f),
                    0.021f);

                locationHeight = seaHeight > 0f
                    ? 0f
                    : Mathf.Lerp(regLocationHeight, portLocationHeight, portLerp);

                if (baseHeight <= 0.021f)
                {
                    float baseHeight1 = Mathf.Lerp(baseHeight, locationHeight, locationWeight);
                    float baseHeight2 = Mathf.Lerp(baseHeight, locationHeight, portLocationWeight);
                    baseHeight = Mathf.Lerp(baseHeight1, baseHeight2, portLerp);
                }
                else
                {
                    baseHeight = Mathf.Lerp(baseHeight, locationHeight, locationWeight);
                }

                float landWeight = Saturate(((baseHeight - 100f) / NewHeight) / 5f);
                transitionWeight *= landWeight;

                float transitionHeight = Mathf.Min(Mathf.Abs(baseHeight - locationHeight), 250f / NewHeight);
                baseHeight += transition * transitionHeight * transitionWeight * 0.5f;

                extraHeight *= 1f - locationWeight;
                baseHeight += (extraHeight + bumps) * Saturate(1.3f - roadWeight * smoothRoads);

                return baseHeight;
            }

            private float GetBaseHeight(Vector2 id, out float extraHeight, out float bumps)
            {
                float heightmapMaxIndex = terrainSize.x - 1f;
                Vector2 tileUv = id / heightmapMaxIndex;
                Vector2 pos = terrainPosition + Vector2.Scale(tileUv, terrainSize);
                Vector2 worldUv = Divide(pos, Vector2.Scale(terrainSize, new Vector2(TerrainX, TerrainY)));

                BiomeWeights w = GetBiomeWeights(worldUv);
                Color colorVar = ColorPerlin(MakePerlin(config.colorVar, pos));

                SwissParams landVarParams = MakeSwiss(config.swissCell, pos);
                float landVar = Mathf.Abs(SwissCellNoise(landVarParams)) *
                                SafeDivide(NewHeight, Mathf.Max(0.0001f, config.swissCell.maxHeight));

                float beachWeight = SmoothStep(0f, 1f,
                    Saturate(w.land / Mathf.Lerp(15f / 255f, 45f / 255f, landVar)));

                float iqMntWeight = Saturate(1f - (w.mountain / 0.25f));
                float swissMntWeight = Saturate(w.mountain * (1f / 0.4f) - 0.6f);
                float jordanMntWeight = Saturate((1f - iqMntWeight) * (1f - swissMntWeight));

                w.mountain = Saturate(w.mountain / 0.25f);
                w.mountain = SmoothStep(0.4f, 0.6f, w.mountain);
                w.hills *= (1f - w.mountain) * (1f - w.desert);
                float hillNoiseFactor = Saturate(w.hills / 0.1f);

                float plateauWeight = Mathf.Max(colorVar.a, w.desert) * (1f - w.mountainBase);
                float duneWeight = w.desert * (1f - w.mountain);

                float baseHeightMax = Mathf.Lerp(BaseHeightMax, BaseHeightHill, w.hills);
                baseHeightMax = Mathf.Lerp(baseHeightMax, BaseHeightMountain, w.mountain);

                float baseHeight = Mathf.Lerp((BaseHeightMin - 1f) / NewHeight,
                    baseHeightMax / NewHeight, w.land);

                float beachMin = Saturate(w.land - Mathf.Lerp(5f, 15f, landVar) / NewHeight);
                float landWeight = Saturate(beachMin /
                    (Mathf.Lerp(CoastlineVar * 10f, CoastlineVar * 20f, landVar) / NewHeight));

                PerlinParams bumpParams = MakePerlin(config.perlinDune, pos);
                PerlinParams bumpsParams = bumpParams;
                float bump = PositivePerlin(bumpParams) * landWeight * (1f - w.mountain);

                bumpsParams.octaves = 8;
                bumpsParams.frequency *= 64f;
                bumpsParams.maxHeight = 6.5f;
                bumps = SimplePerlin(bumpsParams) * landWeight;

                SwissParams rockyBaseParams = MakeSwiss(config.swissCell, pos);
                float rockyBase = SwissCellNoise(rockyBaseParams) * landWeight * w.mountain * 0.25f;

                SwissParams duneParams = MakeSwiss(config.swissDune, pos);
                float dunes = SwissMountainsGen(duneParams);

                SwissParams swissParams = MakeSwiss(config.swissFolded, pos);
                float sm = Saturate(SwissMountains(swissParams));
                float swissMnt = sm * landWeight;

                JordanParams jordanParams = MakeJordan(config.jordanFolded, pos);
                float defaultWarp = jordanParams.warp;
                float defaultDampScale = jordanParams.dampScale;
                jordanParams.warp = Mathf.Lerp(defaultWarp, 200f, plateauWeight);
                jordanParams.dampScale = Mathf.Lerp(defaultDampScale, 1f, plateauWeight);
                jordanParams.warp = Mathf.Lerp(jordanParams.warp, -defaultWarp, hillNoiseFactor);
                jordanParams.persistence = Mathf.Lerp(jordanParams.persistence, 0.2f, hillNoiseFactor);
                jordanParams.damp = Mathf.Lerp(jordanParams.damp, 1f, hillNoiseFactor);
                jordanParams.dampScale = Mathf.Lerp(jordanParams.dampScale, 1f, hillNoiseFactor);
                jordanParams.maxHeight = Mathf.Lerp(jordanParams.maxHeight, 800f, hillNoiseFactor);

                float jordanMnt = JordanMountains(jordanParams) * landWeight;

                SwissParams iqParams = MakeSwiss(config.iqMountain, pos);
                float iqMnt = IQMountains(iqParams) * landWeight * w.mountain;
                iqMnt = Mathf.Lerp(iqMnt, jordanMnt, plateauWeight);

                float mnt = swissMnt * swissMntWeight +
                            jordanMnt * jordanMntWeight +
                            iqMnt * iqMntWeight;
                float hill = jordanMnt * w.hills + dunes * w.desert;

                baseHeight += bump + rockyBase;

                SwissParams faultParams = MakeSwiss(config.swissFaults, pos);
                float subtraction = 1f - SwissTime(faultParams);
                subtraction *= Saturate((colorVar.g * colorVar.b) * 2f - 1f) * w.mountain;

                extraHeight = Saturate(mnt + hill) - landVar * (10f / NewHeight);
                extraHeight -= subtraction *
                               Mathf.Min(extraHeight + (baseHeight - 115f / NewHeight), 500f / NewHeight);

                return baseHeight;
            }

            private BiomeWeights GetBiomeWeights(Vector2 worldUv)
            {
                Color tex = config.biomeMap.SampleBilinear(worldUv);
                Color dTex = config.derivMap.SampleBilinear(worldUv);

                float mountain = SmoothStep(0f, 1f, tex.r);
                float loResBaseHeight = ((dTex.b * 255f) * (BaseHeightScale + NoiseMapScale));
                loResBaseHeight = Saturate((loResBaseHeight - ScaledOceanElevation) / MaxTerrainHeight);

                BiomeWeights weights = new BiomeWeights();
                weights.mountain = mountain;
                weights.mountainBase = mountain;
                weights.desert = tex.g;
                weights.hills = tex.b;
                weights.land = loResBaseHeight;
                return weights;
            }

            private float LocationWeight(
                Vector2 pos,
                float portHeight,
                out float basemapHeight,
                out float wildernessWeight)
            {
                return LocationWeightCore(pos, portHeight, false, out basemapHeight, out wildernessWeight);
            }

            private float PortLocationWeight(
                Vector2 pos,
                float portHeight,
                out float basemapHeight,
                out float wildernessWeight)
            {
                return LocationWeightCore(pos, portHeight, true, out basemapHeight, out wildernessWeight);
            }

            private float LocationWeightCore(
                Vector2 pos,
                float portHeight,
                bool portMode,
                out float basemapHeight,
                out float wildernessWeight)
            {
                float weight = 0f;
                float narrowWeight = 0f;
                float height = 0f;
                float nearestHeight = 0f;
                float nearestDist = 99999999.9f;
                float portLerp = Saturate(portHeight * 10f);
                Vector2 worldSize = Vector2.Scale(terrainSize, new Vector2(TerrainX, TerrainY));
                Vector2 uvOffset = Divide(new Vector2(-1.5f, 0.5f), new Vector2(TerrainX, TerrainY));
                Vector2 fallbackUv = Clamp01(Divide(pos, worldSize)) + uvOffset;
                float fallbackHeight = config.baseHeightmap.SampleBilinear(fallbackUv).r;

                if (config.locationRects == null || config.locationRects.Length == 0)
                {
                    wildernessWeight = 1f;
                    basemapHeight = fallbackHeight;
                    return 0f;
                }

                for (int i = 0; i < config.locationRects.Length; i++)
                {
                    Rect rect = config.locationRects[i];
                    Vector2 locPos = Floor(rect.position);
                    Vector2 locSize = Floor(rect.size);
                    Vector2 locCenter = locPos + locSize * 0.5f;

                    Vector2 uv = Clamp01(Divide(locCenter, worldSize)) + uvOffset;
                    float baseHeight = config.baseHeightmap.SampleBilinear(uv).r;

                    float fadeDist = portMode ? Mathf.Lerp(64f, 3f, portLerp) : 64f;

                    Vector2 nearestEdgeNarrow;
                    Vector2 nearestEdgeRect = NearestRectangularEdgePoint(
                        pos, locPos, locSize, out nearestEdgeNarrow);
                    Vector2 nearestEdgeRad = NearestRadialEdgePoint(
                        pos, locPos, locSize, out nearestEdgeNarrow);
                    Vector2 nearestEdge = Vector2.Lerp(nearestEdgeRad, nearestEdgeRect, portLerp);

                    PerlinParams noiseParams = MakePerlin(config.perlinDune, nearestEdgeNarrow);
                    noiseParams.amplitude = 1f;
                    noiseParams.octaves = 8;
                    noiseParams.frequency = 0.05f;
                    noiseParams.lacunarity = 2f;
                    noiseParams.persistence = 0.73f;
                    noiseParams.maxHeight = NewHeight;
                    float noise = Saturate(PositivePerlin(noiseParams));

                    float maxNarrowDist = Mathf.Lerp(5f, 15f, noise);
                    float narrowDist = Vector2.Distance(pos, nearestEdgeNarrow);
                    narrowWeight += 1f - Saturate(narrowDist / maxNarrowDist);

                    Vector2 edgeDir = NormalizeSafe(nearestEdge - locCenter);
                    Vector2 samplePt = nearestEdge + edgeDir * 5f;

                    noiseParams = MakePerlin(config.perlinDune, samplePt);
                    noiseParams.octaves = 16;
                    noiseParams.frequency = 0.007f;
                    noiseParams.persistence = 0.73f;
                    noiseParams.maxHeight = NewHeight;
                    noise = Mathf.Pow(Saturate(PositivePerlin(noiseParams)), 2f);

                    fadeDist += fadeDist * 8f * noise;
                    float edgeDist = Vector2.Distance(nearestEdge, pos);

                    float w = 1f - Saturate(edgeDist / fadeDist);
                    w = Mathf.Pow(w, 2f);
                    w = SmoothStep(0f, 1f, w);

                    weight += w;

                    if (edgeDist < nearestDist)
                    {
                        nearestHeight = baseHeight;
                        nearestDist = edgeDist;
                    }

                    height += baseHeight * w;
                }

                wildernessWeight = 1f - Saturate(narrowWeight);

                basemapHeight = weight > 0f
                    ? Mathf.Max(80f / NewHeight, height / weight)
                    : fallbackHeight;
                weight = Saturate(weight);
                basemapHeight = Mathf.Lerp(basemapHeight, nearestHeight, Saturate(narrowWeight));
                basemapHeight = Saturate(basemapHeight);
                basemapHeight = Mathf.Lerp(fallbackHeight, basemapHeight, weight);

                return weight;
            }

            private Vector2 NearestEdgePoint(Vector2 pos, Vector2 locPos, Vector2 locSize)
            {
                Vector2 min = locPos;
                Vector2 max = locPos + locSize;

                bool insideY = pos.y > min.y && pos.y < max.y;
                bool insideX = pos.x > min.x && pos.x < max.x;

                if (insideX && insideY)
                    return pos;

                if (insideY)
                {
                    Vector2 left = new Vector2(min.x, pos.y);
                    Vector2 right = new Vector2(max.x, pos.y);
                    return Vector2.Distance(pos, left) < Vector2.Distance(pos, right) ? left : right;
                }

                if (insideX)
                {
                    Vector2 bottom = new Vector2(pos.x, min.y);
                    Vector2 top = new Vector2(pos.x, max.y);
                    return Vector2.Distance(pos, bottom) < Vector2.Distance(pos, top) ? bottom : top;
                }

                Vector2 c1 = new Vector2(min.x, min.y);
                Vector2 c2 = new Vector2(max.x, min.y);
                Vector2 c3 = new Vector2(min.x, max.y);
                Vector2 c4 = new Vector2(max.x, max.y);

                float d1 = Vector2.Distance(pos, c1);
                float d2 = Vector2.Distance(pos, c2);
                float d3 = Vector2.Distance(pos, c3);
                float d4 = Vector2.Distance(pos, c4);

                if (d1 < d2 && d1 < d3 && d1 < d4)
                    return c1;
                if (d2 < d3 && d2 < d4)
                    return c2;
                return d3 < d4 ? c3 : c4;
            }

            private Vector2 NearestRadialEdgePoint(
                Vector2 pos,
                Vector2 locPos,
                Vector2 locSize,
                out Vector2 nearestEdgeNarrow)
            {
                Vector2 locCenter = locPos + locSize * 0.5f;
                float dist = Vector2.Distance(locCenter, pos);
                float edgeDist = Vector2.Distance(locCenter, locPos);
                Vector2 dir = NormalizeSafe(pos - locCenter);

                Vector2 nep = NearestEdgePoint(pos, locPos, locSize);
                nearestEdgeNarrow = nep;

                PerlinParams noiseParams = MakePerlin(config.perlinDune, nep + Vector2.one * 500f);
                noiseParams.octaves = 8;
                noiseParams.frequency = 0.015f;
                noiseParams.persistence = 0.7f;
                noiseParams.maxHeight = NewHeight;
                float noise = Mathf.Pow(Saturate(PositivePerlin(noiseParams)), 2f);

                edgeDist += 7.5f * noise;

                if (dist < edgeDist)
                    return pos;

                return locCenter + dir * edgeDist;
            }

            private static Vector2 NearestRectangularEdgePoint(
                Vector2 pos,
                Vector2 locPos,
                Vector2 locSize,
                out Vector2 nearestEdgeNarrow)
            {
                Vector2 halfLocSize = locSize * 0.5f;
                Vector2 locCenter = locPos + halfLocSize;
                Vector2 offset = pos - locCenter;
                Vector2 clampedOffset = new Vector2(
                    Mathf.Clamp(offset.x, -halfLocSize.x, halfLocSize.x),
                    Mathf.Clamp(offset.y, -halfLocSize.y, halfLocSize.y));

                nearestEdgeNarrow = locCenter + clampedOffset;
                return nearestEdgeNarrow;
            }

            private float GetRoadWeight(Vector2 pos)
            {
                if (config.roadData.N_E_S_W == null ||
                    config.roadData.NW_NE_SW_SE == null ||
                    config.tileableNoise == null)
                    return 0f;

                float weight = 0f;
                for (int x = -1; x <= 1; x++)
                {
                    for (int y = -1; y <= 1; y++)
                        weight += GetRoadWeightForMapPixel(pos, x, y);
                }

                return Saturate(weight);
            }

            private float GetRoadWeightForMapPixel(Vector2 pos, int offsetX, int offsetY)
            {
                int i = (offsetX + 1) + (offsetY + 1) * 3;
                float f = terrainSize.x;
                float h = f * 0.5f;
                Vector2 tp = terrainPosition +
                             Vector2.Scale(
                                 Vector2.Scale(new Vector2(offsetX, offsetY), terrainSize),
                                 new Vector2(1f, -1f));

                Vector2 n = tp + new Vector2(h, f);
                Vector2 e = tp + new Vector2(f, h);
                Vector2 s = tp + new Vector2(h, 0f);
                Vector2 w = tp + new Vector2(0f, h);

                Vector2 nw = tp + new Vector2(0f, f);
                Vector2 ne = tp + new Vector2(f, f);
                Vector2 sw = tp + new Vector2(0f, 0f);
                Vector2 se = tp + new Vector2(f, 0f);

                Vector2 c = tp + new Vector2(h, h);
                float weight = 0f;

                Vector4 nesw = config.roadData.N_E_S_W[i];
                Vector4 diagonals = config.roadData.NW_NE_SW_SE[i];

                if (nesw.x > 0.5f) weight += GetRoadSegmentWeight(pos, n, c);
                if (nesw.y > 0.5f) weight += GetRoadSegmentWeight(pos, e, c);
                if (nesw.z > 0.5f) weight += GetRoadSegmentWeight(pos, s, c);
                if (nesw.w > 0.5f) weight += GetRoadSegmentWeight(pos, w, c);

                if (diagonals.x > 0.5f) weight += GetRoadSegmentWeight(pos, nw, c);
                if (diagonals.y > 0.5f) weight += GetRoadSegmentWeight(pos, ne, c);
                if (diagonals.z > 0.5f) weight += GetRoadSegmentWeight(pos, sw, c);
                if (diagonals.w > 0.5f) weight += GetRoadSegmentWeight(pos, se, c);

                return Saturate(weight);
            }

            private float GetRoadSegmentWeight(Vector2 pos, Vector2 roadStart, Vector2 roadEnd)
            {
                Vector2 roadPoint = NearestPointInLine(pos, roadStart, roadEnd);
                Vector2 dir = NormalizeSafe(pos - roadPoint);
                Vector2 samplePos = roadPoint + dir * 15f;
                samplePos -= Vector2.Scale(Floor(Divide(samplePos, terrainSize)), terrainSize);
                samplePos = Divide(samplePos, terrainSize);

                float noise = config.tileableNoise.SampleBilinear(samplePos).r;
                float fadeDist = 128f + 128f * noise;
                float dist = Vector2.Distance(roadPoint, pos);

                float w = SmoothStep(0f, 1f, 0.77f - Mathf.Clamp01(dist / fadeDist));
                return w * w;
            }

            private static Vector2 NearestPointInLine(Vector2 pos, Vector2 lineStart, Vector2 lineEnd)
            {
                Vector2 line = lineEnd - lineStart;
                float len = line.magnitude;
                if (len <= 1e-6f)
                    return lineStart;

                line /= len;
                Vector2 v = pos - lineStart;
                float d = Vector2.Dot(v, line);
                d = Mathf.Clamp(d, 0f, len);
                return lineStart + line * d;
            }
        }

        private sealed class CpuConfig
        {
            public CpuTexture biomeMap;
            public CpuTexture derivMap;
            public CpuTexture portMap;
            public CpuTexture roadMap;
            public CpuTexture tileableNoise;
            public CpuTexture baseHeightmap;
            public Rect[] locationRects;
            public RoadDataSnapshot roadData;

            public PerlinDef perlinDune;
            public PerlinDef colorVar;
            public SwissDef swissFolded;
            public SwissDef iqMountain;
            public SwissDef swissCell;
            public SwissDef swissFaults;
            public SwissDef swissDune;
            public JordanDef jordanFolded;

            private static SharedResources legacyResources;
            private static SharedResources gpuResources;

            public static bool TryCreate(
                int mapPixelX,
                int mapPixelY,
                bool includeLocationPort,
                bool includeRoadSmoothing,
                TextureSamplingMode textureSamplingMode,
                out CpuConfig config,
                out string error)
            {
                config = null;
                error = string.Empty;

                SharedResources shared;
                if (!TryGetSharedResources(textureSamplingMode, includeLocationPort, out shared, out error))
                    return false;

                config = new CpuConfig();
                config.biomeMap = shared.biomeMap;
                config.derivMap = shared.derivMap;
                config.portMap = shared.portMap;
                config.roadMap = shared.roadMap;
                config.tileableNoise = shared.tileableNoise;
                config.baseHeightmap = shared.baseHeightmap;
                config.perlinDune = shared.perlinDune;
                config.colorVar = shared.colorVar;
                config.swissFolded = shared.swissFolded;
                config.iqMountain = shared.iqMountain;
                config.swissCell = shared.swissCell;
                config.swissFaults = shared.swissFaults;
                config.swissDune = shared.swissDune;
                config.jordanFolded = shared.jordanFolded;
                config.locationRects = includeLocationPort
                    ? BuildLocationRects(mapPixelX, mapPixelY)
                    : new Rect[0];
                config.roadData = includeRoadSmoothing
                    ? ResolveRoadData(mapPixelX, mapPixelY)
                    : RoadDataSnapshot.Empty();
                return true;
            }

            private static bool TryGetSharedResources(
                TextureSamplingMode textureSamplingMode,
                bool requireLocationPort,
                out SharedResources resources,
                out string error)
            {
                resources = textureSamplingMode == TextureSamplingMode.GpuLinearClamp
                    ? gpuResources
                    : legacyResources;
                if (resources != null)
                {
                    if (requireLocationPort && (resources.portMap == null || resources.baseHeightmap == null))
                    {
                        error = "Cached WOD portMap or TerrainComputer.baseHeightmap is not initialized.";
                        return false;
                    }

                    error = string.Empty;
                    return true;
                }

                error = string.Empty;
                Type interestingTerrainsType = ResolveMonobeliskType("Monobelisk.InterestingTerrains");
                if (interestingTerrainsType == null)
                {
                    error = "Monobelisk.InterestingTerrains type is not loaded.";
                    return false;
                }

                Texture2D biomeMap = GetStaticField<Texture2D>(interestingTerrainsType, "biomeMap");
                Texture2D derivMap = GetStaticField<Texture2D>(interestingTerrainsType, "derivMap");
                Texture2D portMap = GetStaticField<Texture2D>(interestingTerrainsType, "portMap");
                Texture2D roadMap = GetStaticField<Texture2D>(interestingTerrainsType, "roadMap");
                Texture2D tileableNoise = GetStaticField<Texture2D>(interestingTerrainsType, "tileableNoise");
                if (biomeMap == null || derivMap == null)
                {
                    error = "WOD biomeMap or derivMap texture is not initialized.";
                    return false;
                }

                Type terrainComputerType = ResolveMonobeliskType("Monobelisk.TerrainComputer");
                Texture2D baseHeightmap = terrainComputerType != null
                    ? GetStaticField<Texture2D>(terrainComputerType, "baseHeightmap")
                    : null;
                if (requireLocationPort && (portMap == null || baseHeightmap == null))
                {
                    error = "WOD portMap or TerrainComputer.baseHeightmap is not initialized.";
                    return false;
                }

                object instance = GetStaticField<object>(interestingTerrainsType, "instance");
                object csParams = instance != null ? GetInstanceField<object>(instance, "csParams") : null;
                if (csParams == null)
                {
                    error = "WOD TerrainComputerParams are not initialized.";
                    return false;
                }

                resources = new SharedResources();
                resources.biomeMap = CpuTexture.FromTexture(biomeMap, textureSamplingMode);
                resources.derivMap = CpuTexture.FromTexture(derivMap, textureSamplingMode);
                resources.portMap = portMap != null ? CpuTexture.FromTexture(portMap, textureSamplingMode) : null;
                resources.roadMap = roadMap != null ? CpuTexture.FromTexture(roadMap, textureSamplingMode) : null;
                resources.tileableNoise = tileableNoise != null
                    ? CpuTexture.FromTexture(tileableNoise, textureSamplingMode)
                    : null;
                resources.baseHeightmap = baseHeightmap != null
                    ? CpuTexture.FromTexture(baseHeightmap, textureSamplingMode)
                    : null;

                resources.perlinDune = ReadPerlin(csParams, "perlinDune");
                resources.colorVar = ReadPerlin(csParams, "colorVar");
                resources.swissFolded = ReadSwiss(csParams, "swissFolded");
                resources.iqMountain = ReadSwiss(csParams, "iqMountain");
                resources.swissCell = ReadSwiss(csParams, "swissCell");
                resources.swissFaults = ReadSwiss(csParams, "swissFaults");
                resources.swissDune = ReadSwiss(csParams, "swissDune");
                resources.jordanFolded = ReadJordan(csParams, "jordanFolded");

                if (textureSamplingMode == TextureSamplingMode.GpuLinearClamp)
                    gpuResources = resources;
                else
                    legacyResources = resources;

                return true;
            }

            private sealed class SharedResources
            {
                public CpuTexture biomeMap;
                public CpuTexture derivMap;
                public CpuTexture portMap;
                public CpuTexture roadMap;
                public CpuTexture tileableNoise;
                public CpuTexture baseHeightmap;

                public PerlinDef perlinDune;
                public PerlinDef colorVar;
                public SwissDef swissFolded;
                public SwissDef iqMountain;
                public SwissDef swissCell;
                public SwissDef swissFaults;
                public SwissDef swissDune;
                public JordanDef jordanFolded;
            }

            private static RoadDataSnapshot ResolveRoadData(int mapPixelX, int mapPixelY)
            {
                try
                {
                    Type basicRoadsType = ResolveMonobeliskType("Monobelisk.Compatibility.BasicRoadsUtils");
                    if (basicRoadsType == null)
                        return RoadDataSnapshot.Empty();

                    MethodInfo method = basicRoadsType.GetMethod("GetRoadData",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new Type[] { typeof(int), typeof(int) },
                        null);
                    if (method == null)
                        return RoadDataSnapshot.Empty();

                    object result = method.Invoke(null, new object[] { mapPixelX, mapPixelY });
                    if (result == null)
                        return RoadDataSnapshot.Empty();

                    Type resultType = result.GetType();
                    FieldInfo diagonalsField = resultType.GetField("NW_NE_SW_SE",
                        BindingFlags.Public | BindingFlags.Instance);
                    FieldInfo cardinalsField = resultType.GetField("N_E_S_W",
                        BindingFlags.Public | BindingFlags.Instance);
                    Vector4[] diagonals = diagonalsField != null
                        ? diagonalsField.GetValue(result) as Vector4[]
                        : null;
                    Vector4[] cardinals = cardinalsField != null
                        ? cardinalsField.GetValue(result) as Vector4[]
                        : null;

                    if (diagonals == null || cardinals == null ||
                        diagonals.Length < 9 || cardinals.Length < 9)
                        return RoadDataSnapshot.Empty();

                    return new RoadDataSnapshot
                    {
                        NW_NE_SW_SE = diagonals,
                        N_E_S_W = cardinals,
                    };
                }
                catch
                {
                    return RoadDataSnapshot.Empty();
                }
            }

            private static Rect[] BuildLocationRects(int mapPixelX, int mapPixelY)
            {
                DaggerfallUnity dfu = DaggerfallUnity.Instance;
                if (dfu == null || dfu.ContentReader == null)
                    return new Rect[0];

                List<Rect> locations = new List<Rect>();
                const int searchSize = 16;

                for (int ox = -searchSize; ox <= searchSize; ox++)
                {
                    for (int oy = -searchSize; oy <= searchSize; oy++)
                    {
                        int mpx = mapPixelX + ox;
                        int mpy = mapPixelY + oy;

                        try
                        {
                            MapPixelData mapPixelData = TerrainHelper.GetMapPixelData(
                                dfu.ContentReader, mpx, mpy);
                            if (!mapPixelData.hasLocation)
                                continue;

                            DFLocation location = dfu.ContentReader.MapFileReader.GetLocation(
                                mapPixelData.mapRegionIndex, mapPixelData.mapLocationIndex);
                            Rect rect = DaggerfallLocation.GetLocationRect(location);
                            rect = WorldCoordRectToTerrainRect(rect);
                            if (rect.width == 0f || rect.height == 0f)
                                continue;

                            locations.Add(ExpandInEachDirection(rect, 1f));
                        }
                        catch
                        {
                            // WOD probes a wide neighbourhood. Keep the CPU
                            // diagnostic tolerant around world edges.
                        }
                    }
                }

                return locations.ToArray();
            }

            private static Rect WorldCoordRectToTerrainRect(Rect rect)
            {
                Vector2 min = WorldCoordToTerrainPosition(rect.min);
                Vector2 max = WorldCoordToTerrainPosition(rect.max);

                Rect result = new Rect();
                result.xMin = min.x;
                result.yMin = min.y;
                result.xMax = max.x;
                result.yMax = max.y;
                return result;
            }

            private static Vector2 WorldCoordToTerrainPosition(Vector2 worldCoords)
            {
                return new Vector2(
                    worldCoords.x / 32768f * 129f,
                    worldCoords.y / 32768f * 129f);
            }

            private static Rect ExpandInEachDirection(Rect src, float amount)
            {
                Vector2 amt = new Vector2(amount, amount);
                return new Rect(src.position - amt, src.size + amt * 2f);
            }
        }

        private sealed class CpuTexture
        {
            private readonly int width;
            private readonly int height;
            private readonly Color[] pixels;
            private readonly TextureSamplingMode samplingMode;

            private CpuTexture(int width, int height, Color[] pixels, TextureSamplingMode samplingMode)
            {
                this.width = width;
                this.height = height;
                this.pixels = pixels;
                this.samplingMode = samplingMode;
            }

            public static CpuTexture FromTexture(Texture2D source, TextureSamplingMode samplingMode)
            {
                Color[] pixels;
                try
                {
                    pixels = source.GetPixels();
                }
                catch
                {
                    Texture2D readable = CopyToReadable(source);
                    try
                    {
                        pixels = readable.GetPixels();
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(readable);
                    }
                }

                return new CpuTexture(source.width, source.height, pixels, samplingMode);
            }

            public Color SampleBilinear(Vector2 uv)
            {
                float x = Mathf.Clamp01(uv.x) * (width - 1);
                float y = Mathf.Clamp01(uv.y) * (height - 1);
                if (samplingMode == TextureSamplingMode.GpuLinearClamp)
                {
                    x = Mathf.Clamp01(uv.x) * width - 0.5f;
                    y = Mathf.Clamp01(uv.y) * height - 0.5f;
                }

                int x0Raw = Mathf.FloorToInt(x);
                int y0Raw = Mathf.FloorToInt(y);
                int x0 = ClampIndex(x0Raw, width);
                int y0 = ClampIndex(y0Raw, height);
                int x1 = ClampIndex(x0Raw + 1, width);
                int y1 = ClampIndex(y0Raw + 1, height);
                float tx = x - x0;
                float ty = y - y0;
                if (samplingMode == TextureSamplingMode.GpuLinearClamp)
                {
                    tx = x - x0Raw;
                    ty = y - y0Raw;
                }

                Color c00 = pixels[y0 * width + x0];
                Color c10 = pixels[y0 * width + x1];
                Color c01 = pixels[y1 * width + x0];
                Color c11 = pixels[y1 * width + x1];
                Color cx0 = Color.Lerp(c00, c10, tx);
                Color cx1 = Color.Lerp(c01, c11, tx);
                return Color.Lerp(cx0, cx1, ty);
            }

            private static int ClampIndex(int value, int size)
            {
                if (value < 0)
                    return 0;
                if (value >= size)
                    return size - 1;
                return value;
            }

            private static Texture2D CopyToReadable(Texture2D source)
            {
                RenderTexture previous = RenderTexture.active;
                RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                try
                {
                    Graphics.Blit(source, rt);
                    RenderTexture.active = rt;
                    Texture2D readable = new Texture2D(source.width, source.height,
                        TextureFormat.RGBA32, false, true);
                    readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                    readable.Apply();
                    return readable;
                }
                finally
                {
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
        }

        private struct BiomeWeights
        {
            public float mountain;
            public float mountainBase;
            public float desert;
            public float hills;
            public float land;
        }

        private struct RoadDataSnapshot
        {
            public Vector4[] NW_NE_SW_SE;
            public Vector4[] N_E_S_W;

            public static RoadDataSnapshot Empty()
            {
                return new RoadDataSnapshot
                {
                    NW_NE_SW_SE = new Vector4[9],
                    N_E_S_W = new Vector4[9],
                };
            }
        }

        private struct PerlinDef
        {
            public int octaves;
            public float frequency;
            public float amplitude;
            public float lacunarity;
            public float persistence;
            public Vector2 offset;
            public float maxHeight;
        }

        private struct SwissDef
        {
            public int octaves;
            public float frequency;
            public float amplitude;
            public float lacunarity;
            public float persistence;
            public Vector2 offset;
            public float ridgeOffset;
            public float warp;
            public float maxHeight;
        }

        private struct JordanDef
        {
            public int octaves;
            public float frequency;
            public float amplitude;
            public float lacunarity;
            public float persistence;
            public float persistence1;
            public Vector2 offset;
            public float warp0;
            public float warp;
            public float damp0;
            public float damp;
            public float dampScale;
            public float maxHeight;
        }

        private struct PerlinParams
        {
            public Vector2 pos;
            public int octaves;
            public float frequency;
            public float amplitude;
            public float lacunarity;
            public float persistence;
            public Vector2 offset;
            public float maxHeight;
        }

        private struct SwissParams
        {
            public Vector2 pos;
            public int octaves;
            public float frequency;
            public float amplitude;
            public float lacunarity;
            public float persistence;
            public Vector2 offset;
            public float ridgeOffset;
            public float warp;
            public float maxHeight;
        }

        private struct JordanParams
        {
            public Vector2 pos;
            public int octaves;
            public float frequency;
            public float amplitude;
            public float lacunarity;
            public float persistence;
            public float persistence1;
            public Vector2 offset;
            public float warp0;
            public float warp;
            public float damp0;
            public float damp;
            public float dampScale;
            public float maxHeight;
        }

        private static PerlinParams MakePerlin(PerlinDef def, Vector2 pos)
        {
            return new PerlinParams
            {
                pos = pos,
                octaves = def.octaves,
                frequency = def.frequency,
                amplitude = def.amplitude,
                lacunarity = def.lacunarity,
                persistence = def.persistence,
                offset = def.offset,
                maxHeight = def.maxHeight,
            };
        }

        private static SwissParams MakeSwiss(SwissDef def, Vector2 pos)
        {
            return new SwissParams
            {
                pos = pos,
                octaves = def.octaves,
                frequency = def.frequency,
                amplitude = def.amplitude,
                lacunarity = def.lacunarity,
                persistence = def.persistence,
                offset = def.offset,
                ridgeOffset = def.ridgeOffset,
                warp = def.warp,
                maxHeight = def.maxHeight,
            };
        }

        private static JordanParams MakeJordan(JordanDef def, Vector2 pos)
        {
            return new JordanParams
            {
                pos = pos,
                octaves = def.octaves,
                frequency = def.frequency,
                amplitude = def.amplitude,
                lacunarity = def.lacunarity,
                persistence = def.persistence,
                persistence1 = def.persistence1,
                offset = def.offset,
                warp0 = def.warp0,
                warp = def.warp,
                damp0 = def.damp0,
                damp = def.damp,
                dampScale = def.dampScale,
                maxHeight = def.maxHeight,
            };
        }

        private static float SimplePerlin(PerlinParams p)
        {
            float sum = 0f;
            for (int i = 0; i < p.octaves; i++)
            {
                float h = Perlin2D((p.pos + p.offset) * p.frequency);
                sum += h * p.amplitude;
                p.frequency *= p.lacunarity;
                p.amplitude *= p.persistence;
            }

            return Mathf.Clamp(sum, -1f, 1f) * (p.maxHeight / NewHeight);
        }

        private static float PositivePerlin(PerlinParams p)
        {
            float sum = 0f;
            for (int i = 0; i < p.octaves; i++)
            {
                float h = Perlin2D((p.pos + p.offset) * p.frequency);
                sum += h * p.amplitude;
                p.frequency *= p.lacunarity;
                p.amplitude *= p.persistence;
            }

            return Saturate(sum * 0.5f + 0.5f) * (p.maxHeight / NewHeight);
        }

        private static Color ColorPerlin(PerlinParams p)
        {
            float r = 0f, g = 0f, b = 0f, a = 0f;
            Vector2 offR = p.offset;
            Vector2 offG = offR + new Vector2(483.58f, 195.28f);
            Vector2 offB = offG + new Vector2(116.26f, 994.28f);
            Vector2 offA = offB + new Vector2(472.89f, 192.57f);

            for (int i = 0; i < p.octaves; i++)
            {
                r += Perlin2D((p.pos + offR) * p.frequency) * p.amplitude;
                g += Perlin2D((p.pos + offG) * p.frequency * 0.2f) * p.amplitude;
                b += Perlin2D((p.pos + offB) * p.frequency * 0.01f) * p.amplitude;
                a += Perlin2D((p.pos + offA) * p.frequency * 0.002f) * p.amplitude;

                p.frequency *= p.lacunarity;
                p.amplitude *= p.persistence;
            }

            r = Saturate(r * 0.5f + 0.5f);
            g = Saturate(g * 0.5f + 0.5f);
            b = Saturate(b * 0.5f + 0.5f);
            a = Saturate(a * 0.5f + 0.5f);

            float pf = Saturate(p.maxHeight / NewHeight);
            float power = Mathf.Lerp(1f, 5f, pf);
            return new Color(Mathf.Pow(r, power), Mathf.Pow(g, power),
                Mathf.Pow(b, power), Mathf.Pow(a, power));
        }

        private static float IQMountains(SwissParams p)
        {
            float sum = 0f;
            Vector2 dsum = Vector2.zero;
            for (int i = 0; i < p.octaves; i++)
            {
                Vector3 n = PerlinSurflet2DDeriv((p.pos + dsum * p.warp + p.offset) * p.frequency);
                dsum += new Vector2(n.y, n.z) * -n.x;
                sum += p.amplitude * n.x / (1f + Vector2.Dot(dsum, dsum));
                p.frequency *= p.lacunarity;
                p.amplitude *= p.persistence;
            }
            return Saturate(sum) * (p.maxHeight / NewHeight);
        }

        private static float SwissMountainsGen(SwissParams p)
        {
            float f = p.frequency;
            float a = Mathf.Lerp(p.amplitude * 0.125f, p.amplitude, p.ridgeOffset);
            float t = 0f;
            Vector2 dsum = Vector2.zero;
            float warp = Mathf.Lerp(p.warp * 0.1f, p.warp, p.ridgeOffset);
            float l = p.lacunarity;

            for (int i = 0; i < p.octaves; i++)
            {
                Vector3 n = SimplexPerlin2DDeriv((p.pos + warp * dsum) * f + p.offset);
                t += a * (1f - Mathf.Abs(n.x));
                dsum += a * new Vector2(n.y, n.z) * -n.x;
                f *= l;
                a *= p.persistence * Saturate(t);
            }

            return t * (p.maxHeight / NewHeight);
        }

        private static float SwissMountains(SwissParams p)
        {
            float f1 = 0.5f;
            float f2 = 0.25f;

            SwissParams p1 = p;
            SwissParams p2 = p;
            p1.frequency *= f1;
            p2.frequency *= f2;
            p1.warp *= f1;
            p2.warp *= f2;

            float w1 = SwissMountainsGen(p1);
            float w2 = SwissMountainsGen(p2);
            w1 = Saturate(w1 * f1 + (1f - f1));
            w2 = Saturate(w2 * f2 + (1f - f2));

            return SwissMountainsGen(p) * w1 * w2;
        }

        private static float SwissCellNoise(SwissParams p)
        {
            float f = p.frequency;
            float a = Mathf.Lerp(p.amplitude * 0.125f, p.amplitude, p.ridgeOffset);
            float t = 0f;
            Vector2 dsum = Vector2.zero;
            float warp = Mathf.Lerp(p.warp * 0.1f, p.warp, p.ridgeOffset);
            float l = p.lacunarity;
            float ridgeDenom = Mathf.Max(0.0001f, p.ridgeOffset);

            for (int i = 0; i < p.octaves; i++)
            {
                Vector3 n = PerlinSurflet2DDeriv((p.pos + warp * dsum) * f);
                float h = p.ridgeOffset - Mathf.Abs(n.x);

                t += a * h;
                dsum += a * ((new Vector2(n.y, n.z) - Vector2.one * (1f - p.ridgeOffset)) / ridgeDenom) * -n.x;
                f *= l;
                a *= p.persistence;
            }

            return t * (p.maxHeight / NewHeight);
        }

        private static float SwissTime(SwissParams p)
        {
            float sum = 0f;
            Vector2 dsum = Vector2.zero;

            for (int i = 0; i < p.octaves; i++)
            {
                Vector3 deriv = SimplexPerlin2DDeriv((p.pos + p.offset + p.warp * dsum) * p.frequency);
                Vector3 n = new Vector3(
                    0.5f * (p.ridgeOffset - Mathf.Abs(Mathf.Sin(deriv.x))),
                    0.5f * (p.ridgeOffset - Mathf.Abs(Mathf.Sin(deriv.y))),
                    0.5f * (p.ridgeOffset - Mathf.Abs(Mathf.Sin(deriv.z))));
                n = new Vector3(n.x * n.x, n.y * n.y, n.z * n.z);

                sum += p.amplitude * n.x;
                dsum += p.amplitude * new Vector2(n.y, n.z) * -n.x;
                p.frequency *= p.lacunarity;
                p.amplitude *= p.persistence;
            }

            float result = 1f - sum;
            return Mathf.Pow(Saturate(result * 2f), 3f) * (p.maxHeight / NewHeight);
        }

        private static float JordanMountains(JordanParams p)
        {
            float f1 = 0.25f;
            float f2 = 0.125f;

            JordanParams p1 = p;
            JordanParams p2 = p;
            p1.frequency *= f1;
            p2.frequency *= f2;

            float w1 = JordanMountainsGen(p1);
            float w2 = JordanMountainsGen(p2);
            w1 = Saturate(w1 * f1 + (1f - f1));
            w2 = Saturate(w2 * f2 + (1f - f2));

            return JordanMountainsGen(p) * w1 * w2;
        }

        private static float JordanMountainsGen(JordanParams p)
        {
            Vector3 n = PerlinSurflet2DDeriv(p.pos * p.frequency);
            Vector3 n2 = n * n.x;
            float sum = n2.x;
            Vector2 dsumWarp = p.warp0 * new Vector2(n2.y, n2.z);
            Vector2 dsumDamp = p.damp0 * new Vector2(n2.y, n2.z);

            float amp = p.persistence1 * p.amplitude;
            float freq = p.lacunarity;
            float dampedAmp = amp * p.persistence;

            for (int i = 1; i < p.octaves; i++)
            {
                n = PerlinSurflet2DDeriv((p.pos * freq + dsumWarp + p.offset) * p.frequency);
                n2 = n * n.x;
                sum += dampedAmp * n2.x;
                dsumWarp += p.warp * new Vector2(n2.y, n2.z);
                dsumDamp += p.damp * new Vector2(n2.y, n2.z);
                freq *= p.lacunarity;
                amp *= p.persistence;
                dampedAmp = amp * (1f - p.dampScale / (1f + Vector2.Dot(dsumDamp, dsumDamp)));
            }

            float dampComp = p.dampScale - 1f;
            return (sum + dampComp) * (p.maxHeight / NewHeight);
        }

        private static float Perlin2D(Vector2 p)
        {
            Vector2 pi = Floor(p);
            Vector4 pf = new Vector4(p.x - pi.x, p.y - pi.y, p.x - (pi.x + 1f), p.y - (pi.y + 1f));

            Vector4 hashX;
            Vector4 hashY;
            Fast32Hash2D(pi, out hashX, out hashY);

            Vector4 gradX = Sub(hashX, 0.49999f);
            Vector4 gradY = Sub(hashY, 0.49999f);
            NormalizeGradients(ref gradX, ref gradY);

            Vector4 gradResults = new Vector4(
                gradX.x * pf.x + gradY.x * pf.y,
                gradX.y * pf.z + gradY.y * pf.y,
                gradX.z * pf.x + gradY.z * pf.w,
                gradX.w * pf.z + gradY.w * pf.w);

            gradResults *= 1.414213562373095f;
            Vector2 blend = InterpolationC2(new Vector2(pf.x, pf.y));
            float res0x = Mathf.Lerp(gradResults.x, gradResults.z, blend.y);
            float res0y = Mathf.Lerp(gradResults.y, gradResults.w, blend.y);
            return Mathf.Lerp(res0x, res0y, blend.x);
        }

        private static Vector3 PerlinSurflet2DDeriv(Vector2 p)
        {
            Vector2 pi = Floor(p);
            Vector4 pf = new Vector4(p.x - pi.x, p.y - pi.y, p.x - (pi.x + 1f), p.y - (pi.y + 1f));

            Vector4 hashX;
            Vector4 hashY;
            Fast32Hash2D(pi, out hashX, out hashY);

            Vector4 gradX = Sub(hashX, 0.49999f);
            Vector4 gradY = Sub(hashY, 0.49999f);
            NormalizeGradients(ref gradX, ref gradY);

            Vector4 gradResults = new Vector4(
                gradX.x * pf.x + gradY.x * pf.y,
                gradX.y * pf.z + gradY.y * pf.y,
                gradX.z * pf.x + gradY.z * pf.w,
                gradX.w * pf.z + gradY.w * pf.w);

            Vector4 m = new Vector4(
                pf.x * pf.x + pf.y * pf.y,
                pf.z * pf.z + pf.y * pf.y,
                pf.x * pf.x + pf.w * pf.w,
                pf.z * pf.z + pf.w * pf.w);
            m = Max(Sub(1f, m), 0f);
            Vector4 m2 = Mul(m, m);
            Vector4 m3 = Mul(m, m2);
            Vector4 temp = Mul(-6f, Mul(m2, gradResults));

            Vector4 pfXzxz = new Vector4(pf.x, pf.z, pf.x, pf.z);
            Vector4 pfYyww = new Vector4(pf.y, pf.y, pf.w, pf.w);
            float xDeriv = Dot(temp, pfXzxz) + Dot(m3, gradX);
            float yDeriv = Dot(temp, pfYyww) + Dot(m3, gradY);

            const float normalization = 2.3703703703703704f;
            return new Vector3(Dot(m3, gradResults), xDeriv, yDeriv) * normalization;
        }

        private static Vector3 SimplexPerlin2DDeriv(Vector2 p)
        {
            const float skewFactor = 0.36602540378443865f;
            const float unskewFactor = 0.21132486540518712f;
            const float simplexTriHeight = 0.7071067811865475f;
            Vector3 simplexPoints = new Vector3(1f - unskewFactor, -unskewFactor, 1f - 2f * unskewFactor);

            p *= simplexTriHeight;
            Vector2 pi = Floor(p + Vector2.one * Vector2.Dot(p, Vector2.one * skewFactor));

            Vector4 hashX;
            Vector4 hashY;
            Fast32Hash2D(pi, out hashX, out hashY);

            Vector2 v0 = pi - Vector2.one * Vector2.Dot(pi, Vector2.one * unskewFactor) - p;
            Vector4 v1PosV1Hash = v0.x < v0.y
                ? new Vector4(simplexPoints.x, simplexPoints.y, hashX.y, hashY.y)
                : new Vector4(simplexPoints.y, simplexPoints.x, hashX.z, hashY.z);
            Vector4 v12 = new Vector4(
                v1PosV1Hash.x + v0.x,
                v1PosV1Hash.y + v0.y,
                simplexPoints.z + v0.x,
                simplexPoints.z + v0.y);

            Vector3 gradX = new Vector3(hashX.x, v1PosV1Hash.z, hashX.w) - Vector3.one * 0.49999f;
            Vector3 gradY = new Vector3(hashY.x, v1PosV1Hash.w, hashY.w) - Vector3.one * 0.49999f;
            NormalizeGradients(ref gradX, ref gradY);

            Vector3 gradResults = new Vector3(
                gradX.x * v0.x + gradY.x * v0.y,
                gradX.y * v12.x + gradY.y * v12.y,
                gradX.z * v12.z + gradY.z * v12.w);

            Vector3 m = new Vector3(
                v0.x * v0.x + v0.y * v0.y,
                v12.x * v12.x + v12.y * v12.y,
                v12.z * v12.z + v12.w * v12.w);
            m = Max(Vector3.one * 0.5f - m, 0f);
            Vector3 m2 = Mul(m, m);
            Vector3 m4 = Mul(m2, m2);
            Vector3 temp = 8f * Mul(Mul(m2, m), gradResults);

            Vector3 vx = new Vector3(v0.x, v12.x, v12.z);
            Vector3 vy = new Vector3(v0.y, v12.y, v12.w);
            float xDeriv = Vector3.Dot(temp, vx) - Vector3.Dot(m4, gradX);
            float yDeriv = Vector3.Dot(temp, vy) - Vector3.Dot(m4, gradY);

            const float normalization = 99.20433458271871f;
            return new Vector3(Vector3.Dot(m4, gradResults), xDeriv, yDeriv) * normalization;
        }

        private static Vector4 Fast32Hash2D(Vector2 gridCell)
        {
            const float domain = 71f;
            const float large = 951.135664f;
            float px = TruncateDomain(gridCell.x, domain) + 26f;
            float py = TruncateDomain(gridCell.y, domain) + 161f;
            float pz = TruncateDomain(gridCell.x + 1f, domain) + 26f;
            float pw = TruncateDomain(gridCell.y + 1f, domain) + 161f;
            px *= px;
            py *= py;
            pz *= pz;
            pw *= pw;

            return new Vector4(
                Frac(px * py / large),
                Frac(pz * py / large),
                Frac(px * pw / large),
                Frac(pz * pw / large));
        }

        private static void Fast32Hash2D(Vector2 gridCell, out Vector4 hash0, out Vector4 hash1)
        {
            const float domain = 71f;
            const float large0 = 951.135664f;
            const float large1 = 642.949883f;
            float px = TruncateDomain(gridCell.x, domain) + 26f;
            float py = TruncateDomain(gridCell.y, domain) + 161f;
            float pz = TruncateDomain(gridCell.x + 1f, domain) + 26f;
            float pw = TruncateDomain(gridCell.y + 1f, domain) + 161f;
            px *= px;
            py *= py;
            pz *= pz;
            pw *= pw;

            Vector4 p = new Vector4(px * py, pz * py, px * pw, pz * pw);
            hash0 = Frac(Mul(p, 1f / large0));
            hash1 = Frac(Mul(p, 1f / large1));
        }

        private static PerlinDef ReadPerlin(object parent, string fieldName)
        {
            object src = GetInstanceField<object>(parent, fieldName);
            return new PerlinDef
            {
                octaves = ReadInt(src, "octaves"),
                frequency = ReadFloat(src, "frequency"),
                amplitude = ReadFloat(src, "amplitude"),
                lacunarity = ReadFloat(src, "lacunarity"),
                persistence = ReadFloat(src, "persistence"),
                offset = ReadVector2(src, "offset"),
                maxHeight = ReadFloat(src, "maxHeight"),
            };
        }

        private static SwissDef ReadSwiss(object parent, string fieldName)
        {
            object src = GetInstanceField<object>(parent, fieldName);
            return new SwissDef
            {
                octaves = ReadInt(src, "octaves"),
                frequency = ReadFloat(src, "frequency"),
                amplitude = ReadFloat(src, "amplitude"),
                lacunarity = ReadFloat(src, "lacunarity"),
                persistence = ReadFloat(src, "persistence"),
                offset = ReadVector2(src, "offset"),
                ridgeOffset = ReadFloat(src, "ridgeOffset"),
                warp = ReadFloat(src, "warp"),
                maxHeight = ReadFloat(src, "maxHeight"),
            };
        }

        private static JordanDef ReadJordan(object parent, string fieldName)
        {
            object src = GetInstanceField<object>(parent, fieldName);
            return new JordanDef
            {
                octaves = ReadInt(src, "octaves"),
                frequency = ReadFloat(src, "frequency"),
                amplitude = ReadFloat(src, "amplitude"),
                lacunarity = ReadFloat(src, "lacunarity"),
                persistence = ReadFloat(src, "persistence"),
                persistence1 = ReadFloat(src, "persistence1"),
                offset = ReadVector2(src, "offset"),
                warp0 = ReadFloat(src, "warp0"),
                warp = ReadFloat(src, "warp"),
                damp0 = ReadFloat(src, "damp0"),
                damp = ReadFloat(src, "damp"),
                dampScale = ReadFloat(src, "damp_scale"),
                maxHeight = ReadFloat(src, "maxHeight"),
            };
        }

        private static Type ResolveMonobeliskType(string typeName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = asm.GetType(typeName, false);
                if (type != null)
                    return type;
            }
            return null;
        }

        private static T GetStaticField<T>(Type type, string name) where T : class
        {
            FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return field != null ? field.GetValue(null) as T : null;
        }

        private static T GetInstanceField<T>(object instance, string name) where T : class
        {
            if (instance == null)
                return null;
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field != null ? field.GetValue(instance) as T : null;
        }

        private static int ReadInt(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field != null ? Convert.ToInt32(field.GetValue(instance)) : 0;
        }

        private static float ReadFloat(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field != null ? Convert.ToSingle(field.GetValue(instance)) : 0f;
        }

        private static Vector2 ReadVector2(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field != null && field.GetValue(instance) is Vector2
                ? (Vector2)field.GetValue(instance)
                : Vector2.zero;
        }

        private static float TruncateDomain(float value, float domain)
        {
            return value - Mathf.Floor(value * (1f / domain)) * domain;
        }

        private static Vector2 Floor(Vector2 v)
        {
            return new Vector2(Mathf.Floor(v.x), Mathf.Floor(v.y));
        }

        private static Vector2 InterpolationC2(Vector2 x)
        {
            return new Vector2(InterpolationC2(x.x), InterpolationC2(x.y));
        }

        private static float InterpolationC2(float x)
        {
            return x * x * x * (x * (x * 6f - 15f) + 10f);
        }

        private static float Saturate(float v)
        {
            return Mathf.Clamp01(v);
        }

        private static Vector2 Clamp01(Vector2 v)
        {
            return new Vector2(Mathf.Clamp01(v.x), Mathf.Clamp01(v.y));
        }

        private static Vector2 NormalizeSafe(Vector2 v)
        {
            float sqrMagnitude = v.sqrMagnitude;
            return sqrMagnitude > 1e-12f ? v / Mathf.Sqrt(sqrMagnitude) : Vector2.zero;
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            float t = Saturate((value - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        private static float SafeDivide(float numerator, float denominator)
        {
            return denominator != 0f ? numerator / denominator : 0f;
        }

        private static Vector2 Divide(Vector2 numerator, Vector2 denominator)
        {
            return new Vector2(
                SafeDivide(numerator.x, denominator.x),
                SafeDivide(numerator.y, denominator.y));
        }

        private static float Frac(float v)
        {
            return v - Mathf.Floor(v);
        }

        private static Vector4 Frac(Vector4 v)
        {
            return new Vector4(Frac(v.x), Frac(v.y), Frac(v.z), Frac(v.w));
        }

        private static Vector4 Sub(Vector4 v, float s)
        {
            return new Vector4(v.x - s, v.y - s, v.z - s, v.w - s);
        }

        private static Vector4 Sub(float s, Vector4 v)
        {
            return new Vector4(s - v.x, s - v.y, s - v.z, s - v.w);
        }

        private static Vector4 Max(Vector4 v, float s)
        {
            return new Vector4(Mathf.Max(v.x, s), Mathf.Max(v.y, s), Mathf.Max(v.z, s), Mathf.Max(v.w, s));
        }

        private static Vector3 Max(Vector3 v, float s)
        {
            return new Vector3(Mathf.Max(v.x, s), Mathf.Max(v.y, s), Mathf.Max(v.z, s));
        }

        private static Vector4 Mul(Vector4 a, Vector4 b)
        {
            return new Vector4(a.x * b.x, a.y * b.y, a.z * b.z, a.w * b.w);
        }

        private static Vector4 Mul(Vector4 a, float s)
        {
            return new Vector4(a.x * s, a.y * s, a.z * s, a.w * s);
        }

        private static Vector4 Mul(float s, Vector4 a)
        {
            return Mul(a, s);
        }

        private static Vector3 Mul(Vector3 a, Vector3 b)
        {
            return new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
        }

        private static float Dot(Vector4 a, Vector4 b)
        {
            return a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        }

        private static void NormalizeGradients(ref Vector4 gradX, ref Vector4 gradY)
        {
            float n0 = 1f / Mathf.Sqrt(gradX.x * gradX.x + gradY.x * gradY.x);
            float n1 = 1f / Mathf.Sqrt(gradX.y * gradX.y + gradY.y * gradY.y);
            float n2 = 1f / Mathf.Sqrt(gradX.z * gradX.z + gradY.z * gradY.z);
            float n3 = 1f / Mathf.Sqrt(gradX.w * gradX.w + gradY.w * gradY.w);

            gradX = new Vector4(gradX.x * n0, gradX.y * n1, gradX.z * n2, gradX.w * n3);
            gradY = new Vector4(gradY.x * n0, gradY.y * n1, gradY.z * n2, gradY.w * n3);
        }

        private static void NormalizeGradients(ref Vector3 gradX, ref Vector3 gradY)
        {
            float n0 = 1f / Mathf.Sqrt(gradX.x * gradX.x + gradY.x * gradY.x);
            float n1 = 1f / Mathf.Sqrt(gradX.y * gradX.y + gradY.y * gradY.y);
            float n2 = 1f / Mathf.Sqrt(gradX.z * gradX.z + gradY.z * gradY.z);

            gradX = new Vector3(gradX.x * n0, gradX.y * n1, gradX.z * n2);
            gradY = new Vector3(gradY.x * n0, gradY.y * n1, gradY.z * n2);
        }
    }
}
#endif
