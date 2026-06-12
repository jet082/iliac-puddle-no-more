// Project:         Iliac Puddle No More
// License:         MIT

using System;
using System.Collections.Generic;
using DaggerfallWorkshop.Game;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepWaters
{
    /// <summary>
    /// Keeps underwater lighting consistent. DFU's exterior IndirectLight and
    /// PlayerTorch are player-following point lights with no shadows, so they
    /// can wash out underwater contrast in a perfect local radius; both are
    /// suppressed while the player is in a deep-water outdoor area.
    /// </summary>
    [DefaultExecutionOrder(31000)]
    public class UnderwaterWaveShadowFix : MonoBehaviour
    {
        private Light suppressedIndirectLight;
        private float restoreIndirectIntensity;
        private bool restoreIndirectEnabled;
        private bool suppressingIndirectLight;
        private GameObject suppressedPlayerTorch;
        private bool suppressingPlayerTorch;

        void OnDisable()
        {
            RestoreSuppressedLights();
        }

        void LateUpdate()
        {
            if (!ShouldFixUnderwaterLighting())
            {
                RestoreSuppressedLights();
                return;
            }

            SuppressPlayerIndirectLight();
            SuppressPlayerTorch();
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
        // This scan runs whenever the player is outside, so it must not be a
        // per-second whole-scene sweep (FindObjectsOfType plus a materials
        // array allocation per renderer was a GC metronome in complex towns).
        // Materials only need re-checking when new ones stream in, so a slow
        // cadence with sliced, non-allocating processing is plenty.
        private const float ScanInterval = 8.0f;
        private const int ScanSliceSize = 150;
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
        private MeshRenderer[] pendingRenderers;
        private int pendingIndex;
        private static readonly List<Material> materialScratch = new List<Material>(8);

        void LateUpdate()
        {
            if (pendingRenderers != null)
            {
                ContinuePatchScan();
                return;
            }

            if (Time.unscaledTime < nextScanTime || !ShouldPatchCutoutQueues())
                return;

            nextScanTime = Time.unscaledTime + ScanInterval;
            pendingRenderers = FindObjectsOfType<MeshRenderer>();
            pendingIndex = 0;
            ContinuePatchScan();
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

        private void ContinuePatchScan()
        {
            int end = Mathf.Min(pendingIndex + ScanSliceSize, pendingRenderers.Length);
            for (; pendingIndex < end; pendingIndex++)
            {
                MeshRenderer renderer = pendingRenderers[pendingIndex];
                if (renderer == null)
                    continue;

                renderer.GetSharedMaterials(materialScratch);
                for (int j = 0; j < materialScratch.Count; j++)
                {
                    Material material = materialScratch[j];
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

            if (pendingIndex >= pendingRenderers.Length)
                pendingRenderers = null;
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
