// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
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
        private static readonly int GlobalUnderwaterProperty = Shader.PropertyToID("_DeepWatersUnderwater");
        private static readonly int GlobalWaterSurfaceYProperty = Shader.PropertyToID("_DeepWatersWaterSurfaceY");

        private Camera hookedCamera;
        private UnderwaterDistanceFogEffect hookedEffect;
        private WaterSurfaceDepthTextureEnabler hookedDepthEnabler;
        private bool diagnosticInitialized;
        private bool lastDiagnosticUnderwater;
        private float nextDiagnosticTime;

        void LateUpdate()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.MainCamera == null)
            {
                SetUnderwaterShaderGlobals(false, 0f);
                if (hookedEffect != null)
                    hookedEffect.enabled = false;
                return;
            }

            EnsureCameraHooks(gameManager.MainCamera);

            float oceanSurfaceY;
            bool underwaterPresentation = TryGetUnderwaterPresentation(gameManager, gameManager.MainCamera, out oceanSurfaceY);
            SetUnderwaterShaderGlobals(underwaterPresentation, oceanSurfaceY);

            if (ShouldRequireDepthTexture(gameManager))
                gameManager.MainCamera.depthTextureMode |= DepthTextureMode.Depth;

            if (hookedEffect != null)
                hookedEffect.enabled = underwaterPresentation;

            // Keep the water-surface underside's horizon curtain converging to
            // the SAME far color as the fog volume, every frame (it tracks the
            // camera's depth darkening).
            if (underwaterPresentation)
                WaterSurfaceResources.SetHorizonColor(
                    UnderwaterDistanceFogEffect.ComputeHorizonAmbientColor(oceanSurfaceY));

            LogVisibilityState(gameManager, gameManager.MainCamera, underwaterPresentation, oceanSurfaceY);
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

        internal static bool TryGetUnderwaterPresentation(GameManager gameManager, Camera camera, out float oceanSurfaceY)
        {
            oceanSurfaceY = 0f;
            if (DeepWaters.Instance == null ||
                gameManager == null ||
                !gameManager.IsPlayingGame() ||
                gameManager.PlayerEnterExit == null ||
                gameManager.PlayerEnterExit.IsPlayerInside)
            {
                return false;
            }

            bool hasOceanSurface = DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY);
            float localOceanSurfaceY;
            if (TryGetLocalOceanSurfaceWorldY(gameManager, camera, out localOceanSurfaceY))
            {
                if (!hasOceanSurface || Mathf.Abs(oceanSurfaceY - localOceanSurfaceY) > 12f)
                    oceanSurfaceY = localOceanSurfaceY;
                hasOceanSurface = true;
            }

            if (!hasOceanSurface)
                return false;

            if (OutdoorSwimDriver.IsPresentationUnderwater(oceanSurfaceY))
                return true;

            if (IsExteriorSwimming(gameManager) &&
                IsCameraOrPlayerBelowSurface(gameManager, camera, oceanSurfaceY))
            {
                return true;
            }

            if (camera != null && IsPointInsideDeepWater(camera.transform.position, oceanSurfaceY, 0.08f))
                return true;

            Vector3 headPosition;
            return TryGetPlayerHeadPosition(gameManager, out headPosition) &&
                   IsPointInsideDeepWater(headPosition, oceanSurfaceY, -0.25f);
        }

        internal static bool IsExteriorSwimming(GameManager gameManager)
        {
            if (gameManager == null)
                return false;

            if (gameManager.PlayerEnterExit != null && gameManager.PlayerEnterExit.IsPlayerSwimming)
                return true;

            PlayerMotor playerMotor = gameManager.PlayerMotor;
            return playerMotor != null &&
                   (playerMotor.IsSwimming ||
                    playerMotor.OnExteriorWater == PlayerMotor.OnExteriorWaterMethod.Swimming);
        }

        private static void SetUnderwaterShaderGlobals(bool underwaterPresentation, float oceanSurfaceY)
        {
            Shader.SetGlobalFloat(GlobalUnderwaterProperty, underwaterPresentation ? 1f : 0f);
            Shader.SetGlobalFloat(GlobalWaterSurfaceYProperty, oceanSurfaceY);
        }

        private static bool TryGetLocalOceanSurfaceWorldY(GameManager gameManager, Camera camera, out float oceanSurfaceY)
        {
            oceanSurfaceY = 0f;

            DeepWaterColumn column;
            if (camera != null &&
                DeepWaterWorld.TryGetWaterColumn(camera.transform.position.x, camera.transform.position.z, out column) &&
                column.Depth > 0.25f)
            {
                oceanSurfaceY = column.OceanWorldY;
                return true;
            }

            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            if (player != null &&
                DeepWaterWorld.TryGetWaterColumn(player.transform.position.x, player.transform.position.z, out column) &&
                column.Depth > 0.25f)
            {
                oceanSurfaceY = column.OceanWorldY;
                return true;
            }

            return false;
        }

        private static bool TryGetPlayerHeadPosition(GameManager gameManager, out Vector3 headPosition)
        {
            headPosition = Vector3.zero;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            if (player == null)
                return false;

            headPosition = player.transform.position;
            headPosition.y += (76 * MeshReader.GlobalScale) - 0.95f;
            return true;
        }

        private static bool IsCameraOrPlayerBelowSurface(GameManager gameManager, Camera camera, float oceanSurfaceY)
        {
            const float cameraSurfacePadding = 0.25f;
            const float headSurfacePadding = -0.15f;

            if (camera != null && camera.transform.position.y <= oceanSurfaceY + cameraSurfacePadding)
                return true;

            Vector3 headPosition;
            return TryGetPlayerHeadPosition(gameManager, out headPosition) &&
                   headPosition.y <= oceanSurfaceY + headSurfacePadding;
        }

        private static bool IsPointInsideDeepWater(Vector3 worldPosition, float oceanSurfaceY, float surfacePadding)
        {
            if (worldPosition.y > oceanSurfaceY + surfacePadding)
                return false;

            DeepWaterColumn column;
            if (DeepWaterWorld.TryGetWaterColumn(worldPosition.x, worldPosition.z, out column))
            {
                const float minimumDepth = 0.25f;
                const float floorGrace = 8f;
                float seafloorWorldY;
                if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(
                    column,
                    worldPosition.x,
                    worldPosition.z,
                    out seafloorWorldY))
                {
                    seafloorWorldY = column.SeafloorWorldY;
                }

                return column.Depth > minimumDepth &&
                       worldPosition.y <= column.OceanWorldY + surfacePadding &&
                       worldPosition.y >= seafloorWorldY - floorGrace;
            }

            float nearbyDepth;
            return DeepWaterWorld.HasNearbyWaterColumn(worldPosition, 4f, 36f, 8, 0.25f, out nearbyDepth);
        }

        private void LogVisibilityState(GameManager gameManager, Camera camera, bool underwaterPresentation, float oceanSurfaceY)
        {
            if (gameManager == null)
                return;

            bool waterContext = underwaterPresentation || IsExteriorSwimming(gameManager);
            bool stateChanged = !diagnosticInitialized || underwaterPresentation != lastDiagnosticUnderwater;
            if (!stateChanged && (!waterContext || Time.unscaledTime < nextDiagnosticTime))
                return;

            diagnosticInitialized = true;
            lastDiagnosticUnderwater = underwaterPresentation;
            nextDiagnosticTime = Time.unscaledTime + 5f;

            Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
            Vector3 headPosition;
            bool hasHead = TryGetPlayerHeadPosition(gameManager, out headPosition);

            DeepWaterColumn cameraColumn;
            bool hasCameraColumn = camera != null &&
                                   DeepWaterWorld.TryGetWaterColumn(cameraPosition.x, cameraPosition.z, out cameraColumn);

            PlayerMotor playerMotor = gameManager.PlayerMotor;
            string exteriorWater = playerMotor != null ? playerMotor.OnExteriorWater.ToString() : "none";
            bool playerSwimming = gameManager.PlayerEnterExit != null && gameManager.PlayerEnterExit.IsPlayerSwimming;
            bool motorSwimming = playerMotor != null && playerMotor.IsSwimming;

            Debug.Log(
                "[DeepWaters.Visibility] underwater=" + underwaterPresentation +
                " effect=" + (hookedEffect != null && hookedEffect.enabled) +
                " playerSwimming=" + playerSwimming +
                " motorSwimming=" + motorSwimming +
                " exteriorWater=" + exteriorWater +
                " cameraY=" + cameraPosition.y.ToString("F2") +
                " headY=" + (hasHead ? headPosition.y.ToString("F2") : "none") +
                " oceanY=" + oceanSurfaceY.ToString("F2") +
                " cameraColumn=" + hasCameraColumn +
                " depthMode=" + (camera != null ? camera.depthTextureMode.ToString() : "none") +
                " renderFog=" + RenderSettings.fog +
                " fogMode=" + RenderSettings.fogMode +
                " fogDensity=" + RenderSettings.fogDensity.ToString("F5") +
                " fogColor=#" + ColorUtility.ToHtmlStringRGB(RenderSettings.fogColor));
        }
    }

    [RequireComponent(typeof(Camera))]
    public class UnderwaterDistanceFogEffect : MonoBehaviour
    {
        private static readonly int FogColorProperty = Shader.PropertyToID("_UnderwaterFogColor");
        private static readonly int FogDensityProperty = Shader.PropertyToID("_FogDensity");
        private static readonly int FogStartProperty = Shader.PropertyToID("_FogStart");
        private static readonly int FogStrengthProperty = Shader.PropertyToID("_FogStrength");
        private static readonly int VisionDistanceProperty = Shader.PropertyToID("_VisionDistance");
        private static readonly int NearTintStrengthProperty = Shader.PropertyToID("_NearTintStrength");
        private static readonly int DepthDarkeningStartProperty = Shader.PropertyToID("_DepthDarkeningStart");
        private static readonly int DepthDarkeningEndProperty = Shader.PropertyToID("_DepthDarkeningEnd");
        private static readonly int WaterSurfaceYProperty = Shader.PropertyToID("_WaterSurfaceY");
        private static readonly int CameraUnderwaterProperty = Shader.PropertyToID("_CameraUnderwater");
        private static readonly int WaterSurfaceTextureProperty = Shader.PropertyToID("_WaterSurfaceTex");
        private static readonly int SurfaceAlphaProperty = Shader.PropertyToID("_SurfaceAlpha");
        private static readonly int SurfaceTileWorldSizeProperty = Shader.PropertyToID("_SurfaceTileWorldSize");
        private static readonly int SurfaceTilingProperty = Shader.PropertyToID("_SurfaceTiling");
        private static readonly int SurfaceScrollProperty = Shader.PropertyToID("_SurfaceScroll");
        private static readonly int RayBottomLeftProperty = Shader.PropertyToID("_DeepWatersRayBL");
        private static readonly int RayBottomRightProperty = Shader.PropertyToID("_DeepWatersRayBR");
        private static readonly int RayTopLeftProperty = Shader.PropertyToID("_DeepWatersRayTL");
        private static readonly int RayTopRightProperty = Shader.PropertyToID("_DeepWatersRayTR");

        // Volumetric regrade shader properties.
        private static readonly int AbsorptionColorProperty = Shader.PropertyToID("_AbsorptionColor");
        private static readonly int ScatterColorProperty = Shader.PropertyToID("_ScatterColor");
        private static readonly int ScatterStrengthProperty = Shader.PropertyToID("_ScatterStrength");
        private static readonly int DeepWaterColorProperty = Shader.PropertyToID("_DeepWaterColor");

        // Vision distance: how far you can see through clear water at the
        // surface before the scene fades to ambient water color. Below the
        // surface the shader trims this back via depth darkening, but never
        // below ~50% of the surface value so the scene stays readable.
        private const float VisionDistanceAtDefaultSetting = 70f;
        private const float MinimumVisionDistance = 22f;
        private const float MaximumVisionDistance = 260f;
        private const float DistanceCurveAtLowStrength = 0.85f;
        private const float DistanceCurveAtFullStrength = 1.40f;
        private const float MinimumNearTint = 0.10f;
        private const float NearTintAtFullStrength = 0.26f;
        private const float FogStartDistance = 0f;
        // Depth darkening: starts at ~6m and reaches the end of the ramp at
        // WaterDepth*0.75 (capped at 110m). At full ramp the scene multiplier
        // sits at ~0.45 and vision at ~0.50× — dim and limited, not pitch.
        private const float DepthDarkeningStart = 6f;
        private const float DepthDarkeningEndMin = 55f;
        private const float DepthDarkeningEndMax = 110f;
        private static readonly Vector4 SurfaceScroll = new Vector4(0.0225f, 0.0375f, 0f, 0f);

        // Tuned absorption coefficients. R is dominant; absorption is divided
        // by the effective vision distance in the shader, so at viewDistance
        // = vision, transmission per channel ≈ exp(-coef * strengthScale).
        // Moderated so distance fade reads as "haze" rather than "wall of
        // teal." Red still fades fastest; green and blue trail behind.
        private static readonly Color DefaultAbsorption = new Color(1.80f, 1.25f, 1.05f, 1f);
        private static readonly Color DefaultScatter = new Color(0.090f, 0.165f, 0.155f, 1f);
        private static readonly Color DefaultDeepWaterColor = new Color(0.012f, 0.022f, 0.026f, 1f);

        /// <summary>
        /// The exact ambient color the fog shader's far curtain converges to,
        /// mirrored in C# so other far-painting surfaces (the water surface
        /// underside's horizon curtain) can use the SAME color. Any mismatch
        /// shows as a band where the distant surface meets the fogged void.
        /// Keep in sync with ApplyDistanceLimit in UnderwaterDistanceFog.shader
        /// and ConfigureVolumetricRegrade below.
        /// </summary>
        internal static Color ComputeHorizonAmbientColor(float oceanSurfaceY)
        {
            float fogStrength = DeepWaters.Instance != null
                ? Mathf.Clamp01(DeepWaters.Instance.UnderwaterFogStrength)
                : 0.5f;

            Color dfuWater = DeepWaters.GetUnderwaterFogColor();
            float dfuLuma = dfuWater.r * 0.299f + dfuWater.g * 0.587f + dfuWater.b * 0.114f;
            Color dfuTeal = new Color(dfuLuma * 0.60f, dfuLuma * 0.95f, dfuLuma * 0.85f, 1f);
            Color scatter = Color.Lerp(DefaultScatter, dfuTeal, 0.25f);

            // Mirror the shader's camera depth darkening.
            float cameraY = GameManager.Instance != null && GameManager.Instance.MainCamera != null
                ? GameManager.Instance.MainCamera.transform.position.y
                : oceanSurfaceY;
            float cameraDepthBelow = Mathf.Max(0f, oceanSurfaceY - cameraY);
            float depthDarkeningEnd = Mathf.Clamp(
                (DeepWaters.Instance != null ? DeepWaters.Instance.WaterDepth : 200f) * 0.75f,
                DepthDarkeningEndMin,
                DepthDarkeningEndMax);
            float t = Mathf.Clamp01((cameraDepthBelow - DepthDarkeningStart) /
                                    Mathf.Max(0.001f, depthDarkeningEnd - DepthDarkeningStart));
            float depthDarkening = t * t * (3f - 2f * t);

            return Color.Lerp(scatter, DefaultDeepWaterColor,
                Mathf.Clamp01(depthDarkening * 0.85f + fogStrength * 0.18f));
        }

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

            return UnderwaterDistanceFog.TryGetUnderwaterPresentation(gameManager, targetCamera, out oceanSurfaceY);
        }

        private Material GetMaterial()
        {
            if (material != null)
                return material;

            // Prefer the source shader in the project so shader edits take
            // effect immediately during local iteration.
            Shader shader = Shader.Find("DeepWaters/UnderwaterDistanceFog");

            if (DeepWaters.Mod != null)
            {
                Shader bundled = DeepWaters.Mod.GetAsset<Shader>("UnderwaterDistanceFog.shader");
                if (shader == null)
                    shader = bundled;
            }

            if (shader == null)
            {
                Debug.LogError("[DeepWaters] DeepWaters/UnderwaterDistanceFog shader not found.");
                enabled = false;
                return null;
            }

            material = new Material(shader) { name = "DeepWaters.UnderwaterDistanceFog" };
            material.hideFlags = HideFlags.HideAndDontSave;
            Debug.Log("[DeepWaters.Visibility] imageEffectShader=" + shader.name + " supported=" + shader.isSupported);
            return material;
        }

        private void ConfigureMaterial(Material fogMaterial, float oceanSurfaceY)
        {
            float fogDistanceMultiplier = Mathf.Max(0.001f, DeepWaters.Instance.UnderwaterFogDistanceMultiplier);
            float fogStrength = Mathf.Clamp01(DeepWaters.Instance.UnderwaterFogStrength);
            float presentationFogStrength = fogStrength;
            float visionDistance = Mathf.Clamp(
                VisionDistanceAtDefaultSetting * fogDistanceMultiplier,
                MinimumVisionDistance,
                MaximumVisionDistance);
            float depthDarkeningEnd = Mathf.Clamp(
                DeepWaters.Instance.WaterDepth * 0.75f,
                DepthDarkeningEndMin,
                DepthDarkeningEndMax);

            fogMaterial.SetColor(FogColorProperty, DeepWaters.GetUnderwaterFogColor());
            fogMaterial.SetFloat(FogDensityProperty, Mathf.Lerp(DistanceCurveAtLowStrength, DistanceCurveAtFullStrength, presentationFogStrength));
            fogMaterial.SetFloat(FogStartProperty, FogStartDistance);
            fogMaterial.SetFloat(FogStrengthProperty, presentationFogStrength);
            fogMaterial.SetFloat(VisionDistanceProperty, visionDistance);
            fogMaterial.SetFloat(NearTintStrengthProperty, Mathf.Lerp(MinimumNearTint, NearTintAtFullStrength, presentationFogStrength));
            fogMaterial.SetFloat(DepthDarkeningStartProperty, DepthDarkeningStart);
            fogMaterial.SetFloat(DepthDarkeningEndProperty, depthDarkeningEnd);
            fogMaterial.SetFloat(WaterSurfaceYProperty, oceanSurfaceY);
            fogMaterial.SetFloat(CameraUnderwaterProperty, 1f);
            fogMaterial.SetFloat(SurfaceAlphaProperty, DeepWaters.Instance.WaterSurfaceBottomAlpha);
            fogMaterial.SetFloat(SurfaceTileWorldSizeProperty, DeepWaterWorld.TileWorldSize);
            fogMaterial.SetFloat(SurfaceTilingProperty, WaterSurfaceResources.SurfaceTextureTiling);
            fogMaterial.SetVector(SurfaceScrollProperty, SurfaceScroll);

            Texture surfaceTexture = WaterSurfaceResources.GetSurfaceTexture();
            if (surfaceTexture != null)
                fogMaterial.SetTexture(WaterSurfaceTextureProperty, surfaceTexture);

            ConfigureVolumetricRegrade(fogMaterial, presentationFogStrength);
            SetCameraRays(fogMaterial);
        }

        private static void ConfigureVolumetricRegrade(Material mat, float fogStrength)
        {
            mat.SetColor(AbsorptionColorProperty, DefaultAbsorption);
            // In-scatter color is a desaturated teal by default so the
            // overall cast feels like ocean water rather than a swimming-pool
            // tile. We mix in a small share of DFU's underwater-fog luma so
            // the ambient still reads as "DFU water" if a parent mod re-themes
            // it, but the cap on the blend keeps things from going saturated.
            Color dfuWater = DeepWaters.GetUnderwaterFogColor();
            float dfuLuma = dfuWater.r * 0.299f + dfuWater.g * 0.587f + dfuWater.b * 0.114f;
            Color dfuTeal = new Color(dfuLuma * 0.60f, dfuLuma * 0.95f, dfuLuma * 0.85f, 1f);
            Color scatter = Color.Lerp(DefaultScatter, dfuTeal, 0.25f);
            mat.SetColor(ScatterColorProperty, scatter);
            mat.SetFloat(ScatterStrengthProperty, Mathf.Lerp(0.85f, 1.30f, fogStrength));
            mat.SetColor(DeepWaterColorProperty, DefaultDeepWaterColor);
        }

        // Send NON-NORMALIZED world-space rays such that
        //   worldPos = cameraPos + ray * LinearEyeDepth(rawDepth)
        // gives the world position of the geometry at that screen UV.
        // The fragment shader normalizes when it needs a direction.
        private void SetCameraRays(Material fogMaterial)
        {
            if (targetCamera == null)
                return;

            float fovY = targetCamera.fieldOfView * Mathf.Deg2Rad;
            float aspect = targetCamera.aspect;
            float halfHeight = Mathf.Tan(fovY * 0.5f);
            float halfWidth = halfHeight * aspect;

            Transform t = targetCamera.transform;
            Vector3 forward = t.forward;
            Vector3 right = t.right;
            Vector3 up = t.up;

            Vector3 rayBL = forward + right * (-halfWidth) + up * (-halfHeight);
            Vector3 rayBR = forward + right * (+halfWidth) + up * (-halfHeight);
            Vector3 rayTL = forward + right * (-halfWidth) + up * (+halfHeight);
            Vector3 rayTR = forward + right * (+halfWidth) + up * (+halfHeight);

            fogMaterial.SetVector(RayBottomLeftProperty, RayVector(rayBL));
            fogMaterial.SetVector(RayBottomRightProperty, RayVector(rayBR));
            fogMaterial.SetVector(RayTopLeftProperty, RayVector(rayTL));
            fogMaterial.SetVector(RayTopRightProperty, RayVector(rayTR));
        }

        private static Vector4 RayVector(Vector3 worldRay)
        {
            return new Vector4(worldRay.x, worldRay.y, worldRay.z, 0f);
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
