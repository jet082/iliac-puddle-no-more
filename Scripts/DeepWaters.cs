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
            Debug.Log("[DeepWaters] Init starting (v0.43.0)");
            Mod = initParams.Mod;
            var go = new GameObject(Mod.Title);
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<DeepWaters>();

            Mod.LoadSettingsCallback = Instance.LoadSettings;
            Instance.LoadSettings();
            Debug.Log("[DeepWaters] Settings loaded");
            Instance.WrapTerrainTexturing();
            Debug.Log("[DeepWaters] Terrain texturing wrapped");
            Instance.RegisterCustomItems();
            Debug.Log("[DeepWaters] Custom items registered");
            InstallSubsystems(go);
            Debug.Log("[DeepWaters] Subsystems installed");

            Mod.IsReady = true;
            Debug.Log("[DeepWaters] Init complete");
        }

        // Drives the deferred neighbor-refresh queue (cross-tile BFS
        // re-propagation). Running on Update keeps the critical streaming
        // path responsive — refresh work spreads over many frames rather
        // than stalling the save-load and tile-promotion paths.
        void Update()
        {
            DeepWaterFloorBuilder.PumpDeferredRefreshes();
        }
    }

}



