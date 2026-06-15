// Project:         Iliac Puddle No More
// License:         MIT

using System.Collections.Generic;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.Serialization;
using UnityEngine;

namespace DeepWaters
{
    /// <summary>
    /// Outdoor swim driver — tricks DFU's PlayerEnterExit into running its dungeon
    /// swim logic outdoors by forging the dungeon/water state around its Update.
    /// Uses two MonoBehaviours with opposite execution orders to bracket the frame:
    /// this one forges before PlayerEnterExit.Update, OutdoorSwimDriverAfter
    /// restores after it (and after PlayerMotor.Update has overwritten the forged
    /// OnExteriorWater, re-forging it for the rest of the frame).
    ///
    /// Ownership model (the part that keeps the surface stable — don't fight DFU):
    ///  - DFU owns the swim flags. PlayerEnterExit recomputes IsPlayerSwimming and
    ///    LevitateMotor.IsSwimming every frame from the forged blockWaterLevel.
    ///  - DFU owns movement. LevitateMotor moves the swimmer, including its native
    ///    surface ascent cap, which sits just below PlayerEnterExit's swim
    ///    threshold so gravity never flaps at the waterline.
    ///  - DFU owns the pose. DecideHeightAction sinks on water entry from the
    ///    forged OnExteriorWater; the only intervention is KeepSurfaceCameraUnsunk,
    ///    which un-sinks the camera while the head is clear of the surface.
    ///  - The mod owns: the ocean surface level, the terrain-collider gate that
    ///    lets the player descend (no real terrain holes — they crash Unity
    ///    2019.4), underwater presentation (fog), boat suppression, shore exit,
    ///    and the swim extras in OutdoorSwimMovementController.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public class OutdoorSwimDriver : MonoBehaviour
    {
        private const short NoWaterSentinel = 10000;

        // Swim-state thresholds. The swim check point is the player's transform
        // plus DFU's classic offset (PlayerSwimCheckY); the player is swimming
        // while it is within these clearances of the waterline — i.e. mostly
        // submerged. Being above water (boat deck, shore) is naturally NOT
        // swimming, which is what makes boats work with no special-casing.
        // Match DFU's forged swim surface to the mod's exit threshold so shallow
        // shore slopes don't flap between DFU-swim and walk every other frame.
        private const float SwimEnterClearance = 0.10f;
        private const float SwimExitClearance = 0.75f;
        private const float SwimPhysicsSurfaceOffset = SwimExitClearance;

        // Presentation (underwater camera/fog) thresholds.
        private const float HeadDiveBelowSurface = 0.25f;
        private const float CameraEnterUnderwaterClearance = 0.04f;
        private const float CameraExitUnderwaterClearance = 0.08f;
        private const float FogCameraClearance = 0.65f;
        private const float OutdoorRenderFogDensityMin = 0.0f;
        private const float OutdoorRenderFogDensityFallbackMax = 0.0012f;
        private const float OutdoorRenderFogDensityCeiling = 0.030f;

        // Shore detection.
        private const float ShoreExitGraceSeconds = 0.85f;
        private const float ShoreAssistAfterWaterSeconds = 1.25f;
        private const float ShoreGroundProbeHeight = 1.25f;
        private const float ShoreGroundProbeDistance = 3.25f;
        private const float ShoreGroundOceanMargin = 0.50f;
        private const float ShoreTerrainStandClearance = 0.35f;

        // Player water-contact probing (TryGetPlayerWaterColumn).
        private const float WaterContactMinimumDepth = 0.15f;
        private const float WaterContactGraceSeconds = 1.25f;
        private const float WaterContactProbeRadiusMin = 0.35f;
        private const float WaterContactProbeRadiusMax = 0.75f;

        // Collider gate. The gate opens/closes on the player's FEET (capsule
        // bottom), not the transform: PlayerHeightChanger's sink/unsink shifts the
        // transform ±0.75 while changing the controller height, but the feet stay
        // put, so pose changes can't flap the gate. Open/close have hysteresis so
        // a surface floater can't sit exactly on the threshold.
        private const float ColliderGateOpenFeetMargin = 0.25f;
        private const float ColliderGateCloseFeetMargin = 1.00f;
        private const float ColliderGateShoreProbeRadiusMin = 2.50f;
        private const float ColliderGateShoreProbeRadiusMax = 4.00f;
        private const float ColliderGateShoreTerrainMargin = 0.35f;
        // The gate is a DISTANCE RING: every loaded ocean-water tile whose
        // footprint lies within this of the submerged player is disabled.
        // Pure transform/flag math, so the set cannot collapse during the
        // map-pixel streaming stalls that starve per-frame water lookups, and
        // no swim speed (multiplier + strokes) can outrun it between frames.
        private const float ColliderGateTileProximityMeters = 96f;
        private const float ColliderGateRefreshIntervalSeconds = 0.15f;
        private const float ColliderGateEjectGuardMargin = 0.5f;
        private const float ColliderGateEjectGuardPaddingMeters = 96f;

        // Boats.
        private const float BoatBoardingSurfaceClearance = 1.10f;
        private const float BoatSnapCooldownSeconds = 0.75f;
        private const string BoatEffectBundleName = "ImOnABoat";
        private const string ComeSailAwayBoatEffectBundleName = "I'm On A Boat";

        private readonly OutdoorSwimDfuBridge dfuBridge = new OutdoorSwimDfuBridge();
        private bool currentlyForged;
        private bool wasPlayerOnBoat;
        private float nextBoatSnapTime;
        private float shoreExitGraceUntil;
        private float shoreAssistAfterWaterUntil;
        private static bool headWaterStateInitialized;
        private static bool headPresentationUnderwater;

        private readonly List<TerrainCollider> disabledWaterTerrainColliders = new List<TerrainCollider>(12);
        private readonly List<TerrainCollider> desiredWaterTerrainColliders = new List<TerrainCollider>(12);
        private readonly List<DaggerfallTerrain> gateDfTerrainScratch = new List<DaggerfallTerrain>(80);
        private readonly List<Terrain> gateTerrainScratch = new List<Terrain>(80);
        private bool waterColliderGateActive;
        private int colliderGateRefreshFrame = -1;
        private float nextColliderGateRefreshTime;
        private float waterContactUntil;

        internal static bool DiagnosticWaterColliderGateActive { get; private set; }
        internal static int DiagnosticDisabledWaterColliderCount { get; private set; }
        internal static int DiagnosticDesiredWaterColliderCount { get; private set; }

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
            SaveLoadManager.OnStartLoad += OnSaveLoad;
            SaveLoadManager.OnLoad += OnSaveLoad;
        }

        void OnDestroy()
        {
            SaveLoadManager.OnStartLoad -= OnSaveLoad;
            SaveLoadManager.OnLoad -= OnSaveLoad;
        }

        void OnDisable()
        {
            ReleaseSwimMotorFrameSpikeGuard();
            RestoreWaterTerrainCollider(force: true);
        }

        private void OnSaveLoad(SaveData_v1 _)
        {
            RestoreWaterTerrainCollider(force: true);
            ClearOutdoorWaterState();
        }

        void Update()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame())
                return;

            PlayerEnterExit pex = gameManager.PlayerEnterExit;
            if (pex == null)
                return;

            if (pex.IsPlayerInside)
            {
                ReleaseSwimMotorFrameSpikeGuard();
                RestoreWaterTerrainCollider();
                if (currentlyForged)
                    Restore();
                return;
            }

            if (IsPlayerOnBoat())
            {
                RestoreWaterTerrainCollider();
                SuppressOutdoorSwimming(true);
                return;
            }
            wasPlayerOnBoat = false;

            float oceanSurfaceY = ComputeOceanSurfaceY();
            UpdateWaterTerrainColliderGate(oceanSurfaceY);
            bool standingOnShore = IsStandingOnShoreGround(oceanSurfaceY);
            bool hasWaterContact = HasRecentCenterWaterContact(oceanSurfaceY);
            bool isSwimming = hasWaterContact && !standingOnShore && IsPlayerAtSwimmingDepth(oceanSurfaceY);
            bool isPresentationUnderwater = hasWaterContact && !standingOnShore && IsPresentationUnderwater(oceanSurfaceY);

            if (!hasWaterContact &&
                (currentlyForged || pex.IsPlayerSwimming || Time.time < shoreAssistAfterWaterUntil) &&
                TryRecoverToVanillaTerrain(oceanSurfaceY))
            {
                MarkShoreExit();
                if (currentlyForged)
                    Restore();

                RequestStandAfterWaterExit();
                ClearCrouchAfterWaterExit();
                return;
            }

            if (isSwimming || isPresentationUnderwater)
                shoreAssistAfterWaterUntil = Time.time + ShoreAssistAfterWaterSeconds;

            GuardSwimMotorFrameSpike(isSwimming);

            if (standingOnShore)
                ResetHeadWaterState(false);

            if (isSwimming)
                DismountForSwimming();

            if (!DeepWaterRuntime.IsLoadGraceActive &&
                (isSwimming || Time.time < shoreAssistAfterWaterUntil) &&
                OutdoorShoreExitAssist.TryMoveToShore(pex, oceanSurfaceY, false, true))
            {
                MarkShoreExit();
                if (currentlyForged)
                    Restore();

                RequestStandAfterWaterExit();
                ClearCrouchAfterWaterExit();
                return;
            }

            if (!isSwimming && !isPresentationUnderwater)
            {
                if (currentlyForged)
                {
                    Restore();
                    RequestStandAfterWaterExit();
                }

                ClearCrouchAfterWaterExit();
                return;
            }

            Forge(pex, oceanSurfaceY, isSwimming);

            if (isPresentationUnderwater)
                ApplyUnderwaterPresentation(pex, oceanSurfaceY);
            else
                RestoreAboveSurfacePresentation(pex);

            if (isSwimming)
                KeepSurfaceCameraUnsunk(isPresentationUnderwater, oceanSurfaceY);
        }

        void FixedUpdate()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame())
                return;

            PlayerEnterExit pex = gameManager.PlayerEnterExit;
            if (pex == null || pex.IsPlayerInside)
                return;

            if (IsPlayerOnBoat())
            {
                SuppressOutdoorSwimming(false);
                return;
            }

            float oceanSurfaceY = ComputeOceanSurfaceY();
            bool standingOnShore = IsStandingOnShoreGround(oceanSurfaceY);
            bool hasWaterContact = HasRecentCenterWaterContact(oceanSurfaceY);
            bool isSwimming = hasWaterContact && !standingOnShore && IsPlayerAtSwimmingDepth(oceanSurfaceY);
            bool isPresentationUnderwater = hasWaterContact && !standingOnShore && IsPresentationUnderwater(oceanSurfaceY);

            if (!isSwimming && !isPresentationUnderwater)
                return;

            // Keep the forged water-audio state (blockWaterLevel, OnExteriorWater,
            // submerged flag) current across physics steps, where PlayerMotor's
            // FixedUpdate reads it for gravity suppression.
            ApplyDfuWaterAudioState(pex, oceanSurfaceY, isSwimming);
        }

        /// <summary>
        /// Runs after PlayerEnterExit.Update (via OutdoorSwimDriverAfter). Un-forges
        /// the dungeon flag so the rest of the game sees outdoors, then re-applies
        /// the water state — PlayerMotor.Update has overwritten OnExteriorWater by
        /// now, so it must be forged again for everything later in the frame.
        /// </summary>
        internal void PostPhaseRestore()
        {
            if (IsPlayerOnBoat())
            {
                if (currentlyForged)
                    Restore();

                ClearBoatSwimPose(false);
                return;
            }

            if (!currentlyForged)
                return;

            float oceanSurfaceY = ComputeOceanSurfaceY();
            UpdateWaterTerrainColliderGate(oceanSurfaceY);
            bool standingOnShore = IsStandingOnShoreGround(oceanSurfaceY);
            if (standingOnShore)
            {
                Restore();
                RequestStandAfterWaterExit();
                return;
            }

            var pex = GameManager.Instance.PlayerEnterExit;
            dfuBridge.RestoreDungeonState(pex);

            bool hasWaterContact = HasRecentCenterWaterContact(oceanSurfaceY);
            bool isSwimming = hasWaterContact && IsPlayerAtSwimmingDepth(oceanSurfaceY);
            bool presentationUnderwater = hasWaterContact && IsPresentationUnderwater(oceanSurfaceY);

            ApplyDfuWaterAudioState(pex, oceanSurfaceY, isSwimming);
            ApplyUnderwaterFogSettings(pex);
            pex.UnderwaterFog?.UpdateFog(presentationUnderwater ? PresentationWaterLevelForFog(oceanSurfaceY) : NoWaterSentinel);
            if (presentationUnderwater)
                ApplyNeutralUnderwaterFogColor();
            KeepSurfaceCameraUnsunk(presentationUnderwater, oceanSurfaceY);
        }

        #region Forge / restore

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
                WorldYToBlockWaterLevel(oceanSurfaceY + SwimPhysicsSurfaceOffset),
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
            // Crouch ("descend") presses while swimming can latch DFU's real
            // crouch state (PlayerMotor recomputes OnExteriorWater by raycast
            // before the late-phase re-forge, so the press reads as a crouch
            // toggle). Clear it for a short window after leaving the water.
            uncrouchAfterExitUntil = Time.time + UncrouchAfterExitSeconds;
        }

        private const float UncrouchAfterExitSeconds = 1.5f;
        private float uncrouchAfterExitUntil;

        private void ClearCrouchAfterWaterExit()
        {
            if (Time.time >= uncrouchAfterExitUntil)
                return;

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerMotor == null || !gameManager.PlayerMotor.IsCrouching)
                return;

            GameObject player = gameManager.PlayerObject;
            var heightChanger = player != null ? player.GetComponent<PlayerHeightChanger>() : null;
            if (heightChanger != null &&
                heightChanger.HeightAction != HeightChangeAction.DoUnsinking &&
                heightChanger.HeightAction != HeightChangeAction.DoStanding)
            {
                heightChanger.HeightAction = HeightChangeAction.DoStanding;
            }
        }

        /// <summary>Teardown for save loads: clear every forged/swim flag.</summary>
        private void ClearOutdoorWaterState()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerEnterExit == null)
                return;

            ResetHeadWaterState(false);
            ReleaseSwimMotorFrameSpikeGuard();
            if (currentlyForged)
            {
                Restore();
            }
            else
            {
                PlayerEnterExit pex = gameManager.PlayerEnterExit;
                pex.IsPlayerSwimming = false;
                dfuBridge.ApplyWaterAudioState(pex, NoWaterSentinel, PlayerMotor.OnExteriorWaterMethod.None, false);
            }

            RequestStandAfterWaterExit();
        }

        private static void RequestStandAfterWaterExit()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerObject == null || gameManager.PlayerMotor == null)
                return;

            var heightChanger = gameManager.PlayerObject.GetComponent<PlayerHeightChanger>();
            if (heightChanger == null)
                return;

            bool shouldUnsink = heightChanger.IsInWaterTile ||
                                heightChanger.HeightAction == HeightChangeAction.DoSinking;

            heightChanger.ForcedSwimCrouch = false;

            if (shouldUnsink && heightChanger.HeightAction != HeightChangeAction.DoUnsinking)
            {
                heightChanger.HeightAction = HeightChangeAction.DoUnsinking;
                return;
            }

            heightChanger.IsInWaterTile = false;

            if (gameManager.PlayerMotor.IsCrouching && heightChanger.HeightAction != HeightChangeAction.DoUnsinking)
                heightChanger.HeightAction = HeightChangeAction.DoStanding;
        }

        /// <summary>
        /// While the player's head is clear of the surface, keep the camera at the
        /// unsunk level so they can see above water. DFU's DecideHeightAction sinks
        /// the camera to swim level on (forged) water entry; this un-sinks it again
        /// whenever the player is at the surface rather than underwater.
        /// </summary>
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

        #endregion

        #region Swim state

        private bool IsPlayerAtSwimmingDepth(float oceanSurfaceY)
        {
            var player = GameManager.Instance?.PlayerObject;
            if (player == null)
                return false;

            float clearance = currentlyForged ? SwimExitClearance : SwimEnterClearance;
            return PlayerSwimCheckY(player.transform.position.y) < oceanSurfaceY + clearance;
        }

        private bool HasRecentCenterWaterContact(float oceanSurfaceY)
        {
            if (IsStandingOnShoreGround(oceanSurfaceY))
            {
                waterContactUntil = 0f;
                return false;
            }

            if (HasCenterWaterContact())
            {
                waterContactUntil = Time.time + WaterContactGraceSeconds;
                return true;
            }

            return Time.time < waterContactUntil;
        }

        private static bool HasCenterWaterContact()
        {
            GameObject player = GameManager.Instance?.PlayerObject;
            if (player == null)
                return false;

            Vector3 position = player.transform.position;
            DeepWaterColumn column;
            float carvedSeafloorY;
            return IsSwimmableWaterMask(position.x, position.z) &&
                   DeepWaterWorld.TryGetWaterColumn(position.x, position.z, out column) &&
                   column.Depth >= WaterContactMinimumDepth &&
                   DeepWaterWorld.TryGetCarvedSeafloorWorldY(position.x, position.z, out carvedSeafloorY);
        }

        private static bool IsSwimmableWaterMask(float worldX, float worldZ)
        {
            DaggerfallTerrain dfTerrain;
            Terrain terrain;
            if (!DeepWaterTerrainLookup.TryGetByWorldPosition(worldX, worldZ, out dfTerrain, out terrain) ||
                dfTerrain == null)
            {
                return false;
            }

            DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();
            if (tile == null || !tile.IsOceanConnected || !tile.HasDistanceField)
                return false;

            if (DeepWaterDistanceBake.HasFineWaterMask)
                return tile.IsCarvedWater(worldX, worldZ);

            float tileWorldSize = DeepWaterWorld.TileWorldSize;
            if (tileWorldSize <= 0f)
                return false;

            Vector3 origin = dfTerrain.transform.position;
            float fracX = (worldX - origin.x) / tileWorldSize;
            float fracZ = (worldZ - origin.z) / tileWorldSize;
            if (fracX < 0f || fracX > 1f || fracZ < 0f || fracZ > 1f)
                return false;

            return DeepWaterWaterClassification.IsLocalPointWater(dfTerrain.MapData, fracX, fracZ) ||
                   tile.IsBakedWater(worldX, worldZ);
        }

        private bool TryRecoverToVanillaTerrain(float oceanSurfaceY)
        {
            GameObject player = GameManager.Instance?.PlayerObject;
            if (player == null)
                return false;

            Vector3 position = player.transform.position;
            DaggerfallTerrain dfTerrain;
            Terrain terrain;
            if (!DeepWaterTerrainLookup.TryGetByWorldPosition(position.x, position.z, out dfTerrain, out terrain) ||
                terrain == null ||
                terrain.terrainData == null)
            {
                return false;
            }

            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            if (position.x < origin.x || position.x > origin.x + size.x ||
                position.z < origin.z || position.z > origin.z + size.z)
            {
                return false;
            }

            float terrainY = origin.y + terrain.SampleHeight(position);
            if (position.y > terrainY + ShoreTerrainStandClearance)
            {
                RestoreWaterTerrainCollider(force: true);
                return true;
            }

            player.transform.position = new Vector3(position.x, terrainY + ShoreTerrainStandClearance, position.z);
            Physics.SyncTransforms();
            RestoreWaterTerrainCollider(force: true);
            return true;
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
                   OutdoorShoreExitAssist.IsValidShoreStandingHit(hit, oceanSurfaceY);
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
            float headY = player.transform.position.y + (76 * MeshReader.GlobalScale) - 0.95f;
            return headY > oceanSurfaceY;
        }

        public static bool IsPresentationUnderwater(float oceanSurfaceY)
        {
            float cameraY = GetCameraY();
            bool shouldBeUnderwater = cameraY < oceanSurfaceY + CameraEnterUnderwaterClearance ||
                                      IsPlayerHeadUnderwaterForPresentation(oceanSurfaceY);
            if (!headWaterStateInitialized)
            {
                headPresentationUnderwater = shouldBeUnderwater;
                headWaterStateInitialized = true;
            }

            if (headPresentationUnderwater)
            {
                if (!shouldBeUnderwater && cameraY > oceanSurfaceY + CameraExitUnderwaterClearance)
                    headPresentationUnderwater = false;
            }
            else if (shouldBeUnderwater)
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

        private static bool IsPlayerHeadUnderwaterForPresentation(float oceanSurfaceY)
        {
            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            if (player == null)
                return false;

            float headY = player.transform.position.y + (76 * MeshReader.GlobalScale) - 0.95f;
            return headY < oceanSurfaceY - HeadDiveBelowSurface;
        }

        private static void ResetHeadWaterState(bool underwater)
        {
            headPresentationUnderwater = underwater;
            headWaterStateInitialized = false;
        }

        public static float ComputeOceanSurfaceY()
        {
            float oceanSurfaceY;
            return DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY) ? oceanSurfaceY : 0f;
        }

        #endregion

        #region Player water contact

        internal static bool TryGetPlayerWaterColumn(out DeepWaterColumn column)
        {
            column = new DeepWaterColumn();

            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            if (player == null)
                return false;

            Vector3 position = player.transform.position;
            if (TryGetUsableWaterColumn(position.x, position.z, out column))
                return true;

            float radius = 0.45f;
            var controller = player.GetComponent<CharacterController>();
            if (controller != null)
                radius = Mathf.Clamp(controller.radius * 0.85f, WaterContactProbeRadiusMin, WaterContactProbeRadiusMax);

            if (TryGetUsableWaterColumn(position.x + radius, position.z, out column) ||
                TryGetUsableWaterColumn(position.x - radius, position.z, out column) ||
                TryGetUsableWaterColumn(position.x, position.z + radius, out column) ||
                TryGetUsableWaterColumn(position.x, position.z - radius, out column))
            {
                return true;
            }

            Camera camera = gameManager != null ? gameManager.MainCamera : null;
            if (camera == null)
                return false;

            Vector3 forward = camera.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                return false;

            forward.Normalize();
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);
            return TryGetUsableWaterColumn(position.x + forward.x * radius, position.z + forward.z * radius, out column) ||
                   TryGetUsableWaterColumn(position.x - forward.x * radius, position.z - forward.z * radius, out column) ||
                   TryGetUsableWaterColumn(position.x + right.x * radius, position.z + right.z * radius, out column) ||
                   TryGetUsableWaterColumn(position.x - right.x * radius, position.z - right.z * radius, out column);
        }

        private static bool TryGetUsableWaterColumn(float worldX, float worldZ, out DeepWaterColumn column)
        {
            if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column))
                return false;

            float carvedSeafloorY;
            if (!DeepWaterWorld.TryGetCarvedSeafloorWorldY(worldX, worldZ, out carvedSeafloorY))
                return false;

            column.SeafloorWorldY = carvedSeafloorY;
            if (column.Parent != null)
                column.SeafloorLocalY = carvedSeafloorY - column.Parent.position.y;

            return column.Depth >= WaterContactMinimumDepth;
        }

        #endregion

        #region Presentation

        private void ApplyUnderwaterPresentation(PlayerEnterExit pex, float oceanSurfaceY)
        {
            ApplyUnderwaterFogSettings(pex);
            pex.UnderwaterFog?.UpdateFog(PresentationWaterLevelForFog(oceanSurfaceY));
            ApplyNeutralUnderwaterFogColor();
        }

        private static void ApplyUnderwaterFogSettings(PlayerEnterExit pex)
        {
            if (pex == null || pex.UnderwaterFog == null || DeepWaters.Instance == null)
                return;

            // Let DFU/Unity fog own rendered geometry. The image effect only
            // fills sky/no-depth gaps and applies a light underwater grade, so
            // using real scene fog avoids screen-space radius bands on terrain.
            float strength = Mathf.Clamp01(DeepWaters.Instance.UnderwaterFogStrength);
            float configuredDensity = DeepWaters.Instance.UnderwaterFogDensityMax;
            float fallbackDensity = Mathf.Lerp(0f, OutdoorRenderFogDensityFallbackMax, strength);
            pex.UnderwaterFog.fogDensityMin = OutdoorRenderFogDensityMin;
            pex.UnderwaterFog.fogDensityMax = Mathf.Clamp(
                Mathf.Max(configuredDensity, fallbackDensity),
                0f,
                OutdoorRenderFogDensityCeiling);
        }

        private static void ApplyNeutralUnderwaterFogColor()
        {
            if (DeepWaters.Instance == null)
                return;

            Color waterColor = DeepWaters.GetUnderwaterFogColor();
            float luma = waterColor.r * 0.299f + waterColor.g * 0.587f + waterColor.b * 0.114f;
            float strength = Mathf.Clamp01(DeepWaters.Instance.UnderwaterFogStrength);
            float neutral = Mathf.Lerp(luma * 0.90f, 0.045f, strength * 0.50f);
            RenderSettings.fogColor = new Color(neutral, neutral, neutral, waterColor.a);
        }

        private void RestoreAboveSurfacePresentation(PlayerEnterExit pex)
        {
            pex.UnderwaterFog?.UpdateFog(NoWaterSentinel);
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

        private static short WorldYToBlockWaterLevel(float worldY)
        {
            return (short)Mathf.Clamp(
                Mathf.Round(-worldY / MeshReader.GlobalScale),
                short.MinValue, short.MaxValue);
        }

        #endregion

        #region Boats

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

        // LevitateMotor moves the player by speed * Time.deltaTime, and the
        // catch-up frame after a map-pixel terrain stall arrives with a huge
        // deltaTime (~1-2s) — one frame of held input lurches the swimmer
        // 8-16m, far outside the collider gate's disabled ring and into an
        // enabled heightfield, which depenetrates them straight up to the
        // surface (the "shoot up when crossing map pixels" eject). Skip DFU's
        // swim movement on those frames by disabling the component before its
        // Update runs (this driver runs at order -32000); the next normal
        // frame re-enables it. Re-enabling here also heals a motor left
        // disabled by older builds.
        private const float MaxSwimFrameDeltaSeconds = 0.1f;
        private bool suppressedSwimMotorForFrameSpike;

        private void GuardSwimMotorFrameSpike(bool isSwimming)
        {
            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            LevitateMotor levitateMotor = player != null ? player.GetComponent<LevitateMotor>() : null;
            if (levitateMotor == null)
                return;

            bool spike = isSwimming &&
                         !levitateMotor.IsLevitating &&
                         Time.deltaTime > MaxSwimFrameDeltaSeconds;
            if (spike)
            {
                if (levitateMotor.enabled)
                {
                    levitateMotor.enabled = false;
                    suppressedSwimMotorForFrameSpike = true;
                }
            }
            else if (!levitateMotor.enabled)
            {
                levitateMotor.enabled = true;
                suppressedSwimMotorForFrameSpike = false;
            }
        }

        private void ReleaseSwimMotorFrameSpikeGuard()
        {
            if (!suppressedSwimMotorForFrameSpike)
                return;

            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            LevitateMotor levitateMotor = player != null ? player.GetComponent<LevitateMotor>() : null;
            if (levitateMotor != null && !levitateMotor.enabled)
                levitateMotor.enabled = true;

            suppressedSwimMotorForFrameSpike = false;
        }

        private void SuppressOutdoorSwimming(bool allowSnap)
        {
            bool justEnteredBoat = !wasPlayerOnBoat;
            ResetHeadWaterState(false);
            ReleaseSwimMotorFrameSpikeGuard();
            RestoreWaterTerrainCollider();
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
                bool shouldUnsink = heightChanger.IsInWaterTile ||
                                    heightChanger.HeightAction == HeightChangeAction.DoSinking;
                heightChanger.ForcedSwimCrouch = false;
                if (shouldUnsink && heightChanger.HeightAction != HeightChangeAction.DoUnsinking)
                    heightChanger.HeightAction = HeightChangeAction.DoUnsinking;

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

        #endregion

        #region Collider gate

        /// <summary>
        /// There are no real terrain holes at runtime (carving them crashes Unity
        /// 2019.4), so to let the player descend below the sea-level heightfield
        /// the gate disables the TerrainColliders of nearby water tiles while the
        /// player is at or below the surface; the carved seafloor mesh collider
        /// catches them at the seabed. The gate manages colliders ONLY — it has no
        /// vote in the swim-state decision.
        /// </summary>
        private void UpdateWaterTerrainColliderGate(float oceanSurfaceY)
        {
            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            if (player == null || gameManager.PlayerEnterExit == null || gameManager.PlayerEnterExit.IsPlayerInside)
            {
                RestoreWaterTerrainCollider();
                return;
            }

            Vector3 position = player.transform.position;
            if (PlayerFeetY(player, position) > oceanSurfaceY + CurrentGateFeetMargin())
            {
                RestoreWaterTerrainCollider();
                return;
            }

            if (ShouldThrottleColliderGateRefresh())
                return;

            // Unknown world state (terrain lookup starved during a map-pixel
            // crossing): FREEZE the gate exactly as it is. Restoring on missing
            // data is what used to eject swimmers mid-crossing; with a frozen
            // set the colliders the player relies on simply stay disabled until
            // the world is readable again.
            DaggerfallTerrain playerDfTerrain;
            Terrain playerTerrain;
            if (!DeepWaterTerrainLookup.TryGetByWorldPosition(position.x, position.z, out playerDfTerrain, out playerTerrain))
                return;

            // Swimmable-water check: only open the gate where a carved
            // seafloor quad actually exists beneath the player (real water
            // VOLUME, depth >= the swimmable minimum). Everywhere else —
            // beaches, shore fringes the carve rejected — the terrain stays
            // solid and is plain walkable/wading ground ("swimming in land"
            // guard). A transient failure here restores colliders, but the
            // padded eject guard holds anything a genuinely submerged player
            // is beneath.
            float carvedSeafloorY;
            if (!IsSwimmableWaterMask(position.x, position.z) ||
                !DeepWaterWorld.TryGetCarvedSeafloorWorldY(position.x, position.z, out carvedSeafloorY))
            {
                if (TryRecoverFromSolidShore(oceanSurfaceY, position))
                    return;

                RestoreWaterTerrainCollider();
                return;
            }

            if (IsNearColliderGateShore(position, oceanSurfaceY))
            {
                RestoreWaterTerrainCollider();
                return;
            }

            // Distance ring over this frame's live terrain snapshot.
            desiredWaterTerrainColliders.Clear();
            DeepWaterTerrainLookup.GetLoadedTerrains(gateDfTerrainScratch, gateTerrainScratch);
            float tileWorldSize = DeepWaterWorld.TileWorldSize;
            for (int i = 0; i < gateDfTerrainScratch.Count; i++)
            {
                DaggerfallTerrain dfTerrain = gateDfTerrainScratch[i];
                Terrain terrain = gateTerrainScratch[i];
                if (dfTerrain == null || terrain == null)
                    continue;

                DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();
                if (tile == null || !tile.IsOceanConnected || !tile.HasDistanceField)
                    continue;

                if (!TileFootprintWithinRadius(dfTerrain.transform.position, tileWorldSize, position, ColliderGateTileProximityMeters))
                    continue;

                TerrainCollider collider = terrain.GetComponent<TerrainCollider>();
                if (collider != null && !ContainsCollider(desiredWaterTerrainColliders, collider))
                    desiredWaterTerrainColliders.Add(collider);
            }

            for (int i = 0; i < desiredWaterTerrainColliders.Count; i++)
                DisableWaterTerrainCollider(desiredWaterTerrainColliders[i]);

            RestoreWaterTerrainCollidersExcept(desiredWaterTerrainColliders);
            waterColliderGateActive = disabledWaterTerrainColliders.Count > 0;
            UpdateColliderGateDiagnostics();
        }

        private bool ShouldThrottleColliderGateRefresh()
        {
            int frame = Time.frameCount;
            if (colliderGateRefreshFrame == frame)
                return true;

            float now = Time.unscaledTime;
            if (now < nextColliderGateRefreshTime)
                return true;

            colliderGateRefreshFrame = frame;
            nextColliderGateRefreshTime = now + ColliderGateRefreshIntervalSeconds;
            return false;
        }

        // XZ distance from the player to the tile's footprint (AABB), squared
        // comparison against the ring radius.
        private static bool TileFootprintWithinRadius(Vector3 tileOrigin, float tileWorldSize, Vector3 playerPosition, float radius)
        {
            float dx = Mathf.Max(Mathf.Max(tileOrigin.x - playerPosition.x, playerPosition.x - (tileOrigin.x + tileWorldSize)), 0f);
            float dz = Mathf.Max(Mathf.Max(tileOrigin.z - playerPosition.z, playerPosition.z - (tileOrigin.z + tileWorldSize)), 0f);
            return dx * dx + dz * dz <= radius * radius;
        }

        // The capsule bottom. Invariant under sink/unsink (those shift the
        // transform and the height together), unlike the transform itself.
        private static float PlayerFeetY(GameObject player, Vector3 position)
        {
            var controller = player.GetComponent<CharacterController>();
            return controller != null ? position.y - controller.height * 0.5f : position.y;
        }

        // Hysteresis: open near/below the waterline (also covers standing on the
        // sea-level heightfield so walking into deep water lets the player sink),
        // and once open, stay open until the feet are well above it.
        private float CurrentGateFeetMargin()
        {
            return waterColliderGateActive ? ColliderGateCloseFeetMargin : ColliderGateOpenFeetMargin;
        }

        private static float GetGateProbeRadius(float min, float max, float radiusScale)
        {
            GameObject player = GameManager.Instance != null ? GameManager.Instance.PlayerObject : null;
            CharacterController controller = player != null ? player.GetComponent<CharacterController>() : null;
            if (controller == null)
                return min;

            return Mathf.Clamp(controller.radius * radiusScale, min, max);
        }

        private static bool IsNearColliderGateShore(Vector3 position, float oceanSurfaceY)
        {
            if (IsSolidShoreForColliderGate(position.x, position.z, oceanSurfaceY))
                return true;

            float radius = GetGateProbeRadius(ColliderGateShoreProbeRadiusMin, ColliderGateShoreProbeRadiusMax, 5f);
            if (IsSolidShoreForColliderGate(position.x + radius, position.z, oceanSurfaceY) ||
                IsSolidShoreForColliderGate(position.x - radius, position.z, oceanSurfaceY) ||
                IsSolidShoreForColliderGate(position.x, position.z + radius, oceanSurfaceY) ||
                IsSolidShoreForColliderGate(position.x, position.z - radius, oceanSurfaceY))
            {
                return true;
            }

            float diagonal = radius * 0.70710678f;
            if (IsSolidShoreForColliderGate(position.x + diagonal, position.z + diagonal, oceanSurfaceY) ||
                IsSolidShoreForColliderGate(position.x + diagonal, position.z - diagonal, oceanSurfaceY) ||
                IsSolidShoreForColliderGate(position.x - diagonal, position.z + diagonal, oceanSurfaceY) ||
                IsSolidShoreForColliderGate(position.x - diagonal, position.z - diagonal, oceanSurfaceY))
            {
                return true;
            }

            Camera camera = GameManager.Instance != null ? GameManager.Instance.MainCamera : null;
            if (camera == null)
                return false;

            Vector3 forward = camera.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                return false;

            forward.Normalize();
            return IsSolidShoreForColliderGate(
                position.x + forward.x * radius,
                position.z + forward.z * radius,
                oceanSurfaceY);
        }

        private static bool IsSolidShoreForColliderGate(float worldX, float worldZ, float oceanSurfaceY)
        {
            DaggerfallTerrain dfTerrain;
            Terrain terrain;
            if (!DeepWaterTerrainLookup.TryGetByWorldPosition(worldX, worldZ, out dfTerrain, out terrain) ||
                dfTerrain == null ||
                terrain == null ||
                terrain.terrainData == null ||
                dfTerrain.MapData.heightmapSamples == null)
            {
                return false;
            }

            float tileWorldSize = DeepWaterWorld.TileWorldSize;
            if (tileWorldSize <= 0f)
                return false;

            Vector3 origin = dfTerrain.transform.position;
            float fracX = (worldX - origin.x) / tileWorldSize;
            float fracZ = (worldZ - origin.z) / tileWorldSize;
            if (fracX < 0f || fracX > 1f || fracZ < 0f || fracZ > 1f)
                return false;

            float terrainWorldY;
            if (!TrySampleVanillaTerrainWorldY(dfTerrain, terrain, fracX, fracZ, out terrainWorldY))
                return false;

            if (terrainWorldY < oceanSurfaceY - ColliderGateShoreTerrainMargin)
                return false;

            bool waterLike;
            if (DeepWaterDistanceBake.HasFineWaterMask)
            {
                waterLike = DeepWaterDistanceBake.IsCarvedWater(dfTerrain.MapPixelX, dfTerrain.MapPixelY, fracX, fracZ);
            }
            else
            {
                bool pureBakedWater =
                    DeepWaterWaterClassification.IsLocalPointPureWaterTile(dfTerrain.MapData, fracX, fracZ) &&
                    DeepWaterDistanceBake.IsWaterAt(dfTerrain.MapPixelX, dfTerrain.MapPixelY, fracX, fracZ);
                waterLike =
                    DeepWaterWaterClassification.IsLocalPointWater(dfTerrain.MapData, fracX, fracZ) ||
                    pureBakedWater;
            }

            return !waterLike || terrainWorldY > oceanSurfaceY + ColliderGateShoreTerrainMargin;
        }

        private bool TryRecoverFromSolidShore(float oceanSurfaceY, Vector3 position)
        {
            if (!IsSolidShoreForColliderGate(position.x, position.z, oceanSurfaceY))
                return false;

            waterContactUntil = 0f;
            return TryRecoverToVanillaTerrain(oceanSurfaceY);
        }

        private static bool TrySampleVanillaTerrainWorldY(
            DaggerfallTerrain dfTerrain,
            Terrain terrain,
            float fracX,
            float fracZ,
            out float worldY)
        {
            worldY = 0f;

            float[,] heights = dfTerrain.MapData.heightmapSamples;
            int rows = heights.GetLength(0);
            int cols = heights.GetLength(1);
            if (rows <= 0 || cols <= 0)
                return false;

            float sampleX = Mathf.Clamp01(fracX) * (cols - 1);
            float sampleZ = Mathf.Clamp01(fracZ) * (rows - 1);
            int x0 = Mathf.Clamp(Mathf.FloorToInt(sampleX), 0, cols - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(sampleZ), 0, rows - 1);
            int x1 = Mathf.Min(x0 + 1, cols - 1);
            int z1 = Mathf.Min(z0 + 1, rows - 1);
            float tx = sampleX - x0;
            float tz = sampleZ - z0;

            float h00 = heights[z0, x0];
            float h10 = heights[z0, x1];
            float h01 = heights[z1, x0];
            float h11 = heights[z1, x1];
            float h0 = Mathf.Lerp(h00, h10, tx);
            float h1 = Mathf.Lerp(h01, h11, tx);
            float normalizedHeight = Mathf.Lerp(h0, h1, tz);

            worldY = terrain.transform.position.y + normalizedHeight * terrain.terrainData.size.y;
            return true;
        }

        private void DisableWaterTerrainCollider(TerrainCollider collider)
        {
            if (collider == null)
                return;

            if (!ContainsCollider(disabledWaterTerrainColliders, collider))
                disabledWaterTerrainColliders.Add(collider);

            if (collider.enabled)
                collider.enabled = false;
        }

        private void RestoreWaterTerrainCollidersExcept(List<TerrainCollider> keepDisabled)
        {
            for (int i = disabledWaterTerrainColliders.Count - 1; i >= 0; i--)
            {
                TerrainCollider collider = disabledWaterTerrainColliders[i];
                if (collider == null)
                {
                    disabledWaterTerrainColliders.RemoveAt(i);
                    continue;
                }

                if (ContainsCollider(keepDisabled, collider))
                    continue;

                if (WouldEjectSubmergedPlayer(collider))
                    continue;

                if (!collider.enabled)
                    collider.enabled = true;
                disabledWaterTerrainColliders.RemoveAt(i);
            }
        }

        // True if re-enabling this water tile's heightfield collider would shove
        // the player upward — i.e. the player is within the tile's XZ footprint
        // and below its terrain surface. Re-enabling a heightfield under the
        // CharacterController makes Unity depenetrate it straight up to the
        // terrain surface (~ocean level), flinging the swimmer out of the water.
        // The map-pixel-cross collider churn would do exactly that without this
        // guard; the held collider is released once the player rises above it.
        private static bool WouldEjectSubmergedPlayer(TerrainCollider collider)
        {
            if (collider == null)
                return false;

            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            if (player == null)
                return false;

            Terrain terrain = collider.GetComponent<Terrain>();
            if (terrain == null || terrain.terrainData == null)
                return false;

            // Padded footprint: protect not just the tile the player is over,
            // but any tile they could swim INTO before the gate next succeeds.
            // During a map-pixel crossing the water-column lookups fail for
            // longer than the transient hold, the desired-collider set
            // collapses, and an unpadded guard releases the NEIGHBOR tile —
            // the player then swims across the boundary into its re-enabled
            // sea-level heightfield and gets depenetrated to the surface
            // ("shoot up when crossing map pixels"). The padding covers the
            // distance reachable during a worst-case stall (~2s of stroke
            // swimming). SampleHeight clamps to the tile edge for positions
            // outside the footprint, which is the right reference height.
            Vector3 position = player.transform.position;
            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            if (position.x < origin.x - ColliderGateEjectGuardPaddingMeters ||
                position.x > origin.x + size.x + ColliderGateEjectGuardPaddingMeters ||
                position.z < origin.z - ColliderGateEjectGuardPaddingMeters ||
                position.z > origin.z + size.z + ColliderGateEjectGuardPaddingMeters)
                return false;

            float terrainSurfaceY = origin.y + terrain.SampleHeight(position);
            return position.y < terrainSurfaceY + ColliderGateEjectGuardMargin;
        }

        // Guarded by default: a collider the player is submerged beneath is held
        // (left disabled) rather than re-enabled. force=true is for genuine
        // teardown (save load / disable) where the player is being repositioned.
        private void RestoreWaterTerrainCollider(bool force = false)
        {
            for (int i = disabledWaterTerrainColliders.Count - 1; i >= 0; i--)
            {
                TerrainCollider collider = disabledWaterTerrainColliders[i];
                if (collider == null)
                {
                    disabledWaterTerrainColliders.RemoveAt(i);
                    continue;
                }

                if (!force && WouldEjectSubmergedPlayer(collider))
                    continue;

                if (!collider.enabled)
                    collider.enabled = true;
                disabledWaterTerrainColliders.RemoveAt(i);
            }

            desiredWaterTerrainColliders.Clear();
            waterColliderGateActive = !force && disabledWaterTerrainColliders.Count > 0;
            UpdateColliderGateDiagnostics();
        }

        private void UpdateColliderGateDiagnostics()
        {
            DiagnosticWaterColliderGateActive = waterColliderGateActive;
            DiagnosticDisabledWaterColliderCount = disabledWaterTerrainColliders.Count;
            DiagnosticDesiredWaterColliderCount = desiredWaterTerrainColliders.Count;
        }

        private static bool ContainsCollider(List<TerrainCollider> colliders, TerrainCollider collider)
        {
            for (int i = 0; i < colliders.Count; i++)
            {
                if (colliders[i] == collider)
                    return true;
            }

            return false;
        }

        #endregion
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

    /// <summary>
    /// Swim extras layered on top of DFU's native swim movement. LevitateMotor
    /// owns the base movement (and the surface float/ascent cap that keeps the
    /// waterline stable); this controller only adds what the mod brings:
    ///  - the SwimSpeedMultiplier setting, applied as a walk-speed modifier so
    ///    DFU's own swim-speed math (and LevitateMotor) picks it up natively;
    ///  - the swim-stroke burst (Run key) with its fatigue cost;
    ///  - safety clamps so the collider gate can't let the player tunnel through
    ///    the seafloor or shore terrain that has no collider right now.
    /// </summary>
    public class OutdoorSwimMovementController : MonoBehaviour
    {
        private const float MultiplierEpsilon = 0.001f;
        private const float StrokeDuration = 0.48f;
        private const float StrokeCooldownSeconds = 0.90f;
        private const float StrokeExtraSpeedMultiplier = 2.65f;
        private const float StrokeFatigueCostFraction = 0.025f;
        private const int StrokeMinimumFatigueCost = 24;
        private const float StrokeTempoScaleExponent = 0.35f;
        // How far above the ocean surface the camera may rise from a stroke
        // before its upward component is cut, mirroring LevitateMotor's native
        // ascent cap so a stroke can't launch the player out of the water.
        private const float SurfaceUpwardCameraClearance = 0.35f;
        private const float SeafloorSwimFloorClearance = 0.18f;
        // Cap how far the shore clamp may lift the player per frame. A swimmer
        // descending into the shore only penetrates a little each frame; a large
        // gap means a transient "not carved" read over genuine deep water, where
        // yanking up would surface-pop them.
        private const float MaxShoreClampCorrection = 2.5f;
        // groundMotor.MoveWithMovingPlatform multiplies by Time.deltaTime, and the
        // catch-up frame after a map-pixel streaming stall has a huge deltaTime;
        // cap the stroke displacement to a normal frame's worth.
        private const float MaxSwimMoveDeltaSeconds = 0.1f;

        private PlayerSpeedChanger speedChanger;
        private PlayerGroundMotor groundMotor;
        private string walkSpeedModId;
        private float appliedMultiplier = 1f;
        private Vector3 strokeDirection;
        private float strokeTimeRemaining;
        private float currentStrokeDuration = StrokeDuration;
        private float nextStrokeTime;
        private bool wasStrokeButtonHeld;

        void OnDisable()
        {
            RemoveSpeedModifier();
            ResetStroke();
        }

        void Update()
        {
            if (!IsOutdoorSwimming() || DeepWaterRuntime.IsLoadGraceActive)
            {
                RemoveSpeedModifier();
                ResetStroke();
                return;
            }

            CachePlayerMotorParts();
            ApplySpeedMultiplier();
            HandleStrokeInput();
            ApplyStrokeMotion();
            ClampAboveRenderedSeafloor();
        }

        private void CachePlayerMotorParts()
        {
            GameObject player = GameManager.Instance != null ? GameManager.Instance.PlayerObject : null;
            if (player == null)
                return;

            PlayerSpeedChanger currentSpeedChanger = player.GetComponent<PlayerSpeedChanger>();
            if (currentSpeedChanger != speedChanger)
            {
                RemoveSpeedModifier();
                speedChanger = currentSpeedChanger;
            }

            groundMotor = player.GetComponent<PlayerGroundMotor>();
        }

        private void ApplySpeedMultiplier()
        {
            if (speedChanger == null)
                return;

            float multiplier = CurrentSwimSpeedMultiplier();
            if (Mathf.Abs(multiplier - 1f) <= MultiplierEpsilon)
            {
                RemoveSpeedModifier();
                return;
            }

            if (!string.IsNullOrEmpty(walkSpeedModId) &&
                Mathf.Abs(multiplier - appliedMultiplier) <= MultiplierEpsilon)
            {
                return;
            }

            RemoveSpeedModifier();
            if (speedChanger.AddWalkSpeedMod(out walkSpeedModId, multiplier, true))
                appliedMultiplier = multiplier;
        }

        private void RemoveSpeedModifier()
        {
            if (speedChanger != null && !string.IsNullOrEmpty(walkSpeedModId))
                speedChanger.RemoveSpeedMod(walkSpeedModId, false, true);

            walkSpeedModId = null;
            appliedMultiplier = 1f;
        }

        private void ResetStroke()
        {
            strokeTimeRemaining = 0f;
            currentStrokeDuration = StrokeDuration;
            wasStrokeButtonHeld = false;
        }

        private void HandleStrokeInput()
        {
            if (DeepWaters.Instance == null || !DeepWaters.Instance.EnableSwimStroke)
                return;

            if (!ConsumeStrokeInputEdge() || Time.time < nextStrokeTime)
                return;

            Vector3 direction;
            if (!TryResolveStrokeDirection(out direction))
                return;

            var playerEntity = GameManager.Instance != null ? GameManager.Instance.PlayerEntity : null;
            if (playerEntity == null)
                return;

            int fatigueCost = Mathf.Max(
                StrokeMinimumFatigueCost,
                Mathf.CeilToInt(playerEntity.MaxFatigue * StrokeFatigueCostFraction));
            if (playerEntity.CurrentFatigue < fatigueCost)
                return;

            playerEntity.DecreaseFatigue(fatigueCost);
            strokeDirection = direction;
            float tempoScale = CurrentStrokeTempoScale();
            currentStrokeDuration = StrokeDuration / tempoScale;
            strokeTimeRemaining = currentStrokeDuration;
            nextStrokeTime = Time.time + StrokeCooldownSeconds / tempoScale;
        }

        private bool ConsumeStrokeInputEdge()
        {
            InputManager input = InputManager.Instance;
            if (input == null)
            {
                wasStrokeButtonHeld = false;
                return false;
            }

            bool held = input.HasAction(InputManager.Actions.Run);
            bool changed = held != wasStrokeButtonHeld;
            wasStrokeButtonHeld = held;
            return changed;
        }

        private void ApplyStrokeMotion()
        {
            if (strokeTimeRemaining <= 0f || groundMotor == null || speedChanger == null)
                return;

            float duration = Mathf.Max(0.01f, currentStrokeDuration);
            float normalizedTime = Mathf.Clamp01(strokeTimeRemaining / duration);
            float fade = Mathf.SmoothStep(0f, 1f, normalizedTime);
            // Base swim speed already includes the speed-multiplier walk mod via
            // GetBaseSpeed, so the multiplier must not be applied again here.
            float swimSpeed = speedChanger.GetSwimSpeed(speedChanger.GetBaseSpeed());
            groundMotor.MoveWithMovingPlatform(strokeDirection * swimSpeed * StrokeExtraSpeedMultiplier * CurrentStrokeTempoScale() * fade * SwimMoveDeltaScale());

            strokeTimeRemaining = Mathf.Max(0f, strokeTimeRemaining - Time.deltaTime);
        }

        private static float SwimMoveDeltaScale()
        {
            float dt = Time.deltaTime;
            if (dt <= MaxSwimMoveDeltaSeconds || dt <= 1e-5f)
                return 1f;

            return MaxSwimMoveDeltaSeconds / dt;
        }

        private static float CurrentSwimSpeedMultiplier()
        {
            return DeepWaters.Instance != null
                ? Mathf.Clamp(DeepWaters.Instance.SwimSpeedMultiplier, 0.25f, 30f)
                : 1f;
        }

        private static float CurrentStrokeTempoScale()
        {
            return Mathf.Max(0.5f, Mathf.Pow(CurrentSwimSpeedMultiplier(), StrokeTempoScaleExponent));
        }

        private static bool TryResolveStrokeDirection(out Vector3 direction)
        {
            direction = Vector3.zero;

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.MainCamera == null)
                return false;

            InputManager input = InputManager.Instance;
            float inputX = input.Horizontal;
            float inputY = input.Vertical;
            bool hasMoveInput = Mathf.Abs(inputX) > 0.001f || Mathf.Abs(inputY) > 0.001f;

            Transform cameraTransform = gameManager.MainCamera.transform;
            if (hasMoveInput)
            {
                float inputModifyFactor = inputX != 0f && inputY != 0f && gameManager.PlayerMotor != null && gameManager.PlayerMotor.limitDiagonalSpeed
                    ? 0.7071f
                    : 1f;
                direction = cameraTransform.TransformDirection(new Vector3(inputX * inputModifyFactor, 0f, inputY * inputModifyFactor));
                direction.y = 0f;
            }
            else
            {
                direction = cameraTransform.forward;
            }

            if (input.HasAction(InputManager.Actions.Jump) || input.HasAction(InputManager.Actions.FloatUp))
                direction.y += 0.65f;
            else if (input.HasAction(InputManager.Actions.Crouch) || input.HasAction(InputManager.Actions.FloatDown))
                direction.y -= 0.65f;

            float oceanSurfaceY;
            if (direction.y > 0f &&
                DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY) &&
                cameraTransform.position.y > oceanSurfaceY + SurfaceUpwardCameraClearance)
            {
                direction.y = 0f;
            }

            if (direction.sqrMagnitude < 0.001f)
                return false;

            direction.Normalize();
            return true;
        }

        #region Anti-tunnel clamps

        private static void ClampAboveRenderedSeafloor()
        {
            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            if (player == null)
                return;

            Vector3 position = player.transform.position;

            // Clamp to the seabed sub-mesh ONLY where one was actually carved.
            // TryGetWaterColumn trusts the bake water mask, but shore cells the
            // bake marks water — yet whose live heightmap has relief — are never
            // carved, so there's no hole and no sub-mesh under them. Detecting
            // the real mesh quad (vs "bake says water") is what tells swimmable
            // water apart from solid shore.
            float seafloorWorldY;
            if (DeepWaterWorld.TryGetCarvedSeafloorWorldY(position.x, position.z, out seafloorWorldY))
            {
                ClampPlayerAboveY(player, position, seafloorWorldY + SeafloorSwimFloorClearance);
                return;
            }

            // No carved seabed here: shore / submerged land. The collider gate
            // disables whole water tiles (their land cells included), which would
            // otherwise let the player swim straight down through the adjacent
            // shore. Keep them above the vanilla terrain.
            ClampAboveVanillaTerrain(player, position);
        }

        private static void ClampAboveVanillaTerrain(GameObject player, Vector3 position)
        {
            DaggerfallTerrain dfTerrain;
            Terrain terrain;
            if (!DeepWaterTerrainLookup.TryGetByWorldPosition(position.x, position.z, out dfTerrain, out terrain) ||
                dfTerrain == null ||
                terrain == null ||
                terrain.terrainData == null)
                return;

            // Only act once the tile is fully initialized. A tile mid-promotion
            // can't resolve its water column yet and would read as "not carved
            // water" everywhere.
            DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();
            if (tile == null || !tile.HasDistanceField)
                return;

            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            if (position.x < origin.x || position.x > origin.x + size.x ||
                position.z < origin.z || position.z > origin.z + size.z)
                return;

            float minimumY = origin.y + terrain.SampleHeight(position) + SeafloorSwimFloorClearance;
            if (position.y >= minimumY)
                return;

            if (minimumY - position.y > MaxShoreClampCorrection)
                return;

            ClampPlayerAboveY(player, position, minimumY);
        }

        private static void ClampPlayerAboveY(GameObject player, Vector3 position, float minimumY)
        {
            if (position.y >= minimumY)
                return;

            player.transform.position = new Vector3(position.x, minimumY, position.z);
            Physics.SyncTransforms();
        }

        #endregion

        private static bool IsOutdoorSwimming()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || !gameManager.IsPlayingGame())
                return false;

            if (gameManager.PlayerMotor == null ||
                gameManager.PlayerEnterExit == null ||
                gameManager.PlayerEntity == null ||
                gameManager.PlayerEntity.IsWaterWalking)
            {
                return false;
            }

            if (gameManager.PlayerEnterExit.IsPlayerInside)
                return false;

            float oceanSurfaceY;
            if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY))
                return false;

            if (gameManager.PlayerMotor.OnExteriorWater == PlayerMotor.OnExteriorWaterMethod.Swimming)
                return true;

            if (gameManager.PlayerEnterExit.IsPlayerSwimming)
                return true;

            return OutdoorSwimDriver.IsPresentationUnderwater(oceanSurfaceY);
        }
    }
}
