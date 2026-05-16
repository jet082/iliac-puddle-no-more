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

        public static bool TryMoveToShore(PlayerEnterExit pex, float oceanY)
        {
            if (pex == null || !pex.IsPlayerSwimming || InputManager.Instance.Vertical <= MinimumForwardInput)
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

            Vector3 probe = controller.transform.position + forward * ForwardProbeDistance;
            probe.y = oceanY + ProbeHeightAboveOcean;

            RaycastHit hit;
            if (!Physics.Raycast(probe, Vector3.down, out hit, ProbeDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return false;

            if (!IsValidLandingHit(hit))
                return false;

            float landingY = hit.point.y;
            if (landingY >= oceanY - 1f && landingY <= oceanY + 12f && landingY > controller.transform.position.y - 0.1f)
            {
                controller.Move(hit.point + Vector3.up * 1.5f + forward * 0.5f - controller.transform.position);
                return true;
            }

            return false;
        }

        private static bool IsValidLandingHit(RaycastHit hit)
        {
            return IsShoreGround(hit.collider);
        }

        internal static bool IsShoreGround(Collider collider)
        {
            if (collider == null)
                return false;

            if (collider.GetComponent<PassiveFishBehaviour>() != null)
                return false;

            if (collider.GetComponentInParent<DaggerfallEnemy>() != null)
                return false;

            return collider.GetComponent<Terrain>() != null ||
                   collider.GetComponentInParent<Terrain>() != null ||
                   HasStaticGeometryTag(collider.transform);
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
