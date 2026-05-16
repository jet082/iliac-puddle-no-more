// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    internal static class UnderwaterLootPlacement
    {
        public const float MinSpawnDistance = 42f;
        public const float MaxSpawnDistance = 72f;

        private const float ForwardSpawnArcDegrees = 110f;
        private const float ForwardBiasChance = 0.7f;
        private const float SeafloorYClearance = 2f;
        private const float LootFloorLift = 0.08f;
        private const int SpawnSpotAttempts = 18;
        private const float SpawnCellSize = 48f;
        private const int MaxRememberedSpawnCells = 128;
        private const float ClusterLootRadius = 11f;
        private const float ClusterLootMinSpacing = 3.0f;
        private const int ClusterLootSpotAttempts = 8;

        private static readonly HashSet<long> recentSpawnCells = new HashSet<long>();
        private static readonly Queue<long> recentSpawnCellOrder = new Queue<long>();

        public static void Reset()
        {
            recentSpawnCells.Clear();
            recentSpawnCellOrder.Clear();
        }

        public static bool PickSpawnSpot(out Vector3 worldPos, out Transform parent, out long spawnCellKey)
        {
            worldPos = Vector3.zero;
            parent = null;
            spawnCellKey = 0L;

            Vector3 playerPos;
            if (!DeepWaterWorld.TryGetPlayerPosition(out playerPos))
                return false;

            for (int attempt = 0; attempt < SpawnSpotAttempts; attempt++)
            {
                float angle = PickSpawnAngle();
                float dist = DeepWaterWorld.PickRingDistance(MinSpawnDistance, MaxSpawnDistance);
                float worldX = playerPos.x + Mathf.Cos(angle) * dist;
                float worldZ = playerPos.z + Mathf.Sin(angle) * dist;
                long key = SpawnCellKey(worldX, worldZ);
                if (recentSpawnCells.Contains(key))
                    continue;

                float worldY;
                Transform terrainParent;
                if (!ResolveSeafloorAt(worldX, worldZ, out worldY, out terrainParent))
                    continue;

                worldPos = new Vector3(worldX, worldY, worldZ);
                parent = terrainParent;
                spawnCellKey = key;
                return true;
            }

            return false;
        }

        public static bool TryPickClusterLootSpot(Vector3 centre, List<Vector3> placedLootSpots, out Vector3 spot)
        {
            for (int attempt = 0; attempt < ClusterLootSpotAttempts; attempt++)
            {
                float r = Mathf.Sqrt(Random.value) * ClusterLootRadius;
                float angle = Random.Range(0f, Mathf.PI * 2f);
                spot = new Vector3(
                    centre.x + Mathf.Cos(angle) * r,
                    0f,
                    centre.z + Mathf.Sin(angle) * r);

                if (IsFarEnoughFromClusterLoot(spot, placedLootSpots))
                    return true;
            }

            spot = Vector3.zero;
            return false;
        }

        public static bool ResolveSeafloorAt(float worldX, float worldZ, out float seafloorWorldY, out Transform terrainTransform)
        {
            seafloorWorldY = 0f;
            terrainTransform = null;

            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
                return false;

            if (column.Parent == null || column.Depth < SeafloorYClearance)
                return false;

            float seafloorLocalY = column.SeafloorLocalY;
            DeepWaterFloorMesh floorMesh = column.Parent.GetComponentInChildren<DeepWaterFloorMesh>();
            if (floorMesh != null)
            {
                float meshLocalY;
                if (floorMesh.TrySampleMeshLocalY(worldX, worldZ, out meshLocalY))
                    seafloorLocalY = meshLocalY;
            }

            if (column.OceanLocalY - seafloorLocalY < SeafloorYClearance)
                return false;

            terrainTransform = column.Parent;
            seafloorWorldY = terrainTransform.position.y + seafloorLocalY + LootFloorLift;
            return true;
        }

        public static void RememberSpawnCell(long key)
        {
            if (recentSpawnCells.Contains(key))
                return;

            recentSpawnCells.Add(key);
            recentSpawnCellOrder.Enqueue(key);

            while (recentSpawnCellOrder.Count > MaxRememberedSpawnCells)
            {
                long oldKey = recentSpawnCellOrder.Dequeue();
                recentSpawnCells.Remove(oldKey);
            }
        }

        private static float PickSpawnAngle()
        {
            Vector3 forward = Vector3.zero;
            GameManager gameManager = GameManager.Instance;
            if (gameManager != null && gameManager.MainCamera != null)
                forward = gameManager.MainCamera.transform.forward;
            if (forward.sqrMagnitude < 0.001f && gameManager != null && gameManager.PlayerObject != null)
                forward = gameManager.PlayerObject.transform.forward;

            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f || Random.value > ForwardBiasChance)
                return Random.Range(0f, Mathf.PI * 2f);

            forward.Normalize();
            float baseAngle = Mathf.Atan2(forward.z, forward.x);
            float arc = ForwardSpawnArcDegrees * Mathf.Deg2Rad;
            return baseAngle + Random.Range(-arc, arc);
        }

        private static bool IsFarEnoughFromClusterLoot(Vector3 spot, List<Vector3> placedLootSpots)
        {
            float minSq = ClusterLootMinSpacing * ClusterLootMinSpacing;
            for (int i = 0; i < placedLootSpots.Count; i++)
            {
                float dx = spot.x - placedLootSpots[i].x;
                float dz = spot.z - placedLootSpots[i].z;
                if (dx * dx + dz * dz < minSq)
                    return false;
            }

            return true;
        }

        private static long SpawnCellKey(float worldX, float worldZ)
        {
            return DeepWaterWorld.WorldCellKey(worldX, worldZ, SpawnCellSize);
        }
    }
}
