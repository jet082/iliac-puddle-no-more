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
        internal const string Version = "v0.55.58-diag";
        internal const string BuildStamp = "2026-06-04 near-shore-steepen";

        public static DeepWaters Instance { get; private set; }
        public static Mod Mod { get; private set; }

        [Invoke(StateManager.StateTypes.Start, 200)]
        public static void Init(InitParams initParams)
        {
            Debug.Log("[DeepWaters] Init starting (" + Version + "; build=" + BuildStamp + ")");
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
            Instance.LoadDistanceBake();
            InstallSubsystems(go);
            Debug.Log("[DeepWaters] Subsystems installed");

            Mod.IsReady = true;
            Debug.Log("[DeepWaters] Init complete");
        }

        // Load the pre-baked distance-to-coast field from the mod's bundled
        // Resources. Without this, DeepWaterTileData.HasDistanceField stays
        // false and every tile reports "NOT ocean-connected (distField=False)"
        // → no carving → can't swim. The working backup pre-dated the bake
        // architecture entirely and used per-tile BFS for distance, which is
        // why the working-backup DeepWaters.cs had no LoadDistanceBake call;
        // the current DeepWaterTileData requires the bake.
        private void LoadDistanceBake()
        {
            if (Mod == null)
            {
                Debug.LogError("[DeepWaters] Cannot load distance bake — Mod reference is null.");
                return;
            }

            TextAsset bakeAsset = Mod.GetAsset<TextAsset>(DeepWaterDistanceBake.BakeAssetName);
            if (bakeAsset == null || bakeAsset.bytes == null || bakeAsset.bytes.Length == 0)
            {
                Debug.LogError("[DeepWaters] Distance bake asset '" +
                               DeepWaterDistanceBake.BakeAssetName +
                               "' not found in mod bundle. Run Tools > Deep Waters > Bake " +
                               "Distance Field in the Unity editor, then rebuild the mod.");
                return;
            }

            if (!DeepWaterDistanceBake.TryLoadBytes(bakeAsset.bytes))
                Debug.LogError("[DeepWaters] Distance bake parse failed — seafloor will not build.");
        }

        // Drives the deferred neighbor-refresh queue (cross-tile BFS
        // re-propagation). Running on Update keeps the critical streaming
        // path responsive — refresh work spreads over many frames rather
        // than stalling the save-load and tile-promotion paths.
        void Update()
        {
            DeepWaterRuntime.PumpPostTransitionRefresh();
            DeepWaterFloorBuilder.PumpDeferredRefreshes();
        }
    }

}
