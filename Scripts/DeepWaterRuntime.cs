// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;

namespace DeepWaters
{
    /// <summary>
    /// Small lifecycle hub for transient outdoor ocean content.
    /// DFU reuses terrain objects during world jumps, so systems that parent
    /// objects to terrain need one shared reset signal when the world is rebuilt.
    /// </summary>
    internal static class DeepWaterRuntime
    {
        public delegate void TransientResetHandler();
        public static event TransientResetHandler OnTransientReset;

        private static bool installed;

        public static void Install()
        {
            if (installed)
                return;

            SaveLoadManager.OnStartLoad += OnStartLoad;
            StreamingWorld.OnTeleportToCoordinates += OnTeleportToCoordinates;
            installed = true;
        }

        public static void Uninstall()
        {
            if (!installed)
                return;

            SaveLoadManager.OnStartLoad -= OnStartLoad;
            StreamingWorld.OnTeleportToCoordinates -= OnTeleportToCoordinates;
            installed = false;
        }

        private static void OnStartLoad(SaveData_v1 saveData)
        {
            ResetTransientState();
        }

        private static void OnTeleportToCoordinates(DFPosition worldPos)
        {
            ResetTransientState();
        }

        public static void ResetTransientState()
        {
            if (OnTransientReset != null)
                OnTransientReset();
        }
    }
}

