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
        private const float SurfaceSpawnDepthMin = 2.2f;
        private const float SurfaceSpawnDepthMax = 4.5f;
        private const float SpawnDepthColumnFraction = 0.45f;
        private const float SchoolMemberMinRadius = 1.2f;
        private const float SchoolMemberMaxRadius = 5f;
        private const float SchoolMemberMinSeparation = 2.2f;
        private const int SchoolPositionAttempts = 24;

        public static float GetSchoolRadius(int schoolSize)
        {
            return Mathf.Clamp(2.5f + schoolSize * 0.45f, SchoolMemberMinRadius, SchoolMemberMaxRadius);
        }

        public static bool TryResolvePosition(float worldX, float worldZ, out Vector3 worldPos, out Transform parent)
        {
            worldPos = Vector3.zero;
            parent = null;

            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
                return false;

            if (column.Depth < MinimumColumnDepth)
                return false;

            float minY = column.SeafloorWorldY + SeafloorClearance;
            float maxY = column.OceanWorldY - SurfaceClearance;
            if (maxY <= minY)
                return false;

            worldPos = new Vector3(worldX, PickFishY(minY, maxY, column.OceanWorldY), worldZ);
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

            float minY = column.SeafloorWorldY + SeafloorClearance;
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

        private static float PickFishY(float minY, float maxY, float oceanY)
        {
            float span = maxY - minY;
            if (span <= 0f)
                return minY;

            float depthMax = Mathf.Min(Mathf.Max(SurfaceSpawnDepthMax, span * SpawnDepthColumnFraction), span);
            float depthMin = Mathf.Min(SurfaceSpawnDepthMin, depthMax);
            return Mathf.Clamp(oceanY - Random.Range(depthMin, depthMax), minY, maxY);
        }
    }
}
