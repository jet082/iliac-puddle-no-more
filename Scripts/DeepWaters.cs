// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Mod bootstrap and settings holder.
    /// </summary>
    public partial class DeepWaters : MonoBehaviour
    {
        public static DeepWaters Instance { get; private set; }
        public static Mod Mod { get; private set; }

        [Invoke(StateManager.StateTypes.Start, 200)]
        public static void Init(InitParams initParams)
        {
            Mod = initParams.Mod;
            var go = new GameObject(Mod.Title);
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<DeepWaters>();

            Mod.LoadSettingsCallback = Instance.LoadSettings;
            Instance.LoadSettings();
            Instance.WrapTerrainTexturing();
            Instance.RegisterCustomItems();
            InstallSubsystems(go);

            Mod.IsReady = true;
        }
    }

}



