// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallConnect;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Banking;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepWaters
{
	internal static class WaterSurfaceResources
	{
		private const string TopSurfaceShaderName = "DeepWaters/TransparentWaterSurfaceTop";
		private const string TopSurfaceShaderAssetName = "TransparentWaterSurfaceTop.shader";
		private const string UndersideSurfaceShaderName = "DeepWaters/TransparentWaterSurfaceUnderside";
		private const string UndersideSurfaceShaderAssetName = "TransparentWaterSurfaceUnderside.shader";
		private const string TransparentRenderType = "Transparent";

		private static readonly int ColorProperty = Shader.PropertyToID("_Color");
		private static readonly int UndersideAlphaProperty = Shader.PropertyToID("_UndersideAlpha");
		private static readonly int UnderwaterFogColorProperty = Shader.PropertyToID("_UnderwaterFogColor");
		private static readonly int WaterColumnDepthProperty = Shader.PropertyToID("_WaterColumnDepth");
		private static readonly int WaterColumnFogDepthProperty = Shader.PropertyToID("_WaterColumnFogDepth");
		private static readonly int WaterColumnFogStrengthProperty = Shader.PropertyToID("_WaterColumnFogStrength");
		private static readonly int WaterSurfaceVisionDistanceProperty = Shader.PropertyToID("_WaterSurfaceVisionDistance");
		private static readonly int WaterSurfaceFalloffProperty = Shader.PropertyToID("_WaterSurfaceFalloff");
		private static readonly int SurfaceOpaqueFadeStartProperty = Shader.PropertyToID("_SurfaceOpaqueFadeStart");
		private static readonly int SurfaceOpaqueFadeEndProperty = Shader.PropertyToID("_SurfaceOpaqueFadeEnd");
		private static readonly int HorizonColorProperty = Shader.PropertyToID("_HorizonColor");
		private static readonly int SrcBlendProperty = Uniforms.SrcBlend;
		private static readonly int DstBlendProperty = Uniforms.DstBlend;
		private static readonly int ZWriteProperty = Uniforms.ZWrite;

		private static Material sharedTopMaterial;
		private static Material sharedUndersideMaterial;
		private static Texture sharedSurfaceTexture;
		private static readonly Color SurfaceTint = new Color(0.519f, 0.527f, 0.467f, 1f);
		private static readonly Color NightSurfaceTint = new Color(0.055f, 0.105f, 0.12f, 1f);
		private static readonly Color FallbackSurfaceColor = new Color(0.075f, 0.24f, 0.38f, 1f);
		private const float TopSurfaceVisionMultiplier = 1.0f;
		private const float TopSurfaceFogStrengthMultiplier = 1.0f;
		private const float TopSurfaceFalloffMultiplier = 1.0f;
		private const float MaximumTopSurfaceVisionDistance = 36f;

		internal const float SurfaceTextureTiling = 128f;

		internal static Material GetTopMaterial()
		{
			if (sharedTopMaterial == null)
			{
				sharedTopMaterial = CreateMaterial(
					TopSurfaceShaderName,
					TopSurfaceShaderAssetName,
					"DeepWaters.WaterSurface.Top");
			}

			return sharedTopMaterial;
		}

		internal static Material GetUndersideMaterial()
		{
			if (sharedUndersideMaterial == null)
			{
				sharedUndersideMaterial = CreateMaterial(
					UndersideSurfaceShaderName,
					UndersideSurfaceShaderAssetName,
					"DeepWaters.WaterSurface.Underside");
			}

			return sharedUndersideMaterial;
		}

		// Called per frame while underwater so the underside's opaque horizon
		// matches the fog volume's far ambient. Any difference reads as a band.
		internal static void SetHorizonColor(Color color)
		{
			if (sharedUndersideMaterial != null && sharedUndersideMaterial.HasProperty(HorizonColorProperty))
				sharedUndersideMaterial.SetColor(HorizonColorProperty, color);

			ApplyDynamicUndersideSettings(sharedUndersideMaterial);
		}

		internal static Texture GetSurfaceTexture()
		{
			if (sharedSurfaceTexture != null)
				return sharedSurfaceTexture;

			Texture2D waterTex = LoadWaterTexture();
			if (waterTex == null)
				return null;

			ApplyWaterTextureSettings(waterTex);
			sharedSurfaceTexture = waterTex;
			return sharedSurfaceTexture;
		}

		internal static void ApplyMaterialSettings()
		{
			if (DeepWaters.Instance == null)
				return;

			ConfigureTopMaterial(sharedTopMaterial);
			ConfigureUndersideMaterial(sharedUndersideMaterial);
		}

		internal static void RefreshDynamicMaterialSettings()
		{
			ApplyDynamicTopSettings(sharedTopMaterial);
			ApplyDynamicUndersideSettings(sharedUndersideMaterial);
		}

		private static Material CreateMaterial(string shaderName, string shaderAssetName, string materialName)
		{
			Shader shader = LoadShader(shaderName, shaderAssetName);
			if (shader == null)
				return null;

			Material material = new Material(shader) { name = materialName };
			ConfigureTransparentMaterial(material);
			ApplyBaseTexture(material);
			return material;
		}

		private static Shader LoadShader(string shaderName, string shaderAssetName)
		{
			Shader shader = Shader.Find(shaderName);

			if (shader == null && DeepWaters.Mod != null)
				shader = DeepWaters.Mod.GetAsset<Shader>(shaderAssetName);

			if (shader == null)
			{
				Debug.LogError(
					"[DeepWaters] " + shaderName + " shader not found. Water surfaces will not render.");
			}

			return shader;
		}

		private static void ConfigureTopMaterial(Material material)
		{
			if (material == null || DeepWaters.Instance == null)
				return;

			ConfigureTransparentMaterial(material);

			if (material.HasProperty(ColorProperty))
			{
				Color color = GetTimeAdjustedSurfaceTint();
				color.a = DeepWaters.Instance.WaterSurfaceTopAlpha;
				material.SetColor(ColorProperty, color);
			}

			ApplySharedWaterProperties(material);

			if (material.HasProperty(WaterColumnFogStrengthProperty))
				material.SetFloat(WaterColumnFogStrengthProperty, Mathf.Clamp01(GetWaterColumnFogStrength() * TopSurfaceFogStrengthMultiplier));
			if (material.HasProperty(WaterSurfaceVisionDistanceProperty))
				material.SetFloat(WaterSurfaceVisionDistanceProperty, GetTopSurfaceVisionDistance() * TopSurfaceVisionMultiplier);
			if (material.HasProperty(WaterSurfaceFalloffProperty))
				material.SetFloat(WaterSurfaceFalloffProperty, Mathf.Clamp01(DeepWaters.Instance.WaterSurfaceDistanceFalloff * TopSurfaceFalloffMultiplier));
		}

		private static void ConfigureUndersideMaterial(Material material)
		{
			if (material == null || DeepWaters.Instance == null)
				return;

			ConfigureTransparentMaterial(material);

			if (material.HasProperty(ColorProperty))
				material.SetColor(ColorProperty, GetTimeAdjustedSurfaceTint());

			ApplySharedWaterProperties(material);
			ApplyDynamicUndersideSettings(material);
		}

		private static void ApplyDynamicTopSettings(Material material)
		{
			if (material == null || DeepWaters.Instance == null)
				return;

			if (material.HasProperty(ColorProperty))
			{
				Color color = GetTimeAdjustedSurfaceTint();
				color.a = DeepWaters.Instance.WaterSurfaceTopAlpha;
				material.SetColor(ColorProperty, color);
			}
		}

		private static void ApplyDynamicUndersideSettings(Material material)
		{
			if (material == null || DeepWaters.Instance == null)
				return;

			if (material.HasProperty(ColorProperty))
				material.SetColor(ColorProperty, GetTimeAdjustedSurfaceTint());

			float shallow = GetPlayerShallowWaterFactor();
			float bottomAlpha = DeepWaters.Instance.WaterSurfaceBottomAlpha;
			if (material.HasProperty(UndersideAlphaProperty))
				material.SetFloat(UndersideAlphaProperty, Mathf.Lerp(bottomAlpha, bottomAlpha * 0.35f, shallow));

			// Near shore, a close opaque curtain reads as a hard dark strip at
			// the waterline. Keep the void guard in deep water; push it out in
			// shallow columns where terrain already closes the horizon.
			float curtainVision = DeepWaters.Instance.UnderwaterVisionDistance;
			if (material.HasProperty(SurfaceOpaqueFadeStartProperty))
				material.SetFloat(SurfaceOpaqueFadeStartProperty, curtainVision * Mathf.Lerp(1.7f, 4.0f, shallow));
			if (material.HasProperty(SurfaceOpaqueFadeEndProperty))
				material.SetFloat(SurfaceOpaqueFadeEndProperty, curtainVision * Mathf.Lerp(3.8f, 8.0f, shallow));
		}

		private static float GetPlayerShallowWaterFactor()
		{
			Vector3 position;
			DeepWaterColumn column;
			if (!DeepWaterWorld.TryGetPlayerPosition(out position) ||
				!DeepWaterWorld.TryGetWaterColumn(position.x, position.z, out column))
			{
				return 0f;
			}

			return 1f - Mathf.InverseLerp(3f, 12f, column.Depth);
		}

		private static Color GetTimeAdjustedSurfaceTint()
		{
			return Color.Lerp(NightSurfaceTint, SurfaceTint, GetDaylightFactor());
		}

		private static float GetDaylightFactor()
		{
			DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
			if (dfUnity != null && dfUnity.WorldTime != null && dfUnity.WorldTime.Now.IsNight)
				return 0f;

			GameManager gameManager = GameManager.Instance;
			SunlightManager sunlightManager = gameManager != null ? gameManager.SunlightManager : null;
			return sunlightManager != null ? Mathf.Clamp01(sunlightManager.DaylightScale) : 1f;
		}

		private static void ApplySharedWaterProperties(Material material)
		{
			if (material.HasProperty(UnderwaterFogColorProperty))
				material.SetColor(UnderwaterFogColorProperty, DeepWaters.GetUnderwaterFogColor());

			if (material.HasProperty(WaterColumnDepthProperty))
				material.SetFloat(WaterColumnDepthProperty, Mathf.Max(1f, DeepWaters.Instance.WaterDepth));

			if (material.HasProperty(WaterColumnFogDepthProperty))
				material.SetFloat(WaterColumnFogDepthProperty, GetWaterColumnFogDepth());

			if (material.HasProperty(WaterColumnFogStrengthProperty))
				material.SetFloat(WaterColumnFogStrengthProperty, GetWaterColumnFogStrength());

			// Above-water seabed fade. Anchored to the underwater vision distance
			// so looking down from the surface is no clearer than looking around
			// underwater, and shortened by the distance falloff slider.
			if (material.HasProperty(WaterSurfaceVisionDistanceProperty))
				material.SetFloat(WaterSurfaceVisionDistanceProperty, GetTopSurfaceVisionDistance());

			if (material.HasProperty(WaterSurfaceFalloffProperty))
				material.SetFloat(WaterSurfaceFalloffProperty, Mathf.Clamp01(DeepWaters.Instance.WaterSurfaceDistanceFalloff));

			// Opaque horizon curtain: the surface is fully opaque past this range,
			// hiding the loaded-world edge behind an opaque sea.
			float curtainVision = GetTopSurfaceVisionDistance();
			if (material.HasProperty(SurfaceOpaqueFadeStartProperty))
				material.SetFloat(SurfaceOpaqueFadeStartProperty, curtainVision * 0.45f);
			if (material.HasProperty(SurfaceOpaqueFadeEndProperty))
				material.SetFloat(SurfaceOpaqueFadeEndProperty, curtainVision * 2.15f);

			if (material.HasProperty(HorizonColorProperty))
				material.SetColor(HorizonColorProperty, DeepWaters.GetUnderwaterFogColor());
		}

		private static void ConfigureTransparentMaterial(Material material)
		{
			material.SetOverrideTag("RenderType", TransparentRenderType);
			material.renderQueue = (int)RenderQueue.Transparent;

			if (material.HasProperty(SrcBlendProperty))
				material.SetInt(SrcBlendProperty, (int)BlendMode.SrcAlpha);

			if (material.HasProperty(DstBlendProperty))
				material.SetInt(DstBlendProperty, (int)BlendMode.OneMinusSrcAlpha);

			if (material.HasProperty(ZWriteProperty))
				material.SetInt(ZWriteProperty, 0);

			material.DisableKeyword(KeyWords.CutOut);
			material.EnableKeyword(KeyWords.Fade);
			material.DisableKeyword(KeyWords.Transparent);
		}

		private static void ApplyBaseTexture(Material material)
		{
			Texture surfaceTexture = GetSurfaceTexture();
			if (surfaceTexture == null)
			{
				material.color = FallbackSurfaceColor;
				return;
			}

			material.mainTexture = surfaceTexture;
			material.mainTextureScale = new Vector2(SurfaceTextureTiling, SurfaceTextureTiling);
		}

		private static Texture2D LoadWaterTexture()
		{
			if (DaggerfallUnity.Instance == null ||
				DaggerfallUnity.Instance.MaterialReader == null ||
				DaggerfallUnity.Instance.MaterialReader.TextureReader == null)
			{
				return null;
			}

			return DaggerfallUnity.Instance.MaterialReader.TextureReader.GetTexture2D(302, 0, 0);
		}

		private static void ApplyWaterTextureSettings(Texture texture)
		{
			if (texture == null)
				return;

			texture.wrapMode = TextureWrapMode.Repeat;
			texture.filterMode = FilterMode.Point;
		}

		private static float GetWaterColumnFogDepth()
		{
			return Mathf.Max(
				2f,
				DeepWaters.Instance.WaterDepth * DeepWaters.Instance.UnderwaterFogDistanceMultiplier);
		}

		private static float GetWaterColumnFogStrength()
		{
			return Mathf.Clamp01(DeepWaters.Instance.UnderwaterFogStrength);
		}

		private static float GetTopSurfaceVisionDistance()
		{
			return Mathf.Min(DeepWaters.Instance.UnderwaterVisionDistance, MaximumTopSurfaceVisionDistance);
		}
	}

    /// <summary>
    /// Per-terrain visible water surface. The mesh is clipped to the same
    /// local-water classification used for seabed holes and swimming.
    ///
    /// Water uses generated per-tile meshes and shared custom materials for
    /// every terrain tile. The top and underside are separate renderers so
    /// above-water transparency cannot be overridden by underwater behavior.
    /// </summary>
    internal static class WaterSurfaceManager
    {
        private const string VisualChildName = "DeepWaters_Surface";
        private const string TopSurfaceChildName = "DeepWaters_Surface_Top";
        private const string UndersideSurfaceChildName = "DeepWaters_Surface_Underside";
        private const string GeneratedMeshName = "DeepWaters.SurfaceMesh";
        private const int SurfaceGridResolution = 128;
        private const int ShorelineSeedScanCells = 32;
        private const int ShorelineSurfaceFeatherCells = 4;
        private const float SurfaceRenderYOffset = 0.03f;

        private static bool installed;

        internal static void Install()
        {
            if (installed)
                return;

            DaggerfallTerrain.OnPromoteTerrainData += HandlePromote;
            installed = true;
		}

        internal static void RefreshLoadedSurfaces()
        {
            DaggerfallTerrain[] terrains = Object.FindObjectsOfType<DaggerfallTerrain>();
            for (int i = 0; i < terrains.Length; i++)
                RefreshLoadedSurface(terrains[i]);
        }

        internal static void RefreshLoadedSurface(DaggerfallTerrain dfTerrain)
        {
            if (dfTerrain == null)
                return;

            Terrain terrain = dfTerrain.GetComponent<Terrain>();
            if (terrain != null && terrain.terrainData != null)
                HandlePromoteCore(dfTerrain, terrain.terrainData, false);
        }

        private static void HandlePromote(DaggerfallTerrain sender, TerrainData terrainData)
        {
            HandlePromoteCore(sender, terrainData, true);
        }

        // The genuine promote event is the safe pre-first-render window to build
        // the surface even while terrain is streaming, so it must NOT be gated on
        // CanMutateTerrainData — otherwise tiles streamed in as the player swims
        // get a carved seabed but no water surface above (gaps in the ceiling).
        // Only the forced refresh of already-live terrains is gated. The surface
        // build never mutates terrainData; it only adds a child mesh renderer.
        private static void HandlePromoteCore(DaggerfallTerrain sender, TerrainData terrainData, bool fromPromoteEvent)
        {
			if (sender == null || terrainData == null)
				return;

			if (!fromPromoteEvent && !DeepWaterRuntime.CanMutateTerrainData)
				return;

			if (DeepWaters.Instance == null ||
				!DeepWaters.Instance.SpawnWaterSurfaces ||
				!ShouldHaveSurface(sender))
			{
				RemoveExisting(sender);
				return;
			}

			Mesh surfaceMesh = BuildSurfaceMesh(sender, terrainData);
			if (surfaceMesh == null)
			{
				RemoveExisting(sender);
				return;
			}

			EnsureVisibleSurface(sender, terrainData, surfaceMesh);
		}

        private static void EnsureVisibleSurface(DaggerfallTerrain terrain, TerrainData terrainData, Mesh surfaceMesh)
        {
            var sampler = DaggerfallUnity.Instance.TerrainSampler;
            float oceanY = sampler.OceanElevation / sampler.MaxTerrainHeight * terrainData.size.y;

            Transform existing = terrain.transform.Find(VisualChildName);
            GameObject visualGO;
            if (existing == null)
            {
                visualGO = new GameObject(VisualChildName);
                visualGO.transform.SetParent(terrain.transform, false);
            }
            else
            {
                visualGO = existing.gameObject;
            }

			if (visualGO.GetComponent<DeepWatersWaterSurface>() == null)
				visualGO.AddComponent<DeepWatersWaterSurface>();

            MeshFilter topFilter = EnsureSurfaceRenderer(
                visualGO.transform,
                TopSurfaceChildName,
                WaterSurfaceResources.GetTopMaterial(),
                true);
            MeshFilter undersideFilter = EnsureSurfaceRenderer(
                visualGO.transform,
                UndersideSurfaceChildName,
                WaterSurfaceResources.GetUndersideMaterial(),
                true);
            ReplaceSurfaceMesh(topFilter, undersideFilter, surfaceMesh);

            WaterSurfaceResources.ApplyMaterialSettings();
            visualGO.transform.localPosition = new Vector3(0f, oceanY + SurfaceRenderYOffset, 0f);
            visualGO.transform.localScale    = Vector3.one;
            visualGO.transform.localRotation = Quaternion.identity;
        }

        private static MeshFilter EnsureSurfaceRenderer(
            Transform root,
            string childName,
            Material material,
            bool visible)
        {
            Transform existing = root.Find(childName);
            GameObject surfaceGO;
            if (existing == null)
            {
                surfaceGO = new GameObject(childName);
                surfaceGO.transform.SetParent(root, false);
            }
            else
            {
                surfaceGO = existing.gameObject;
            }

            surfaceGO.transform.localPosition = Vector3.zero;
            surfaceGO.transform.localRotation = Quaternion.identity;
            surfaceGO.transform.localScale = Vector3.one;

            var meshFilter = surfaceGO.GetComponent<MeshFilter>();
            if (meshFilter == null)
                meshFilter = surfaceGO.AddComponent<MeshFilter>();

            var meshRenderer = surfaceGO.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                meshRenderer = surfaceGO.AddComponent<MeshRenderer>();

            if (meshRenderer.sharedMaterial != material)
                meshRenderer.sharedMaterial = material;

            meshRenderer.enabled = material != null && visible;
            DeepWaterRendering.DisableShadows(meshRenderer);
            return meshFilter;
        }

        private static Mesh BuildSurfaceMesh(DaggerfallTerrain terrain, TerrainData terrainData)
        {
            int n = SurfaceGridResolution;
            float sizeX = terrainData.size.x;
            float sizeZ = terrainData.size.z;
            bool hasOwnWater = DeepWaterWaterClassification.MapDataHasWater(terrain.MapData);
            bool hasBakedWater =
                DeepWaterDistanceBake.IsLoaded &&
                DeepWaterDistanceBake.MapPixelHasWaterCells(terrain.MapPixelX, terrain.MapPixelY);

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            if (IsFullWaterTile(terrain, hasOwnWater))
            {
                AppendSurfaceQuad(
                    0f, 1f,
                    0f, 1f,
                    sizeX,
                    sizeZ,
                    vertices,
                    uvs,
                    triangles);
                return CreateSurfaceMesh(vertices, uvs, triangles);
            }

            bool[,] cells = new bool[n, n];
            bool[,] used = new bool[n, n];
            DeepWaterTileData tile = terrain.GetComponent<DeepWaterTileData>();

            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    cells[z, x] = (hasOwnWater || hasBakedWater) &&
                        IsSurfaceCellWater(terrain, terrainData, tile, x, z, n);
                }
            }

            AddNeighborWaterConnectedShoreline(terrain, cells, n);
            AddLocalShorelineFeather(terrain, cells, n);

            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    if (!cells[z, x] || used[z, x])
                        continue;

                    int width = 1;
                    while (x + width < n && cells[z, x + width] && !used[z, x + width])
                        width++;

                    int height = 1;
                    bool canGrow = true;
                    while (z + height < n && canGrow)
                    {
                        for (int xx = x; xx < x + width; xx++)
                        {
                            if (!cells[z + height, xx] || used[z + height, xx])
                            {
                                canGrow = false;
                                break;
                            }
                        }

                        if (canGrow)
                            height++;
                    }

                    for (int zz = z; zz < z + height; zz++)
                        for (int xx = x; xx < x + width; xx++)
                            used[zz, xx] = true;

                    AppendSurfaceQuad(
                        x / (float)n,
                        (x + width) / (float)n,
                        z / (float)n,
                        (z + height) / (float)n,
                        sizeX,
                        sizeZ,
                        vertices,
                        uvs,
                        triangles);
                }
            }

            if (vertices.Count == 0)
                return null;

            return CreateSurfaceMesh(vertices, uvs, triangles);
        }

        private static bool IsFullWaterTile(DaggerfallTerrain terrain, bool hasOwnWater)
        {
            if (!hasOwnWater || terrain == null)
                return false;

            if (!DeepWaterDistanceBake.IsLoaded)
                return true;

            if (!DeepWaterDistanceBake.MapPixelHasWaterCells(terrain.MapPixelX, terrain.MapPixelY) ||
                DeepWaterDistanceBake.MapPixelHasLandCells(terrain.MapPixelX, terrain.MapPixelY))
                return false;

            return DeepWaterWaterClassification.MapDataFullySubmerged(terrain.MapData);
        }

        private static bool ShouldHaveSurface(DaggerfallTerrain terrain)
        {
            if (terrain == null)
                return false;

            if (DeepWaterWaterClassification.MapDataHasWater(terrain.MapData))
                return true;

            if (!DeepWaterDistanceBake.IsLoaded)
                return false;

            return DeepWaterDistanceBake.MapPixelOrCardinalNeighborHasWaterCells(terrain.MapPixelX, terrain.MapPixelY);
        }

        private static bool IsSurfaceCellWater(
            DaggerfallTerrain terrain,
            TerrainData terrainData,
            DeepWaterTileData tile,
            int cellX,
            int cellZ,
            int resolution)
        {
            if (terrain == null ||
                terrainData == null)
            {
                return false;
            }

            if (CellContainsPromotedClippedWaterTile(terrain, cellX, cellZ, resolution) ||
                DeepWaterWaterClassification.CellContainsWaterTile(terrain.MapData, cellX, cellZ, resolution))
                return true;

            if (!DeepWaterWaterClassification.IsCellVisuallyWet(terrain.MapData, cellX, cellZ, resolution))
                return false;

			if (IsBakedShoreSurfaceCell(terrain, cellX, cellZ, resolution))
				return true;

            float x0 = cellX / (float)resolution;
            float x1 = (cellX + 1) / (float)resolution;
            float z0 = cellZ / (float)resolution;
            float z1 = (cellZ + 1) / (float)resolution;

            return IsSurfaceSampleWater(terrain, terrainData, tile, Mathf.Lerp(x0, x1, 0.25f), Mathf.Lerp(z0, z1, 0.25f)) ||
                   IsSurfaceSampleWater(terrain, terrainData, tile, Mathf.Lerp(x0, x1, 0.75f), Mathf.Lerp(z0, z1, 0.25f)) ||
                   IsSurfaceSampleWater(terrain, terrainData, tile, Mathf.Lerp(x0, x1, 0.25f), Mathf.Lerp(z0, z1, 0.75f)) ||
                   IsSurfaceSampleWater(terrain, terrainData, tile, Mathf.Lerp(x0, x1, 0.75f), Mathf.Lerp(z0, z1, 0.75f));
        }

        private static bool IsBakedSurfaceWater(DaggerfallTerrain terrain, float fracX, float fracZ)
        {
            return terrain != null &&
                DeepWaterDistanceBake.IsLoaded &&
                DeepWaterDistanceBake.IsWaterAt(terrain.MapPixelX, terrain.MapPixelY, fracX, fracZ);
        }

        private static bool CellContainsPromotedClippedWaterTile(
            DaggerfallTerrain terrain,
            int cellX,
            int cellZ,
            int resolution)
        {
            if (terrain == null || terrain.TileMap == null || resolution <= 0)
                return false;

            Color32[] tileMap = terrain.TileMap;
            int dim = Mathf.RoundToInt(Mathf.Sqrt(tileMap.Length));
            if (dim <= 0 || dim * dim != tileMap.Length)
                return false;

            int x0 = Mathf.Clamp(cellX * dim / resolution, 0, dim - 1);
            int x1 = Mathf.Clamp(((cellX + 1) * dim - 1) / resolution, 0, dim - 1);
            int z0 = Mathf.Clamp(cellZ * dim / resolution, 0, dim - 1);
            int z1 = Mathf.Clamp(((cellZ + 1) * dim - 1) / resolution, 0, dim - 1);
            bool textureArray = TerrainUsesTextureArrayShader(terrain);

            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (DeepWaterTerrainCapRenderer.ShouldClipPromotedWaterTexel(
                        terrain,
                        tileMap[z * dim + x].a,
                        textureArray,
                        x,
                        z,
                        dim))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TerrainUsesTextureArrayShader(DaggerfallTerrain terrain)
        {
            Terrain unityTerrain = terrain != null ? terrain.GetComponent<Terrain>() : null;
            Material material = unityTerrain != null ? unityTerrain.materialTemplate : null;
            Shader shader = material != null ? material.shader : null;
            string name = shader != null ? shader.name : null;
            return name == "Daggerfall/TilemapTextureArray" ||
                   name == "DeepWaters/TilemapTextureArrayClipWater";
        }

        private static bool IsSurfaceSampleWater(
            DaggerfallTerrain terrain,
            TerrainData terrainData,
            DeepWaterTileData tile,
            float fracX,
            float fracZ)
        {
            if (DeepWaterWaterClassification.IsLocalPointWater(terrain.MapData, fracX, fracZ))
                return true;

            if (tile == null || !tile.IsOceanConnected || !tile.HasDistanceField)
                return false;

            float worldX = terrain.transform.position.x + fracX * terrainData.size.x;
            float worldZ = terrain.transform.position.z + fracZ * terrainData.size.z;
            return
                DeepWaterWaterClassification.IsLocalPointPureWaterTile(terrain.MapData, fracX, fracZ) &&
                tile.IsBakedWater(worldX, worldZ);
        }

        private static void AddNeighborWaterConnectedShoreline(DaggerfallTerrain terrain, bool[,] cells, int n)
        {
            if (terrain == null || !DeepWaterDistanceBake.IsLoaded)
                return;

            DeepWaterTileData tile = terrain.GetComponent<DeepWaterTileData>();
            if (tile == null || !tile.IsOceanConnected)
                return;

            int px = terrain.MapPixelX;
            int py = terrain.MapPixelY;
            bool westWater = NeighborHasWater(px - 1, py);
            bool eastWater = NeighborHasWater(px + 1, py);
            bool northWater = NeighborHasWater(px, py - 1);
            bool southWater = NeighborHasWater(px, py + 1);
            if (!westWater && !eastWater && !northWater && !southWater)
                return;

            bool[,] visited = new bool[n, n];
            var queue = new Queue<int>();

            if (westWater)
                for (int z = 0; z < n; z++)
                    EnqueueFirstSubmergedShoreCell(terrain, visited, queue, 0, 1, z, 0, n);

            if (eastWater)
                for (int z = 0; z < n; z++)
                    EnqueueFirstSubmergedShoreCell(terrain, visited, queue, n - 1, -1, z, 0, n);

            if (northWater)
                for (int x = 0; x < n; x++)
                    EnqueueFirstSubmergedShoreCell(terrain, visited, queue, x, 0, 0, 1, n);

            if (southWater)
                for (int x = 0; x < n; x++)
                    EnqueueFirstSubmergedShoreCell(terrain, visited, queue, x, 0, n - 1, -1, n);

            while (queue.Count > 0)
            {
                int encoded = queue.Dequeue();
                int x = encoded & 0xffff;
                int z = encoded >> 16;
                cells[z, x] = true;

                EnqueueShoreCell(terrain, visited, queue, x - 1, z, n);
                EnqueueShoreCell(terrain, visited, queue, x + 1, z, n);
                EnqueueShoreCell(terrain, visited, queue, x, z - 1, n);
                EnqueueShoreCell(terrain, visited, queue, x, z + 1, n);
            }
        }

        private static void AddLocalShorelineFeather(DaggerfallTerrain terrain, bool[,] cells, int n)
        {
            if (terrain == null || cells == null || n <= 0 || ShorelineSurfaceFeatherCells <= 0)
                return;

            bool[,] source = new bool[n, n];
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    source[z, x] = cells[z, x];

            for (int pass = 0; pass < ShorelineSurfaceFeatherCells; pass++)
            {
                bool changed = false;
                bool[,] next = new bool[n, n];
                for (int z = 0; z < n; z++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        next[z, x] = source[z, x];
                        if (source[z, x] ||
                            !HasAdjacentSurfaceCell(source, x, z, n) ||
                            (!DeepWaterWaterClassification.IsCellVisuallyWet(terrain.MapData, x, z, n) &&
                             !IsBakedShoreSurfaceCell(terrain, x, z, n)))
                        {
                            continue;
                        }

                        next[z, x] = true;
                        changed = true;
                    }
                }

                source = next;
                if (!changed)
                    break;
            }

            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    cells[z, x] = source[z, x];
        }

        private static bool HasAdjacentSurfaceCell(bool[,] cells, int x, int z, int n)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                        continue;

                    int xx = x + dx;
                    int zz = z + dz;
                    if (xx >= 0 && zz >= 0 && xx < n && zz < n && cells[zz, xx])
                        return true;
                }
            }

            return false;
        }

        private static bool IsBakedShoreSurfaceCell(DaggerfallTerrain terrain, int cellX, int cellZ, int resolution)
        {
            if (terrain == null || !DeepWaterDistanceBake.IsLoaded || resolution <= 0)
                return false;

            float x0 = cellX / (float)resolution;
            float x1 = (cellX + 1) / (float)resolution;
            float z0 = cellZ / (float)resolution;
            float z1 = (cellZ + 1) / (float)resolution;
            return IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.5f), Mathf.Lerp(z0, z1, 0.5f)) ||
                   IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.25f), Mathf.Lerp(z0, z1, 0.25f)) ||
                   IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.75f), Mathf.Lerp(z0, z1, 0.25f)) ||
                   IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.25f), Mathf.Lerp(z0, z1, 0.75f)) ||
                   IsBakedSurfaceWater(terrain, Mathf.Lerp(x0, x1, 0.75f), Mathf.Lerp(z0, z1, 0.75f));
        }

        private static bool NeighborHasWater(int mapPixelX, int mapPixelY)
        {
            return DeepWaterDistanceBake.MapPixelHasWaterCells(mapPixelX, mapPixelY) ||
                   DeepWaterDistanceBake.MapPixelHasFineWaterCells(mapPixelX, mapPixelY);
        }

        private static void EnqueueFirstSubmergedShoreCell(
            DaggerfallTerrain terrain,
            bool[,] visited,
            Queue<int> queue,
            int startX,
            int stepX,
            int startZ,
            int stepZ,
            int n)
        {
            int maxScan = Mathf.Min(ShorelineSeedScanCells, n);
            for (int i = 0; i < maxScan; i++)
            {
                int x = startX + stepX * i;
                int z = startZ + stepZ * i;
                if (x < 0 || z < 0 || x >= n || z >= n)
                    break;

                if (DeepWaterWaterClassification.IsCellVisuallyWet(terrain.MapData, x, z, n))
                {
                    EnqueueShoreCell(terrain, visited, queue, x, z, n);
                    break;
                }
            }
        }

        private static void EnqueueShoreCell(
            DaggerfallTerrain terrain,
            bool[,] visited,
            Queue<int> queue,
            int x,
            int z,
            int n)
        {
            if (x < 0 || z < 0 || x >= n || z >= n || visited[z, x])
                return;

            visited[z, x] = true;
            if (DeepWaterWaterClassification.IsCellVisuallyWet(terrain.MapData, x, z, n))
                queue.Enqueue((z << 16) | x);
        }

        private static Mesh CreateSurfaceMesh(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles)
        {
            var mesh = new Mesh { name = GeneratedMeshName };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AppendSurfaceQuad(
            float fracX0,
            float fracX1,
            float fracZ0,
            float fracZ1,
            float sizeX,
            float sizeZ,
            List<Vector3> vertices,
            List<Vector2> uvs,
            List<int> triangles)
        {
            float x0 = fracX0 * sizeX;
            float x1 = fracX1 * sizeX;
            float z0 = fracZ0 * sizeZ;
            float z1 = fracZ1 * sizeZ;

            int start = vertices.Count;
            vertices.Add(new Vector3(x0, 0f, z0));
            vertices.Add(new Vector3(x1, 0f, z0));
            vertices.Add(new Vector3(x1, 0f, z1));
            vertices.Add(new Vector3(x0, 0f, z1));

            uvs.Add(new Vector2(fracX0, fracZ0));
            uvs.Add(new Vector2(fracX1, fracZ0));
            uvs.Add(new Vector2(fracX1, fracZ1));
            uvs.Add(new Vector2(fracX0, fracZ1));

            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 1);
            triangles.Add(start);
            triangles.Add(start + 3);
            triangles.Add(start + 2);
        }

        private static void ReplaceSurfaceMesh(MeshFilter topFilter, MeshFilter undersideFilter, Mesh newMesh)
        {
            Mesh oldTopMesh = topFilter.sharedMesh;
            Mesh oldUndersideMesh = undersideFilter.sharedMesh;

            topFilter.sharedMesh = newMesh;
            undersideFilter.sharedMesh = newMesh;

            DestroyGeneratedMesh(oldTopMesh, newMesh);
            if (oldUndersideMesh != oldTopMesh)
                DestroyGeneratedMesh(oldUndersideMesh, newMesh);
        }

        private static void DestroyGeneratedMesh(Mesh mesh, Mesh replacement)
        {
            if (mesh != null && mesh != replacement && mesh.name == GeneratedMeshName)
                Object.Destroy(mesh);
        }

        private static void RemoveExisting(DaggerfallTerrain terrain)
        {
            var visual = terrain.transform.Find(VisualChildName);
            if (visual != null)
            {
                var destroyedMeshes = new HashSet<Mesh>();
                MeshFilter[] meshFilters = visual.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < meshFilters.Length; i++)
                {
                    Mesh mesh = meshFilters[i].sharedMesh;
                    if (mesh != null &&
                        mesh.name == GeneratedMeshName &&
                        destroyedMeshes.Add(mesh))
                    {
                        Object.Destroy(mesh);
                    }
                }

                Object.Destroy(visual.gameObject);
            }
        }

    }

    /// <summary>
    /// Marker component for generated water-surface renderers.
    /// </summary>
    internal class DeepWatersWaterSurface : MonoBehaviour
    {
    }

	/// <summary>
	/// DFU places player-owned ships as ordinary exterior locations, using the
	/// terrain height sampled under the location root. Deep water lowers that
	/// sample to the seafloor, so the owned ship scene needs one explicit
	/// waterline anchor.
	/// </summary>
	internal class PlayerShipWaterlineFix : MonoBehaviour
	{
		private const float CheckInterval = 0.5f;
		private const float PositionTolerance = 0.05f;

		private float nextCheckTime;

		void OnEnable()
		{
			StreamingWorld.OnCreateLocationGameObject += OnCreateLocationGameObject;
			StreamingWorld.OnUpdateLocationGameObject += OnUpdateLocationGameObject;
			StreamingWorld.OnAvailableLocationGameObject += OnAvailableLocationGameObject;
			StreamingWorld.OnFloatingOriginChange += OnFloatingOriginChange;
			DeepWaterRuntime.OnTransientReset += ScheduleImmediateCheck;
		}

		void OnDisable()
		{
			StreamingWorld.OnCreateLocationGameObject -= OnCreateLocationGameObject;
			StreamingWorld.OnUpdateLocationGameObject -= OnUpdateLocationGameObject;
			StreamingWorld.OnAvailableLocationGameObject -= OnAvailableLocationGameObject;
			StreamingWorld.OnFloatingOriginChange -= OnFloatingOriginChange;
			DeepWaterRuntime.OnTransientReset -= ScheduleImmediateCheck;
		}

		void LateUpdate()
		{
			if (Time.time < nextCheckTime)
				return;

			nextCheckTime = Time.time + CheckInterval;
			AnchorCurrentShipLocation();
		}

		private void OnCreateLocationGameObject(DaggerfallLocation dfLocation)
		{
			AnchorShipLocation(dfLocation);
		}

		private void OnUpdateLocationGameObject(GameObject locationObject, bool allowYield)
		{
			if (locationObject == null)
				return;

			AnchorShipLocation(locationObject.GetComponent<DaggerfallLocation>());
		}

		private void OnAvailableLocationGameObject()
		{
			AnchorCurrentShipLocation();
		}

		private void OnFloatingOriginChange()
		{
			ScheduleImmediateCheck();
		}

		private void ScheduleImmediateCheck()
		{
			nextCheckTime = 0f;
		}

		private static void AnchorCurrentShipLocation()
		{
			GameManager gameManager = GameManager.Instance;
			if (gameManager == null || !gameManager.IsPlayingGame() || gameManager.StreamingWorld == null)
				return;

			AnchorShipLocation(gameManager.StreamingWorld.CurrentPlayerLocationObject);
		}

		private static void AnchorShipLocation(DaggerfallLocation location)
		{
			GameManager gameManager = GameManager.Instance;
			if (gameManager == null || !gameManager.IsPlayingGame())
				return;

			if (!IsOwnedShipLocation(location))
				return;

			float oceanY;
			if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY))
				return;

			Vector3 position = location.transform.position;
			if (Mathf.Abs(position.y - oceanY) <= PositionTolerance)
				return;

			position.y = oceanY;
			location.transform.position = position;
		}

		private static bool IsOwnedShipLocation(DaggerfallLocation location)
		{
			if (location == null ||
				location.Summary.LocationType != DFRegion.LocationTypes.HomeYourShips ||
				!DaggerfallBankManager.OwnsShip)
			{
				return false;
			}

			DFPosition shipCoords = DaggerfallBankManager.GetShipCoords();
			return shipCoords != null &&
				location.Summary.MapPixelX == shipCoords.X &&
				location.Summary.MapPixelY == shipCoords.Y;
		}
	}
}
