// Project:         Iliac Puddle No More
// License:         MIT

using System;
using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepWaters
{
    /// <summary>
    /// Keeps third-party wave presentation consistent around deep water. DFU's exterior
    /// IndirectLight and PlayerTorch are player-following point lights with no
    /// shadows, so they can wash out underwater contrast in a perfect local
    /// radius. Third-party wave renderers are surface effects; from above they
    /// can hide Deep Waters' transparent surface, and from below they can appear
    /// as opaque dither bands, so known wave meshes are hidden while the player
    /// is in a deep-water outdoor area.
    /// </summary>
    [DefaultExecutionOrder(31000)]
    public class UnderwaterWaveShadowFix : MonoBehaviour
    {
        private const string ComeSailAwayWaveObjectName = "WaveObject";
        private const string ComeSailAwayWaveShaderName = "Daggerfall/Dither/Wave";
        private const string ComeSailAwayBillboardWaterShaderName = "Daggerfall/BillboardWaterMasked";
        private const string AnimatedWaterShaderName = "Daggerfall/Animated";
        private const string AnimatedWaterMaterialName = "PWater";
        private const float ScanInterval = 1.0f;
        private const float MinimumWorldVerticalBounds = 256f;
        private const float SurfaceSuppressMinimumDepth = 0.5f;
        private const float SurfaceSuppressNearbyMinDistance = 4f;
        private const float SurfaceSuppressNearbyMaxDistance = 64f;
        private const int SurfaceSuppressNearbyDirections = 8;

        private MeshRenderer[] cachedRenderers = new MeshRenderer[0];
        private float nextScanTime;
        private Light suppressedIndirectLight;
        private float restoreIndirectIntensity;
        private bool restoreIndirectEnabled;
        private bool suppressingIndirectLight;
        private GameObject suppressedPlayerTorch;
        private bool suppressingPlayerTorch;
        private readonly Dictionary<MeshRenderer, WaveRendererShadowState> waveRendererStates =
            new Dictionary<MeshRenderer, WaveRendererShadowState>();
        private readonly HashSet<int> loggedSuppressedRendererIds = new HashSet<int>();

        private struct WaveRendererShadowState
        {
            public UnityEngine.Rendering.ShadowCastingMode ShadowCastingMode;
            public bool ReceiveShadows;
            public bool Enabled;

            public WaveRendererShadowState(UnityEngine.Rendering.ShadowCastingMode shadowCastingMode, bool receiveShadows, bool enabled)
            {
                ShadowCastingMode = shadowCastingMode;
                ReceiveShadows = receiveShadows;
                Enabled = enabled;
            }
        }

        void OnDisable()
        {
            RestoreSuppressedLights();
        }

        void LateUpdate()
        {
            bool shouldFixUnderwaterLighting = ShouldFixUnderwaterLighting();
            bool shouldSuppressExternalSurfaces = shouldFixUnderwaterLighting || ShouldSuppressExternalSurfaceRenderers();

            if (!shouldFixUnderwaterLighting)
            {
                RestoreSuppressedLightsOnly();
            }
            else
            {
                SuppressPlayerIndirectLight();
                SuppressPlayerTorch();
            }

            if (!shouldSuppressExternalSurfaces)
            {
                RestoreWaveRendererShadows();
                return;
            }

            if (Time.unscaledTime >= nextScanTime)
            {
                RefreshWaveRenderers();
                nextScanTime = Time.unscaledTime + ScanInterval;
            }

            PatchKnownWaveMeshes();
        }

        private void SuppressPlayerIndirectLight()
        {
            GameManager gameManager = GameManager.Instance;
            SunlightManager sunlightManager = gameManager != null ? gameManager.SunlightManager : null;
            Light indirectLight = sunlightManager != null ? sunlightManager.IndirectLight : null;
            if (indirectLight == null)
            {
                RestoreIndirectLight();
                return;
            }

            if (!suppressingIndirectLight || suppressedIndirectLight != indirectLight)
            {
                RestoreIndirectLight();
                suppressedIndirectLight = indirectLight;
                suppressingIndirectLight = true;
            }

            // SunlightManager refreshes this light in Update(). Capture that
            // current DFU-authored value, then zero it before rendering.
            restoreIndirectIntensity = indirectLight.intensity;
            restoreIndirectEnabled = indirectLight.enabled;
            indirectLight.intensity = 0f;
        }

        private void SuppressPlayerTorch()
        {
            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            EnablePlayerTorch torch = player != null ? player.GetComponent<EnablePlayerTorch>() : null;
            GameObject torchObject = torch != null ? torch.PlayerTorch : null;
            if (torchObject == null)
            {
                RestorePlayerTorch();
                return;
            }

            if (!suppressingPlayerTorch || suppressedPlayerTorch != torchObject)
            {
                RestorePlayerTorch();
                suppressedPlayerTorch = torchObject;
                suppressingPlayerTorch = true;
            }

            if (torchObject.activeSelf)
                torchObject.SetActive(false);
        }

        private void RestoreSuppressedLights()
        {
            RestoreSuppressedLightsOnly();
            RestoreWaveRendererShadows();
        }

        private void RestoreSuppressedLightsOnly()
        {
            RestoreIndirectLight();
            RestorePlayerTorch();
        }

        private void RestoreIndirectLight()
        {
            if (!suppressingIndirectLight)
                return;

            if (suppressedIndirectLight != null)
            {
                suppressedIndirectLight.enabled = restoreIndirectEnabled;
                suppressedIndirectLight.intensity = restoreIndirectIntensity;
            }

            suppressedIndirectLight = null;
            suppressingIndirectLight = false;
        }

        private void RestorePlayerTorch()
        {
            if (!suppressingPlayerTorch)
                return;

            // EnablePlayerTorch owns this object's normal active state and
            // recalculates it every Update. Do not restore an underwater-forged
            // value here.
            suppressedPlayerTorch = null;
            suppressingPlayerTorch = false;
        }

        private void RestoreWaveRendererShadows()
        {
            foreach (KeyValuePair<MeshRenderer, WaveRendererShadowState> pair in waveRendererStates)
            {
                MeshRenderer renderer = pair.Key;
                if (renderer == null)
                    continue;

                renderer.shadowCastingMode = pair.Value.ShadowCastingMode;
                renderer.receiveShadows = pair.Value.ReceiveShadows;
                renderer.enabled = pair.Value.Enabled;
            }

            waveRendererStates.Clear();
        }

        private void RefreshWaveRenderers()
        {
            var waveRenderers = new List<MeshRenderer>();
            GameObject waveObject = GameObject.Find(ComeSailAwayWaveObjectName);
            if (waveObject != null)
            {
                MeshRenderer[] renderers = waveObject.GetComponentsInChildren<MeshRenderer>();
                AddWaveRenderers(renderers, waveRenderers);
            }

            AddWaveRenderers(FindObjectsOfType<MeshRenderer>(), waveRenderers);

            cachedRenderers = waveRenderers.ToArray();
        }

        private static bool ShouldFixUnderwaterLighting()
        {
            GameManager gameManager = GameManager.Instance;
            if (DeepWaters.Instance == null ||
                gameManager == null ||
                !gameManager.IsPlayingGame() ||
                gameManager.PlayerEnterExit == null ||
                gameManager.PlayerEnterExit.IsPlayerInside)
            {
                return false;
            }

            float oceanSurfaceY;
            return UnderwaterDistanceFog.TryGetUnderwaterPresentation(gameManager, gameManager.MainCamera, out oceanSurfaceY);
        }

        private static bool ShouldSuppressExternalSurfaceRenderers()
        {
            GameManager gameManager = GameManager.Instance;
            if (DeepWaters.Instance == null ||
                !DeepWaters.Instance.SpawnWaterSurfaces ||
                gameManager == null ||
                !gameManager.IsPlayingGame() ||
                gameManager.PlayerEnterExit == null ||
                gameManager.PlayerEnterExit.IsPlayerInside)
            {
                return false;
            }

            Transform playerTransform = gameManager.PlayerObject != null
                ? gameManager.PlayerObject.transform
                : null;
            if (playerTransform != null && IsNearVisibleDeepWaterSurface(playerTransform.position))
                return true;

            Camera camera = gameManager.MainCamera;
            return camera != null && IsNearVisibleDeepWaterSurface(camera.transform.position);
        }

        private static bool IsNearVisibleDeepWaterSurface(Vector3 worldPosition)
        {
            DeepWaterColumn column;
            if (DeepWaterWorld.TryGetWaterColumn(worldPosition.x, worldPosition.z, out column) &&
                column.Depth > SurfaceSuppressMinimumDepth)
            {
                return true;
            }

            float nearbyDepth;
            return DeepWaterWorld.HasNearbyWaterColumn(
                worldPosition,
                SurfaceSuppressNearbyMinDistance,
                SurfaceSuppressNearbyMaxDistance,
                SurfaceSuppressNearbyDirections,
                SurfaceSuppressMinimumDepth,
                out nearbyDepth);
        }

        private void PatchKnownWaveMeshes()
        {
            for (int i = 0; i < cachedRenderers.Length; i++)
            {
                MeshRenderer renderer = cachedRenderers[i];
                if (renderer == null)
                    continue;

                if (!waveRendererStates.ContainsKey(renderer))
                {
                    waveRendererStates.Add(
                        renderer,
                        new WaveRendererShadowState(renderer.shadowCastingMode, renderer.receiveShadows, renderer.enabled));
                }

                DeepWaterRendering.DisableShadows(renderer);
                renderer.enabled = false;
                LogSuppressedSurfaceRenderer(renderer);

                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || filter.sharedMesh.vertexCount == 0)
                    continue;

                ExpandWaveMeshBounds(filter);
            }
        }

        private static void AddWaveRenderers(MeshRenderer[] renderers, List<MeshRenderer> waveRenderers)
        {
            if (renderers == null)
                return;

            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer != null &&
                    IsWaveRenderer(renderer) &&
                    !waveRenderers.Contains(renderer))
                {
                    waveRenderers.Add(renderer);
                }
            }
        }

        private static bool IsWaveRenderer(MeshRenderer renderer)
        {
            if (IsDeepWatersOwnRenderer(renderer))
                return false;

            if (renderer.gameObject != null && renderer.gameObject.name == ComeSailAwayWaveObjectName)
                return true;

            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null)
                    continue;

                if (IsKnownExternalWaterMaterial(material.name))
                    return true;

                if (material.shader != null && IsKnownExternalWaterShader(material.shader.name))
                    return true;
            }

            return false;
        }

        private static bool IsDeepWatersOwnRenderer(MeshRenderer renderer)
        {
            return renderer != null &&
                   (renderer.GetComponentInParent<DeepWatersWaterSurface>() != null ||
                    renderer.GetComponentInParent<DeepWaterFloorMesh>() != null);
        }

        private static bool IsKnownExternalWaterShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName))
                return false;

            return shaderName.Equals(ComeSailAwayWaveShaderName, StringComparison.OrdinalIgnoreCase) ||
                   shaderName.IndexOf(ComeSailAwayBillboardWaterShaderName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   shaderName.IndexOf(AnimatedWaterShaderName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsKnownExternalWaterMaterial(string materialName)
        {
            if (string.IsNullOrEmpty(materialName))
                return false;

            return materialName.Equals(AnimatedWaterMaterialName, StringComparison.OrdinalIgnoreCase) ||
                   materialName.IndexOf(AnimatedWaterMaterialName + " ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   materialName.IndexOf("CurrentMaterial", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ExpandWaveMeshBounds(MeshFilter filter)
        {
            Mesh mesh = filter.sharedMesh;
            Bounds bounds = mesh.bounds;

            float scaleY = Mathf.Abs(filter.transform.lossyScale.y);
            if (scaleY < 0.001f)
                scaleY = 1f;

            float waterDepth = DeepWaters.Instance != null ? DeepWaters.Instance.WaterDepth : 35f;
            float worldVerticalBounds = Mathf.Max(MinimumWorldVerticalBounds, waterDepth + 192f);
            float localBelowSurface = worldVerticalBounds / scaleY;
            float localAboveSurface = 32f / scaleY;

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            min.y = Mathf.Min(min.y, -localBelowSurface);
            max.y = Mathf.Max(max.y, localAboveSurface);
            bounds.SetMinMax(min, max);

            mesh.bounds = bounds;
        }

        private void LogSuppressedSurfaceRenderer(MeshRenderer renderer)
        {
            if (renderer == null)
                return;

            int id = renderer.GetInstanceID();
            if (!loggedSuppressedRendererIds.Add(id))
                return;

            string materialName = "none";
            string shaderName = "none";
            Material material = renderer.sharedMaterial;
            if (material != null)
            {
                materialName = material.name;
                shaderName = material.shader != null ? material.shader.name : "none";
            }

            Debug.Log(
                "[DeepWaters.SurfaceSuppressor] Hiding external underwater surface renderer path='" +
                GetTransformPath(renderer.transform) +
                "' material='" + materialName +
                "' shader='" + shaderName +
                "' bounds=" + renderer.bounds);
        }

        private static string GetTransformPath(Transform transform)
        {
            if (transform == null)
                return "none";

            string path = transform.name;
            Transform parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return path;
        }
    }

    /// <summary>
    /// DFU flats are alpha-tested cutouts, but some billboard batch shaders sit
    /// in the Transparent queue. Depth-based underwater post effects then see
    /// terrain depth below the horizon and no depth above it, producing a hard
    /// horizontal cut through the same object. Move known alpha-tested flat
    /// materials into the AlphaTest queue so they participate in depth like
    /// cutout geometry.
    /// </summary>
    [DefaultExecutionOrder(30950)]
    public class CutoutDepthQueueFix : MonoBehaviour
    {
        private const float ScanInterval = 1.0f;
        private const string TransparentCutoutRenderType = "TransparentCutout";

        private static readonly string[] CutoutShaderNames =
        {
            "Daggerfall/Billboard",
            "Daggerfall/BillboardBatch",
            "Daggerfall/BillboardBatchNoShadows",
            "Daggerfall/BillboardWaterMasked",
        };

        private readonly HashSet<int> patchedMaterialIds = new HashSet<int>();
        private float nextScanTime;

        void LateUpdate()
        {
            if (Time.unscaledTime < nextScanTime || !ShouldPatchCutoutQueues())
                return;

            nextScanTime = Time.unscaledTime + ScanInterval;
            PatchLoadedCutoutMaterials();
        }

        private static bool ShouldPatchCutoutQueues()
        {
            GameManager gameManager = GameManager.Instance;
            return DeepWaters.Instance != null &&
                   gameManager != null &&
                   gameManager.IsPlayingGame() &&
                   gameManager.PlayerEnterExit != null &&
                   !gameManager.PlayerEnterExit.IsPlayerInside;
        }

        private void PatchLoadedCutoutMaterials()
        {
            MeshRenderer[] renderers = FindObjectsOfType<MeshRenderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Material[] materials = renderer.sharedMaterials;
                for (int j = 0; j < materials.Length; j++)
                {
                    Material material = materials[j];
                    if (material == null || material.shader == null)
                        continue;

                    int materialId = material.GetInstanceID();
                    if (patchedMaterialIds.Contains(materialId))
                        continue;

                    if (!IsKnownAlphaCutoutShader(material.shader.name))
                        continue;

                    material.SetOverrideTag("RenderType", TransparentCutoutRenderType);
                    material.renderQueue = (int)RenderQueue.AlphaTest;
                    patchedMaterialIds.Add(materialId);
                }
            }
        }

        private static bool IsKnownAlphaCutoutShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName))
                return false;

            for (int i = 0; i < CutoutShaderNames.Length; i++)
            {
                if (shaderName.Equals(CutoutShaderNames[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}

