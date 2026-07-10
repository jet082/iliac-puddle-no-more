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
		private static readonly int SurfaceOpaqueFadeStartProperty = Shader.PropertyToID("_SurfaceOpaqueFadeStart");
		private static readonly int SurfaceOpaqueFadeEndProperty = Shader.PropertyToID("_SurfaceOpaqueFadeEnd");
		private static readonly int PlayerPositionProperty = Shader.PropertyToID("_DeepWatersPlayerPosition");
		private static readonly int HorizonColorProperty = Shader.PropertyToID("_HorizonColor");
		private static readonly int SrcBlendProperty = Uniforms.SrcBlend;
		private static readonly int DstBlendProperty = Uniforms.DstBlend;
		private static readonly int ZWriteProperty = Uniforms.ZWrite;

		private static Material sharedTopMaterial;
		private static Material sharedUndersideMaterial;
		private static Texture sharedSurfaceTexture;
		private static readonly Color SurfaceTint = new Color(0.519f, 0.527f, 0.467f, 1f);
		private static readonly Color NightSurfaceTint = new Color(0.055f, 0.105f, 0.12f, 1f);
		// "Darker Surface Water" slider endpoints: 0 = barely-tinted (light), the
		// 0.5 midpoint = SurfaceTint (the default look), 1 = darker.
		private static readonly Color LightSurfaceTint = new Color(0.72f, 0.78f, 0.82f, 1f);
		private static readonly Color DarkSurfaceTint = new Color(0.21f, 0.21f, 0.19f, 1f);
		private static readonly Color FallbackSurfaceColor = new Color(0.075f, 0.24f, 0.38f, 1f);
		private const float TopSurfaceFogStrengthMultiplier = 1.0f;
		private const float TopSurfaceOpaqueEndVisionMultiplier = 0.55f;
		private const float MaximumTopSurfaceVisionDistance = 36f;
		private const float NearOpaqueTopSurfaceAlpha = 0.95f;
		private const float OpaqueTopSurfaceVisionDistance = 10000f;
		private const float NightTopSurfaceAlphaBoost = 0.18f;

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
			ApplySceneTintGlobal();
		}

		private static readonly int SceneTintProperty = Shader.PropertyToID("_DeepWatersSceneTint");

		// Tint the seafloor + decorations by the water surface color, but ONLY
		// when the camera is above the surface (looking down through the water).
		// rgb = the water tint, a = 1 enables it; underwater we send a = 0 so the
		// seafloor/decorations render untinted (the underwater view is unchanged).
		private static void ApplySceneTintGlobal()
		{
			Color tint = new Color(1f, 1f, 1f, 0f);
			GameManager gameManager = GameManager.Instance;
			Camera cam = gameManager != null ? gameManager.MainCamera : null;
			float oceanY;
			if (cam != null && DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY) &&
				cam.transform.position.y > oceanY)
			{
				tint = GetTimeAdjustedSurfaceTint();
				tint.a = 1f;
			}

			Shader.SetGlobalColor(SceneTintProperty, tint);
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
				color.a = GetTimeAdjustedTopSurfaceAlpha();
				material.SetColor(ColorProperty, color);
			}

			ApplySharedWaterProperties(material);

			if (material.HasProperty(WaterColumnFogStrengthProperty))
				material.SetFloat(WaterColumnFogStrengthProperty, Mathf.Clamp01(GetWaterColumnFogStrength() * TopSurfaceFogStrengthMultiplier));
			if (material.HasProperty(WaterSurfaceVisionDistanceProperty))
				material.SetFloat(WaterSurfaceVisionDistanceProperty, GetTopSurfaceVisionDistanceForMaterial());

			if (material.HasProperty(ZWriteProperty))
				material.SetInt(ZWriteProperty, IsTopSurfaceNearOpaque() ? 1 : 0);
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
				color.a = GetTimeAdjustedTopSurfaceAlpha();
				material.SetColor(ColorProperty, color);
			}

			if (material.HasProperty(ZWriteProperty))
				material.SetInt(ZWriteProperty, IsTopSurfaceNearOpaque() ? 1 : 0);

			if (material.HasProperty(WaterSurfaceVisionDistanceProperty))
				material.SetFloat(WaterSurfaceVisionDistanceProperty, GetTopSurfaceVisionDistanceForMaterial());
			if (material.HasProperty(SurfaceOpaqueFadeStartProperty))
				material.SetFloat(SurfaceOpaqueFadeStartProperty, 0f);
			if (material.HasProperty(SurfaceOpaqueFadeEndProperty))
				material.SetFloat(SurfaceOpaqueFadeEndProperty, GetTopSurfaceOpaqueFadeEnd());
			if (material.HasProperty(PlayerPositionProperty))
				material.SetVector(PlayerPositionProperty, GetPlayerPositionForShader());
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
				material.SetFloat(UndersideAlphaProperty, bottomAlpha);

			// Near shore, a close opaque curtain reads as a hard dark strip at
			// the waterline. Keep the void guard in deep water; push it out in
			// shallow columns where terrain already closes the horizon.
			float curtainVision = GetTopSurfaceVisionDistance();
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

		private static Color GetBaseSurfaceTint()
		{
			// "Darker Surface Water": 0 = no/light tint, 0.5 = default, 1 = darker.
			float d = DeepWaters.Instance != null ? Mathf.Clamp01(DeepWaters.Instance.DarkerSurfaceWater) : 0.5f;
			return d <= 0.5f
				? Color.Lerp(LightSurfaceTint, SurfaceTint, d / 0.5f)
				: Color.Lerp(SurfaceTint, DarkSurfaceTint, (d - 0.5f) / 0.5f);
		}

		private static Color GetTimeAdjustedSurfaceTint()
		{
			// Night tint is layered on top via the daylight lerp, so it is
			// unaffected by the Darker Surface Water slider (which only shapes
			// the daytime base tint).
			return Color.Lerp(NightSurfaceTint, GetBaseSurfaceTint(), GetDaylightFactor());
		}

		private static float GetTimeAdjustedTopSurfaceAlpha()
		{
			return Mathf.Clamp01(DeepWaters.Instance.WaterSurfaceTopAlpha + (1f - GetDaylightFactor()) * NightTopSurfaceAlphaBoost);
		}

		private static float GetDaylightFactor()
		{
			return DeepWaters.GetDaylightFactor();
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

			// Opaque horizon curtain: the surface is fully opaque past this range,
			// hiding the loaded-world edge behind an opaque sea.
			float curtainVision = GetTopSurfaceOpaqueFadeEnd();
			if (material.HasProperty(SurfaceOpaqueFadeStartProperty))
				material.SetFloat(SurfaceOpaqueFadeStartProperty, 0f);
			if (material.HasProperty(SurfaceOpaqueFadeEndProperty))
				material.SetFloat(SurfaceOpaqueFadeEndProperty, curtainVision);
			if (material.HasProperty(PlayerPositionProperty))
				material.SetVector(PlayerPositionProperty, GetPlayerPositionForShader());

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

		private static float GetTopSurfaceVisionDistanceForMaterial()
		{
			if (IsTopSurfaceNearOpaque())
				return OpaqueTopSurfaceVisionDistance;

			// Top-down visibility is the SAME as the underwater vision distance
			// (driven by Underwater Fog Distance), so you see the seafloor from
			// above out to roughly the same range you see it from below.
			return DeepWaters.Instance.UnderwaterVisionDistance;
		}

		internal static float GetTopSurfaceOpaqueFadeEnd()
		{
			return Mathf.Max(1f, DeepWaterWorld.UnderwaterVisionDistance * TopSurfaceOpaqueEndVisionMultiplier);
		}

		private static Vector4 GetPlayerPositionForShader()
		{
			Vector3 position;
			if (!DeepWaterWorld.TryGetPlayerPosition(out position))
			{
				Camera camera = Camera.main;
				position = camera != null ? camera.transform.position : Vector3.zero;
			}

			return new Vector4(position.x, position.y, position.z, 1f);
		}

		private static bool IsTopSurfaceNearOpaque()
		{
			return DeepWaters.Instance.WaterSurfaceTopAlpha >= NearOpaqueTopSurfaceAlpha;
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
        // Thickness of the surface trigger box (below) that external mods raycast
        // against to detect deep-waters ocean, e.g. Warm Ashes ship placement.
        private const float SurfaceTriggerThickness = 0.5f;

        private static bool installed;

        internal static void Install()
        {
            if (installed)
                return;

            DaggerfallTerrain.OnPromoteTerrainData += HandlePromote;
			AnimatedWaterSurfaceBridge.Install();
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
            // Far tiles build with the floor builder's deferred pump instead
            // of on the promote frame (BuildSurfaceFor below).
            if (sender != null && !DeepWaterFloorBuilder.IsNearPlayerPixel(sender))
                return;

            HandlePromoteCore(sender, terrainData, true);
        }

        // Deferred-pump entry: same trust level as a genuine promote (the
        // surface build never mutates terrain data, it only adds child
        // renderers), so it must not be gated on CanMutateTerrainData.
        internal static void BuildSurfaceFor(DaggerfallTerrain sender, TerrainData terrainData)
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

			// Rebuild guard (same pattern as the floor builder): DFU allocates a
			// fresh heightmapSamples array per genuine promote, so reference
			// equality on it plus the map pixel identifies an identical build.
			Transform existingVisual = sender.transform.Find(VisualChildName);
			if (existingVisual != null)
			{
				DeepWatersWaterSurface existingMarker = existingVisual.GetComponent<DeepWatersWaterSurface>();
				if (existingMarker != null &&
					existingMarker.BuiltMapPixelX == sender.MapPixelX &&
					existingMarker.BuiltMapPixelY == sender.MapPixelY &&
					object.ReferenceEquals(existingMarker.BuiltHeightmapSamples, sender.MapData.heightmapSamples))
				{
					return;
				}
			}

			long timing = DeepWaterPromoteTiming.Begin();
			Mesh surfaceMesh = BuildSurfaceMesh(sender, terrainData);
			if (surfaceMesh == null)
			{
				DeepWaterPromoteTiming.End(timing, "surface", sender.MapPixelX, sender.MapPixelY);
				RemoveExisting(sender);
				return;
			}

			EnsureVisibleSurface(sender, terrainData, surfaceMesh);
			DeepWaterPromoteTiming.End(timing, "surface", sender.MapPixelX, sender.MapPixelY);
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

			DeepWatersWaterSurface marker = visualGO.GetComponent<DeepWatersWaterSurface>();
			if (marker == null)
				marker = visualGO.AddComponent<DeepWatersWaterSurface>();
			marker.BuiltMapPixelX = terrain.MapPixelX;
			marker.BuiltMapPixelY = terrain.MapPixelY;
			marker.BuiltHeightmapSamples = terrain.MapData.heightmapSamples;
			marker.Terrain = terrain;
			marker.SurfaceMesh = surfaceMesh;

            MeshFilter topFilter = EnsureSurfaceRenderer(
                visualGO.transform,
                TopSurfaceChildName,
                WaterSurfaceResources.GetTopMaterial());
            MeshFilter undersideFilter = EnsureSurfaceRenderer(
                visualGO.transform,
                UndersideSurfaceChildName,
                WaterSurfaceResources.GetUndersideMaterial());
            ReplaceSurfaceMesh(topFilter, undersideFilter, surfaceMesh);

            WaterSurfaceResources.ApplyMaterialSettings();
            visualGO.transform.localPosition = new Vector3(0f, oceanY + SurfaceRenderYOffset, 0f);
            visualGO.transform.localScale    = Vector3.one;
            visualGO.transform.localRotation = Quaternion.identity;

            // Tile-spanning trigger box so external mods can locate the deep-waters
            // ocean surface by a downward raycast (they look for a collider named
            // "DeepWaters_Surface"; Warm Ashes uses this to place ships). Trigger
            // only: it never blocks the swimmer, and every deep-waters raycast uses
            // QueryTriggerInteraction.Ignore, so our own swim/shore logic never sees it.
            BoxCollider surfaceTrigger = visualGO.GetComponent<BoxCollider>();
            if (surfaceTrigger == null)
                surfaceTrigger = visualGO.AddComponent<BoxCollider>();
            surfaceTrigger.isTrigger = true;
            surfaceTrigger.center = new Vector3(terrainData.size.x * 0.5f, 0f, terrainData.size.z * 0.5f);
            surfaceTrigger.size = new Vector3(terrainData.size.x, SurfaceTriggerThickness, terrainData.size.z);
        }

        private static MeshFilter EnsureSurfaceRenderer(
            Transform root,
            string childName,
            Material material)
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

            meshRenderer.enabled = material != null;
            DeepWaterRendering.DisableShadows(meshRenderer);
            return meshFilter;
        }

        private static Mesh BuildSurfaceMesh(DaggerfallTerrain terrain, TerrainData terrainData)
        {
            int n = SurfaceGridResolution;
            float sizeX = terrainData.size.x;
            float sizeZ = terrainData.size.z;
			bool animatedWater = AnimatedWaterSurfaceBridge.IsAnimatedWaterMaterial(terrain.TerrainMaterial);
            bool hasOwnWater = DeepWaterWaterClassification.MapDataHasWater(terrain.MapData);
            bool hasBakedWater =
                DeepWaterDistanceBake.IsLoaded &&
                DeepWaterDistanceBake.MapPixelHasWaterCells(terrain.MapPixelX, terrain.MapPixelY);

            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

			bool fullWaterTile = IsFullWaterTile(terrain, hasOwnWater);
            if (fullWaterTile && !animatedWater)
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
            DeepWaterTileData tile = terrain.GetComponent<DeepWaterTileData>();

			if (fullWaterTile)
			{
				for (int z = 0; z < n; z++)
					for (int x = 0; x < n; x++)
						cells[z, x] = true;
			}
			else
			{
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
			}

			if (animatedWater)
				return CreateUniformSurfaceMesh(cells, n, sizeX, sizeZ);

			bool[,] used = new bool[n, n];

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
				   name == "Daggerfall/AnimatedWater/TilemapTextureArray" ||
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

        private static readonly Queue<int> featherQueue = new Queue<int>();
        private static int[] featherDepthScratch;

        // Constrained dilation of the surface-cell set into wet/baked-shore
        // cells, ShorelineSurfaceFeatherCells rings deep. Single BFS over the
        // frontier on pooled buffers (the old version rescanned the whole grid
        // and allocated two fresh bool[n,n] per feather step).
        private static void AddLocalShorelineFeather(DaggerfallTerrain terrain, bool[,] cells, int n)
        {
            if (terrain == null || cells == null || n <= 0 || ShorelineSurfaceFeatherCells <= 0)
                return;

            if (featherDepthScratch == null || featherDepthScratch.Length < n * n)
                featherDepthScratch = new int[n * n];
            System.Array.Clear(featherDepthScratch, 0, n * n);
            featherQueue.Clear();

            // Seed with the current surface set at depth 1 (0 = unvisited).
            for (int z = 0; z < n; z++)
                for (int x = 0; x < n; x++)
                    if (cells[z, x])
                    {
                        featherDepthScratch[z * n + x] = 1;
                        featherQueue.Enqueue((z << 16) | x);
                    }

            while (featherQueue.Count > 0)
            {
                int encoded = featherQueue.Dequeue();
                int x = encoded & 0xffff;
                int z = encoded >> 16;
                int depth = featherDepthScratch[z * n + x];
                if (depth > ShorelineSurfaceFeatherCells)
                    continue;

                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0)
                            continue;

                        int xx = x + dx;
                        int zz = z + dz;
                        if (xx < 0 || zz < 0 || xx >= n || zz >= n ||
                            featherDepthScratch[zz * n + xx] != 0)
                        {
                            continue;
                        }

                        featherDepthScratch[zz * n + xx] = depth + 1;
                        if (!DeepWaterWaterClassification.IsCellVisuallyWet(terrain.MapData, xx, zz, n) &&
                            !IsBakedShoreSurfaceCell(terrain, xx, zz, n))
                        {
                            continue;
                        }

                        cells[zz, xx] = true;
                        featherQueue.Enqueue((zz << 16) | xx);
                    }
                }
            }
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

		private static Mesh CreateUniformSurfaceMesh(bool[,] cells, int resolution, float sizeX, float sizeZ)
		{
			int stride = resolution + 1;
			var vertices = new List<Vector3>(stride * stride);
			var uvs = new List<Vector2>(stride * stride);
			var triangles = new List<int>(resolution * resolution * 6);

			for (int z = 0; z <= resolution; z++)
			{
				float fracZ = z / (float)resolution;
				for (int x = 0; x <= resolution; x++)
				{
					float fracX = x / (float)resolution;
					vertices.Add(new Vector3(fracX * sizeX, 0f, fracZ * sizeZ));
					uvs.Add(new Vector2(fracX, fracZ));
				}
			}

			for (int z = 0; z < resolution; z++)
			{
				for (int x = 0; x < resolution; x++)
				{
					if (!cells[z, x])
						continue;

					int start = z * stride + x;
					triangles.Add(start);
					triangles.Add(start + stride + 1);
					triangles.Add(start + 1);
					triangles.Add(start);
					triangles.Add(start + stride);
					triangles.Add(start + stride + 1);
				}
			}

			if (triangles.Count == 0)
				return null;

			Mesh mesh = CreateSurfaceMesh(vertices, uvs, triangles);
			mesh.RecalculateNormals();
			mesh.RecalculateTangents();
			Bounds bounds = mesh.bounds;
			bounds.Expand(new Vector3(0f, 4f, 0f));
			mesh.bounds = bounds;
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
        internal int BuiltMapPixelX = int.MinValue;
        internal int BuiltMapPixelY = int.MinValue;
        internal float[,] BuiltHeightmapSamples;
		internal DaggerfallTerrain Terrain;
		internal Mesh SurfaceMesh;

		private void OnEnable()
		{
			AnimatedWaterSurfaceBridge.Register(this);
		}

		private void OnDisable()
		{
			AnimatedWaterSurfaceBridge.Unregister(this);
		}
    }

	internal static class AnimatedWaterSurfaceBridge
	{
		private const string AnimatedWaterShaderPrefix = "Daggerfall/AnimatedWater/";
		private const int SourceLayer = 31;
		private static readonly int AnimatedWaterEnabledProperty = Shader.PropertyToID("_DeepWatersAnimatedWaterEnabled");
		private static readonly int AnimatedWaterTextureProperty = Shader.PropertyToID("_DeepWatersAnimatedWaterTexture");
		private static readonly int AnimatedWaterTextureTexelSizeProperty = Shader.PropertyToID("_DeepWatersAnimatedWaterTexture_TexelSize");
		private static readonly int TilemapTextureProperty = Shader.PropertyToID("_TilemapTex");
		private static readonly List<DeepWatersWaterSurface> surfaces = new List<DeepWatersWaterSurface>();
		private static RenderTexture sourceTexture;
		private static Camera sourceCamera;
		private static Texture2D allWaterTilemap;
		private static MaterialPropertyBlock sourceProperties;
		private static bool renderingSource;
		private static bool installed;

		internal static void Install()
		{
			if (installed)
				return;

			Camera.onPreRender += HandleCameraPreRender;
			installed = true;
		}

		internal static bool IsAnimatedWaterMaterial(Material material)
		{
			return material != null && material.shader != null &&
				material.shader.name.StartsWith(AnimatedWaterShaderPrefix);
		}

		internal static void Register(DeepWatersWaterSurface surface)
		{
			if (surface != null && !surfaces.Contains(surface))
				surfaces.Add(surface);
		}

		internal static void Unregister(DeepWatersWaterSurface surface)
		{
			surfaces.Remove(surface);
		}

		private static void HandleCameraPreRender(Camera camera)
		{
			if (renderingSource)
				return;

			GameManager gameManager = GameManager.Instance;
			Camera mainCamera = gameManager != null ? gameManager.MainCamera : null;
			if (camera == null || camera != mainCamera)
				return;

			Shader.SetGlobalFloat(AnimatedWaterEnabledProperty, 0f);
			EnsureSourceTexture(camera);
			EnsureSourceCamera(camera);
			if (sourceTexture == null || sourceCamera == null)
				return;

			float oceanY;
			bool belowSurface = DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY) &&
				camera.transform.position.y < oceanY;
			if (sourceProperties == null)
				sourceProperties = new MaterialPropertyBlock();
			sourceProperties.Clear();
			sourceProperties.SetTexture(TilemapTextureProperty, GetAllWaterTilemap());
			int drawCount = 0;
			for (int i = surfaces.Count - 1; i >= 0; i--)
			{
				DeepWatersWaterSurface surface = surfaces[i];
				if (surface == null)
				{
					surfaces.RemoveAt(i);
					continue;
				}

				Material material = surface.Terrain != null ? surface.Terrain.TerrainMaterial : null;
				if (surface.SurfaceMesh == null || !IsAnimatedWaterMaterial(material))
					continue;

				Graphics.DrawMesh(
					surface.SurfaceMesh,
					surface.transform.localToWorldMatrix,
					material,
					SourceLayer,
					sourceCamera,
					0,
					sourceProperties,
					ShadowCastingMode.Off,
					false,
					null,
					LightProbeUsage.Off,
					null);
				drawCount++;
			}

			if (drawCount == 0)
				return;

			bool previousInvertCulling = GL.invertCulling;
			try
			{
				renderingSource = true;
				GL.invertCulling = belowSurface;
				sourceCamera.Render();
			}
			finally
			{
				GL.invertCulling = previousInvertCulling;
				renderingSource = false;
			}

			Shader.SetGlobalTexture(AnimatedWaterTextureProperty, sourceTexture);
			Shader.SetGlobalVector(
				AnimatedWaterTextureTexelSizeProperty,
				new Vector4(
					1f / sourceTexture.width,
					1f / sourceTexture.height,
					sourceTexture.width,
					sourceTexture.height));
			Shader.SetGlobalFloat(AnimatedWaterEnabledProperty, 1f);
		}

		private static void EnsureSourceCamera(Camera camera)
		{
			if (sourceCamera == null)
			{
				GameObject sourceObject = new GameObject("Deep Waters Animated Water Camera")
				{
					hideFlags = HideFlags.HideAndDontSave,
				};
				sourceCamera = sourceObject.AddComponent<Camera>();
			}

			sourceCamera.CopyFrom(camera);
			sourceCamera.transform.SetPositionAndRotation(
				camera.transform.position,
				camera.transform.rotation);
			sourceCamera.targetTexture = sourceTexture;
			sourceCamera.cullingMask = 1 << SourceLayer;
			sourceCamera.clearFlags = CameraClearFlags.SolidColor;
			sourceCamera.backgroundColor = Color.clear;
			sourceCamera.depthTextureMode = DepthTextureMode.None;
			sourceCamera.renderingPath = RenderingPath.Forward;
			sourceCamera.allowHDR = false;
			sourceCamera.allowMSAA = false;
			sourceCamera.enabled = false;
		}

		private static Texture2D GetAllWaterTilemap()
		{
			if (allWaterTilemap != null)
				return allWaterTilemap;

			allWaterTilemap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
			{
				name = "Deep Waters All-Water Tilemap",
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Clamp,
				hideFlags = HideFlags.HideAndDontSave,
			};
			allWaterTilemap.SetPixel(0, 0, Color.clear);
			allWaterTilemap.Apply(false, true);
			return allWaterTilemap;
		}

		private static void EnsureSourceTexture(Camera camera)
		{
			RenderTexture target = camera.targetTexture;
			int width = Mathf.Max(1, target != null ? target.width : camera.pixelWidth);
			int height = Mathf.Max(1, target != null ? target.height : camera.pixelHeight);
			if (sourceTexture != null && sourceTexture.width == width && sourceTexture.height == height)
				return;

			if (sourceTexture != null)
			{
				sourceTexture.Release();
				Object.Destroy(sourceTexture);
			}

			sourceTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
			{
				name = "Deep Waters Animated Water Source",
				filterMode = FilterMode.Bilinear,
				wrapMode = TextureWrapMode.Clamp,
			};
			sourceTexture.Create();
		}
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
			DeepWaterRuntime.OnTransientReset += ScheduleImmediateCheck;
		}

		void OnDisable()
		{
			DeepWaterRuntime.OnTransientReset -= ScheduleImmediateCheck;
		}

		void LateUpdate()
		{
			if (Time.time < nextCheckTime)
				return;

			nextCheckTime = Time.time + CheckInterval;
			AnchorCurrentShipLocation();
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
