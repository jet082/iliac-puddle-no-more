// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    internal static class OutdoorShoreExitAssist
    {
        private const float ForwardProbeDistance = 3.5f;
        private const float ProbeHeightAboveOcean = 13f;
        private const float ProbeDistance = 18f;
        private const float MinimumForwardInput = 0.02f;
        private const float MinimumLandingNormalY = 0.45f;
        private const float ShoreLandingWaterMargin = 0.25f;
        private const float MaximumLandingAboveOcean = 8f;
        private const float MinimumOpenWaterColumnDepth = 2f;

        public static bool TryMoveToShore(
            PlayerEnterExit pex,
            float oceanY,
            bool requireSwimming = true,
            bool requireForwardInput = true)
        {
            if (pex == null)
                return false;

            if (requireSwimming && !pex.IsPlayerSwimming)
                return false;

            if (requireForwardInput && InputManager.Instance.Vertical <= MinimumForwardInput)
                return false;

            var controller = GameManager.Instance.PlayerController;
            var camera = GameManager.Instance.MainCamera;
            if (controller == null || camera == null)
                return false;

            Vector3 forward = camera.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
                return false;

            forward.Normalize();

            if (TryMoveToLanding(controller, forward, forward * 0.5f, oceanY))
                return true;

            return TryMoveToLanding(controller, Vector3.zero, Vector3.zero, oceanY);
        }

        private static bool TryMoveToLanding(
            CharacterController controller,
            Vector3 probeOffset,
            Vector3 landingOffset,
            float oceanY)
        {
            Vector3 probe = controller.transform.position + probeOffset;
            probe.y = oceanY + ProbeHeightAboveOcean;

            RaycastHit hit;
            if (!Physics.Raycast(probe, Vector3.down, out hit, ProbeDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return false;

            if (!IsValidLandingHit(hit, oceanY))
                return false;

            float landingY = hit.point.y;
            if (landingY >= oceanY - 1f &&
                landingY <= oceanY + MaximumLandingAboveOcean &&
                landingY > controller.transform.position.y - 0.1f)
            {
                controller.Move(hit.point + Vector3.up * 1.5f + landingOffset - controller.transform.position);
                return true;
            }

            return false;
        }

        internal static bool IsValidShoreStandingHit(RaycastHit hit, float oceanY)
        {
            return IsValidLandingHit(hit, oceanY);
        }

        private static bool IsValidLandingHit(RaycastHit hit, float oceanY)
        {
            if (!IsShoreGround(hit.collider))
                return false;

            if (hit.normal.y < MinimumLandingNormalY)
                return false;

            DeepWaterColumn column;
            if (DeepWaterWorld.TryGetWaterColumn(hit.point.x, hit.point.z, out column))
            {
                if (column.Depth >= MinimumOpenWaterColumnDepth)
                    return false;

                if (hit.point.y <= column.OceanWorldY + ShoreLandingWaterMargin)
                    return false;
            }

            return hit.point.y >= oceanY - 1f;
        }

        internal static bool IsShoreGround(Collider collider)
        {
            if (collider == null)
                return false;

            if (collider.isTrigger)
                return false;

            var player = GameManager.Instance != null ? GameManager.Instance.PlayerObject : null;
            if (player != null && collider.transform.IsChildOf(player.transform))
                return false;

            if (collider.GetComponent<PassiveFishBehaviour>() != null)
                return false;

            if (collider.GetComponentInParent<DeepWaterFloorMesh>() != null ||
                collider.GetComponentInParent<DeepWatersWaterSurface>() != null)
            {
                return false;
            }

            if (collider.GetComponentInParent<DaggerfallEnemy>() != null)
                return false;

            if (collider.GetComponentInParent<DaggerfallLoot>() != null ||
                collider.GetComponentInParent<DaggerfallBillboard>() != null)
            {
                return false;
            }

            // DFU shoreline/location colliders are not consistently tagged.
            // After excluding water, actors, loot, and billboards, an upward
            // solid hit above the waterline is shore enough for this handoff.
            return true;
        }

        private static bool HasStaticGeometryTag(Transform transform)
        {
            while (transform != null)
            {
                if (transform.CompareTag(DaggerfallUnity.staticGeometryTag))
                    return true;

                transform = transform.parent;
            }

            return false;
        }
    }
}
