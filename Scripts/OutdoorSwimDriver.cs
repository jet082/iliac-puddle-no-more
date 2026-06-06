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
    /// Outdoor swim driver — tricks DFU's PlayerEnterExit into running its dungeon swim logic outdoors.
    /// Uses two MonoBehaviours with opposite execution orders to bracket PlayerEnterExit.Update's frame.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public class OutdoorSwimDriver : MonoBehaviour
    {
        private const short NoWaterSentinel = 10000;
        private const float SwimEnterClearance = 0.10f;
        private const float SwimExitClearance = 2.00f;
        private const float SwimPhysicsSurfaceOffset = 0.55f;
        private const float HeadDiveBelowSurface = 0.25f;
        private const float CameraEnterUnderwaterClearance = 0.04f;
        private const float CameraExitUnderwaterClearance = 0.08f;
        private const float FogCameraClearance = 0.65f;
        private const float OutdoorRenderFogDensityMin = 0.0f;
        private const float OutdoorRenderFogDensityFallbackMax = 0.0012f;
        private const float OutdoorRenderFogDensityCeiling = 0.030f;
        private const float ShoreExitGraceSeconds = 0.85f;
        private const float ShoreGroundProbeHeight = 1.25f;
        private const float ShoreGroundProbeDistance = 3.25f;
        private const float ShoreGroundOceanMargin = 0.50f;
        private const float WaterContactMinimumDepth = 0.15f;
        private const float WaterContactProbeRadiusMin = 0.35f;
        private const float WaterContactProbeRadiusMax = 0.75f;
        private const float ColliderGateProbeRadiusMin = 1.25f;
        private const float ColliderGateProbeRadiusMax = 2.25f;
        private const float ColliderGateTransientHoldSeconds = 0.65f;
        private const float BoatBoardingSurfaceClearance = 1.10f;
        private const float BoatSnapCooldownSeconds = 0.75f;
        private const string BoatEffectBundleName = "ImOnABoat";
        private const string ComeSailAwayBoatEffectBundleName = "I'm On A Boat";
        public static bool ColliderGateDiagnostics = false;

        private readonly OutdoorSwimDfuBridge dfuBridge = new OutdoorSwimDfuBridge();
        private bool currentlyForged;
        private bool wasPlayerOnBoat;
        private float nextBoatSnapTime;
        private float shoreExitGraceUntil;
        private static bool headWaterStateInitialized;
        private static bool headPresentationUnderwater;
        private readonly List<TerrainCollider> disabledWaterTerrainColliders = new List<TerrainCollider>(12);
        private readonly List<TerrainCollider> desiredWaterTerrainColliders = new List<TerrainCollider>(12);
        private bool waterColliderGateActive;
        private bool loggedColliderGateSwim;
        private float colliderGateHoldUntil;

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
            SaveLoadManager.OnLoad += OnLoad;
        }

        void OnDestroy()
        {
            SaveLoadManager.OnStartLoad -= OnStartLoad;
            SaveLoadManager.OnLoad -= OnLoad;
        }

        private void OnStartLoad(SaveData_v1 _)
        {
            RestoreWaterTerrainCollider();
            ClearOutdoorWaterState();
        }

        private void OnLoad(SaveData_v1 _)
        {
            RestoreWaterTerrainCollider();
            ClearOutdoorWaterState();
        }

        void OnDisable()
        {
            RestoreWaterTerrainCollider();
        }

        void Update()
        {
            if (GameManager.Instance == null || !GameManager.Instance.IsPlayingGame())
                return;

            if (GameManager.IsGamePaused)
                return;

            PlayerEnterExit pex = GameManager.Instance.PlayerEnterExit;
            if (pex == null)
                return;

            if (!pex.IsPlayerInside &&
                DeepWaterRuntime.IsLoadGraceActive &&
                !DeepWaterHoleApplier.DisableRuntimeTerrainHoles)
            {
                RestoreWaterTerrainCollider();
                ClearOutdoorWaterState();
                return;
            }

            if (!pex.IsPlayerInside &&
                !currentlyForged &&
                DeepWaterHoleApplier.IsTerrainHoleMutationSettling)
            {
                ClearOutdoorWaterState();
                return;
            }

            if (pex.IsPlayerInside)
            {
                RestoreWaterTerrainCollider();
                HandleIndoors();
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
            UpdateWaterTerrainColliderGate();
            bool standingOnShore = !waterColliderGateActive && IsStandingOnShoreGround(oceanSurfaceY);
            bool isSwimming = !standingOnShore && (IsPlayerAtSwimmingDepth(oceanSurfaceY) || waterColliderGateActive);
            bool isPresentationUnderwater = !standingOnShore && IsPresentationUnderwater(oceanSurfaceY);

            if (standingOnShore)
                ResetHeadWaterState(false);

            if (isSwimming && waterColliderGateActive)
                LogColliderGateSwim(oceanSurfaceY);

            if (isSwimming)
                DismountForSwimming();

            if (!isSwimming && !isPresentationUnderwater)
            {
                if (currentlyForged)
                {
                    Restore();
                    RequestStandAfterWaterExit();
                }
                else
                {
                    ApplyDfuWaterAudioState(pex, oceanSurfaceY, false);
                    ApplyOutdoorSwimMotorState(pex, false);
                    RequestStandAfterWaterExit();
                }
                return;
            }

            Forge(pex, oceanSurfaceY, isSwimming);

            if (isPresentationUnderwater)
                ApplyUnderwaterPresentation(pex, oceanSurfaceY);
            else
                RestoreAboveSurfacePresentation(pex);

            if (isSwimming)
            {
                KeepSurfaceCameraUnsunk(isPresentationUnderwater, oceanSurfaceY);
                if (!DeepWaterRuntime.IsLoadGraceActive &&
                    OutdoorShoreExitAssist.TryMoveToShore(pex, oceanSurfaceY))
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

            if (GameManager.IsGamePaused)
                return;

            if (GameManager.Instance.PlayerEnterExit.IsPlayerInside)
                return;

            if (!currentlyForged && DeepWaterHoleApplier.IsTerrainHoleMutationSettling)
                return;

            if (IsPlayerOnBoat())
            {
                SuppressOutdoorSwimming(false);
                return;
            }

            float oceanSurfaceY = ComputeOceanSurfaceY();
            UpdateWaterTerrainColliderGate();
            bool standingOnShore = !waterColliderGateActive && IsStandingOnShoreGround(oceanSurfaceY);
            bool isSwimming = !standingOnShore && (IsPlayerAtSwimmingDepth(oceanSurfaceY) || waterColliderGateActive);
            bool isPresentationUnderwater = !standingOnShore && IsPresentationUnderwater(oceanSurfaceY);
            if (!isSwimming && !isPresentationUnderwater)
                return;

            var pex = GameManager.Instance.PlayerEnterExit;
            ApplyDfuWaterAudioState(pex, oceanSurfaceY, isSwimming);
            ApplyOutdoorSwimMotorState(pex, isSwimming);
        }

        private void HandleIndoors()
        {
            RestoreWaterTerrainCollider();
            // If we were swimming and entered a building, restore swim state.
            if (currentlyForged) Restore();
        }

        private void SuppressOutdoorSwimming(bool allowSnap)
        {
            bool justEnteredBoat = !wasPlayerOnBoat;
            ResetHeadWaterState(false);
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
                {
                    heightChanger.HeightAction = HeightChangeAction.DoUnsinking;
                }

                heightChanger.IsInWaterTile = false;
            }

            if (snapAboveSurface)
                TrySnapPlayerAboveWaterSurface(player);
        }

        private void ClearOutdoorWaterState()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerEnterExit == null)
                return;

            ResetHeadWaterState(false);
            RestoreWaterTerrainCollider();
            if (currentlyForged)
                Restore();
            else
            {
                PlayerEnterExit pex = gameManager.PlayerEnterExit;
                pex.IsPlayerSwimming = false;
                dfuBridge.ApplyWaterAudioState(pex, NoWaterSentinel, PlayerMotor.OnExteriorWaterMethod.None, false);
                ApplyOutdoorSwimMotorState(pex, false);
            }

            RequestStandAfterWaterExit();
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

        private void UpdateWaterTerrainColliderGate()
        {
            // Holes mode: the vanilla ocean terrain is physically carved by
            // DeepWaterHoleApplier, so the player sinks through real holes
            // and we must NOT disable whole-tile TerrainColliders. The
            // single-tile gate (one collider off at a time) was the no-holes
            // fallback and the direct cause of the invisible-wall / invisible
            // -floor seams and the swim-under-the-world artifacts. With holes
            // on, swim detection falls back to IsPlayerAtSwimmingDepth.
            if (!DeepWaterHoleApplier.DisableRuntimeTerrainHoles)
            {
                RestoreWaterTerrainCollider();
                return;
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerObject == null || gameManager.PlayerEnterExit == null)
            {
                RestoreWaterTerrainCollider();
                return;
            }

            if (gameManager.PlayerEnterExit.IsPlayerInside)
            {
                RestoreWaterTerrainCollider();
                return;
            }

            Vector3 position = gameManager.PlayerObject.transform.position;
            float oceanSurfaceY = ComputeOceanSurfaceY();
            if (position.y > oceanSurfaceY + SwimExitClearance)
            {
                RestoreWaterTerrainCollider();
                return;
            }

            CollectNearbyWaterTerrainColliders(position, desiredWaterTerrainColliders);
            if (desiredWaterTerrainColliders.Count == 0)
            {
                if (disabledWaterTerrainColliders.Count > 0 && Time.time < colliderGateHoldUntil)
                    waterColliderGateActive = true;
                else
                    RestoreWaterTerrainCollider();
                return;
            }

            for (int i = 0; i < desiredWaterTerrainColliders.Count; i++)
                DisableWaterTerrainCollider(desiredWaterTerrainColliders[i]);

            RestoreWaterTerrainCollidersExcept(desiredWaterTerrainColliders);
            waterColliderGateActive = disabledWaterTerrainColliders.Count > 0;
            if (waterColliderGateActive)
                colliderGateHoldUntil = Time.time + ColliderGateTransientHoldSeconds;
        }

        private void CollectNearbyWaterTerrainColliders(Vector3 position, List<TerrainCollider> colliders)
        {
            colliders.Clear();

            AddWaterTerrainColliderAt(position.x, position.z, colliders);

            float radius = GetColliderGateProbeRadius();
            AddWaterTerrainColliderAt(position.x + radius, position.z, colliders);
            AddWaterTerrainColliderAt(position.x - radius, position.z, colliders);
            AddWaterTerrainColliderAt(position.x, position.z + radius, colliders);
            AddWaterTerrainColliderAt(position.x, position.z - radius, colliders);

            float diagonal = radius * 0.70710678f;
            AddWaterTerrainColliderAt(position.x + diagonal, position.z + diagonal, colliders);
            AddWaterTerrainColliderAt(position.x + diagonal, position.z - diagonal, colliders);
            AddWaterTerrainColliderAt(position.x - diagonal, position.z + diagonal, colliders);
            AddWaterTerrainColliderAt(position.x - diagonal, position.z - diagonal, colliders);
        }

        private static float GetColliderGateProbeRadius()
        {
            GameObject player = GameManager.Instance != null ? GameManager.Instance.PlayerObject : null;
            CharacterController controller = player != null ? player.GetComponent<CharacterController>() : null;
            if (controller == null)
                return ColliderGateProbeRadiusMin;

            return Mathf.Clamp(
                controller.radius * 3f,
                ColliderGateProbeRadiusMin,
                ColliderGateProbeRadiusMax);
        }

        private static void AddWaterTerrainColliderAt(
            float worldX,
            float worldZ,
            List<TerrainCollider> colliders)
        {
            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(worldX, worldZ, out column) ||
                column.Depth < WaterContactMinimumDepth ||
                column.Terrain == null)
            {
                TerrainCollider fallbackCollider;
                if (TryGetFallbackWaterTerrainCollider(worldX, worldZ, out fallbackCollider) &&
                    !ContainsCollider(colliders, fallbackCollider))
                {
                    colliders.Add(fallbackCollider);
                }
                return;
            }

            TerrainCollider collider = column.Terrain.GetComponent<TerrainCollider>();
            if (collider != null && !ContainsCollider(colliders, collider))
                colliders.Add(collider);
        }

        private static bool TryGetFallbackWaterTerrainCollider(
            float worldX,
            float worldZ,
            out TerrainCollider collider)
        {
            collider = null;

            DaggerfallTerrain dfTerrain;
            Terrain terrain;
            if (!DeepWaterTerrainLookup.TryGetByWorldPosition(worldX, worldZ, out dfTerrain, out terrain) ||
                dfTerrain == null ||
                terrain == null)
            {
                return false;
            }

            float tileWorldSize = DeepWaterWorld.TileWorldSize;
            if (tileWorldSize <= 0f)
                return false;

            Vector3 origin = dfTerrain.transform.position;
            float fracX = (worldX - origin.x) / tileWorldSize;
            float fracZ = (worldZ - origin.z) / tileWorldSize;
            if (fracX < -0.01f || fracX > 1.01f || fracZ < -0.01f || fracZ > 1.01f)
                return false;

            fracX = Mathf.Clamp01(fracX);
            fracZ = Mathf.Clamp01(fracZ);
            bool waterLike =
                DeepWaterWaterClassification.IsLocalPointWater(dfTerrain.MapData, fracX, fracZ) ||
                (DeepWaterDistanceBake.HasFineWaterMask &&
                 DeepWaterDistanceBake.IsCarvedWater(dfTerrain.MapPixelX, dfTerrain.MapPixelY, fracX, fracZ));
            if (!waterLike)
                return false;

            collider = terrain.GetComponent<TerrainCollider>();
            return collider != null;
        }

        private void DisableWaterTerrainCollider(TerrainCollider collider)
        {
            if (collider == null)
                return;

            if (!ContainsCollider(disabledWaterTerrainColliders, collider))
                disabledWaterTerrainColliders.Add(collider);

            if (collider.enabled)
            {
                collider.enabled = false;
                if (ColliderGateDiagnostics)
                    Debug.Log("[DeepWaters.Swim] Disabled nearby water TerrainCollider to avoid Unity Terrain holes.");
            }
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

                if (!collider.enabled)
                    collider.enabled = true;
                disabledWaterTerrainColliders.RemoveAt(i);
            }
        }

        private void RestoreWaterTerrainCollider()
        {
            waterColliderGateActive = false;
            loggedColliderGateSwim = false;

            for (int i = disabledWaterTerrainColliders.Count - 1; i >= 0; i--)
            {
                TerrainCollider collider = disabledWaterTerrainColliders[i];
                if (collider != null && !collider.enabled)
                    collider.enabled = true;
            }

            disabledWaterTerrainColliders.Clear();
            desiredWaterTerrainColliders.Clear();
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

        private void LogColliderGateSwim(float oceanSurfaceY)
        {
            if (loggedColliderGateSwim)
                return;

            if (!ColliderGateDiagnostics)
                return;

            var player = GameManager.Instance != null ? GameManager.Instance.PlayerObject : null;
            string playerY = player != null ? player.transform.position.y.ToString("F2") : "none";
            Debug.Log("[DeepWaters.Swim] Collider gate is driving outdoor swim state " +
                      "(playerY=" + playerY +
                      " oceanY=" + oceanSurfaceY.ToString("F2") + ").");
            loggedColliderGateSwim = true;
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
            if (isSwimming)
                dfuBridge.ForgeDungeonState(pex);
            else
                dfuBridge.RestoreDungeonState(pex);

            ApplyDfuWaterAudioState(pex, oceanSurfaceY, isSwimming);
            ApplyOutdoorSwimMotorState(pex, isSwimming);
            if (isSwimming)
                RequestSwimAfterWaterEnter();
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
            ApplyOutdoorSwimMotorState(pex, false);
            pex.UnderwaterFog?.UpdateFog(NoWaterSentinel);
            currentlyForged = false;
            ResetHeadWaterState(false);
        }

        private static void ApplyOutdoorSwimMotorState(PlayerEnterExit pex, bool isSwimming)
        {
            if (pex != null)
                pex.IsPlayerSwimming = isSwimming;

            var player = GameManager.Instance != null ? GameManager.Instance.PlayerObject : null;
            var levitateMotor = player != null ? player.GetComponent<LevitateMotor>() : null;
            if (levitateMotor != null)
                levitateMotor.IsSwimming = isSwimming;
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

        private static void RequestSwimAfterWaterEnter()
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerObject == null)
                return;

            var heightChanger = gameManager.PlayerObject.GetComponent<PlayerHeightChanger>();
            if (heightChanger == null)
                return;

            heightChanger.ForcedSwimCrouch = true;
            if (!heightChanger.IsInWaterTile ||
                heightChanger.HeightAction == HeightChangeAction.DoUnsinking)
            {
                heightChanger.HeightAction = HeightChangeAction.DoSinking;
            }
        }

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
            if (player == null)
                return false;

            float headY = player.transform.position.y + (76 * MeshReader.GlobalScale) - 0.95f;
            return headY > oceanSurfaceY;
        }

        public static bool IsPresentationUnderwater(float oceanSurfaceY)
        {
            float cameraY = GetCameraY();
            bool shouldBeUnderwater = ShouldEnterUnderwaterPresentation(cameraY, oceanSurfaceY) ||
                                      IsPlayerHeadUnderwaterForPresentation(oceanSurfaceY);
            if (!headWaterStateInitialized)
            {
                headPresentationUnderwater = shouldBeUnderwater;
                headWaterStateInitialized = true;
            }

            if (headPresentationUnderwater)
            {
                if (!shouldBeUnderwater && cameraY > oceanSurfaceY + CameraExitUnderwaterClearance)
                {
                    headPresentationUnderwater = false;
                }
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

        private static bool ShouldEnterUnderwaterPresentation(float cameraY, float oceanSurfaceY)
        {
            return cameraY < oceanSurfaceY + CameraEnterUnderwaterClearance;
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

            return column.Depth >= WaterContactMinimumDepth;
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
            UpdateWaterTerrainColliderGate();
            bool standingOnShore = !waterColliderGateActive && IsStandingOnShoreGround(oceanSurfaceY);
            if (standingOnShore)
            {
                Restore();
                RequestStandAfterWaterExit();
                return;
            }

            var pex = GameManager.Instance.PlayerEnterExit;
            dfuBridge.RestoreDungeonState(pex);

            bool isSwimming = IsPlayerAtSwimmingDepth(oceanSurfaceY) || waterColliderGateActive;
            bool presentationUnderwater = IsPresentationUnderwater(oceanSurfaceY);

            ApplyDfuWaterAudioState(pex, oceanSurfaceY, isSwimming);
            ApplyOutdoorSwimMotorState(pex, isSwimming);
            if (isSwimming)
                RequestSwimAfterWaterEnter();
            else if (!presentationUnderwater)
                RequestStandAfterWaterExit();

            ApplyUnderwaterFogSettings(pex);
            pex.UnderwaterFog?.UpdateFog(presentationUnderwater ? PresentationWaterLevelForFog(oceanSurfaceY) : NoWaterSentinel);
            if (presentationUnderwater)
                ApplyNeutralUnderwaterFogColor();
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
        void Update()
        {
            // Freeze swim bookkeeping while paused. The early phase
            // (OutdoorSwimDriver.Update) already bails on IsGamePaused; if
            // this late phase kept running it would un-forge DFU's dungeon-
            // swim state every paused frame, so opening the console made DFU
            // decide the player had left the water and float them up to the
            // surface. With both phases frozen, the player stays put while
            // paused and resumes swimming cleanly on unpause.
            if (GameManager.IsGamePaused)
                return;

            owner?.PostPhaseRestore();
        }
    }

    [DefaultExecutionOrder(32010)]
    public class OutdoorSwimMovementController : MonoBehaviour
    {
        private const float MultiplierEpsilon = 0.001f;
        private const float StrokeDuration = 0.48f;
        private const float StrokeCooldownSeconds = 0.90f;
        private const float StrokeExtraSpeedMultiplier = 2.65f;
        private const float StrokeFatigueCostFraction = 0.025f;
        private const int StrokeMinimumFatigueCost = 24;
        private const float SurfaceUpwardStrokeMargin = 0.65f;
        private const float StrokeTempoScaleExponent = 0.35f;
        private const float SeafloorSwimFloorClearance = 0.18f;

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
            strokeTimeRemaining = 0f;
            currentStrokeDuration = StrokeDuration;
            wasStrokeButtonHeld = false;
        }

        void Update()
        {
            if (!IsOutdoorSwimming())
            {
                RemoveSpeedModifier();
                strokeTimeRemaining = 0f;
                currentStrokeDuration = StrokeDuration;
                wasStrokeButtonHeld = false;
                return;
            }

            if (DeepWaterRuntime.IsLoadGraceActive)
            {
                RemoveSpeedModifier();
                strokeTimeRemaining = 0f;
                currentStrokeDuration = StrokeDuration;
                wasStrokeButtonHeld = false;
                return;
            }

            CachePlayerMotorParts();
            RemoveSpeedModifier();
            ApplySwimMotion();
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
            if (DeepWaters.Instance == null || speedChanger == null)
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

        private void ApplySwimMotion()
        {
            if (groundMotor == null || speedChanger == null)
                return;

            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.MainCamera == null || gameManager.PlayerEntity == null)
                return;

            if (gameManager.PlayerEntity.IsParalyzed)
                return;

            Vector3 direction = Vector3.zero;
            InputManager input = InputManager.Instance;
            if (input != null)
            {
                float inputX = input.Horizontal;
                float inputY = input.Vertical;
                if (Mathf.Abs(inputX) > 0.001f || Mathf.Abs(inputY) > 0.001f)
                {
                    float inputModifyFactor = inputX != 0f && inputY != 0f && gameManager.PlayerMotor != null && gameManager.PlayerMotor.limitDiagonalSpeed
                        ? 0.7071f
                        : 1f;
                    Vector3 horizontal = gameManager.MainCamera.transform.TransformDirection(new Vector3(inputX * inputModifyFactor, 0f, inputY * inputModifyFactor));
                    horizontal.y = 0f;
                    direction += horizontal;
                }

                bool overEncumbered = (gameManager.PlayerEntity.CarriedWeight * 4 > 250) &&
                                      !gameManager.PlayerEntity.GodMode;
                if (overEncumbered && !gameManager.PlayerEntity.IsWaterWalking)
                    direction += Vector3.down;
                else if (input.HasAction(InputManager.Actions.Jump) || input.HasAction(InputManager.Actions.FloatUp))
                    direction += Vector3.up;
                else if (input.HasAction(InputManager.Actions.Crouch) || input.HasAction(InputManager.Actions.FloatDown))
                    direction += Vector3.down;
            }

            if (direction.y > 0f)
            {
                float oceanSurfaceY;
                if (DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanSurfaceY) &&
                    gameManager.MainCamera.transform.position.y > oceanSurfaceY - SurfaceUpwardStrokeMargin)
                {
                    direction.y = 0f;
                }
            }

            if (direction.sqrMagnitude < 0.001f)
                return;

            direction.Normalize();
            groundMotor.MoveWithMovingPlatform(direction * CurrentSwimSpeed());
        }

        private void RemoveSpeedModifier()
        {
            if (speedChanger != null && !string.IsNullOrEmpty(walkSpeedModId))
                speedChanger.RemoveSpeedMod(walkSpeedModId, false, true);

            walkSpeedModId = null;
            appliedMultiplier = 1f;
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
            groundMotor.MoveWithMovingPlatform(strokeDirection * CurrentSwimSpeed() * StrokeExtraSpeedMultiplier * CurrentStrokeTempoScale() * fade);

            strokeTimeRemaining = Mathf.Max(0f, strokeTimeRemaining - Time.deltaTime);
        }

        private static void ClampAboveRenderedSeafloor()
        {
            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            if (player == null)
                return;

            Vector3 position = player.transform.position;
            DeepWaterColumn column;
            if (!DeepWaterWorld.TryGetWaterColumn(position.x, position.z, out column))
                return;

            float seafloorWorldY;
            if (!DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, position.x, position.z, out seafloorWorldY))
                return;

            float minimumY = seafloorWorldY + SeafloorSwimFloorClearance;
            if (position.y >= minimumY)
                return;

            player.transform.position = new Vector3(position.x, minimumY, position.z);
            Physics.SyncTransforms();
        }

        private float CurrentSwimSpeed()
        {
            if (speedChanger == null)
                return 0f;

            float baseSpeed = speedChanger.GetBaseSpeed();
            return speedChanger.GetSwimSpeed(baseSpeed) * CurrentSwimSpeedMultiplier();
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
                cameraTransform.position.y > oceanSurfaceY - SurfaceUpwardStrokeMargin)
            {
                direction.y = 0f;
            }

            if (direction.sqrMagnitude < 0.001f)
                return false;

            direction.Normalize();
            return true;
        }

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

            GameObject player = gameManager.PlayerObject;
            if (player == null)
                return false;

            if (gameManager.PlayerMotor.OnExteriorWater == PlayerMotor.OnExteriorWaterMethod.Swimming)
                return true;

            if (gameManager.PlayerEnterExit.IsPlayerSwimming)
                return true;

            return OutdoorSwimDriver.IsPresentationUnderwater(oceanSurfaceY);
        }
    }
}
