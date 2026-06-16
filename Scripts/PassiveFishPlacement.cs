// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using UnityEngine;

namespace DeepWaters
{
    internal static class PassiveFishPlacement
    {
        public const float MinimumColumnDepth = 4f;

        private const float SeafloorClearance = 1.2f;
        private const float SurfaceClearance = 1.4f;
        private const float SchoolMemberMinRadius = 1.2f;
        private const float SchoolMemberMaxRadius = 5f;
        private const float SchoolMemberMinSeparation = 2.2f;
        private const int SchoolPositionAttempts = 24;

        public static float GetSchoolRadius(int schoolSize)
        {
            return Mathf.Clamp(2.5f + schoolSize * 0.45f, SchoolMemberMinRadius, SchoolMemberMaxRadius);
        }

        // School-center resolve: vertical position comes from the species'
        // absolute depth band. Returns false when that band doesn't overlap this
        // column, so a deep-water species is never forced onto a shallow floor.
        public static bool TryResolvePosition(float worldX, float worldZ, PassiveFishSpecies species, out Vector3 worldPos, out Transform parent)
        {
            worldPos = Vector3.zero;

            float minY, maxY, oceanY;
            if (!TryResolveColumnRange(worldX, worldZ, out minY, out maxY, out oceanY, out parent))
                return false;

            float y;
            if (!TryPickFishY(minY, maxY, oceanY, species, out y))
            {
                parent = null;
                return false;
            }

            worldPos = new Vector3(worldX, y, worldZ);
            return true;
        }

        // Band-agnostic resolve for schoolmate base positions, whose Y is then
        // overridden by ClampToSchoolDepth — only XZ/column validity matters here.
        public static bool TryResolvePosition(float worldX, float worldZ, out Vector3 worldPos, out Transform parent)
        {
            worldPos = Vector3.zero;

            float minY, maxY, oceanY;
            if (!TryResolveColumnRange(worldX, worldZ, out minY, out maxY, out oceanY, out parent))
                return false;

            worldPos = new Vector3(worldX, Random.Range(minY, maxY), worldZ);
            return true;
        }

        private static bool TryResolveColumnRange(float worldX, float worldZ, out float minY, out float maxY, out float oceanY, out Transform parent)
        {
            minY = 0f;
            maxY = 0f;
            oceanY = 0f;
            parent = null;

            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
                return false;

            if (column.Depth < MinimumColumnDepth)
                return false;

            float seafloorWorldY;
            if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, worldX, worldZ, out seafloorWorldY))
                return false;

            oceanY = column.OceanWorldY;
            minY = seafloorWorldY + SeafloorClearance;
            maxY = oceanY - SurfaceClearance;
            if (maxY <= minY)
                return false;

            parent = column.Parent;
            return true;
        }

        public static bool TryPickSchoolmatePosition(
            Vector3 schoolCenter,
            float schoolRadius,
            List<Vector3> existingPositions,
            out Vector3 worldPos,
            out Transform parent)
        {
            worldPos = Vector3.zero;
            parent = null;

            for (int attempt = 0; attempt < SchoolPositionAttempts; attempt++)
            {
                Vector2 offset = Random.insideUnitCircle;
                if (offset.sqrMagnitude < 0.01f)
                    offset = Vector2.right;

                offset.Normalize();
                offset *= Random.Range(SchoolMemberMinRadius, schoolRadius);

                if (!TryResolvePosition(schoolCenter.x + offset.x, schoolCenter.z + offset.y, out worldPos, out parent))
                    continue;

                ClampToSchoolDepth(schoolCenter, ref worldPos);

                if (IsFarEnoughFromSchoolmates(worldPos, existingPositions))
                    return true;
            }

            return false;
        }

        private static void ClampToSchoolDepth(Vector3 schoolCenter, ref Vector3 worldPos)
        {
            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(worldPos.x, worldPos.z, out column))
                return;

            float seafloorWorldY;
            if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, worldPos.x, worldPos.z, out seafloorWorldY))
                return;

            float minY = seafloorWorldY + SeafloorClearance;
            float maxY = column.OceanWorldY - SurfaceClearance;
            worldPos.y = Mathf.Clamp(schoolCenter.y + Random.Range(-1.0f, 1.0f), minY, maxY);
        }

        private static bool IsFarEnoughFromSchoolmates(Vector3 worldPos, List<Vector3> existingPositions)
        {
            if (existingPositions == null)
                return true;

            float minDistanceSq = SchoolMemberMinSeparation * SchoolMemberMinSeparation;
            for (int i = 0; i < existingPositions.Count; i++)
            {
                if ((worldPos - existingPositions[i]).sqrMagnitude < minDistanceSq)
                    return false;
            }

            return true;
        }

        // Vertical spawn position from the species' ABSOLUTE depth band — a
        // fraction of the deepest possible ocean (the WaterDepth setting), not of
        // this column. So a reef fish's shallow band keeps it near the surface
        // even over the abyss, while a deep fish's band doesn't fit a shallow
        // column at all (returns false -> it won't spawn there). The chosen depth
        // is the band intersected with the column's swimmable range; schoolmates
        // then cluster around it via ClampToSchoolDepth.
        private static bool TryPickFishY(float minY, float maxY, float oceanY, PassiveFishSpecies species, out float y)
        {
            y = 0f;
            if (maxY <= minY)
                return false;

            float maxOceanDepth = DeepWaters.Instance != null
                ? Mathf.Max(1f, DeepWaters.Instance.WaterDepth)
                : DeepBathymetry.MaxAbsoluteDepth;

            // Species' preferred window and the column's swimmable window, both as
            // metres below the surface (maxY is shallowest, minY is deepest).
            float bandShallow = species.MinDepthFraction * maxOceanDepth;
            float bandDeep = species.MaxDepthFraction * maxOceanDepth;
            float availShallow = oceanY - maxY;
            float availDeep = oceanY - minY;

            float lo = Mathf.Max(bandShallow, availShallow);
            float hi = Mathf.Min(bandDeep, availDeep);
            if (hi <= lo)
                return false;

            y = oceanY - Random.Range(lo, hi);
            return true;
        }
    }
}
