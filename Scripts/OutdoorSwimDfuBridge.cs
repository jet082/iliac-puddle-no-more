// Project:         Iliac Puddle No More
// License:         MIT

using System.Reflection;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    internal sealed class OutdoorSwimDfuBridge
    {
        private FieldInfo isPlayerInsideDungeonField;
        private FieldInfo isPlayerSubmergedField;
        private FieldInfo onExteriorWaterMethodField;
        private FieldInfo dungeonField;
        private FieldInfo lastPlayerDungeonBlockIndexField;

        private bool outdoorDungeonParentAssigned;
        private DaggerfallDungeon outdoorDungeonParent;

        public bool Initialize()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            isPlayerInsideDungeonField = typeof(PlayerEnterExit).GetField("isPlayerInsideDungeon", flags);
            isPlayerSubmergedField = typeof(PlayerEnterExit).GetField("isPlayerSubmerged", flags);
            onExteriorWaterMethodField = typeof(PlayerMotor).GetField("onExteriorWaterMethod", flags);
            dungeonField = typeof(PlayerEnterExit).GetField("dungeon", flags);
            lastPlayerDungeonBlockIndexField = typeof(PlayerEnterExit).GetField("lastPlayerDungeonBlockIndex", flags);

            return isPlayerInsideDungeonField != null &&
                   isPlayerSubmergedField != null &&
                   onExteriorWaterMethodField != null &&
                   dungeonField != null &&
                   lastPlayerDungeonBlockIndexField != null;
        }

        public void ForgeDungeonState(PlayerEnterExit pex)
        {
            AssignOutdoorDungeonParent(pex);
            isPlayerInsideDungeonField.SetValue(pex, true);
        }

        public void RestoreDungeonState(PlayerEnterExit pex)
        {
            isPlayerInsideDungeonField.SetValue(pex, false);
            ClearOutdoorDungeonParent(pex);
        }

        public void ApplyWaterAudioState(
            PlayerEnterExit pex,
            short blockWaterLevel,
            PlayerMotor.OnExteriorWaterMethod exteriorWaterMethod,
            bool playerSubmerged)
        {
            pex.blockWaterLevel = blockWaterLevel;

            GameManager gameManager = GameManager.Instance;
            if (gameManager != null && gameManager.PlayerMotor != null)
                onExteriorWaterMethodField.SetValue(gameManager.PlayerMotor, exteriorWaterMethod);

            isPlayerSubmergedField.SetValue(pex, playerSubmerged);
        }

        private void AssignOutdoorDungeonParent(PlayerEnterExit pex)
        {
            DaggerfallDungeon currentDungeon = dungeonField.GetValue(pex) as DaggerfallDungeon;
            if (currentDungeon != null && currentDungeon != outdoorDungeonParent)
                return;

            DaggerfallDungeon parent = GetOutdoorDungeonParent();
            if (parent == null)
                return;

            dungeonField.SetValue(pex, parent);
            lastPlayerDungeonBlockIndexField.SetValue(pex, -1);
            outdoorDungeonParentAssigned = true;
        }

        private void ClearOutdoorDungeonParent(PlayerEnterExit pex)
        {
            if (!outdoorDungeonParentAssigned)
                return;

            DaggerfallDungeon currentDungeon = dungeonField.GetValue(pex) as DaggerfallDungeon;
            if (currentDungeon == outdoorDungeonParent)
                dungeonField.SetValue(pex, null);

            lastPlayerDungeonBlockIndexField.SetValue(pex, -1);
            outdoorDungeonParentAssigned = false;
        }

        private DaggerfallDungeon GetOutdoorDungeonParent()
        {
            Transform exteriorParent = GetExteriorParent();
            if (outdoorDungeonParent != null)
            {
                if (exteriorParent != null && outdoorDungeonParent.transform.parent != exteriorParent)
                    outdoorDungeonParent.transform.SetParent(exteriorParent, false);

                return outdoorDungeonParent;
            }

            GameObject go = new GameObject("DeepWaters Outdoor Spell Parent");
            if (exteriorParent != null)
                go.transform.SetParent(exteriorParent, false);

            outdoorDungeonParent = go.AddComponent<DaggerfallDungeon>();
            return outdoorDungeonParent;
        }

        private static Transform GetExteriorParent()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
                return null;

            if (gameManager.PlayerGPS != null &&
                gameManager.PlayerGPS.IsPlayerInLocationRect &&
                gameManager.StreamingWorld != null &&
                gameManager.StreamingWorld.CurrentPlayerLocationObject != null)
            {
                return gameManager.StreamingWorld.CurrentPlayerLocationObject.transform;
            }

            return gameManager.StreamingTarget != null ? gameManager.StreamingTarget.transform : null;
        }
    }
}
