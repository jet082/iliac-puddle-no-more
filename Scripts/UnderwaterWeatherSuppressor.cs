// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Weather;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Temporarily hides DFU's player-following rain and snow particles while
    /// the player is swimming outdoors. Actual weather state is left untouched.
    /// </summary>
    [DefaultExecutionOrder(32001)]
    public class UnderwaterWeatherSuppressor : MonoBehaviour
    {
        private bool suppressing;

        void LateUpdate()
        {
            PlayerWeather weather;
            if (!TryGetPlayerWeather(out weather))
            {
                suppressing = false;
                return;
            }

            if (ShouldSuppress(weather))
            {
                SetPrecipitationParticles(weather, false, false);
                suppressing = true;
            }
            else if (suppressing)
            {
                ApplyCurrentWeatherParticles(weather);
                suppressing = false;
            }
        }

        void OnDisable()
        {
            RestoreWeatherParticles();
        }

        private void RestoreWeatherParticles()
        {
            PlayerWeather weather;
            if (suppressing && TryGetPlayerWeather(out weather))
                ApplyCurrentWeatherParticles(weather);

            suppressing = false;
        }

        private static bool TryGetPlayerWeather(out PlayerWeather weather)
        {
            weather = null;

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerObject == null)
                return false;

            weather = gameManager.PlayerObject.GetComponent<PlayerWeather>();
            return weather != null;
        }

        private static bool ShouldSuppress(PlayerWeather weather)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame())
                return false;

            PlayerEnterExit pex = gameManager.PlayerEnterExit;
            if (pex == null || pex.IsPlayerInside || !pex.IsPlayerSwimming)
                return false;

            PlayerEntity playerEntity = gameManager.PlayerEntity;
            if (playerEntity != null && playerEntity.IsWaterWalking)
                return false;

            return IsPrecipitation(weather.WeatherType);
        }

        private static bool IsPrecipitation(WeatherType weatherType)
        {
            return weatherType == WeatherType.Rain ||
                   weatherType == WeatherType.Thunder ||
                   weatherType == WeatherType.Snow;
        }

        private static void ApplyCurrentWeatherParticles(PlayerWeather weather)
        {
            GameManager gameManager = GameManager.Instance;
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
    }
}

