// Project:         Iliac Puddle No More
// License:         MIT

using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.Serialization;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Outdoor swim driver — tricks DFU's PlayerEnterExit into running its dungeon swim logic outdoors.
    /// Uses two MonoBehaviours with opposite execution orders to bracket PlayerEnterExit.Update's frame.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public class OutdoorSwimDriver : MonoBehaviour
    {
        private const short NoWaterSentinel = 10000;
        private const float SwimEnterClearance = 0.10f;
        private const float SwimExitClearance = 0.75f;
        private const float SwimPhysicsSurfaceOffset = 0.55f;
        private const float HeadDiveBelowSurface = 0.25f;
        private const float CameraEnterUnderwaterClearance = 0.04f;
        private const float CameraExitUnderwaterClearance = 0.08f;
        private const float FogCameraClearance = 0.65f;
        private const float ShoreExitGraceSeconds = 0.85f;
        private const float ShoreGroundProbeHeight = 1.25f;
        private const float ShoreGroundProbeDistance = 3.25f;
        private const float ShoreGroundOceanMargin = 0.50f;
        private const float BoatBoardingSurfaceClearance = 1.10f;
        private const float BoatSnapCooldownSeconds = 0.75f;
        private const string BoatEffectBundleName = "ImOnABoat";
        private const string ComeSailAwayBoatEffectBundleName = "I'm On A Boat";

        private readonly OutdoorSwimDfuBridge dfuBridge = new OutdoorSwimDfuBridge();
        private bool currentlyForged;
        private bool wasPlayerOnBoat;
        private float nextBoatSnapTime;
        private float shoreExitGraceUntil;
        private static bool headWaterStateInitialized;
        private static bool headPresentationUnderwater;

        public static OutdoorSwimDriver Install(GameObject host)
        {
            var driver = host.GetComponent<OutdoorSwimDriver>() ?? host.AddComponent<OutdoorSwimDriver>();
            var after = host.GetComponent<OutdoorSwimDriverAfter>() ?? host.AddComponent<OutdoorSwimDriverAfter>();
            after.owner = driver;
            return driver;
        }

        void Awake()
        {
            if (!dfuBridge.Initialize())
            {
                Debug.LogError("[DeepWaters] Reflection failed: Could not find required private fields in PlayerEnterExit or PlayerMotor. This mod version is likely incompatible with your Daggerfall Unity version.");
                enabled = false;
            }
        }

        void OnEnable()
        {
            SaveLoadManager.OnStartLoad += OnStartLoad;
        }

        void OnDestroy()
        {
            SaveLoadManager.OnStartLoad -= OnStartLoad;
        }

        private void OnStartLoad(SaveData_v1 _)
        {
            if (currentlyForged) Restore();
        }

        void Update()
        {
            if (GameManager.Instance == null || !GameManager.Instance.IsPlayingGame())
                return;

            if (GameManager.Instance.PlayerEnterExit.IsPlayerInside)
            {
                HandleIndoors();
                return;
            }

            if (IsPlayerOnBoat())
            {
                SuppressOutdoorSwimming(true);
                return;
            }
            wasPlayerOnBoat = false;

            float oceanSurfaceY = ComputeOceanSurfaceY();
            bool standingOnShore = IsStandingOnShoreGround(oceanSurfaceY);
            bool isSwimming = !standingOnShore && IsPlayerAtSwimmingDepth(oceanSurfaceY);
            bool isPresentationUnderwater = !standingOnShore && IsPresentationUnderwater(oceanSurfaceY);

            if (standingOnShore)
                ResetHeadWaterState(false);

            if (isSwimming)
                DismountForSwimming();

            if (!isSwimming && !isPresentationUnderwater)
            {
                if (currentlyForged)
                {
                    Restore();
                    RequestStandAfterWaterExit();
                }
                return;
            }

            var pex = GameManager.Instance.PlayerEnterExit;
            Forge(pex, oceanSurfaceY, isSwimming);

            if (isPresentationUnderwater)
                ApplyUnderwaterPresentation(pex, oceanSurfaceY);
            else
                RestoreAboveSurfacePresentation(pex);

            if (isSwimming)
            {
                KeepSurfaceCameraUnsunk(isPresentationUnderwater, oceanSurfaceY);
                if (OutdoorShoreExitAssist.TryMoveToShore(pex, oceanSurfaceY))
                {
                    MarkShoreExit();
                    if (currentlyForged)
                    {
                        Restore();
                        RequestStandAfterWaterExit();
                    }
                }
            }
        }

        void FixedUpdate()
        {
            if (GameManager.Instance == null || !GameManager.Instance.IsPlayingGame())
                return;

            if (GameManager.Instance.PlayerEnterExit.IsPlayerInside)
                return;

            if (IsPlayerOnBoat())
            {
                SuppressOutdoorSwimming(false);
                return;
            }

            float oceanSurfaceY = ComputeOceanSurfaceY();
            bool standingOnShore = IsStandingOnShoreGround(oceanSurfaceY);
            bool isSwimming = !standingOnShore && IsPlayerAtSwimmingDepth(oceanSurfaceY);
            bool isPresentationUnderwater = !standingOnShore && IsPresentationUnderwater(oceanSurfaceY);
            if (!isSwimming && !isPresentationUnderwater)
                return;

            ApplyDfuWaterAudioState(GameManager.Instance.PlayerEnterExit, oceanSurfaceY, isSwimming);
        }

        private void HandleIndoors()
        {
            // If we were swimming and entered a building, restore swim state.
            if (currentlyForged) Restore();
        }

        private void SuppressOutdoorSwimming(bool allowSnap)
        {
            bool justEnteredBoat = !wasPlayerOnBoat;
            ResetHeadWaterState(false);
            if (currentlyForged)
                Restore();

            ClearBoatSwimPose(allowSnap && justEnteredBoat);
            if (allowSnap)
                wasPlayerOnBoat = true;
        }

        private void ClearBoatSwimPose(bool snapAboveSurface)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null)
                return;

            PlayerEnterExit pex = gameManager.PlayerEnterExit;
            if (pex != null)
            {
                pex.IsPlayerSwimming = false;
                dfuBridge.ApplyWaterAudioState(pex, NoWaterSentinel, PlayerMotor.OnExteriorWaterMethod.None, false);
            }

            GameObject player = gameManager.PlayerObject;
            if (player == null)
                return;

            var heightChanger = player.GetComponent<PlayerHeightChanger>();
            if (heightChanger != null)
            {
                heightChanger.ForcedSwimCrouch = false;
                if ((heightChanger.IsInWaterTile || heightChanger.HeightAction == HeightChangeAction.DoSinking) &&
                    heightChanger.HeightAction != HeightChangeAction.DoUnsinking)
                {
                    heightChanger.HeightAction = HeightChangeAction.DoUnsinking;
                }

                heightChanger.IsInWaterTile = false;
            }

            if (snapAboveSurface)
                TrySnapPlayerAboveWaterSurface(player);
        }

        private void TrySnapPlayerAboveWaterSurface(GameObject player)
        {
            if (Time.time < nextBoatSnapTime)
                return;

            float oceanSurfaceY;
            if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY))
                return;

            Vector3 position = player.transform.position;
            float targetY = oceanSurfaceY + BoatBoardingSurfaceClearance;
            if (position.y >= targetY)
                return;

            player.transform.position = new Vector3(position.x, targetY, position.z);
            nextBoatSnapTime = Time.time + BoatSnapCooldownSeconds;
        }

        private static bool IsPlayerOnBoat()
        {
            var manager = GameManager.Instance != null ? GameManager.Instance.PlayerEffectManager : null;
            if (manager == null)
                return false;

            LiveEffectBundle[] bundles = manager.EffectBundles;
            if (bundles == null)
                return false;

            for (int i = 0; i < bundles.Length; i++)
            {
                LiveEffectBundle bundle = bundles[i];
                if (bundle != null && IsBoatEffectBundle(bundle.name))
                    return true;
            }

            return false;
        }

        private static bool IsBoatEffectBundle(string bundleName)
        {
            return bundleName == BoatEffectBundleName ||
                   bundleName == ComeSailAwayBoatEffectBundleName;
        }

        private static void DismountForSwimming()
        {
            TransportManager transportManager = GameManager.Instance.TransportManager;
            if (transportManager == null)
                return;

            if (transportManager.TransportMode == TransportModes.Horse ||
                transportManager.TransportMode == TransportModes.Cart)
            {
                transportManager.TransportMode = TransportModes.Foot;
            }
        }

        private void Forge(PlayerEnterExit pex, float oceanSurfaceY, bool isSwimming)
        {
            dfuBridge.ForgeDungeonState(pex);
            ApplyDfuWaterAudioState(pex, oceanSurfaceY, isSwimming);
            currentlyForged = true;
        }

        private void ApplyDfuWaterAudioState(PlayerEnterExit pex, float oceanSurfaceY, bool isSwimming)
        {
            dfuBridge.ApplyWaterAudioState(
                pex,
                WorldYToBlockWaterLevel(SwimPhysicsWaterY(oceanSurfaceY)),
                isSwimming ? PlayerMotor.OnExteriorWaterMethod.Swimming : PlayerMotor.OnExteriorWaterMethod.None,
                IsPlayerHeadUnderwater(oceanSurfaceY));
        }
        
        private void Restore()
        {
            var pex = GameManager.Instance.PlayerEnterExit;
            dfuBridge.RestoreDungeonState(pex);
            dfuBridge.ApplyWaterAudioState(pex, NoWaterSentinel, PlayerMotor.OnExteriorWaterMethod.None, false);
            pex.UnderwaterFog?.UpdateFog(NoWaterSentinel);
            currentlyForged = false;
            ResetHeadWaterState(false);
        }

        private static void RequestStandAfterWaterExit()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerObject == null || gameManager.PlayerMotor == null)
                return;

            var heightChanger = gameManager.PlayerObject.GetComponent<PlayerHeightChanger>();
            if (heightChanger == null)
                return;

            heightChanger.ForcedSwimCrouch = false;
            heightChanger.IsInWaterTile = false;

            if (heightChanger.HeightAction == HeightChangeAction.DoSinking)
            {
                heightChanger.HeightAction = HeightChangeAction.DoUnsinking;
                return;
            }

            if (gameManager.PlayerMotor.IsCrouching && heightChanger.HeightAction != HeightChangeAction.DoUnsinking)
                heightChanger.HeightAction = HeightChangeAction.DoStanding;
        }

        private void ApplyUnderwaterPresentation(PlayerEnterExit pex, float oceanSurfaceY)
        {
            ApplyUnderwaterFogSettings(pex);
            pex.UnderwaterFog?.UpdateFog(PresentationWaterLevelForFog(oceanSurfaceY));
        }

        private static void ApplyUnderwaterFogSettings(PlayerEnterExit pex)
        {
            if (pex == null || pex.UnderwaterFog == null || DeepWaters.Instance == null)
                return;

            pex.UnderwaterFog.fogDensityMax = DeepWaters.Instance.UnderwaterFogDensityMax;
        }

        private void RestoreAboveSurfacePresentation(PlayerEnterExit pex)
        {
            pex.UnderwaterFog?.UpdateFog(NoWaterSentinel);
        }

        private void KeepSurfaceCameraUnsunk(bool presentationUnderwater, float oceanSurfaceY)
        {
            if (presentationUnderwater)
                return;

            var player = GameManager.Instance?.PlayerObject;
            if (player == null)
                return;

            if (!IsPlayerHeadClearOfSurface(player, oceanSurfaceY))
                return;

            var heightChanger = player.GetComponent<PlayerHeightChanger>();
            if (heightChanger == null)
                return;

            heightChanger.ForcedSwimCrouch = false;
            if ((heightChanger.IsInWaterTile || heightChanger.HeightAction == HeightChangeAction.DoSinking) &&
                heightChanger.HeightAction != HeightChangeAction.DoUnsinking)
            {
                heightChanger.HeightAction = HeightChangeAction.DoUnsinking;
            }
        }
        
        private bool IsPlayerAtSwimmingDepth(float oceanSurfaceY)
        {
            var player = GameManager.Instance?.PlayerObject;
            if (player == null)
                return false;

            float clearance = currentlyForged ? SwimExitClearance : SwimEnterClearance;
            return PlayerSwimCheckY(player.transform.position.y) < oceanSurfaceY + clearance;
        }

        private bool IsStandingOnShoreGround(float oceanSurfaceY)
        {
            if (Time.time < shoreExitGraceUntil)
                return true;

            var controller = GameManager.Instance?.PlayerController;
            if (controller == null)
                return false;

            Vector3 probe = controller.transform.position + Vector3.up * ShoreGroundProbeHeight;
            RaycastHit hit;
            if (!Physics.Raycast(probe, Vector3.down, out hit, ShoreGroundProbeDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return false;

            return hit.point.y >= oceanSurfaceY - ShoreGroundOceanMargin &&
                   OutdoorShoreExitAssist.IsShoreGround(hit.collider);
        }

        private void MarkShoreExit()
        {
            shoreExitGraceUntil = Time.time + ShoreExitGraceSeconds;
            ResetHeadWaterState(false);
        }

        private static float PlayerSwimCheckY(float playerY)
        {
            return playerY + (50 * MeshReader.GlobalScale) - 0.95f;
        }

        private bool IsPlayerHeadUnderwater(float oceanSurfaceY)
        {
            var player = GameManager.Instance?.PlayerObject;
            if (player == null)
                return false;

            float headY = player.transform.position.y + (76 * MeshReader.GlobalScale) - 0.95f;
            return headY < oceanSurfaceY - HeadDiveBelowSurface;
        }

        private static bool IsPlayerHeadClearOfSurface(GameObject player, float oceanSurfaceY)
        {
            if (player == null)
                return false;

            float headY = player.transform.position.y + (76 * MeshReader.GlobalScale) - 0.95f;
            return headY > oceanSurfaceY;
        }

        public static bool IsPresentationUnderwater(float oceanSurfaceY)
        {
            float cameraY = GetCameraY();
            if (!headWaterStateInitialized)
            {
                headPresentationUnderwater = ShouldEnterUnderwaterPresentation(cameraY, oceanSurfaceY);
                headWaterStateInitialized = true;
            }

            if (headPresentationUnderwater)
            {
                if (cameraY > oceanSurfaceY + CameraExitUnderwaterClearance)
                {
                    headPresentationUnderwater = false;
                }
            }
            else if (ShouldEnterUnderwaterPresentation(cameraY, oceanSurfaceY))
            {
                headPresentationUnderwater = true;
            }

            return headPresentationUnderwater;
        }

        private static float GetCameraY()
        {
            var cam = GameManager.Instance?.MainCamera;
            return cam != null ? cam.transform.position.y : float.PositiveInfinity;
        }

        private static bool ShouldEnterUnderwaterPresentation(float cameraY, float oceanSurfaceY)
        {
            return cameraY < oceanSurfaceY + CameraEnterUnderwaterClearance;
        }

        private static void ResetHeadWaterState(bool underwater)
        {
            headPresentationUnderwater = underwater;
            headWaterStateInitialized = false;
        }
        
        internal void PostPhaseRestore()
        {
            if (IsPlayerOnBoat())
            {
                if (currentlyForged)
                    Restore();

                ClearBoatSwimPose(false);
                return;
            }

            if (!currentlyForged) return;

            float oceanSurfaceY = ComputeOceanSurfaceY();
            bool standingOnShore = IsStandingOnShoreGround(oceanSurfaceY);
            if (standingOnShore)
            {
                Restore();
                RequestStandAfterWaterExit();
                return;
            }

            var pex = GameManager.Instance.PlayerEnterExit;
            dfuBridge.RestoreDungeonState(pex);

            bool isSwimming = IsPlayerAtSwimmingDepth(oceanSurfaceY);
            bool presentationUnderwater = IsPresentationUnderwater(oceanSurfaceY);

            ApplyDfuWaterAudioState(pex, oceanSurfaceY, isSwimming);
            ApplyUnderwaterFogSettings(pex);
            pex.UnderwaterFog?.UpdateFog(presentationUnderwater ? PresentationWaterLevelForFog(oceanSurfaceY) : NoWaterSentinel);
            KeepSurfaceCameraUnsunk(presentationUnderwater, oceanSurfaceY);
        }
        
        public static float ComputeOceanSurfaceY()
        {
            float oceanSurfaceY;
            return DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY) ? oceanSurfaceY : 0f;
        }
        
        private static short WorldYToBlockWaterLevel(float worldY)
        {
            return (short)Mathf.Clamp(
                Mathf.Round(-worldY / MeshReader.GlobalScale),
                short.MinValue, short.MaxValue);
        }

        private static float SwimPhysicsWaterY(float oceanSurfaceY)
        {
            return oceanSurfaceY + SwimPhysicsSurfaceOffset;
        }

        private static short PresentationWaterLevelForFog(float oceanSurfaceY)
        {
            var cam = GameManager.Instance?.MainCamera;
            if (cam == null)
                return WorldYToBlockWaterLevel(oceanSurfaceY);

            // Vanilla UnderwaterFog keys off camera height. Third-person cameras can sit
            // above the player while the player's head is underwater, so lift the fog's
            // effective waterline just enough that the active camera still receives fog.
            float adjustedCameraY = cam.transform.position.y + (50 * MeshReader.GlobalScale) - 0.95f;
            float cameraFogWaterY = adjustedCameraY - FogCameraClearance;
            return WorldYToBlockWaterLevel(Mathf.Max(oceanSurfaceY, cameraFogWaterY));
        }
    }

    /// <summary>
    /// Companion that runs after PlayerEnterExit.Update to restore state.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public class OutdoorSwimDriverAfter : MonoBehaviour
    {
        internal OutdoorSwimDriver owner;
        void Update() { owner?.PostPhaseRestore(); }
    }
}
