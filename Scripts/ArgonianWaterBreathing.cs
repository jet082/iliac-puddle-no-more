// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Re-applies IsWaterBreathing = true to the player every frame if
    /// they're an Argonian.
    ///
    /// Timing matters: the flag is cleared each frame inside
    /// EntityEffectManager.Update -> DoConstantEffects -> ClearConstantEffects.
    /// We have to set it AFTER that clears, but BEFORE the next frame's
    /// PlayerEntity.FixedUpdate reads it for the drowning check.
    ///
    /// LateUpdate is the right hook: runs after all Updates (so after
    /// EntityEffectManager has cleared the flag), and before any
    /// FixedUpdate that might fire between now and the next frame's
    /// Update phase.
    ///
    /// Vanilla DFU only gives Argonians a 50% chance per drowning tick
    /// to skip breath loss — they can still drown, just slower. With
    /// this enabled, line 323 of PlayerEntity.cs short-circuits because
    /// IsWaterBreathing is true: breath stays at 0 forever (no decrement,
    /// no health damage). Works everywhere — outdoors, dungeons, anywhere
    /// submerged.
    /// </summary>
    public class ArgonianWaterBreathing : MonoBehaviour
    {
        void LateUpdate()
        {
            if (DeepWaters.Instance == null || !DeepWaters.Instance.ArgonianInfiniteBreath) return;
            if (GameManager.Instance == null || !GameManager.Instance.IsPlayingGame()) return;

            var entity = GameManager.Instance.PlayerEntity;
            if (entity != null && entity.Race == Races.Argonian)
                entity.IsWaterBreathing = true;
        }
    }
}
