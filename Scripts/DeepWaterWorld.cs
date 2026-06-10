// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    internal struct DeepWaterColumn
    {
        public DaggerfallTerrain DaggerfallTerrain;
        public Terrain Terrain;
        public TerrainData TerrainData;
        public Transform Parent;
        public float SeafloorLocalY;
        public float OceanLocalY;
        public float SeafloorWorldY;
        public float OceanWorldY;
        public int SampleX;
        public int SampleY;

        public float Depth
        {
            get { return OceanWorldY - SeafloorWorldY; }
        }
    }

    /// <summary>
    /// Shared world/terrain helpers for systems that need to resolve ocean
    /// columns from arbitrary world X/Z positions.
    /// </summary>
    internal static class DeepWaterWorld
    {
        private const float FallbackEncounterMinSpawnDistance = 35f;
        private const float FallbackEncounterMaxSpawnDistance = 55f;
        private const float FallbackEncounterViewSafetyDistance = 55f;
        private static readonly Dictionary<Transform, DeepWaterFloorMesh> floorMeshCache =
            new Dictionary<Transform, DeepWaterFloorMesh>();

        public static float TileWorldSize
        {
            get { return MapsFile.WorldMapTerrainDim * MeshReader.GlobalScale; }
        }

        public static float EncounterSpawnMinDistance
        {
            get { return DeepWaters.Instance != null ? DeepWaters.Instance.EncounterSpawnMinDistance : FallbackEncounterMinSpawnDistance; }
        }

        public static float EncounterSpawnMaxDistance
        {
            get { return DeepWaters.Instance != null ? DeepWaters.Instance.EncounterSpawnMaxDistance : FallbackEncounterMaxSpawnDistance; }
        }

        public static float EncounterSpawnViewSafetyDistance
        {
            get { return DeepWaters.Instance != null ? DeepWaters.Instance.EncounterSpawnViewSafetyDistance : FallbackEncounterViewSafetyDistance; }
        }

        public static float UnderwaterVisionDistance
        {
            get { return DeepWaters.Instance != null ? DeepWaters.Instance.UnderwaterVisionDistance : 70f; }
        }

        public static bool TryGetPlayerPosition(out Vector3 position)
        {
            position = Vector3.zero;
            var gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame() || gameManager.PlayerObject == null)
                return false;

            position = gameManager.PlayerObject.transform.position;
            return true;
        }

        public static bool IsPlayerOutdoors()
        {
            var gameManager = GameManager.Instance;
            return gameManager != null &&
                   gameManager.IsPlayingGame() &&
                   gameManager.PlayerObject != null &&
                   gameManager.PlayerEnterExit != null &&
                   !gameManager.PlayerEnterExit.IsPlayerInside;
        }

        public static bool IsPlayerInExteriorWaterContext()
        {
            var gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame() || gameManager.PlayerObject == null)
                return false;

            if (gameManager.PlayerEnterExit != null && !gameManager.PlayerEnterExit.IsPlayerInside)
                return true;

            return gameManager.PlayerMotor != null &&
                   gameManager.PlayerMotor.OnExteriorWater != PlayerMotor.OnExteriorWaterMethod.None;
        }

        public static bool IsPlayerInOrAboveDeepWater(float minimumDepth)
        {
            var gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame() || gameManager.PlayerObject == null || gameManager.MainCamera == null)
                return false;

            float oceanSurfaceY;
            if (!TryGetOceanSurfaceWorldY(out oceanSurfaceY))
                return false;

            if (gameManager.MainCamera.transform.position.y < oceanSurfaceY - 0.25f)
                return true;

            DeepWaterColumn column;
            if (!TryGetWaterColumn(gameManager.PlayerObject.transform.position.x, gameManager.PlayerObject.transform.position.z, out column))
                return false;

            return column.Depth >= minimumDepth;
        }

        public static Vector3 PickRingPoint(Vector3 center, float minDistance, float maxDistance)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            float distance = PickRingDistance(minDistance, maxDistance);
            return new Vector3(
                center.x + Mathf.Cos(angle) * distance,
                center.y,
                center.z + Mathf.Sin(angle) * distance);
        }

        public static float PickRingDistance(float minDistance, float maxDistance)
        {
            float minSq = minDistance * minDistance;
            float maxSq = maxDistance * maxDistance;
            return Mathf.Sqrt(Mathf.Lerp(minSq, maxSq, Random.value));
        }

        public static int RollCount(float scaledCount)
        {
            int targetCount = Mathf.FloorToInt(Mathf.Max(0f, scaledCount));
            if (Random.value < scaledCount - targetCount)
                targetCount++;

            return targetCount;
        }

        public static bool IsOutsideImmediateView(Vector3 worldPos, Vector3 playerPos, float visibleDistance, float viewportMargin)
        {
            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.MainCamera == null)
                return true;

            Vector3 flatDelta = worldPos - playerPos;
            flatDelta.y = 0f;
            if (flatDelta.sqrMagnitude > visibleDistance * visibleDistance)
                return true;

            Vector3 viewport = gameManager.MainCamera.WorldToViewportPoint(worldPos);
            if (viewport.z <= 0f)
                return true;

            return viewport.x < -viewportMargin ||
                   viewport.x > 1f + viewportMargin ||
                   viewport.y < -viewportMargin ||
                   viewport.y > 1f + viewportMargin;
        }

        public static float OceanSurfaceWorldY()
        {
            float oceanY;
            return TryGetOceanSurfaceWorldY(out oceanY) ? oceanY : 0f;
        }

        // Last successfully-computed ocean surface Y, used to bridge transient
        // resolution failures (see below).
        private static bool hasCachedOceanSurfaceY;
        private static float cachedOceanSurfaceY;

        public static bool TryGetOceanSurfaceWorldY(out float oceanY)
        {
            oceanY = 0f;

            var gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame() || gameManager.StreamingWorld == null || DaggerfallUnity.Instance == null)
            {
                // During a map-pixel crossing the terrain update can stall for
                // ~1s, and these references / IsPlayingGame() briefly report
                // unavailable for individual frames. Returning false here makes
                // the swim driver read oceanY=0, decide the player isn't in
                // water, drop swim state, re-enable gravity for that frame, and
                // fling the swimmer — the repeated up/down surface bob. The
                // ocean surface is effectively constant (it only moves on a
                // vertical floating-origin shift, which is reflected the next
                // successful call), so bridge the gap with the last good value
                // instead of spuriously reporting "no ocean". (issue 5)
                if (hasCachedOceanSurfaceY)
                {
                    oceanY = cachedOceanSurfaceY;
                    return true;
                }

                return false;
            }

            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            oceanY = sampler.OceanElevation * gameManager.StreamingWorld.TerrainScale
                   + gameManager.StreamingWorld.WorldCompensation.y;
            cachedOceanSurfaceY = oceanY;
            hasCachedOceanSurfaceY = true;
            return true;
        }

        public static bool TryGetWaterColumn(float worldX, float worldZ, out DeepWaterColumn column)
        {
            column = new DeepWaterColumn();

            var gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame())
                return false;

            var streamingWorld = gameManager.StreamingWorld;
            var playerGPS = gameManager.PlayerGPS;
            if (streamingWorld == null || playerGPS == null)
                return false;

            float tileWorldSize = TileWorldSize;
            DaggerfallTerrain dfTerrain;
            Terrain terrain;
            if (!DeepWaterTerrainLookup.TryGetByWorldPosition(worldX, worldZ, out dfTerrain, out terrain))
            {
                DaggerfallTerrain playerDfTerrain;
                Terrain playerTerrain;
                if (!DeepWaterTerrainLookup.TryGet(streamingWorld, playerGPS.CurrentMapPixel.X, playerGPS.CurrentMapPixel.Y, out playerDfTerrain, out playerTerrain))
                    return false;

                Vector3 playerTileOrigin = playerDfTerrain.transform.position;
                int tileDx = Mathf.FloorToInt((worldX - playerTileOrigin.x) / tileWorldSize);
                int tileDz = Mathf.FloorToInt((worldZ - playerTileOrigin.z) / tileWorldSize);
                int targetX = playerGPS.CurrentMapPixel.X + tileDx;
                int targetY = playerGPS.CurrentMapPixel.Y - tileDz;

                if (!DeepWaterTerrainLookup.TryGet(streamingWorld, targetX, targetY, out dfTerrain, out terrain))
                    return false;
            }

            if (dfTerrain == null || terrain == null || terrain.terrainData == null || dfTerrain.MapData.heightmapSamples == null)
                return false;

            float fracX = (worldX - dfTerrain.transform.position.x) / tileWorldSize;
            float fracZ = (worldZ - dfTerrain.transform.position.z) / tileWorldSize;
            if (fracX < 0f || fracX > 1f || fracZ < 0f || fracZ > 1f)
                return false;

            var tile = dfTerrain.GetComponent<DeepWaterTileData>();
            if (tile == null || !tile.IsOceanConnected || !tile.HasDistanceField)
                return false;

            if (!DeepWaterWaterClassification.IsLocalPointWater(dfTerrain.MapData, fracX, fracZ))
                return false;

            if (DeepWaterDistanceBake.HasFineWaterMask && !tile.IsCarvedWater(worldX, worldZ))
                return false;

            float[,] heights = dfTerrain.MapData.heightmapSamples;
            int hDim0 = heights.GetLength(0);
            int hDim1 = heights.GetLength(1);
            int sx = Mathf.Clamp((int)(fracX * (hDim1 - 1)), 0, hDim1 - 1);
            int sy = Mathf.Clamp((int)(fracZ * (hDim0 - 1)), 0, hDim0 - 1);

            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanLocalY = (sampler.OceanElevation / sampler.MaxTerrainHeight) * terrain.terrainData.size.y;
            float seafloorLocalY = ResolveSeafloorLocalY(dfTerrain, terrain.terrainData, worldX, worldZ, oceanLocalY, heights[sy, sx]);

            column.DaggerfallTerrain = dfTerrain;
            column.Terrain = terrain;
            column.TerrainData = terrain.terrainData;
            column.Parent = dfTerrain.transform;
            column.SeafloorLocalY = seafloorLocalY;
            column.OceanLocalY = oceanLocalY;
            column.SeafloorWorldY = dfTerrain.transform.position.y + seafloorLocalY;
            column.OceanWorldY = dfTerrain.transform.position.y + oceanLocalY;
            column.SampleX = sx;
            column.SampleY = sy;
            return true;
        }

        public static bool TryGetRenderedSeafloorLocalY(
            DeepWaterColumn column,
            float worldX,
            float worldZ,
            out float seafloorLocalY)
        {
            seafloorLocalY = column.SeafloorLocalY;

            if (column.Parent == null)
                return false;

            DeepWaterFloorMesh floorMesh = GetCachedFloorMesh(column.Parent);
            if (floorMesh != null)
            {
                float meshLocalY;
                if (floorMesh.TrySampleMeshLocalY(worldX, worldZ, out meshLocalY))
                {
                    seafloorLocalY = meshLocalY;
                    return true;
                }
            }

            return true;
        }

        private static DeepWaterFloorMesh GetCachedFloorMesh(Transform parent)
        {
            if (parent == null)
                return null;

            DeepWaterFloorMesh floorMesh;
            if (floorMeshCache.TryGetValue(parent, out floorMesh) && floorMesh != null)
                return floorMesh;

            floorMesh = parent.GetComponentInChildren<DeepWaterFloorMesh>();
            floorMeshCache[parent] = floorMesh;
            return floorMesh;
        }

        public static bool TryGetRenderedSeafloorWorldY(
            DeepWaterColumn column,
            float worldX,
            float worldZ,
            out float seafloorWorldY)
        {
            seafloorWorldY = column.SeafloorWorldY;

            float seafloorLocalY;
            if (!TryGetRenderedSeafloorLocalY(column, worldX, worldZ, out seafloorLocalY))
                return false;

            seafloorWorldY = column.Parent.position.y + seafloorLocalY;
            return true;
        }

        /// <summary>
        /// Seafloor world Y at a position only when an ACTUAL carved seabed mesh
        /// quad exists there. Unlike <see cref="TryGetWaterColumn"/> (which trusts
        /// the bake water mask) this returns false where the carve was rejected —
        /// e.g. a shore cell the bake marks water but whose live heightmap has
        /// relief, so no hole/sub-mesh was built. Callers use the false result to
        /// treat the position as solid ground rather than swimmable water.
        /// </summary>
        public static bool TryGetCarvedSeafloorWorldY(float worldX, float worldZ, out float seafloorWorldY)
        {
            seafloorWorldY = 0f;

            DeepWaterColumn column;
            if (!TryGetWaterColumn(worldX, worldZ, out column) || column.Parent == null)
                return false;

            DeepWaterFloorMesh floorMesh = GetCachedFloorMesh(column.Parent);
            if (floorMesh == null)
                return false;

            float meshLocalY;
            if (!floorMesh.TrySampleMeshLocalY(worldX, worldZ, out meshLocalY))
                return false;

            seafloorWorldY = column.Parent.position.y + meshLocalY;
            return true;
        }

        /// <summary>
        /// Resolve seafloor local Y at a world position. Ocean-connected tiles
        /// route through <see cref="DeepBathymetry.SampleDepthMeters"/> so the
        /// returned Y matches the sub-mesh geometry. Other tiles (rivers,
        /// lakes, inland water) fall back to the vanilla heightmap sample —
        /// which for water cells is at ocean elevation, giving depth=0 and
        /// letting consumer code reject those positions naturally.
        /// </summary>
        private static float ResolveSeafloorLocalY(
            DaggerfallTerrain dfTerrain,
            TerrainData terrainData,
            float worldX,
            float worldZ,
            float oceanLocalY,
            float vanillaSample)
        {
            var tile = dfTerrain.GetComponent<DeepWaterTileData>();
            if (tile != null && tile.IsOceanConnected && tile.HasDistanceField)
            {
                float shoreDistance = tile.GetDistanceToEdgeMeters(worldX, worldZ);
                float noiseX, noiseZ;
                tile.GetNoiseWorldCoords(worldX, worldZ, out noiseX, out noiseZ);
                float depth = DeepBathymetry.SampleDepthMeters(
                    noiseX, noiseZ, tile.GetBlendedClimateBaseDepth(worldX, worldZ), shoreDistance);
                return oceanLocalY - depth;
            }

            return vanillaSample * terrainData.size.y;
        }

        public static bool TryResolveSeafloorPosition(
            float worldX,
            float worldZ,
            float minimumDepth,
            float seafloorLift,
            out Vector3 worldPos,
            out Transform parent)
        {
            worldPos = Vector3.zero;
            parent = null;

            DeepWaterColumn column;
            if (!TryGetWaterColumn(worldX, worldZ, out column))
                return false;

            if (column.Depth < minimumDepth)
                return false;

            float seafloorWorldY;
            if (!TryGetRenderedSeafloorWorldY(column, worldX, worldZ, out seafloorWorldY))
                return false;

            if (column.OceanWorldY - seafloorWorldY < minimumDepth)
                return false;

            worldPos = new Vector3(worldX, seafloorWorldY + seafloorLift, worldZ);
            parent = column.Parent;
            return true;
        }

        public static bool AlignObjectBottomToWorldY(GameObject gameObject, float bottomWorldY)
        {
            if (gameObject == null)
                return false;

            Bounds bounds;
            if (!TryGetObjectWorldBounds(gameObject, out bounds))
                return false;

            float deltaY = bottomWorldY - bounds.min.y;
            if (Mathf.Abs(deltaY) <= 0.001f)
                return true;

            gameObject.transform.position += Vector3.up * deltaY;
            return true;
        }

        private static bool TryGetObjectWorldBounds(GameObject gameObject, out Bounds bounds)
        {
            bounds = new Bounds(gameObject != null ? gameObject.transform.position : Vector3.zero, Vector3.zero);
            if (gameObject == null)
                return false;

            bool hasBounds = false;
            Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null)
                    continue;

                if (!hasBounds)
                {
                    bounds = renderers[i].bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
            }

            if (hasBounds)
                return true;

            Collider[] colliders = gameObject.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] == null)
                    continue;

                if (!hasBounds)
                {
                    bounds = colliders[i].bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(colliders[i].bounds);
                }
            }

            return hasBounds;
        }

        public static bool HasNearbyWaterColumn(Vector3 center, float minDistance, float maxDistance, int directions, float minimumDepth, out float depth)
        {
            if (TryGetWaterColumnDepth(center.x, center.z, minimumDepth, out depth))
                return true;

            float midDistance = (minDistance + maxDistance) * 0.5f;
            return ProbeWaterColumnRing(center, minDistance, directions, minimumDepth, out depth) ||
                   ProbeWaterColumnRing(center, midDistance, directions, minimumDepth, out depth) ||
                   ProbeWaterColumnRing(center, maxDistance, directions, minimumDepth, out depth);
        }

        private static bool ProbeWaterColumnRing(Vector3 center, float radius, int directions, float minimumDepth, out float depth)
        {
            int probes = Mathf.Max(1, directions);
            for (int i = 0; i < probes; i++)
            {
                float angle = (Mathf.PI * 2f * i) / probes;
                float worldX = center.x + Mathf.Cos(angle) * radius;
                float worldZ = center.z + Mathf.Sin(angle) * radius;
                if (TryGetWaterColumnDepth(worldX, worldZ, minimumDepth, out depth))
                    return true;
            }

            depth = 0f;
            return false;
        }

        private static bool TryGetWaterColumnDepth(float worldX, float worldZ, float minimumDepth, out float depth)
        {
            depth = 0f;

            DeepWaterColumn column;
            if (!TryGetWaterColumn(worldX, worldZ, out column))
                return false;

            depth = column.Depth;
            return depth >= minimumDepth;
        }

        public static long TileKey(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        public static long WorldCellKey(float worldX, float worldZ, float cellSize)
        {
            int cellX = Mathf.FloorToInt(worldX / cellSize);
            int cellZ = Mathf.FloorToInt(worldZ / cellSize);
            return ((long)cellX << 32) | (uint)cellZ;
        }
    }

}
