// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Decorates DFU terrain texturing with a height-lowering pass after
    /// location blending and before tile classification.
    /// </summary>
    public sealed class DeepWaterTexturing : ITerrainTexturing
    {
        private readonly ITerrainTexturing inner;

        public DeepWaterTexturing(ITerrainTexturing inner)
        {
            this.inner = inner;
        }

        public bool ConvertWaterTiles() => inner.ConvertWaterTiles();

        public JobHandle ScheduleAssignTilesJob(
            ITerrainSampler terrainSampler,
            ref MapPixelData mapData,
            JobHandle dependencies,
            bool march = true)
        {
            if (DeepWaters.Instance == null || DeepWaters.Instance.WaterDepth <= 0f)
                return inner.ScheduleAssignTilesJob(terrainSampler, ref mapData, dependencies, march);

            int hDim = terrainSampler.HeightmapDimension;
            float maxTerrainHeight = terrainSampler.MaxTerrainHeight;
            float oceanElevation   = terrainSampler.OceanElevation;

            // All heights in heightmapData are normalised 0..1 by the sampler.
            // Express the threshold and the depth in the same normalised space.
            float oceanThresholdNormalised = oceanElevation / maxTerrainHeight;
            float depthNormalised          = DeepWaters.Instance.WaterDepth / maxTerrainHeight;

            var mask = new NativeArray<byte>(hDim * hDim, Allocator.TempJob);
            mapData.nativeArrayList.Add(mask); // disposed with other job memory

            var markJob = new MarkDeepWaterJob
            {
                heightmapData            = mapData.heightmapData,
                oceanThresholdNormalised = oceanThresholdNormalised,
                hDim                     = hDim,
                mask                     = mask,
            };
            JobHandle markHandle = markJob.Schedule(hDim * hDim, 64, dependencies);

            var lowerJob = new LowerMarkedHeightsJob
            {
                heightmapData            = mapData.heightmapData,
                mask                     = mask,
                depthNormalised          = depthNormalised,
                oceanThresholdNormalised = oceanThresholdNormalised,
                hDim                     = hDim,
                mapPixelX                = mapData.mapPixelX,
                mapPixelY                = mapData.mapPixelY,
            };
            JobHandle lowerHandle = lowerJob.Schedule(hDim * hDim, 64, markHandle);

            // Run the inner texturing. Its AssignTilesJob will classify deep-
            // water samples as tile value 0 (pure water texture) — which is
            // exactly what we DON'T want visible on the ocean floor, because
            // we draw our own water mesh above it.
            JobHandle innerHandle = inner.ScheduleAssignTilesJob(terrainSampler, ref mapData, lowerHandle, march);

            // Post-pass: convert all "pure water" tiles (tilemapData == 0) to
            // "pure dirt" (tilemapData == 1). Transition tiles at the shore
            // have nonzero values (5+ via the marching-squares lookup) and are
            // left alone, so the beach gradient still works. This leaves the
            // actual ocean floor textured as dirt, with our water mesh above.
            var convertJob = new ConvertFloorTilesJob
            {
                tilemapData = mapData.tilemapData,
            };
            JobHandle convertHandle = convertJob.Schedule(mapData.tilemapData.Length, 64, innerHandle);

            return convertHandle;
        }

        /// <summary>
        /// Burst-compiled: every tile previously classified as "pure water"
        /// (value 0, no rotate/flip bits) becomes "pure dirt" (value 1). We
        /// match on the raw byte so we also catch any value-0 entries from
        /// location blending or other upstream stages. Values other than 0 —
        /// which includes all marching-squares transition tiles — are
        /// preserved, so beaches, coastlines, and shore gradients render
        /// normally.
        /// </summary>
        [BurstCompile]
        private struct ConvertFloorTilesJob : IJobParallelFor
        {
            public NativeArray<byte> tilemapData;

            public void Execute(int index)
            {
                if (tilemapData[index] == 0)
                    tilemapData[index] = 1;
            }
        }

        /// <summary>
        /// First pass: mark water samples that are safe to lower. Land and
        /// water directly adjacent to land stay at the original ocean height.
        /// </summary>
        [BurstCompile]
        private struct MarkDeepWaterJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> heightmapData;
            public float oceanThresholdNormalised;
            public int hDim;
            [WriteOnly] public NativeArray<byte> mask;

            public void Execute(int index)
            {
                int x = index % hDim;
                int y = index / hDim;

                // Case 1: land sample.
                if (heightmapData[index] > oceanThresholdNormalised)
                {
                    mask[index] = 0;
                    return;
                }

                // Land-adjacency check, edge-clamped. Reads OOB as
                // self (the edge sample), which means an edge-water
                // sample with no in-tile neighbours land doesn't see
                // any land via this clamp.
                int xPrev = (x > 0) ? x - 1 : 0;
                int xNext = (x < hDim - 1) ? x + 1 : hDim - 1;
                int yPrev = (y > 0) ? y - 1 : 0;
                int yNext = (y < hDim - 1) ? y + 1 : hDim - 1;

                bool nextToLand =
                       heightmapData[y     * hDim + xPrev] > oceanThresholdNormalised
                    || heightmapData[y     * hDim + xNext] > oceanThresholdNormalised
                    || heightmapData[yPrev * hDim + x    ] > oceanThresholdNormalised
                    || heightmapData[yNext * hDim + x    ] > oceanThresholdNormalised;

                mask[index] = nextToLand ? (byte)0 : (byte)1;
            }
        }

        /// <summary>
        /// Second pass: lower marked water samples to the seabed.
        /// Inline copy of DeepWaterFloorHeight.ComputeLoweredHeight.
        /// Burst prefers inline math; keep both copies in lockstep.
        /// </summary>
        [BurstCompile]
        private struct LowerMarkedHeightsJob : IJobParallelFor
        {
            public NativeArray<float> heightmapData;
            [ReadOnly] public NativeArray<byte> mask;
            public float depthNormalised;
            public float oceanThresholdNormalised;
            public int hDim;
            public int mapPixelX;
            public int mapPixelY;

            public void Execute(int index)
            {
                if (mask[index] == 0) return;

                int x = index % hDim;
                int y = index / hDim;

                // World heightmap coords. X: standard mapPixelX*(hDim-1)+x.
                // Y: flipped — (500 - mapPixelY)*(hDim-1)+y — which makes
                // adjacent tiles in Y agree on shared edge samples
                // (T's row hDim-1 = T-top's row 0 in world Y).
                int worldHx = mapPixelX * (hDim - 1) + x;
                int worldHy = (500 - mapPixelY) * (hDim - 1) + y;

                // Keep this math identical to DeepWaterFloorHeight.ComputeLoweredHeight.
                float budget = math.min(depthNormalised, oceanThresholdNormalised);
                float lift = Mathf.PerlinNoise(worldHx * 0.008f, worldHy * 0.008f) * 0.5f
                           + Mathf.PerlinNoise(worldHx * 0.030f, worldHy * 0.030f) * 0.35f
                           + Mathf.PerlinNoise(worldHx * 0.13f,  worldHy * 0.13f)  * 0.15f;
                lift = math.clamp(lift, 0f, 0.5f);
                float h = oceanThresholdNormalised - budget * (1f - lift);
                if (h < 0f) h = 0f;
                if (h > oceanThresholdNormalised) h = oceanThresholdNormalised;
                heightmapData[index] = h;
            }
        }
    }
}
