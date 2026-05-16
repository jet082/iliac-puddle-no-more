// Project:         Iliac Puddle No More
// License:         MIT

using System.Reflection;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using UnityEngine;

namespace DeepWaters
{
    internal class FishLootIcon : MonoBehaviour
    {
        public Texture2D Texture;
    }

    internal class FishLootIconBridge : MonoBehaviour
    {
        private static FieldInfo remoteTargetIconPanelField;

        void LateUpdate()
        {
            var inventoryWindow = DaggerfallUI.UIManager.TopWindow as DaggerfallInventoryWindow;
            if (inventoryWindow == null || inventoryWindow.LootTarget == null)
                return;

            FishLootIcon icon = inventoryWindow.LootTarget.GetComponent<FishLootIcon>();
            if (icon == null || icon.Texture == null)
                return;

            Panel panel = GetRemoteTargetIconPanel(inventoryWindow);
            if (panel != null)
                panel.BackgroundTexture = icon.Texture;
        }

        private static Panel GetRemoteTargetIconPanel(DaggerfallInventoryWindow inventoryWindow)
        {
            if (inventoryWindow == null)
                return null;

            if (remoteTargetIconPanelField == null)
                remoteTargetIconPanelField = typeof(DaggerfallInventoryWindow).GetField(
                    "remoteTargetIconPanel",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            return remoteTargetIconPanelField != null
                ? remoteTargetIconPanelField.GetValue(inventoryWindow) as Panel
                : null;
        }
    }
}
