// Project:         Iliac Puddle No More
// License:         MIT

using UnityEngine;
using DaggerfallWorkshop.Game;

namespace DeepWaters
{
    internal static class DeepWaterRendering
    {
        public static void DisableShadows(GameObject go)
        {
            if (go == null)
                return;

            Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
                DisableShadows(renderers[i]);
        }

        public static void DisableShadows(Renderer renderer)
        {
            if (renderer == null)
                return;

            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        public static void FaceMainCamera(Transform transform)
        {
            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.MainCamera == null || transform == null)
                return;

            Vector3 viewDirection = -gameManager.MainCamera.transform.forward;
            if (viewDirection.sqrMagnitude < 0.0001f)
                return;

            transform.LookAt(transform.position + viewDirection, Vector3.up);
        }
    }
}
