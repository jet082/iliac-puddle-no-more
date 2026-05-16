// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop.Game;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Installs the camera image effect that handles underwater visibility.
    /// Above-water water appearance is handled by the surface shader; this pass
    /// only runs while underwater to fog sky/no-depth pixels that DFU's normal
    /// RenderSettings fog cannot see.
    /// </summary>
    public class UnderwaterDistanceFog : MonoBehaviour
    {
        private Camera hookedCamera;
        private UnderwaterDistanceFogEffect hookedEffect;
        private WaterSurfaceDepthTextureEnabler hookedDepthEnabler;

        void LateUpdate()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.MainCamera == null)
                return;

            EnsureCameraHooks(gameManager.MainCamera);

            if (ShouldRequireDepthTexture(gameManager))
                gameManager.MainCamera.depthTextureMode |= DepthTextureMode.Depth;

            if (hookedEffect != null)
                hookedEffect.enabled = ShouldEnableEffect(gameManager);
        }

        private void EnsureCameraHooks(Camera camera)
        {
            if (hookedCamera == camera && hookedEffect != null && hookedDepthEnabler != null)
                return;

            hookedCamera = camera;
            hookedDepthEnabler = camera.GetComponent<WaterSurfaceDepthTextureEnabler>();
            if (hookedDepthEnabler == null)
                hookedDepthEnabler = camera.gameObject.AddComponent<WaterSurfaceDepthTextureEnabler>();

            hookedEffect = camera.GetComponent<UnderwaterDistanceFogEffect>();
            if (hookedEffect == null)
                hookedEffect = camera.gameObject.AddComponent<UnderwaterDistanceFogEffect>();
        }

        internal static bool ShouldRequireDepthTexture(GameManager gameManager)
        {
            return DeepWaters.Instance != null &&
                   DeepWaters.Instance.SpawnWaterSurfaces &&
                   gameManager.IsPlayingGame() &&
                   gameManager.PlayerEnterExit != null &&
                   !gameManager.PlayerEnterExit.IsPlayerInside;
        }

        private static bool ShouldEnableEffect(GameManager gameManager)
        {
            if (DeepWaters.Instance == null ||
                !gameManager.IsPlayingGame() ||
                gameManager.PlayerEnterExit == null ||
                gameManager.PlayerEnterExit.IsPlayerInside)
            {
                return false;
            }

            float oceanSurfaceY;
            if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY))
                return false;

            return OutdoorSwimDriver.IsPresentationUnderwater(oceanSurfaceY);
        }
    }

    [RequireComponent(typeof(Camera))]
    public class UnderwaterDistanceFogEffect : MonoBehaviour
    {
        private static readonly int FogColorProperty = Shader.PropertyToID("_UnderwaterFogColor");
        private static readonly int FogDensityProperty = Shader.PropertyToID("_FogDensity");
        private static readonly int GeometryFogDensityProperty = Shader.PropertyToID("_GeometryFogDensity");
        private static readonly int GeometryFogStartProperty = Shader.PropertyToID("_GeometryFogStart");
        private static readonly int FogStartProperty = Shader.PropertyToID("_FogStart");
        private static readonly int FogStrengthProperty = Shader.PropertyToID("_FogStrength");
        private static readonly int WaterSurfaceYProperty = Shader.PropertyToID("_WaterSurfaceY");
        private static readonly int CameraUnderwaterProperty = Shader.PropertyToID("_CameraUnderwater");
        private static readonly int InvProjectionProperty = Shader.PropertyToID("_DeepWatersInvProjection");
        private static readonly int CameraToWorldProperty = Shader.PropertyToID("_DeepWatersCameraToWorld");

        // This pass hides sky/no-depth leaks and distant above-surface objects
        // seen from below. Underwater terrain/decorations keep DFU's normal fog.
        private const float NoDepthFogDensityAtDefaultDistance = 0.0875f;
        private const float AboveSurfaceFogDensityAtDefaultDistance = 0.065f;
        private const float AboveSurfaceFogStartAtDefaultDistance = 25f;
        private const float AboveSurfaceFogMinimumStartDistance = 12f;
        private const float FogStartDistance = 1.0f;

        private Camera targetCamera;
        private Material material;

        void OnEnable()
        {
            targetCamera = GetComponent<Camera>();
            targetCamera.depthTextureMode |= DepthTextureMode.Depth;
        }

        void OnDestroy()
        {
            if (material != null)
            {
                Destroy(material);
                material = null;
            }
        }

        void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            float oceanSurfaceY;
            if (!ShouldApply(out oceanSurfaceY))
            {
                Graphics.Blit(source, destination);
                return;
            }

            Material fogMaterial = GetMaterial();
            if (fogMaterial == null)
            {
                Graphics.Blit(source, destination);
                return;
            }

            ConfigureMaterial(fogMaterial, oceanSurfaceY);
            Graphics.Blit(source, destination, fogMaterial);
        }

        private bool ShouldApply(out float oceanSurfaceY)
        {
            oceanSurfaceY = 0f;

            GameManager gameManager = GameManager.Instance;
            if (DeepWaters.Instance == null ||
                gameManager == null ||
                !gameManager.IsPlayingGame() ||
                gameManager.PlayerEnterExit == null ||
                gameManager.PlayerEnterExit.IsPlayerInside)
            {
                return false;
            }

            if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY))
                return false;

            return OutdoorSwimDriver.IsPresentationUnderwater(oceanSurfaceY);
        }

        private Material GetMaterial()
        {
            if (material != null)
                return material;

            Shader shader = null;
            if (DeepWaters.Mod != null)
                shader = DeepWaters.Mod.GetAsset<Shader>("UnderwaterDistanceFog.shader");

            if (shader == null)
                shader = Shader.Find("DeepWaters/UnderwaterDistanceFog");

            if (shader == null)
            {
                Debug.LogError("[DeepWaters] DeepWaters/UnderwaterDistanceFog shader not found.");
                enabled = false;
                return null;
            }

            material = new Material(shader) { name = "DeepWaters.UnderwaterDistanceFog" };
            material.hideFlags = HideFlags.HideAndDontSave;
            return material;
        }

        private void ConfigureMaterial(Material fogMaterial, float oceanSurfaceY)
        {
            float fogDistanceMultiplier = Mathf.Max(0.001f, DeepWaters.Instance.UnderwaterFogDistanceMultiplier);
            bool cameraUnderwater = OutdoorSwimDriver.IsPresentationUnderwater(oceanSurfaceY);

            fogMaterial.SetColor(FogColorProperty, DeepWaters.GetUnderwaterFogColor());
            fogMaterial.SetFloat(FogDensityProperty, NoDepthFogDensityAtDefaultDistance / fogDistanceMultiplier);
            fogMaterial.SetFloat(GeometryFogDensityProperty, AboveSurfaceFogDensityAtDefaultDistance / fogDistanceMultiplier);
            fogMaterial.SetFloat(GeometryFogStartProperty, GetAboveSurfaceFogStartDistance(fogDistanceMultiplier));
            fogMaterial.SetFloat(FogStartProperty, FogStartDistance);
            fogMaterial.SetFloat(FogStrengthProperty, 1f);
            fogMaterial.SetFloat(WaterSurfaceYProperty, oceanSurfaceY);
            fogMaterial.SetFloat(CameraUnderwaterProperty, cameraUnderwater ? 1f : 0f);

            Matrix4x4 projection = GL.GetGPUProjectionMatrix(targetCamera.projectionMatrix, false);
            fogMaterial.SetMatrix(InvProjectionProperty, projection.inverse);
            fogMaterial.SetMatrix(CameraToWorldProperty, targetCamera.cameraToWorldMatrix);
        }

        private static float GetAboveSurfaceFogStartDistance(float fogDistanceMultiplier)
        {
            return Mathf.Max(
                AboveSurfaceFogMinimumStartDistance,
                AboveSurfaceFogStartAtDefaultDistance * fogDistanceMultiplier);
        }

    }

    [RequireComponent(typeof(Camera))]
    public class WaterSurfaceDepthTextureEnabler : MonoBehaviour
    {
        private Camera targetCamera;

        void Awake()
        {
            targetCamera = GetComponent<Camera>();
        }

        void OnPreCull()
        {
            if (targetCamera == null)
                targetCamera = GetComponent<Camera>();

            GameManager gameManager = GameManager.Instance;
            if (targetCamera != null &&
                gameManager != null &&
                UnderwaterDistanceFog.ShouldRequireDepthTexture(gameManager))
            {
                targetCamera.depthTextureMode |= DepthTextureMode.Depth;
            }
        }
    }
}
