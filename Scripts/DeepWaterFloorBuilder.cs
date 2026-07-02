// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Listens to DaggerfallTerrain promotion. For ocean-connected tiles:
    ///   1. Attaches a <see cref="DeepWaterTileData"/> cache (climate +
    ///      distance-to-coast field).
    ///   2. Computes the per-cell carve mask (which cells are deep water).
    ///   3. Builds a <see cref="DeepWaterFloorMesh"/> child from that mask.
    ///
    /// The vanilla terrain is never modified: real Unity terrain holes
    /// native-crash Unity 2019.4 (TerrainRenderer::ForceSplitParent when a
    /// holed patch subdivides), so the heightfield stays intact and the
    /// outdoor swim collider gate disables its collider locally to let the
    /// player descend to the seafloor mesh.
    /// </summary>
    internal static class DeepWaterFloorBuilder
    {
        private const string FloorChildName = "DeepWaters_Seafloor";
        // Buffer of non-hole vanilla terrain we keep around the shoreline.
        // Larger values push the hole-to-seafloor wall further offshore and
        // make the wall taller (because depth grows with distance from
        // coast). The cliff is visible from the surface as a small mini-wall
        // just out into the water. 3m keeps a safety margin against
        // floating-point hole/terrain race conditions without making the
        // shore step prominent.
        // Internal so DeepWaterFloorMesh can read the same constant
        // when sampling the bake's distance field (it's used as the
        // shore-buffer cutoff in two places).
        //
        // Reduced from 3 m to 0.5 m to push carved holes much closer to
        // the actual visible shore. The 3 m value was a paranoid safety
        // margin against per-tile BFS errors at tile boundaries; with
        // the global bake's deterministic distance field, adjacent
        // tiles agree exactly at their shared edge, so the safety
        // margin doesn't need to be that wide. 0.5 m leaves just
        // enough buffer to avoid the heightmap-interpolation seam where
        // IT's beach sand starts to fade up from sea level.
        internal const float HoleBufferMeters = 0.5f;

        // Decorations wait for this so spawn heights sample the finished floor.
        internal static event System.Action<DaggerfallTerrain> OnFloorRefreshed;

        private static bool installed;

        internal static void Install()
        {
            if (installed) return;
            DaggerfallTerrain.OnPromoteTerrainData += HandlePromote;
            installed = true;
        }

        // Force=true bypasses HandlePromoteCore's IsCurrentBuild guard so
        // callers that genuinely need a rebuild (settings changed, save
        // load completed) actually get one even when DFU hasn't
        // re-allocated heightmap arrays.
        internal static void RefreshLoadedTiles(bool force = false)
        {
            DaggerfallTerrain[] terrains = Object.FindObjectsOfType<DaggerfallTerrain>();
            for (int i = 0; i < terrains.Length; i++)
                RefreshLoadedTile(terrains[i], force);
        }

        private static void RefreshLoadedTile(DaggerfallTerrain dfTerrain, bool force = false)
        {
            if (dfTerrain == null)
                return;

            Terrain unityTerrain = dfTerrain.GetComponent<Terrain>();
            if (unityTerrain != null && unityTerrain.terrainData != null)
                HandlePromote(dfTerrain, unityTerrain.terrainData, force);
        }

        private static void HandlePromote(DaggerfallTerrain dfTerrain, TerrainData terrainData)
        {
            // Genuine DFU promote event. It fires from inside
            // StreamingWorld's terrain update — BEFORE this tile renders its
            // first frame — so the TerrainRenderer LOD quadtree does not yet
            // exist and carving holes here cannot hit the ForceSplitParent
            // crash, whether the GameObject is active (brand-new tiles are
            // created active) or inactive (recycled tiles). This, not the
            // active flag, is the safe carve window.
            HandlePromote(dfTerrain, terrainData, force: false, fromPromoteEvent: true);
        }

        private static void HandlePromote(DaggerfallTerrain dfTerrain, TerrainData terrainData, bool force)
        {
            // Forced refresh (settings change / post-load). The tile has
            // already rendered, so an immediate SetHoles here is the unstable
            // live-terrain write path; the carve from the original promote
            // event still stands, so this path never re-carves.
            HandlePromote(dfTerrain, terrainData, force, fromPromoteEvent: false);
        }

        private static void HandlePromote(DaggerfallTerrain dfTerrain, TerrainData terrainData, bool force, bool fromPromoteEvent)
        {
            try
            {
                HandlePromoteCore(dfTerrain, terrainData, force, fromPromoteEvent);
            }
            catch (System.Exception ex)
            {
                int mx = dfTerrain != null ? dfTerrain.MapPixelX : -1;
                int my = dfTerrain != null ? dfTerrain.MapPixelY : -1;
                Debug.LogError("[DeepWaters] HandlePromote crashed for tile (" + mx + "," + my +
                               "): " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private static void HandlePromoteCore(DaggerfallTerrain dfTerrain, TerrainData terrainData, bool force, bool fromPromoteEvent)
        {
            if (dfTerrain == null || terrainData == null) return;

            // The genuine promote event is the safe pre-first-render window to
            // carve holes and build the seafloor child, so let it through even
            // though CanMutateTerrainData reports terrain-streaming-active.
            // Only the forced refresh path (already-rendered tiles) is gated,
            // because mutating a live terrain is the unstable write path.
            if (!fromPromoteEvent && !DeepWaterRuntime.CanMutateTerrainData)
                return;

            // NOTE: do NOT gate seafloor generation on SpawnWaterSurfaces.
            // SpawnWaterSurfaces controls only the visible water-plane mesh
            // (WaterSurfaceManager); the carved seabed, swim depth, and
            // underwater world must still generate when the surface is hidden,
            // otherwise turning surfaces off breaks water generation entirely.
            if (DeepWaters.Instance == null)
            {
                RemoveFloor(dfTerrain);
                return;
            }

            // IsCurrentBuild guard. DFU's promote pipeline allocates a fresh
            // float[,] heightmapSamples on every real promote, so reference
            // equality reliably tells us whether the existing mesh was built
            // from the heightmap the tile is presently wearing. Skipping
            // current tiles matters: RefreshPlayerArea re-promotes the whole
            // loaded ring every streaming pulse, and a spurious rebuild bumps
            // BuildVersion, which tears down and respawns the tile's
            // decorations.
            if (!force)
            {
                Transform existingFloorChild = dfTerrain.transform.Find(FloorChildName);
                if (existingFloorChild != null)
                {
                    DeepWaterFloorMesh existingMesh = existingFloorChild.GetComponent<DeepWaterFloorMesh>();
                    if (existingMesh != null &&
                        existingMesh.BuildVersion > 0 &&
                        existingMesh.BuiltMapPixelX == dfTerrain.MapPixelX &&
                        existingMesh.BuiltMapPixelY == dfTerrain.MapPixelY &&
                        object.ReferenceEquals(
                            existingMesh.LastBuiltHeightmapSamples,
                            dfTerrain.MapData.heightmapSamples))
                    {
                        existingMesh.EnsureRuntimeCollider();
                        UpdateTerrainCapRenderer(dfTerrain);
                        return;
                    }
                }
            }

            int climateIndex = ResolveClimateIndex(dfTerrain);
            DeepWaterTileData tile = EnsureTileData(dfTerrain);
            tile.Initialize(dfTerrain, climateIndex);

            if (!tile.IsOceanConnected || !tile.HasDistanceField)
            {
                RemoveFloor(dfTerrain);
                return;
            }

            bool[,] holes;
            bool hasAnyHoles = ComputeHoleMask(dfTerrain, tile, terrainData, out holes);
            if (!hasAnyHoles)
            {
                RemoveFloor(dfTerrain);
                return;
            }

            BuildOrRefreshFloor(dfTerrain, terrainData, tile, holes);
            UpdateTerrainCapRenderer(dfTerrain);
			if (OnFloorRefreshed != null)
			{
				try { OnFloorRefreshed(dfTerrain); }
				catch (System.Exception ex)
				{
					Debug.LogWarning("[DeepWaters.Builder] OnFloorRefreshed subscriber threw for tile (" +
						dfTerrain.MapPixelX + "," + dfTerrain.MapPixelY + "): " + ex.Message);
				}
			}
        }

        private static int ResolveClimateIndex(DaggerfallTerrain dfTerrain)
        {
            DaggerfallUnity dfu = DaggerfallUnity.Instance;
            if (dfu == null || dfu.ContentReader == null || dfu.ContentReader.MapFileReader == null)
                return (int)MapsFile.Climates.Ocean;

            return dfu.ContentReader.MapFileReader.GetClimateIndex(dfTerrain.MapPixelX, dfTerrain.MapPixelY);
        }

        private static DeepWaterTileData EnsureTileData(DaggerfallTerrain dfTerrain)
        {
            var existing = dfTerrain.GetComponent<DeepWaterTileData>();
            if (existing != null) return existing;
            return dfTerrain.gameObject.AddComponent<DeepWaterTileData>();
        }

        private static bool ComputeHoleMask(
            DaggerfallTerrain dfTerrain,
            DeepWaterTileData tile,
            TerrainData terrainData,
            out bool[,] holes)
        {
            holes = null;
            float[,] heights = dfTerrain.MapData.heightmapSamples;
            if (heights == null) return false;

            int holeRes = terrainData.holesResolution;
            int hRows = heights.GetLength(0);
            int hCols = heights.GetLength(1);
            if (holeRes > hRows - 1 || holeRes > hCols - 1)
                holeRes = Mathf.Min(hRows, hCols) - 1;

            holes = new bool[holeRes, holeRes];
            bool anyHole = false;

            // Phase B path (v4 bake with fine mask): per-cell carve query
            // against the global fine mask. Adjacent tiles read the SAME
            // bake value at their shared boundary world positions, so
            // they agree on every boundary cell's carve decision —
            // eliminating cross-pixel seams and the 1-pixel walls at the
            // shore that came from per-tile heightmap reclassification
            // mismatches.
            //
            // Hot-loop optimization: we used to call tile.IsCarvedWater
            // (worldX, worldZ) which routed through DeepWaterTileData's
            // GetGlobalMapFractions, which reads transform.position once
            // per cell. With ~16k cells × ~30 tiles per stream cycle
            // that's ~500k Unity transform.position calls per pulse,
            // which directly produced the v0.53.0 perf regression. We
            // can compute fracX/fracZ from (hx, hy) and the hole grid
            // size alone (independent of world XZ + tile origin), then
            // call DeepWaterDistanceBake.IsCarvedWater directly. No
            // transform access, no world-position math, no distance
            // lookup (the carve decision doesn't need it — distance is
            // only used for the seafloor mesh depth profile, sampled at
            // vertex time).
            //
            // Pre-v4 fallback: the original heightmap any-corner check.
            // Matches WaterSurfaceManager.HasWaterTile's water plane
            // criterion exactly. Boundary cells where 3 of 4 corners sit
            // on the beach gradient still get carved if 1 corner reaches
            // the clamp.
            bool useBakeMask = DeepWaterDistanceBake.HasFineWaterMask && !tile.UsesLocalWaterFallback;
            int mapPixelX = dfTerrain.MapPixelX;
            int mapPixelY = dfTerrain.MapPixelY;
            float invHoleRes = 1f / holeRes;

            // CRASH-FIX gate: only carve a cell whose ALL FOUR heightmap corners
            // sit at/below ocean level (fully-flat ocean floor). Carving a cell
            // that has shore RELIEF puts a hole in a terrain patch that
            // subdivides, and Unity 2019.4 native-crashes its render thread when
            // it splits a holed patch — that is BOTH the load crash (the player's
            // coastal tile) and the near-shore surface crash. The minimal working
            // build carves with exactly this all-corners test and never crashes;
            // the bake below still decides which flat cells to carve so cross-
            // tile seams stay fixed.
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanThreshold = sampler.OceanElevation / sampler.MaxTerrainHeight;
            const float oceanThresholdEps = 1e-5f;

            for (int hy = 0; hy < holeRes; hy++)
            {
                float fracZ = (hy + 0.5f) * invHoleRes;
                for (int hx = 0; hx < holeRes; hx++)
                {
                    holes[hy, hx] = true;
                    float fracX = (hx + 0.5f) * invHoleRes;
                    bool pureBakedWater = !useBakeMask &&
                        DeepWaterWaterClassification.IsLocalPointPureWaterTile(dfTerrain.MapData, fracX, fracZ) &&
                        DeepWaterDistanceBake.IsWaterAt(mapPixelX, mapPixelY, fracX, fracZ);
					bool localWater = DeepWaterWaterClassification.IsLocalPointWater(dfTerrain.MapData, fracX, fracZ);
					if (tile.UsesLocalWaterFallback)
					{
						if (!localWater)
							continue;
					}
					else if (!useBakeMask && !localWater && !pureBakedWater)
					{
						continue;
					}

					// Reject dry raised land, but keep live-water shore cells:
					// WoD can promote water over non-flat terrain. The cap
					// renderer clips those texels, so the floor must exist below.
                    bool flatOceanCell =
                        heights[hy, hx]         <= oceanThreshold + oceanThresholdEps &&
                        heights[hy, hx + 1]     <= oceanThreshold + oceanThresholdEps &&
                        heights[hy + 1, hx]     <= oceanThreshold + oceanThresholdEps &&
                        heights[hy + 1, hx + 1] <= oceanThreshold + oceanThresholdEps;
					bool liveWaterCell = (useBakeMask || tile.UsesLocalWaterFallback) && localWater;
					if (!flatOceanCell && !pureBakedWater && !liveWaterCell)
						continue;

                    bool isWater = true;
                    if (isWater && useBakeMask)
                    {
                        isWater = tile.IsCarvedWater(mapPixelX, mapPixelY, fracX, fracZ);
                    }

                    if (!isWater) continue;

                    holes[hy, hx] = false;
                    anyHole = true;
                }
            }

            return anyHole;
        }

        private static void BuildOrRefreshFloor(DaggerfallTerrain dfTerrain, TerrainData terrainData, DeepWaterTileData tile, bool[,] holes)
        {
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanLocalY = (sampler.OceanElevation / sampler.MaxTerrainHeight) * terrainData.size.y;

            Transform existing = dfTerrain.transform.Find(FloorChildName);
            GameObject floorGO;
            DeepWaterFloorMesh meshComp;

            if (existing == null)
            {
                floorGO = new GameObject(FloorChildName);
                floorGO.transform.SetParent(dfTerrain.transform, false);
                floorGO.transform.localPosition = Vector3.zero;
                floorGO.transform.localRotation = Quaternion.identity;
                floorGO.transform.localScale = Vector3.one;
                floorGO.AddComponent<MeshFilter>();
                var mr = floorGO.AddComponent<MeshRenderer>();
                DeepWaterRendering.DisableShadows(mr);
                meshComp = floorGO.AddComponent<DeepWaterFloorMesh>();
            }
            else
            {
                floorGO = existing.gameObject;
                floorGO.transform.localPosition = Vector3.zero;
                floorGO.transform.localRotation = Quaternion.identity;
                floorGO.transform.localScale = Vector3.one;
                meshComp = floorGO.GetComponent<DeepWaterFloorMesh>();
                if (meshComp == null)
                    meshComp = floorGO.AddComponent<DeepWaterFloorMesh>();
            }

            meshComp.Build(dfTerrain, tile, oceanLocalY, holes);
        }

        private static void RemoveFloor(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null) return;
            DeepWaterTerrainCapRenderer.Restore(dfTerrain);
            Transform existing = dfTerrain.transform.Find(FloorChildName);
            if (existing == null) return;

            var meshComp = existing.GetComponent<DeepWaterFloorMesh>();
            if (meshComp != null) meshComp.TearDown();

            Object.Destroy(existing.gameObject);
        }

        private static void UpdateTerrainCapRenderer(DaggerfallTerrain dfTerrain)
        {
            bool hidePureOceanCap = DeepWaterTerrainCapRenderer.ShouldHidePureOceanCap(dfTerrain);
            DeepWaterTerrainCapRenderer.Apply(dfTerrain, hidePureOceanCap);
            // Mixed land/water tiles still need their land terrain, but the
            // pure-water texels are an opaque square cap over the generated
            // shore floor. Clip only those texels; shore transition tiles keep
            // rendering normally.
            DeepWaterTerrainCapRenderer.ApplyWaterTexelClip(
                dfTerrain,
                !hidePureOceanCap && DeepWaterWaterClassification.MapDataHasWater(dfTerrain.MapData));
        }
    }

	internal static class DeepWaterTerrainCapRenderer
	{
		private sealed class HiddenCapMarker : MonoBehaviour
		{
			internal bool HasOriginalDrawHeightmap;
			internal bool OriginalDrawHeightmap;
			internal bool Hidden;
			internal Shader OriginalShader;
			internal Texture OriginalTilemapTexture;
			internal Texture2D PatchedTilemapTexture;
			internal bool WaterTexelsClipped;
		}

		private static readonly int TilemapTexProperty = Shader.PropertyToID("_TilemapTex");
		private static Shader tilemapTextureArrayClipShader;
		private static Shader tilemapClipShader;
		private static bool clipShadersResolved;
		private static bool loggedUnknownTerrainShader;

		internal static void Apply(DaggerfallTerrain dfTerrain, bool hide)
		{
			if (dfTerrain == null)
				return;

			Terrain terrain = dfTerrain.GetComponent<Terrain>();
			if (terrain == null)
				return;

			if (!hide)
			{
				Restore(dfTerrain);
				return;
			}

			HiddenCapMarker marker = dfTerrain.GetComponent<HiddenCapMarker>();
			if (marker == null)
				marker = dfTerrain.gameObject.AddComponent<HiddenCapMarker>();

			if (!marker.HasOriginalDrawHeightmap)
			{
				marker.OriginalDrawHeightmap = terrain.drawHeightmap;
				marker.HasOriginalDrawHeightmap = true;
			}

			terrain.drawHeightmap = false;
			marker.Hidden = true;
		}

		internal static void Restore(DaggerfallTerrain dfTerrain)
		{
			if (dfTerrain == null)
				return;

			ApplyWaterTexelClip(dfTerrain, false);

			HiddenCapMarker marker = dfTerrain.GetComponent<HiddenCapMarker>();
			if (marker == null || !marker.Hidden)
				return;

			Terrain terrain = dfTerrain.GetComponent<Terrain>();
			if (terrain != null && marker.HasOriginalDrawHeightmap)
				terrain.drawHeightmap = marker.OriginalDrawHeightmap;

			marker.Hidden = false;
		}

		internal static void ApplyWaterTexelClip(DaggerfallTerrain dfTerrain, bool clip)
		{
			if (dfTerrain == null)
				return;

			Terrain terrain = dfTerrain.GetComponent<Terrain>();
			Material material = terrain != null ? terrain.materialTemplate : null;
			if (material == null || material.shader == null)
				return;

			HiddenCapMarker marker = dfTerrain.GetComponent<HiddenCapMarker>();

			if (!clip)
			{
				if (marker != null && marker.WaterTexelsClipped)
				{
					if (marker.OriginalShader != null)
						material.shader = marker.OriginalShader;
					RestoreTilemapTexture(material, marker);
					marker.WaterTexelsClipped = false;
				}
				return;
			}

			if (marker != null && marker.WaterTexelsClipped)
			{
				ApplyTilemapTextureClip(dfTerrain, material, marker);
				return;
			}

			string originalShaderName = material.shader.name;
			Shader clipShader = ResolveClipShader(originalShaderName);
			if (clipShader == null)
				return;

			if (marker == null)
				marker = dfTerrain.gameObject.AddComponent<HiddenCapMarker>();

			marker.OriginalShader = material.shader;
			material.shader = clipShader;
			marker.WaterTexelsClipped = true;
			ApplyTilemapTextureClip(dfTerrain, material, marker);
		}

		private static void ApplyTilemapTextureClip(DaggerfallTerrain dfTerrain, Material material, HiddenCapMarker marker)
		{
			if (dfTerrain == null || material == null || marker == null)
				return;

			Color32[] source = dfTerrain.TileMap;
			if (source == null || source.Length == 0)
				return;

			int dim = Mathf.RoundToInt(Mathf.Sqrt(source.Length));
			if (dim <= 0 || dim * dim != source.Length)
				return;

			bool textureArray =
				(marker.OriginalShader != null && marker.OriginalShader.name == "Daggerfall/TilemapTextureArray") ||
				(material.shader != null && material.shader.name == "DeepWaters/TilemapTextureArrayClipWater");

			var pixels = new Color32[source.Length];
			bool changed = false;
			for (int z = 0; z < dim; z++)
			{
				for (int x = 0; x < dim; x++)
				{
					int i = z * dim + x;
					Color32 color = source[i];
					bool waterTexel = IsClippedWaterTileData(color.a, textureArray);
					if (waterTexel && ShouldClipPromotedWaterTexel(dfTerrain, color.a, textureArray, x, z, dim))
					{
						color.r = 255;
						color.g = 0;
						color.b = 255;
						color.a = 0;
						changed = true;
					}
					else if (waterTexel && TryFindNearestSolidTexel(source, textureArray, x, z, dim, out color))
					{
						changed = true;
					}
					pixels[i] = color;
				}
			}

			if (!changed)
				return;

			if (marker.OriginalTilemapTexture == null)
				marker.OriginalTilemapTexture = material.GetTexture(TilemapTexProperty);

			if (marker.PatchedTilemapTexture != null)
				Object.Destroy(marker.PatchedTilemapTexture);

			Texture2D texture = new Texture2D(dim, dim, TextureFormat.RGBA32, false);
			texture.name = "DeepWaters_ClippedTilemap_" + dfTerrain.MapPixelX + "_" + dfTerrain.MapPixelY;
			texture.filterMode = FilterMode.Point;
			texture.wrapMode = TextureWrapMode.Clamp;
			texture.SetPixels32(pixels);
			texture.Apply(false, true);

			marker.PatchedTilemapTexture = texture;
			material.SetTexture(TilemapTexProperty, texture);
		}

		private static bool TryFindNearestSolidTexel(
			Color32[] source,
			bool textureArray,
			int texelX,
			int texelZ,
			int dim,
			out Color32 replacement)
		{
			replacement = default(Color32);
			if (source == null || dim <= 0)
				return false;

			const int maxRadius = 8;
			for (int radius = 1; radius <= maxRadius; radius++)
			{
				for (int dz = -radius; dz <= radius; dz++)
				{
					for (int dx = -radius; dx <= radius; dx++)
					{
						if (Mathf.Abs(dx) != radius && Mathf.Abs(dz) != radius)
							continue;

						int x = texelX + dx;
						int z = texelZ + dz;
						if (x < 0 || z < 0 || x >= dim || z >= dim)
							continue;

						Color32 candidate = source[z * dim + x];
						if (!IsClippedWaterTileData(candidate.a, textureArray))
						{
							replacement = candidate;
							return true;
						}
					}
				}
			}

			return false;
		}

		private static void RestoreTilemapTexture(Material material, HiddenCapMarker marker)
		{
			if (material != null && marker.OriginalTilemapTexture != null)
				material.SetTexture(TilemapTexProperty, marker.OriginalTilemapTexture);

			if (marker.PatchedTilemapTexture != null)
			{
				Object.Destroy(marker.PatchedTilemapTexture);
				marker.PatchedTilemapTexture = null;
			}

			marker.OriginalTilemapTexture = null;
		}

		internal static bool IsClippedWaterTileData(byte tileData, bool textureArray)
		{
			int tileIndex = textureArray ? tileData >> 2 : tileData & 0x3f;
			return tileIndex == 0 ||
				(tileIndex >= 5 && tileIndex <= 7) ||
				tileIndex == 48;
		}

		internal static bool ShouldClipPromotedWaterTexel(
			DaggerfallTerrain terrain,
			byte tileData,
			bool textureArray,
			int texelX,
			int texelZ,
			int texelDim)
		{
			return IsClippedWaterTileData(tileData, textureArray) &&
				IsPromotedTerrainTexelSafeToClip(terrain, texelX, texelZ, texelDim);
		}

		private static bool IsPromotedTerrainTexelSafeToClip(
			DaggerfallTerrain terrain,
			int texelX,
			int texelZ,
			int texelDim)
		{
			if (terrain == null || terrain.MapData.heightmapSamples == null || texelDim <= 0)
				return true;

			DaggerfallUnity dfu = DaggerfallUnity.Instance;
			if (dfu == null || dfu.TerrainSampler == null)
				return true;

			float threshold = dfu.TerrainSampler.OceanElevation /
				dfu.TerrainSampler.MaxTerrainHeight + 1e-5f;
			float[,] heights = terrain.MapData.heightmapSamples;
			int rows = heights.GetLength(0);
			int cols = heights.GetLength(1);
			if (rows <= 0 || cols <= 0)
				return true;

			int x0 = Mathf.Clamp(Mathf.FloorToInt(texelX * (cols - 1) / (float)texelDim), 0, cols - 1);
			int x1 = Mathf.Clamp(Mathf.CeilToInt((texelX + 1) * (cols - 1) / (float)texelDim), 0, cols - 1);
			int z0 = Mathf.Clamp(Mathf.FloorToInt(texelZ * (rows - 1) / (float)texelDim), 0, rows - 1);
			int z1 = Mathf.Clamp(Mathf.CeilToInt((texelZ + 1) * (rows - 1) / (float)texelDim), 0, rows - 1);

			for (int z = z0; z <= z1; z++)
			{
				for (int x = x0; x <= x1; x++)
				{
					if (heights[z, x] > threshold)
						return false;
				}
			}

			return true;
		}

		private static Shader ResolveClipShader(string currentShaderName)
		{
			if (!clipShadersResolved)
			{
				clipShadersResolved = true;
				tilemapTextureArrayClipShader = LoadModShader(
					"DeepWaters/TilemapTextureArrayClipWater",
					"DeepWaterTilemapTextureArrayClipWater.shader");
				tilemapClipShader = LoadModShader(
					"DeepWaters/TilemapClipWater",
					"DeepWaterTilemapClipWater.shader");
			}

			if (currentShaderName == "DeepWaters/TilemapTextureArrayClipWater")
				return tilemapTextureArrayClipShader;
			if (currentShaderName == "DeepWaters/TilemapClipWater")
				return tilemapClipShader;
			if (currentShaderName == "Daggerfall/TilemapTextureArray")
				return tilemapTextureArrayClipShader;
			if (currentShaderName == "Daggerfall/Tilemap")
				return tilemapClipShader;

			if (!loggedUnknownTerrainShader)
			{
				loggedUnknownTerrainShader = true;
				Debug.Log("[DeepWaters.Cap] Terrain uses shader '" + currentShaderName +
					"' — no water-texel clip variant available, so the sea-level " +
					"water cap stays visible on mixed land/water map pixels.");
			}

			return null;
		}

		private static Shader LoadModShader(string shaderName, string assetName)
		{
			Shader shader = Shader.Find(shaderName);

			if (shader == null && DeepWaters.Mod != null)
				shader = DeepWaters.Mod.GetAsset<Shader>(assetName);

			if (shader == null)
				Debug.LogWarning("[DeepWaters.Cap] " + shaderName + " shader not found; " +
					"water-texel clipping unavailable for this terrain mode.");

			return shader;
		}

		internal static bool ShouldHidePureOceanCap(DaggerfallTerrain dfTerrain)
		{
			if (dfTerrain == null)
				return false;

			if (!DeepWaterDistanceBake.IsLoaded ||
				!DeepWaterDistanceBake.MapPixelHasWaterCells(dfTerrain.MapPixelX, dfTerrain.MapPixelY))
			{
				return false;
			}

			if (DeepWaterDistanceBake.MapPixelHasLandCells(dfTerrain.MapPixelX, dfTerrain.MapPixelY))
				return false;

			return DeepWaterWaterClassification.MapDataHasWater(dfTerrain.MapData) &&
				DeepWaterWaterClassification.MapDataFullySubmerged(dfTerrain.MapData);
		}
	}
}
