// Project:         Iliac Puddle No More
// License:         MIT

using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Caches a boolean gate result for a fixed interval. The nearby-water
    /// probe both the encounter pulse and the loot pulse run is expensive
    /// (a ring of column lookups), so each only re-probes every couple of
    /// seconds and reuses the last answer in between.
    /// </summary>
    internal sealed class TimedGateCache
    {
        private readonly float intervalSeconds;
        private float nextCheckTime;
        private bool hasValue;
        private bool lastResult;

        public TimedGateCache(float intervalSeconds)
        {
            this.intervalSeconds = intervalSeconds;
        }

        public bool IsFresh
        {
            get { return hasValue && Time.time < nextCheckTime; }
        }

        public bool Value
        {
            get { return lastResult; }
        }

        public bool Store(bool result)
        {
            lastResult = result;
            hasValue = true;
            nextCheckTime = Time.time + intervalSeconds;
            return result;
        }

        public void Invalidate()
        {
            hasValue = false;
            lastResult = false;
        }
    }
}
