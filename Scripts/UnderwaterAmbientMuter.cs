// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Applies a low-pass filter to the main audio listener when the player's
    /// head is below the water surface, creating a muffled underwater sound effect.
    /// </summary>
    public class UnderwaterAmbientMuter : MonoBehaviour
    {
        private AudioLowPassFilter lowPassFilter;
        private GameObject listenerObject;
        private bool isMuffled;

        // The frequency to cut off at. 22kHz is max (no filter), lower values are more muffled.
        private const float UnderwaterCutoffFrequency = 1000f;

        void Update()
        {
            if (DeepWaters.Instance == null)
                return;

            // Lazily find the AudioListener once. In DFU, it's on a child of the main camera.
            if (listenerObject == null)
            {
                var mainCamera = GameManager.Instance?.MainCamera;
                if (mainCamera == null) return;
                var listener = mainCamera.GetComponentInChildren<AudioListener>();
                if (listener == null)
                {
                    enabled = false;
                    return;
                }
                listenerObject = listener.gameObject;
            }

            // Don't apply effect when indoors.
            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerEnterExit == null || gameManager.PlayerEnterExit.IsPlayerInside)
            {
                if (isMuffled) RemoveFilter();
                return;
            }

            // Apply or remove filter based on head/camera position vs the ocean surface.
            float oceanY;
            if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY))
            {
                if (isMuffled) RemoveFilter();
                return;
            }

            bool submerged = OutdoorSwimDriver.IsPresentationUnderwater(oceanY);

            if (submerged && !isMuffled)
                ApplyFilter();
            else if (!submerged && isMuffled)
                RemoveFilter();
        }

        void OnDisable()
        {
            // Ensure filter is removed if this component is disabled or destroyed.
            if (isMuffled)
                RemoveFilter();
        }

        private void ApplyFilter()
        {
            if (listenerObject == null) return;

            // Add filter if it doesn't exist.
            if (lowPassFilter == null)
                lowPassFilter = listenerObject.AddComponent<AudioLowPassFilter>();

            // Set muffled properties.
            if (lowPassFilter != null)
            {
                lowPassFilter.cutoffFrequency = UnderwaterCutoffFrequency;
                lowPassFilter.enabled = true;
            }
            
            isMuffled = true;
        }

        private void RemoveFilter()
        {
            if (lowPassFilter != null)
                lowPassFilter.enabled = false;

            isMuffled = false;
        }
    }
}
