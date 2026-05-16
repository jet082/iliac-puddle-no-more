// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Plays DFU's submerged splash sound while the player swims.
    /// </summary>
    public class SwimmingSfxBridge : MonoBehaviour
    {
        private const float SwimSoundDistance = 2.5f;
        private const float SwimSoundVolumeScale = 0.7f;

        private Vector3 lastPosition;
        private float accumulatedDistance;
        private bool tracking;

        void LateUpdate()
        {
            if (GameManager.Instance == null || !GameManager.Instance.IsPlayingGame())
            {
                ResetTracking();
                return;
            }

            PlayerEnterExit pex = GameManager.Instance.PlayerEnterExit;
            GameObject player = GameManager.Instance.PlayerObject;
            PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
            if (pex == null || player == null || playerEntity == null || !pex.IsPlayerSwimming || playerEntity.IsWaterWalking)
            {
                ResetTracking();
                return;
            }

            Vector3 position = player.transform.position;
            if (!tracking)
            {
                lastPosition = position;
                accumulatedDistance = 0f;
                tracking = true;
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

        private void ResetTracking()
        {
            tracking = false;
            accumulatedDistance = 0f;
        }
    }
}

