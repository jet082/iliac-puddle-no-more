// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallConnect;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Banking;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// DFU places player-owned ships as ordinary exterior locations, using the
    /// terrain height sampled under the location root. Deep water lowers that
    /// sample to the seafloor, so the owned ship scene needs one explicit
    /// waterline anchor.
    /// </summary>
    public class PlayerShipWaterlineFix : MonoBehaviour
    {
        private const float CheckInterval = 0.5f;
        private const float PositionTolerance = 0.05f;

        private float nextCheckTime;

        void OnEnable()
        {
            StreamingWorld.OnCreateLocationGameObject += OnCreateLocationGameObject;
            StreamingWorld.OnUpdateLocationGameObject += OnUpdateLocationGameObject;
            StreamingWorld.OnAvailableLocationGameObject += OnAvailableLocationGameObject;
            StreamingWorld.OnFloatingOriginChange += OnFloatingOriginChange;
            DeepWaterRuntime.OnTransientReset += ScheduleImmediateCheck;
        }

        void OnDisable()
        {
            StreamingWorld.OnCreateLocationGameObject -= OnCreateLocationGameObject;
            StreamingWorld.OnUpdateLocationGameObject -= OnUpdateLocationGameObject;
            StreamingWorld.OnAvailableLocationGameObject -= OnAvailableLocationGameObject;
            StreamingWorld.OnFloatingOriginChange -= OnFloatingOriginChange;
            DeepWaterRuntime.OnTransientReset -= ScheduleImmediateCheck;
        }

        void LateUpdate()
        {
            if (Time.time < nextCheckTime)
                return;

            nextCheckTime = Time.time + CheckInterval;
            AnchorCurrentShipLocation();
        }

        private void OnCreateLocationGameObject(DaggerfallLocation dfLocation)
        {
            AnchorShipLocation(dfLocation);
        }

        private void OnUpdateLocationGameObject(GameObject locationObject, bool allowYield)
        {
            if (locationObject == null)
                return;

            AnchorShipLocation(locationObject.GetComponent<DaggerfallLocation>());
        }

        private void OnAvailableLocationGameObject()
        {
            AnchorCurrentShipLocation();
        }

        private void OnFloatingOriginChange()
        {
            ScheduleImmediateCheck();
        }

        private void ScheduleImmediateCheck()
        {
            nextCheckTime = 0f;
        }

        private static void AnchorCurrentShipLocation()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.StreamingWorld == null)
                return;

            AnchorShipLocation(gameManager.StreamingWorld.CurrentPlayerLocationObject);
        }

        private static void AnchorShipLocation(DaggerfallLocation location)
        {
            if (!IsOwnedShipLocation(location))
                return;

            float oceanY;
            if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY))
                return;

            Vector3 position = location.transform.position;
            if (Mathf.Abs(position.y - oceanY) <= PositionTolerance)
                return;

            position.y = oceanY;
            location.transform.position = position;
        }

        private static bool IsOwnedShipLocation(DaggerfallLocation location)
        {
            if (location == null ||
                location.Summary.LocationType != DFRegion.LocationTypes.HomeYourShips ||
                !DaggerfallBankManager.OwnsShip)
            {
                return false;
            }

            DFPosition shipCoords = DaggerfallBankManager.GetShipCoords();
            return shipCoords != null &&
                   location.Summary.MapPixelX == shipCoords.X &&
                   location.Summary.MapPixelY == shipCoords.Y;
        }
    }
}
