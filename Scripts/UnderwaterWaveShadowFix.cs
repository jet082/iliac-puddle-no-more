// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Keeps third-party wave presentation consistent underwater. DFU's exterior
    /// IndirectLight and PlayerTorch are player-following point lights with no
    /// shadows, so they can wash out underwater contrast in a perfect local
    /// radius. Third-party wave shadows are also unstable from below, so known
    /// wave renderers have shadow casting disabled only while submerged.
    /// </summary>
    [DefaultExecutionOrder(31000)]
    public class UnderwaterWaveShadowFix : MonoBehaviour
    {
        private const string ComeSailAwayWaveObjectName = "WaveObject";
        private const string ComeSailAwayWaveShaderName = "Daggerfall/Dither/Wave";
        private const float ScanInterval = 1.0f;
        private const float MinimumWorldVerticalBounds = 256f;

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

        private struct WaveRendererShadowState
        {
            public UnityEngine.Rendering.ShadowCastingMode ShadowCastingMode;
            public bool ReceiveShadows;

            public WaveRendererShadowState(UnityEngine.Rendering.ShadowCastingMode shadowCastingMode, bool receiveShadows)
            {
                ShadowCastingMode = shadowCastingMode;
                ReceiveShadows = receiveShadows;
            }
        }

        void OnDisable()
        {
            RestoreSuppressedLights();
        }

        void LateUpdate()
        {
            if (!ShouldFixWaveShadows())
            {
                RestoreSuppressedLights();
                return;
            }

            SuppressPlayerIndirectLight();
            SuppressPlayerTorch();
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
            RestoreIndirectLight();
            RestorePlayerTorch();
            RestoreWaveRendererShadows();
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

            if (waveRenderers.Count == 0)
                AddWaveRenderers(FindObjectsOfType<MeshRenderer>(), waveRenderers);

            cachedRenderers = waveRenderers.ToArray();
        }

        private static bool ShouldFixWaveShadows()
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
            if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY))
                return false;

            return OutdoorSwimDriver.IsPresentationUnderwater(oceanSurfaceY);
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
                        new WaveRendererShadowState(renderer.shadowCastingMode, renderer.receiveShadows));
                }

                DeepWaterRendering.DisableShadows(renderer);

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
                if (renderer != null && IsWaveRenderer(renderer))
                    waveRenderers.Add(renderer);
            }
        }

        private static bool IsWaveRenderer(MeshRenderer renderer)
        {
            if (renderer.gameObject != null && renderer.gameObject.name == ComeSailAwayWaveObjectName)
                return true;

            Material[] materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null &&
                    material.shader != null &&
                    material.shader.name == ComeSailAwayWaveShaderName)
                {
                    return true;
                }
            }

            return false;
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
    }
}

