// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
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
        internal const string Version = "v0.56.1";
        internal const string BuildStamp = "2026-06-10 painted-water-authority";

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

            // Pick the bake that matches the active terrain heightmap. Stock
            // DefaultTerrainSampler means no terrain overhaul (Interesting
            // Terrains / WoD replace the sampler with their own type), so the
            // vanilla bake lines the carve/shore data up with vanilla coasts.
            string assetName = DeepWaterDistanceBake.BakeAssetName;
            bool vanillaTerrain = DaggerfallUnity.Instance != null &&
                                  DaggerfallUnity.Instance.TerrainSampler is DefaultTerrainSampler;
            if (vanillaTerrain)
            {
                if (Mod.HasAsset(DeepWaterDistanceBake.VanillaBakeAssetName))
                {
                    assetName = DeepWaterDistanceBake.VanillaBakeAssetName;
                }
                else
                {
                    Debug.LogWarning("[DeepWaters] Vanilla terrain is active but no '" +
                                     DeepWaterDistanceBake.VanillaBakeAssetName +
                                     "' asset is bundled — falling back to the terrain-overhaul bake. " +
                                     "Shore depths may not match vanilla coastlines. Run Tools > Deep " +
                                     "Waters > Bake Distance Field (Vanilla Terrain) and rebuild the mod.");
                }
            }

            Debug.Log("[DeepWaters] Loading distance bake '" + assetName + "' (terrain sampler: " +
                      (DaggerfallUnity.Instance != null && DaggerfallUnity.Instance.TerrainSampler != null
                          ? DaggerfallUnity.Instance.TerrainSampler.GetType().Name
                          : "none") + ").");

            TextAsset bakeAsset = Mod.GetAsset<TextAsset>(assetName);
            if (bakeAsset == null || bakeAsset.bytes == null || bakeAsset.bytes.Length == 0)
            {
                Debug.LogError("[DeepWaters] Distance bake asset '" + assetName +
                               "' not found in mod bundle. Run Tools > Deep Waters > Bake " +
                               "Distance Field in the Unity editor, then rebuild the mod.");
                return;
            }

            if (!DeepWaterDistanceBake.TryLoadBytes(bakeAsset.bytes))
                Debug.LogError("[DeepWaters] Distance bake parse failed — seafloor will not build.");
        }

        void Update()
        {
            DeepWaterRuntime.PumpPostTransitionRefresh();
        }
    }

}
