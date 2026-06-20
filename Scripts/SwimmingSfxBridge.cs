// Project:         Iliac Puddle No More
// License:         MIT

using System;
using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Weather;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeepWaters
{
	[DefaultExecutionOrder(32001)]
	internal class UnderwaterPresentationEffects : MonoBehaviour
	{
		private const float SwimSoundDistance = 2.5f;
		private const float SwimSoundVolumeScale = 0.7f;
		private const float UnderwaterCutoffFrequency = 1000f;
		private const float CutoutScanInterval = 8.0f;
		private const int CutoutScanSliceSize = 150;
		private const string TransparentCutoutRenderType = "TransparentCutout";

		private static readonly string[] CutoutShaderNames =
		{
			"Daggerfall/Billboard",
			"Daggerfall/BillboardBatch",
			"Daggerfall/BillboardBatchNoShadows",
			"Daggerfall/BillboardWaterMasked",
		};
		private static readonly List<Material> materialScratch = new List<Material>(8);

		private AudioLowPassFilter lowPassFilter;
		private GameObject listenerObject;
		private Vector3 lastPosition;
		private float accumulatedDistance;
		private bool trackingSwimDistance;
		private bool suppressingWeather;
		private Light suppressedIndirectLight;
		private float restoreIndirectIntensity;
		private bool restoreIndirectEnabled;
		private bool suppressingIndirectLight;
		private GameObject suppressedPlayerTorch;
		private bool suppressingPlayerTorch;
		private readonly HashSet<int> patchedMaterialIds = new HashSet<int>();
		private float nextCutoutScanTime;
		private MeshRenderer[] pendingRenderers;
		private int pendingIndex;

		void Update()
		{
			UpdateAudioFilter();
		}

		void LateUpdate()
		{
			UpdateSwimSfxAndWeather();
			UpdateCutoutDepthQueues();
			UpdateLightingSuppression();
		}

		void OnDisable()
		{
			pendingRenderers = null;
			ResetSwimTracking();
			RemoveAudioFilter();
			RestoreWeatherParticles();
			RestoreSuppressedLights();
		}

		private void UpdateAudioFilter()
		{
			GameManager gameManager = GameManager.Instance;
			if (DeepWaters.Instance == null || gameManager == null || !gameManager.IsPlayingGame() ||
				gameManager.PlayerEnterExit == null || gameManager.PlayerEnterExit.IsPlayerInside)
			{
				RemoveAudioFilter();
				return;
			}

			if (listenerObject == null)
			{
				if (gameManager.MainCamera == null)
					return;

				AudioListener listener = gameManager.MainCamera.GetComponentInChildren<AudioListener>();
				if (listener == null)
					return;

				listenerObject = listener.gameObject;
			}

			float oceanY;
			if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY) || !OutdoorSwimDriver.IsPresentationUnderwater(oceanY))
			{
				RemoveAudioFilter();
				return;
			}

			if (lowPassFilter == null)
				lowPassFilter = listenerObject.AddComponent<AudioLowPassFilter>();

			if (lowPassFilter != null)
			{
				lowPassFilter.cutoffFrequency = UnderwaterCutoffFrequency;
				lowPassFilter.enabled = true;
			}

		}

		private void UpdateSwimSfxAndWeather()
		{
			GameManager gameManager = GameManager.Instance;
			if (gameManager == null || !gameManager.IsPlayingGame())
			{
				ResetSwimTracking();
				RestoreWeatherParticles();
				return;
			}

			PlayerEnterExit pex = gameManager.PlayerEnterExit;
			GameObject player = gameManager.PlayerObject;
			PlayerEntity playerEntity = gameManager.PlayerEntity;
			bool swimming = pex != null && player != null && playerEntity != null && pex.IsPlayerSwimming && !playerEntity.IsWaterWalking;
			if (swimming)
				UpdateSwimSfx(player);
			else
				ResetSwimTracking();

			UpdateWeatherParticles(gameManager, pex, playerEntity);
		}

		private void UpdateSwimSfx(GameObject player)
		{
			Vector3 position = player.transform.position;
			if (!trackingSwimDistance)
			{
				lastPosition = position;
				accumulatedDistance = 0f;
				trackingSwimDistance = true;
				return;
			}

			accumulatedDistance += Vector3.Distance(position, lastPosition);
			lastPosition = position;
			if (accumulatedDistance < SwimSoundDistance)
				return;

			DaggerfallAudioSource audioSource = player.GetComponent<DaggerfallAudioSource>();
			if (audioSource != null)
				audioSource.PlayOneShot(SoundClips.SplashSmall, 0, SwimSoundVolumeScale);

			accumulatedDistance = 0f;
		}

		private void UpdateWeatherParticles(GameManager gameManager, PlayerEnterExit pex, PlayerEntity playerEntity)
		{
			PlayerWeather weather = gameManager.PlayerObject != null ? gameManager.PlayerObject.GetComponent<PlayerWeather>() : null;
			if (weather == null)
			{
				suppressingWeather = false;
				return;
			}

			if (pex != null && !pex.IsPlayerInside && pex.IsPlayerSwimming &&
				(playerEntity == null || !playerEntity.IsWaterWalking) &&
				IsPrecipitation(weather.WeatherType))
			{
				SetPrecipitationParticles(weather, false, false);
				suppressingWeather = true;
			}
			else if (suppressingWeather)
			{
				ApplyCurrentWeatherParticles(weather, gameManager);
				suppressingWeather = false;
			}
		}

		private void ResetSwimTracking()
		{
			trackingSwimDistance = false;
			accumulatedDistance = 0f;
		}

		private void RemoveAudioFilter()
		{
			if (lowPassFilter != null)
				lowPassFilter.enabled = false;

		}

		private void RestoreWeatherParticles()
		{
			GameManager gameManager = GameManager.Instance;
			PlayerWeather weather = gameManager != null && gameManager.PlayerObject != null ? gameManager.PlayerObject.GetComponent<PlayerWeather>() : null;
			if (suppressingWeather && weather != null)
				ApplyCurrentWeatherParticles(weather, gameManager);

			suppressingWeather = false;
		}

		private static bool IsPrecipitation(WeatherType weatherType)
		{
			return weatherType == WeatherType.Rain ||
				weatherType == WeatherType.Thunder ||
				weatherType == WeatherType.Snow;
		}

		private static void ApplyCurrentWeatherParticles(PlayerWeather weather, GameManager gameManager)
		{
			if (gameManager == null ||
				gameManager.PlayerEnterExit == null ||
				gameManager.PlayerEnterExit.IsPlayerInside)
			{
				SetPrecipitationParticles(weather, false, false);
				return;
			}

			switch (weather.WeatherType)
			{
				case WeatherType.Rain:
				case WeatherType.Thunder:
					SetPrecipitationParticles(weather, true, false);
					break;
				case WeatherType.Snow:
					SetPrecipitationParticles(weather, false, true);
					break;
				default:
					SetPrecipitationParticles(weather, false, false);
					break;
			}
		}

		private static void SetPrecipitationParticles(PlayerWeather weather, bool rainActive, bool snowActive)
		{
			if (weather.RainParticles != null)
				weather.RainParticles.SetActive(rainActive);

			if (weather.SnowParticles != null)
				weather.SnowParticles.SetActive(snowActive);
		}

		private void UpdateLightingSuppression()
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

			suppressedPlayerTorch = null;
			suppressingPlayerTorch = false;
		}

		private void UpdateCutoutDepthQueues()
		{
			if (pendingRenderers != null)
			{
				ContinuePatchScan();
				return;
			}

			if (Time.unscaledTime < nextCutoutScanTime || !ShouldPatchCutoutQueues())
				return;

			nextCutoutScanTime = Time.unscaledTime + CutoutScanInterval;
			pendingRenderers = FindObjectsOfType<MeshRenderer>();
			pendingIndex = 0;
			ContinuePatchScan();
		}

		private void ContinuePatchScan()
		{
			int end = Mathf.Min(pendingIndex + CutoutScanSliceSize, pendingRenderers.Length);
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

		private static bool ShouldPatchCutoutQueues()
		{
			GameManager gameManager = GameManager.Instance;
			return DeepWaters.Instance != null &&
				gameManager != null &&
				gameManager.IsPlayingGame() &&
				gameManager.PlayerEnterExit != null &&
				!gameManager.PlayerEnterExit.IsPlayerInside;
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
}

