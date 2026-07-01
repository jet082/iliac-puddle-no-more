// Project:         Iliac Puddle No More
// License:         MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;

namespace DeepWaters
{
    public partial class DeepWaters
    {
        private static void InstallSubsystems(GameObject go)
        {
            // Keep streaming hitches from turning into multi-step physics catch-up
            // frames that trip the swim motor spike guard.
            if (Time.maximumDeltaTime > 0.1f)
                Time.maximumDeltaTime = 0.1f;

            // === Core path ===
            // The floor builder must subscribe to OnPromoteTerrainData before
            // any tile promotes.
            DeepWaterRuntime.Install();
            DeepWaterFloorBuilder.Install();
            OutdoorSwimDriver.Install(go);
            // Swim extras (speed multiplier, strokes, anti-tunnel clamps)
            // layered on top of DFU's native swim movement.
            go.AddComponent<OutdoorSwimMovementController>();
            go.AddComponent<DeepWaterSwimWorldTracker>();

            // === Content and presentation ===
            WaterSurfaceManager.Install();
            // Latest stable path does not widen DFU's terrain ring here. The
            // regular promote hooks populate seafloor/decor as tiles stream in,
            // while avoiding a larger synchronous terrain load on pixel changes.
            UnderwaterEnemySpawner.Install();
            UnderwaterPassiveFishSpawner.Install();
            UnderwaterEncounterPulse.Install();
            UnderwaterDecorations.Install();
            UnderwaterLootSpawner.Install();
            go.AddComponent<PlayerShipWaterlineFix>();
            go.AddComponent<UnderwaterDistanceFog>();
            go.AddComponent<UnderwaterPresentationEffects>();
            DeepWaterDiagnosticsRunner.Install(go);
        }

    }

    internal sealed class DeepWaterDiagnosticsRunner : MonoBehaviour
    {
        private const string EnableArg = "-deepWatersTest";
        private const string EnableArgAlt = "--deep-waters-test";
        private const string CharacterArg = "-deepWatersTestCharacter";
        private const string SavesArg = "-deepWatersTestSaves";
        private const string DurationArg = "-deepWatersTestDuration";
        private const string QuitArg = "-deepWatersTestQuit";
        private const float InitialWindowSeconds = 10f;
        private const float TransitionWindowSeconds = 10f;
        private const float DefaultDurationSeconds = 600f;
        private const float MoveSpeed = 9f;
        private const float UnderwaterOffset = -8f;
        private const float AboveWaterOffset = 8f;
        private const float TargetedScenarioSeconds = 35f;
        private const float TargetedVisualHoldSeconds = 5f;
        private const float TargetedScreenshotIntervalSeconds = 5f;

		private static readonly HashSet<string> TargetedScenarioSaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"ddd", "eee", "fff", "ggg", "hhh", "iii", "jjj", "kkkk", "lll", "mmm",
			"nnn", "ooo", "qqq", "rrr", "sss", "ttt", "mystery", "distance fog test",
			"ledge", "ledge2", "weird bathymetry", "gap1", "gap2", "gap3", "day", "midday", "night", "nightunderwater", "bottomunderwaternight", "bottomunderwaterday", "overdeepwater", "overdeepwater2", "sailing", "sailingbottom", "wodbrokenterrain", "vanillabrokenshelf"
		};

		private static readonly HashSet<string> VisualScenarioSaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"eee", "ggg", "hhh", "jjj", "nnn", "ooo", "rrr", "sss", "ttt", "distance fog test",
			"ledge", "ledge2", "weird bathymetry", "gap1", "gap2", "gap3", "day", "midday", "night", "nightunderwater", "bottomunderwaternight", "bottomunderwaterday", "overdeepwater", "overdeepwater2", "sailing", "sailingbottom", "wodbrokenterrain", "vanillabrokenshelf"
		};

		private static readonly HashSet<string> BiomeVisualProbeSaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"temperate", "swamp", "tropical", "desert", "cold", "open ocean", "open ocean 2", "mystery"
		};

		private static readonly HashSet<string> MovementProbeSaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"iii", "kkkk", "lll", "mmm", "qqq", "desert"
		};

		private static readonly Dictionary<string, string> ForwardScenarioPhases =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				{ "ddd", "ddd_straight_shore_entry" },
				{ "iii", "iii_straight_shore_entry" },
				{ "kkkk", "kkkk_straight_shore_entry" },
				{ "lll", "lll_straight_shore_probe" },
				{ "mmm", "mmm_straight_water_entry" },
				{ "qqq", "qqq_straight_boat_probe" },
				{ "mystery", "mystery_straight_lake_probe" },
				{ "desert", "desert_straight_lake_probe" }
			};

        private readonly List<MetricWindow> windows = new List<MetricWindow>();
        private StreamWriter writer;
        private string characterName = "Miranda";
        private string[] saveNames = { "bbb", "ccc" };
        private float durationSeconds = DefaultDurationSeconds;
        private bool quitWhenDone;
        private bool running;
        private DFPosition lastPixel;
        private bool hasLastPixel;
        private float nextRuntimeNoiseSuppressionTime;
        private Vector3 lastFramePlayer;
        private float lastFrameTime;
        private bool hasLastFramePlayer;

        internal static void Install(GameObject host)
        {
            if (!IsEnabled())
                return;

            host.AddComponent<DeepWaterDiagnosticsRunner>();
        }

        private static bool IsEnabled()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == EnableArg || args[i] == EnableArgAlt)
                    return true;
            }

            return false;
        }

        private void Start()
        {
            ParseArgs();
            StartCoroutine(RunSuite());
        }

        private void Update()
        {
            if (!running)
                return;

            CloseBlockingUi();
            if (Time.realtimeSinceStartup >= nextRuntimeNoiseSuppressionTime)
            {
                DisableNoisyRuntimeSystems();
                nextRuntimeNoiseSuppressionTime = Time.realtimeSinceStartup + 2f;
            }

            for (int i = windows.Count - 1; i >= 0; i--)
            {
                if (windows[i].Update(this))
                    windows.RemoveAt(i);
            }
        }

        private void OnDestroy()
        {
            if (writer != null)
            {
                writer.Flush();
                writer.Dispose();
                writer = null;
            }
        }

        private void ParseArgs()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == CharacterArg && i + 1 < args.Length)
                    characterName = args[++i];
                else if (args[i] == SavesArg && i + 1 < args.Length)
                    saveNames = args[++i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                else if (args[i] == DurationArg && i + 1 < args.Length)
                    float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out durationSeconds);
                else if (args[i] == QuitArg)
                    quitWhenDone = true;
            }

            if (durationSeconds <= 0f)
                durationSeconds = DefaultDurationSeconds;
        }

        private IEnumerator RunSuite()
        {
            running = true;
            OpenWriter();
            Debug.Log("[DeepWaters.Diagnostics] Starting diagnostics for " + characterName + " saves " + string.Join(",", saveNames));

            yield return WaitForSaveLoadReady();

            SaveLoadManager.Instance.EnumerateSaves();
            for (int i = 0; i < saveNames.Length; i++)
            {
                string saveName = saveNames[i].Trim();
                if (string.IsNullOrEmpty(saveName))
                    continue;

                yield return LoadSave(saveName);
                if (IsTargetedScenarioSave(saveName))
                {
                    yield return RunTargetedScenario(saveName);
                }
                else
                {
                    yield return RecordInitialWindow(saveName);
                    yield return RunTraversal(saveName);
                }
            }

            while (windows.Count > 0)
                yield return null;

            running = false;
            Debug.Log("[DeepWaters.Diagnostics] Complete: " + ((FileStream)writer.BaseStream).Name);
            writer.Flush();

            if (quitWhenDone)
                Application.Quit();
        }

        private IEnumerator WaitForSaveLoadReady()
        {
            while (SaveLoadManager.Instance == null || !SaveLoadManager.Instance.IsReady())
                yield return null;
        }

        private IEnumerator LoadSave(string saveName)
        {
            SaveLoadManager saveLoad = SaveLoadManager.Instance;
            saveLoad.EnumerateSaves();
            int key = saveLoad.FindSaveFolderByNames(characterName, saveName);
            if (key < 0)
            {
                Debug.LogError("[DeepWaters.Diagnostics] Save not found: character='" + characterName + "' save='" + saveName + "'");
                yield break;
            }

            string saveFolder = saveLoad.GetSaveFolder(key);
            SavedPlayerPose savedPose;
            bool hasSavedPose = TryReadSavedPlayerPose(saveFolder, out savedPose);

            Debug.Log("[DeepWaters.Diagnostics] Loading " + characterName + " / " + saveName +
                      " key=" + key +
                      " folder=" + Path.GetFileName(saveFolder) +
                      (hasSavedPose
                          ? " pos=" + VectorString(savedPose.Position) +
                            " comp=" + VectorString(savedPose.WorldCompensation) +
                            " yaw=" + savedPose.Yaw.ToString("F2", CultureInfo.InvariantCulture) +
                            " pitch=" + savedPose.Pitch.ToString("F2", CultureInfo.InvariantCulture)
                          : " pose=<not found>"));
            saveLoad.Load(key);
            yield return null;

            while (saveLoad.LoadInProgress)
                yield return null;

            CloseBlockingUi();
            yield return null;
            ApplySavedPose(hasSavedPose, savedPose);
            CloseBlockingUi();
            yield return new WaitForSecondsRealtime(2f);
            ApplySavedPose(hasSavedPose, savedPose);
            CloseBlockingUi();
            DisableNoisyRuntimeSystems();
            ApplySavedPose(hasSavedPose, savedPose);
            hasLastPixel = TryGetCurrentPixel(out lastPixel);
        }

        private static bool TryReadSavedPlayerPose(string saveFolder, out SavedPlayerPose pose)
        {
            pose = new SavedPlayerPose();
            if (string.IsNullOrEmpty(saveFolder))
                return false;

            string path = Path.Combine(saveFolder, "SaveData.txt");
            if (!File.Exists(path))
                return false;

            string text = File.ReadAllText(path);
            int playerPosition = text.IndexOf("\"playerPosition\"", StringComparison.Ordinal);
            if (playerPosition < 0)
                playerPosition = 0;

            return TryReadJsonVector3(text, "\"position\"", playerPosition, out pose.Position) &&
                   TryReadJsonVector3(text, "\"worldCompensation\"", playerPosition, out pose.WorldCompensation) &&
                   TryReadJsonFloat(text, "\"yaw\"", playerPosition, out pose.Yaw) &&
                   TryReadJsonFloat(text, "\"pitch\"", playerPosition, out pose.Pitch);
        }

        private static bool TryReadJsonVector3(string text, string key, int startIndex, out Vector3 value)
        {
            value = Vector3.zero;
            int keyIndex = text.IndexOf(key, Math.Max(0, startIndex), StringComparison.Ordinal);
            if (keyIndex < 0)
                return false;

            float x;
            float y;
            float z;
            if (!TryReadJsonFloat(text, "\"x\"", keyIndex, out x) ||
                !TryReadJsonFloat(text, "\"y\"", keyIndex, out y) ||
                !TryReadJsonFloat(text, "\"z\"", keyIndex, out z))
                return false;

            value = new Vector3(x, y, z);
            return true;
        }

        private static bool TryReadJsonFloat(string text, string key, int startIndex, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(text))
                return false;

            int keyIndex = text.IndexOf(key, Math.Max(0, startIndex), StringComparison.Ordinal);
            if (keyIndex < 0)
                return false;

            int colon = text.IndexOf(':', keyIndex + key.Length);
            if (colon < 0)
                return false;

            int valueStart = colon + 1;
            while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart]))
                valueStart++;

            int valueEnd = valueStart;
            while (valueEnd < text.Length && "-+.0123456789eE".IndexOf(text[valueEnd]) >= 0)
                valueEnd++;

            if (valueEnd <= valueStart)
                return false;

            return float.TryParse(
                text.Substring(valueStart, valueEnd - valueStart),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static void ApplySavedPose(bool hasSavedPose, SavedPlayerPose pose)
        {
            if (!hasSavedPose || !GameManager.HasInstance)
                return;

            GameManager gameManager = GameManager.Instance;
            PlayerMouseLook mouseLook = gameManager.PlayerMouseLook;
            if (mouseLook == null)
                return;

            mouseLook.SetFacing(pose.Yaw, pose.Pitch);

            // PlayerMouseLook normally applies these transforms in Update().
            // The diagnostics harness captures and moves immediately after load, so
            // force the rendered camera rig to the saved facing now.
            if (mouseLook.characterBody != null)
            {
                mouseLook.transform.localEulerAngles = new Vector3(pose.Pitch, 0f, 0f);
                mouseLook.characterBody.transform.localEulerAngles = new Vector3(0f, pose.Yaw, 0f);
            }
            else
            {
                mouseLook.transform.localEulerAngles = new Vector3(pose.Pitch, pose.Yaw, 0f);
            }

            Physics.SyncTransforms();
        }

        private static string VectorString(Vector3 vector)
        {
            return "(" +
                   vector.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                   vector.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                   vector.z.ToString("F2", CultureInfo.InvariantCulture) + ")";
        }

        private IEnumerator RecordInitialWindow(string saveName)
        {
            DFPosition current;
            if (!TryGetCurrentPixel(out current))
                current = lastPixel;

            StartWindow(saveName, "initial_load", "first_10s", current, current);
            float end = Time.realtimeSinceStartup + InitialWindowSeconds;
            while (Time.realtimeSinceStartup < end)
                yield return null;
        }

        private IEnumerator RunTraversal(string saveName)
        {
            float phaseDuration = Mathf.Max(30f, durationSeconds / 4f);
            yield return RunPhase(saveName, "underwater_outbound", phaseDuration, new Vector3(1f, 0f, 0.15f), UnderwaterOffset);
            yield return RunPhase(saveName, "above_water_levitation", phaseDuration, new Vector3(0.15f, 0f, 1f), AboveWaterOffset);
            yield return RunPhase(saveName, "underwater_return", phaseDuration, new Vector3(-1f, 0f, -0.15f), UnderwaterOffset);
            yield return RunPhase(saveName, "surface_boat_like", phaseDuration, new Vector3(-0.15f, 0f, -1f), AboveWaterOffset);
        }

        private static bool IsTargetedScenarioSave(string saveName)
        {
			return TargetedScenarioSaves.Contains(saveName) || IsBiomeVisualProbeSave(saveName);
        }

        private IEnumerator RunTargetedScenario(string saveName)
        {
            yield return CaptureDiagnosticScreenshot(saveName, "after-load");

            if (VisualScenarioSaves.Contains(saveName) || IsBiomeVisualProbeSave(saveName))
            {
                string visualPhase = saveName.ToLowerInvariant() + "_shoreline_visual";
                yield return RunStationaryPhase(saveName, visualPhase, TargetedVisualHoldSeconds);
                yield return CaptureDiagnosticScreenshot(saveName, "shoreline-hold");
                // Look down to evaluate vertical visibility through the surface
                // (top/bottom seafloor view-distance parity).
                yield return CapturePitchScreenshot(saveName, "look-down", 65f);
				if (string.Equals(saveName, "desert", StringComparison.OrdinalIgnoreCase))
				{
					yield return CaptureOffsetYawScreenshot(saveName, "left-look", -90f);
					yield return CaptureOffsetYawScreenshot(saveName, "right-look", 90f);
					float desertSeconds = Mathf.Min(TargetedScenarioSeconds, Mathf.Max(10f, durationSeconds));
					yield return RunNaturalForwardPhase(saveName, "desert_straight_lake_probe", desertSeconds);
					yield return CaptureDiagnosticScreenshot(saveName, "desert_straight_lake_probe-end");
				}
				if (string.Equals(saveName, "mystery", StringComparison.OrdinalIgnoreCase))
					yield return CaptureOffsetYawScreenshot(saveName, "right-look", 90f);
                yield break;
            }

            string phase;
			if (!ForwardScenarioPhases.TryGetValue(saveName ?? string.Empty, out phase))
				phase = "fff_straight_seam_probe";
            float seconds = Mathf.Min(TargetedScenarioSeconds, Mathf.Max(10f, durationSeconds));
            yield return RunNaturalForwardPhase(saveName, phase, seconds);
            yield return CaptureDiagnosticScreenshot(saveName, phase + "-end");
        }

		private static bool IsBiomeVisualProbeSave(string saveName)
		{
			return BiomeVisualProbeSaves.Contains(saveName);
		}

        private IEnumerator RunStationaryPhase(string saveName, string phase, float seconds)
        {
            DFPosition current;
            if (TryGetCurrentPixel(out current))
                StartWindow(saveName, phase, "visual_hold", current, hasLastPixel ? lastPixel : current);

            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
                yield return null;
        }

        private IEnumerator RunNaturalForwardPhase(string saveName, string phase, float seconds)
        {
            Vector3 direction = GetCameraForwardFlat();
            if (direction.sqrMagnitude < 0.001f)
                direction = Vector3.forward;
            bool writeFrameMovement = MovementProbeSaves.Contains(saveName);
            if (writeFrameMovement)
                ResetFrameMovementProbe();

            DFPosition current;
            if (TryGetCurrentPixel(out current))
            {
                StartWindow(saveName, phase, "phase_start", current, hasLastPixel ? lastPixel : current);
                lastPixel = current;
                hasLastPixel = true;
            }

            float end = Time.realtimeSinceStartup + seconds;
            float nextScreenshot = Time.realtimeSinceStartup + TargetedScreenshotIntervalSeconds;
            while (Time.realtimeSinceStartup < end)
            {
                MovePlayerNatural(direction);
                CheckMapPixelTransition(saveName, phase);
                if (writeFrameMovement)
                    WriteMovementSnapshot(saveName, phase, "frame");

                if (Time.realtimeSinceStartup >= nextScreenshot)
                {
                    yield return CaptureDiagnosticScreenshot(saveName, phase + "-" + Mathf.RoundToInt(seconds - (end - Time.realtimeSinceStartup)) + "s");
                    nextScreenshot = Time.realtimeSinceStartup + TargetedScreenshotIntervalSeconds;
                }

                yield return null;
            }
        }

        private static Vector3 GetCameraForwardFlat()
        {
            GameManager gameManager = GameManager.Instance;
            Camera camera = gameManager != null ? gameManager.MainCamera : null;
            Vector3 direction = camera != null ? camera.transform.forward : Vector3.forward;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f)
                direction.Normalize();

            return direction;
        }

        private IEnumerator RunPhase(string saveName, string phase, float seconds, Vector3 direction, float waterOffset)
        {
            direction.y = 0f;
            direction.Normalize();

            DFPosition current;
            if (TryGetCurrentPixel(out current))
            {
                StartWindow(saveName, phase, "phase_start", current, hasLastPixel ? lastPixel : current);
                lastPixel = current;
                hasLastPixel = true;
            }

            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end)
            {
                MovePlayer(direction, waterOffset);
                CheckMapPixelTransition(saveName, phase);
                yield return null;
            }
        }

        private void MovePlayer(Vector3 direction, float waterOffset)
        {
            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            if (player == null)
                return;

            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            Vector3 delta = direction * MoveSpeed * dt;
            CharacterController controller = player.GetComponent<CharacterController>();
            if (controller != null && controller.enabled)
                controller.Move(delta);
            else
                player.transform.position += delta;

            float oceanY;
            if (DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY))
            {
                Vector3 pos = player.transform.position;
                pos.y = oceanY + waterOffset;
                player.transform.position = pos;
            }

            Physics.SyncTransforms();
        }

        private void MovePlayerNatural(Vector3 direction)
        {
            GameManager gameManager = GameManager.Instance;
            GameObject player = gameManager != null ? gameManager.PlayerObject : null;
            if (player == null)
                return;

            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            Vector3 delta = direction * MoveSpeed * dt;
            CharacterController controller = player.GetComponent<CharacterController>();
            if (controller != null && controller.enabled)
                controller.Move(delta);
            else
                player.transform.position += delta;

            float oceanY;
            if (DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY) && gameManager.PlayerEnterExit != null)
                OutdoorShoreExitAssist.TryMoveToShore(gameManager.PlayerEnterExit, oceanY, false, false);

            Physics.SyncTransforms();
        }

        private void CheckMapPixelTransition(string saveName, string phase)
        {
            DFPosition current;
            if (!TryGetCurrentPixel(out current))
                return;

            if (!hasLastPixel)
            {
                lastPixel = current;
                hasLastPixel = true;
                return;
            }

            if (current.X == lastPixel.X && current.Y == lastPixel.Y)
                return;

            DFPosition previous = lastPixel;
            lastPixel = current;
            StartWindow(saveName, phase, "map_pixel_transition", current, previous);
        }

        private static bool TryGetCurrentPixel(out DFPosition pixel)
        {
            pixel = new DFPosition();
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerGPS == null)
                return false;

            pixel = gameManager.PlayerGPS.CurrentMapPixel;
            return true;
        }

        private void StartWindow(string saveName, string phase, string eventName, DFPosition current, DFPosition former)
        {
            windows.Add(new MetricWindow(saveName, phase, eventName, current, former));
        }

        private static void CloseBlockingUi()
        {
            if (SaveLoadManager.Instance != null && SaveLoadManager.Instance.LoadInProgress)
                return;

            if (DaggerfallUI.UIManager != null)
            {
                for (int i = 0; i < 16 && DaggerfallUI.UIManager.WindowCount > 0; i++)
                    DaggerfallUI.UIManager.PopWindow();
            }

            if (GameManager.HasInstance)
                GameManager.Instance.PauseGame(false);
        }

        private static void DisableNoisyRuntimeSystems()
        {
            if (GameManager.HasInstance)
            {
                KeepPlayerAliveForDiagnostics(GameManager.Instance);

                WeaponManager weaponManager = GameManager.Instance.WeaponManager;
                if (weaponManager != null)
                {
                    if (!weaponManager.Sheathed)
                        weaponManager.SheathWeapons();

                    weaponManager.enabled = false;
                    if (weaponManager.ScreenWeapon != null)
                        weaponManager.ScreenWeapon.enabled = false;
                }
            }

            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour != null && behaviour.enabled && behaviour.GetType().Name == "BLBSkybox")
                    behaviour.enabled = false;
            }
        }

        private static void KeepPlayerAliveForDiagnostics(GameManager gameManager)
        {
            PlayerEntity playerEntity = gameManager != null ? gameManager.PlayerEntity : null;
            if (playerEntity == null)
                return;

            playerEntity.GodMode = true;
            if (playerEntity.CurrentHealth < playerEntity.MaxHealth)
                playerEntity.CurrentHealth = playerEntity.MaxHealth;
            if (playerEntity.CurrentFatigue < playerEntity.MaxFatigue)
                playerEntity.CurrentFatigue = playerEntity.MaxFatigue;
            if (playerEntity.CurrentMagicka < playerEntity.MaxMagicka)
                playerEntity.CurrentMagicka = playerEntity.MaxMagicka;
        }

        private IEnumerator CaptureDiagnosticScreenshot(string saveName, string label)
        {
            string dir = Path.Combine(Application.persistentDataPath, "DeepWatersDiagnostics");
            Directory.CreateDirectory(dir);
            string safeLabel = (label ?? "capture").Replace(' ', '-').Replace(':', '-');
            string path = Path.Combine(
                dir,
                "deep-waters-" + saveName + "-" + safeLabel + "-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png");
            WriteSnapshot(saveName, "screenshot", safeLabel);
            LogTerrainSurfaceSnapshot(saveName, safeLabel);
            LogShoreProfileSnapshot(saveName, safeLabel);
            LogClassificationGrid(saveName, safeLabel);
            LogSpawnDiagnostics(saveName, safeLabel);
            LogCameraRenderState(saveName, safeLabel);
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log("[DeepWaters.Diagnostics] Screenshot: " + path);
            yield return new WaitForSecondsRealtime(0.5f);
        }

        // Diagnostic for the "top transparency breaks while piloting" report:
        // Come Sail Away forces RenderingPath.Forward when sailing, which changes
        // how _CameraDepthTexture is available to the transparent surface pass.
        private void LogCameraRenderState(string saveName, string label)
        {
            GameManager gm = GameManager.Instance;
            Camera cam = gm != null ? gm.MainCamera : null;
            if (cam == null)
                return;

            float oceanY;
            bool hasOcean = DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY);
            var dw = DeepWaters.Instance;
            DeepWaterColumn col;
            float colDepth = DeepWaterWorld.TryGetWaterColumn(cam.transform.position.x, cam.transform.position.z, out col) ? col.Depth : -1f;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[DeepWaters.Diagnostics] CamRender save={0} label={1} path={2} actual={3} camY={4:F2} oceanY={5:F2} colDepth={11:F1} | fogStr={6:F2} fogDist={7:F2} visionDist={8:F1} topAlpha={9:F2} botAlpha={10:F2}",
                saveName, label, cam.renderingPath, cam.actualRenderingPath,
                cam.transform.position.y, oceanY,
                dw != null ? dw.UnderwaterFogStrength : -1f,
                dw != null ? dw.UnderwaterFogDistance : -1f,
                dw != null ? dw.UnderwaterVisionDistance : -1f,
                dw != null ? dw.WaterSurfaceTopAlpha : -1f,
                dw != null ? dw.WaterSurfaceBottomAlpha : -1f,
                colDepth));
        }

		private IEnumerator CaptureOffsetYawScreenshot(string saveName, string label, float yawOffset)
		{
			if (!GameManager.HasInstance || GameManager.Instance.PlayerMouseLook == null)
				yield break;

			PlayerMouseLook mouseLook = GameManager.Instance.PlayerMouseLook;
			float yaw = mouseLook.Yaw;
			float pitch = mouseLook.Pitch;
			SetDiagnosticFacing(yaw + yawOffset, pitch);
			yield return null;
			yield return CaptureDiagnosticScreenshot(saveName, label);
			SetDiagnosticFacing(yaw, pitch);
		}

		private IEnumerator CapturePitchScreenshot(string saveName, string label, float pitch)
		{
			if (!GameManager.HasInstance || GameManager.Instance.PlayerMouseLook == null)
				yield break;

			PlayerMouseLook mouseLook = GameManager.Instance.PlayerMouseLook;
			float yaw = mouseLook.Yaw;
			float origPitch = mouseLook.Pitch;
			SetDiagnosticFacing(yaw, pitch);
			yield return null;
			yield return CaptureDiagnosticScreenshot(saveName, label);
			SetDiagnosticFacing(yaw, origPitch);
		}

		private static void SetDiagnosticFacing(float yaw, float pitch)
		{
			PlayerMouseLook mouseLook = GameManager.Instance.PlayerMouseLook;
			mouseLook.SetFacing(yaw, pitch);
			if (mouseLook.characterBody != null)
			{
				mouseLook.transform.localEulerAngles = new Vector3(pitch, 0f, 0f);
				mouseLook.characterBody.transform.localEulerAngles = new Vector3(0f, yaw, 0f);
			}
			else
			{
				mouseLook.transform.localEulerAngles = new Vector3(pitch, yaw, 0f);
			}
			Physics.SyncTransforms();
		}

        private static void LogTerrainSurfaceSnapshot(string saveName, string label)
        {
            if (GameManager.Instance == null || GameManager.Instance.PlayerObject == null)
                return;

            Vector3 player = GameManager.Instance.PlayerObject.transform.position;
            DaggerfallTerrain[] terrains = UnityEngine.Object.FindObjectsOfType<DaggerfallTerrain>();
            var sb = new StringBuilder();
            sb.Append("[DeepWaters.Diagnostics] SurfaceSnapshot save=")
              .Append(saveName)
              .Append(" label=")
              .Append(label)
              .Append(" player=")
              .Append(VectorString(player));

            for (int i = 0; i < terrains.Length; i++)
            {
                DaggerfallTerrain dfTerrain = terrains[i];
                if (dfTerrain == null)
                    continue;

                Terrain unityTerrain = dfTerrain.GetComponent<Terrain>();
                TerrainData data = unityTerrain != null ? unityTerrain.terrainData : null;
                Vector3 center = dfTerrain.transform.position;
                if (data != null)
                    center += new Vector3(data.size.x * 0.5f, 0f, data.size.z * 0.5f);

                if ((center - player).sqrMagnitude > 2200f * 2200f)
                    continue;

                Transform surface = dfTerrain.transform.Find("DeepWaters_Surface");
                MeshFilter filter = surface != null ? surface.GetComponentInChildren<MeshFilter>() : null;
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();
                Material material = unityTerrain != null ? unityTerrain.materialTemplate : null;

                sb.Append(" | tile=")
                  .Append(dfTerrain.MapPixelX)
                  .Append(":")
                  .Append(dfTerrain.MapPixelY)
                  .Append(" dist=")
                  .Append(Vector3.Distance(center, player).ToString("F0", CultureInfo.InvariantCulture))
                  .Append(" draw=")
                  .Append(unityTerrain != null && unityTerrain.drawHeightmap ? "1" : "0")
                  .Append(" surf=")
                  .Append(surface != null ? "1" : "0")
                  .Append(" verts=")
                  .Append(mesh != null ? mesh.vertexCount.ToString(CultureInfo.InvariantCulture) : "0")
                  .Append(" ocean=")
                  .Append(tile != null && tile.IsOceanConnected ? "1" : "0")
                  .Append(" df=")
                  .Append(tile != null && tile.HasDistanceField ? "1" : "0")
                  .Append(" shader=")
                  .Append(material != null && material.shader != null ? material.shader.name : "none");
            }

            string line = sb.ToString();
            Debug.Log(line);

            string dir = Path.Combine(Application.persistentDataPath, "DeepWatersDiagnostics");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "surface-snapshots.log"),
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }

        private static void LogShoreProfileSnapshot(string saveName, string label)
        {
            if (GameManager.Instance == null || GameManager.Instance.PlayerObject == null)
                return;

            Vector3 player = GameManager.Instance.PlayerObject.transform.position;
            Vector3 forward = GetCameraForwardFlat();
            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.forward;

            var sb = new StringBuilder();
            sb.Append("[DeepWaters.Diagnostics] ShoreProfile save=")
              .Append(saveName)
              .Append(" label=")
              .Append(label)
              .Append(" player=")
              .Append(VectorString(player))
              .Append(" forward=(")
              .Append(forward.x.ToString("F2", CultureInfo.InvariantCulture))
              .Append(",")
              .Append(forward.z.ToString("F2", CultureInfo.InvariantCulture))
              .Append(")");

            for (float distance = 0f; distance <= 360f; distance += 24f)
            {
                Vector3 sample = player + forward * distance;
                PlayerProbe probe = SamplePlayerProbe(sample);
                sb.Append(" | d=")
                  .Append(distance.ToString("F0", CultureInfo.InvariantCulture))
                  .Append(" pos=")
                  .Append(VectorString(sample))
                  .Append(" pix=")
                  .Append(probe.TerrainPixel)
                  .Append(" frac=")
                  .Append(FloatCell(probe.LocalFracX))
                  .Append("/")
                  .Append(FloatCell(probe.LocalFracZ))
                  .Append(" h=")
                  .Append(FloatCell(probe.HeightSample))
                  .Append(" terrainY=")
                  .Append(FloatCell(probe.TerrainSampleWorldY))
                  .Append(" water=")
                  .Append(probe.LocalPointWater ? "1" : "0")
                  .Append(" baked=")
                  .Append(probe.BakedWater ? "1" : "0")
                  .Append(" carved=")
                  .Append(probe.CarvedWater ? "1" : "0")
                  .Append(" col=")
                  .Append(probe.ColumnPresent ? "1" : "0")
                  .Append(" depth=")
                  .Append(FloatCell(probe.ColumnDepth))
                  .Append(" floor=")
                  .Append(FloatCell(probe.CarvedSeafloorY))
                  .Append(" down=")
                  .Append(FloatCell(probe.DownHitY))
                  .Append("/")
                  .Append(FloatCell(probe.DownHitNormalY));
            }

            string line = sb.ToString();
            Debug.Log(line);

            string dir = Path.Combine(Application.persistentDataPath, "DeepWatersDiagnostics");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "shore-profiles.log"),
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }

        // Definitive per-texel classification dump for the camera's tile and its
        // north neighbour (where the shore spit continues). Two aligned grids per
        // tile: terrain height above ocean, and a 3-bit classifier code
        // localWater|baked|carved ('0'=all land .. '7'=all water). North is at the
        // top, west on the left; 'C' marks the camera cell.
        private static void LogClassificationGrid(string saveName, string label)
        {
            GameManager gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.PlayerObject == null)
                return;

            Vector3 player = gameManager.PlayerObject.transform.position;
            float tileWorldSize = DeepWaterWorld.TileWorldSize;
            if (tileWorldSize <= 0f)
                return;

            float oceanY;
            if (!DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY))
                oceanY = player.y;

            var sb = new StringBuilder();
            sb.Append("[DeepWaters.Diagnostics] ClassGrid save=").Append(saveName)
              .Append(" label=").Append(label)
              .Append(" player=").Append(VectorString(player))
              .Append(" oceanY=").Append(oceanY.ToString("F2", CultureInfo.InvariantCulture))
              .Append(Environment.NewLine)
              .Append("height: ' '=submerged '.'<0.25 ':'<0.5 '-'<1 '+'<2 '#'>=2  (above ocean, m)")
              .Append(Environment.NewLine)
              .Append("class:  bit0=localWater bit1=baked bit2=carved  ('0'=all land '7'=all water)  C=camera")
              .Append(Environment.NewLine);

            AppendTileGrid(sb, player.x, player.z, tileWorldSize, oceanY, player);
            AppendTileGrid(sb, player.x, player.z + tileWorldSize, tileWorldSize, oceanY, player);

            string text = sb.ToString();
            Debug.Log(text);

            string dir = Path.Combine(Application.persistentDataPath, "DeepWatersDiagnostics");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "classification-grids.log"),
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine);
        }

        private static void AppendTileGrid(
            StringBuilder sb, float sampleX, float sampleZ, float tileWorldSize, float oceanY, Vector3 player)
        {
            DaggerfallTerrain dfTerrain;
            Terrain terrain;
            if (!DeepWaterTerrainLookup.TryGetByWorldPosition(sampleX, sampleZ, out dfTerrain, out terrain) ||
                dfTerrain == null || terrain == null)
            {
                sb.Append("tile @(").Append(sampleX.ToString("F0", CultureInfo.InvariantCulture)).Append(",")
                  .Append(sampleZ.ToString("F0", CultureInfo.InvariantCulture)).Append("): not loaded")
                  .Append(Environment.NewLine);
                return;
            }

            const int n = 64;
            Vector3 origin = dfTerrain.transform.position;
            DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();

            // Cap renderer's own data source + decoding, so we see its per-cell verdict.
            Color32[] tileMap = dfTerrain.TileMap;
            int tmDim = tileMap != null ? Mathf.RoundToInt(Mathf.Sqrt(tileMap.Length)) : 0;
            Material mat = terrain.materialTemplate;
            bool textureArray = mat != null && mat.shader != null && mat.shader.name.Contains("TextureArray");

            int camX = Mathf.FloorToInt((player.x - origin.x) / tileWorldSize * n);
            int camZ = Mathf.FloorToInt((player.z - origin.z) / tileWorldSize * n);

            sb.Append("=== tile ").Append(dfTerrain.MapPixelX).Append(":").Append(dfTerrain.MapPixelY)
              .Append(" (n=").Append(n).Append(", ~").Append((tileWorldSize / n).ToString("F0", CultureInfo.InvariantCulture))
              .Append("m/cell, north=top) ===").Append(Environment.NewLine);

            var heightRows = new StringBuilder();
            var classRows = new StringBuilder();
            var capRows = new StringBuilder();
            for (int cz = n - 1; cz >= 0; cz--)
            {
                float fracZ = (cz + 0.5f) / n;
                for (int cx = 0; cx < n; cx++)
                {
                    float fracX = (cx + 0.5f) / n;
                    float worldX = origin.x + fracX * tileWorldSize;
                    float worldZ = origin.z + fracZ * tileWorldSize;
                    bool isCam = cx == camX && cz == camZ;

                    float terrainY = terrain.SampleHeight(new Vector3(worldX, 0f, worldZ)) + origin.y;
                    float above = terrainY - oceanY;
                    heightRows.Append(isCam ? 'C' : HeightChar(above));

                    int lw = DeepWaterWaterClassification.IsLocalPointWater(dfTerrain.MapData, fracX, fracZ) ? 1 : 0;
                    int bk = tile != null && tile.IsBakedWater(worldX, worldZ) ? 2 : 0;
                    int cv = tile != null && tile.IsCarvedWater(worldX, worldZ) ? 4 : 0;
                    classRows.Append(isCam ? 'C' : (char)('0' + (lw | bk | cv)));

                    // Cap renderer per-cell verdict: '.'=not a clip-water texel,
                    // 'X'=water texel that IS clipped, 'W'=water texel NOT clipped (pokes).
                    char capChar = '.';
                    if (tileMap != null && tmDim > 0)
                    {
                        int tx = Mathf.Clamp((int)(fracX * tmDim), 0, tmDim - 1);
                        int tz = Mathf.Clamp((int)(fracZ * tmDim), 0, tmDim - 1);
                        byte a = tileMap[tz * tmDim + tx].a;
                        if (DeepWaterTerrainCapRenderer.IsClippedWaterTileData(a, textureArray))
                        {
                            capChar = DeepWaterTerrainCapRenderer.ShouldClipPromotedWaterTexel(
                                dfTerrain, a, textureArray, tx, tz, tmDim) ? 'X' : 'W';
                        }
                    }
                    capRows.Append(isCam ? 'C' : capChar);
                }
                heightRows.Append(Environment.NewLine);
                classRows.Append(Environment.NewLine);
                capRows.Append(Environment.NewLine);
            }

            sb.Append("[HEIGHT]").Append(Environment.NewLine).Append(heightRows)
              .Append("[CLASS]").Append(Environment.NewLine).Append(classRows)
              .Append("[CAP] .=not-water-texel X=clipped W=water-NOT-clipped(pokes)").Append(Environment.NewLine).Append(capRows);
        }

        private static void LogSpawnDiagnostics(string saveName, string label)
        {
            var s = DeepWaters.Instance;
            string line = "[DeepWaters.Diagnostics] Spawn save=" + saveName + " label=" + label +
                " fishLive=" + UnderwaterPassiveFishSpawner.LiveCount +
                " enemyLive=" + UnderwaterEnemySpawner.LiveCount +
                " | fishCap=" + (s != null ? UnderwaterPassiveFishSpawner.EffectiveFishCap() : 0) +
                " enemyCap=" + (s != null ? UnderwaterEnemySpawner.EffectiveEnemyCap() : 0) +
                " fishFreq=" + (s != null ? s.PassiveFishFrequency.ToString("F2", CultureInfo.InvariantCulture) : "0") +
                " enemyFreq=" + (s != null ? s.EnemyFrequency.ToString("F2", CultureInfo.InvariantCulture) : "0") +
                " waterDepth=" + (s != null ? s.WaterDepth.ToString("F0", CultureInfo.InvariantCulture) : "0");

            Debug.Log(line);
            string dir = Path.Combine(Application.persistentDataPath, "DeepWatersDiagnostics");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "spawn-counts.log"),
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }

        private static char HeightChar(float aboveOcean)
        {
            if (aboveOcean <= 0f) return ' ';
            if (aboveOcean < 0.25f) return '.';
            if (aboveOcean < 0.5f) return ':';
            if (aboveOcean < 1f) return '-';
            if (aboveOcean < 2f) return '+';
            return '#';
        }

        private void WriteSnapshot(string saveName, string phase, string eventName)
        {
            WriteSnapshot(saveName, phase, eventName, float.NaN, float.NaN);
        }

        private void WriteMovementSnapshot(string saveName, string phase, string eventName)
        {
            float horizontalSpeed = float.NaN;
            float verticalSpeed = float.NaN;
            if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
            {
                Vector3 player = GameManager.Instance.PlayerObject.transform.position;
                float now = Time.realtimeSinceStartup;
                if (hasLastFramePlayer)
                {
                    float dt = Mathf.Max(0.0001f, now - lastFrameTime);
                    Vector3 delta = player - lastFramePlayer;
                    horizontalSpeed = new Vector2(delta.x, delta.z).magnitude / dt;
                    verticalSpeed = delta.y / dt;
                }

                lastFramePlayer = player;
                lastFrameTime = now;
                hasLastFramePlayer = true;
            }

            WriteSnapshot(saveName, phase, eventName, horizontalSpeed, verticalSpeed);
        }

        private void ResetFrameMovementProbe()
        {
            hasLastFramePlayer = false;
            lastFramePlayer = Vector3.zero;
            lastFrameTime = 0f;
        }

        private void WriteSnapshot(string saveName, string phase, string eventName, float horizontalSpeed, float verticalSpeed)
        {
            if (writer == null)
                return;

            DFPosition current;
            if (!TryGetCurrentPixel(out current))
                current = hasLastPixel ? lastPixel : new DFPosition();

            DFPosition former = hasLastPixel ? lastPixel : current;
            var window = new MetricWindow(saveName, phase, eventName, current, former);
            WriteWindow(window, 0f, 0f, CountPixelAverage(current), CountPixelAverage(former), SampleRuntimeState(), horizontalSpeed, verticalSpeed);
        }

        private void OpenWriter()
        {
            string dir = Path.Combine(Application.persistentDataPath, "DeepWatersDiagnostics");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "deep-waters-diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
            writer = new StreamWriter(path, false, Encoding.UTF8);
            writer.AutoFlush = true;
            writer.WriteLine("utc,save,phase,event,currentPixel,formerPixel,seconds,fps,decorationsCurrent,decorationsFormer,enemiesCurrent,enemiesFormer,fishCurrent,fishFormer,lootCurrent,lootFormer,rubbleCurrent,rubbleFormer,contentEligibleCurrent,contentEligibleFormer,loadGateActive,loadGateCount,loadGateAge,terrainUpdateActive,loadGraceActive,heavyWorkBlocked,heavyWorkResumeIn,postRefreshPending,decorQueue,decorQueuedTerrains,locationSkippedLast,locationDeferred,playerX,playerY,playerZ,oceanY,columnPresent,columnDepth,renderedSeafloorY,carvedPresent,carvedSeafloorY,downHitY,downHitNormalY,downHitShore,playerSwimming,controllerGrounded,cameraYaw,cameraPitch,cameraTransformYaw,cameraForwardX,cameraForwardZ,playerTransformYaw,worldCompX,worldCompY,worldCompZ,gpsWorldX,gpsWorldZ,terrainPixel,localFracX,localFracZ,tileValue,tileIndex,heightSample,localPointWater,bakedWater,carvedWater,rawFineWater,localMissedByFineBake,oceanConnected,horizontalSpeed,verticalSpeed,waterGateActive,waterGateDisabled,waterGateDesired");
        }

        private void WriteWindow(MetricWindow window, float seconds, float fps, Counts current, Counts former, RuntimeState runtime)
        {
            WriteWindow(window, seconds, fps, current, former, runtime, float.NaN, float.NaN);
        }

        private void WriteWindow(MetricWindow window, float seconds, float fps, Counts current, Counts former, RuntimeState runtime, float horizontalSpeed, float verticalSpeed)
        {
            Vector3 player = Vector3.zero;
            if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
                player = GameManager.Instance.PlayerObject.transform.position;
            PlayerProbe probe = SamplePlayerProbe(player);

            writer.WriteLine(string.Join(",",
                Csv(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)),
                Csv(window.SaveName),
                Csv(window.Phase),
                Csv(window.EventName),
                Csv(PixelString(window.CurrentPixel)),
                Csv(PixelString(window.FormerPixel)),
                seconds.ToString("F2", CultureInfo.InvariantCulture),
                fps.ToString("F2", CultureInfo.InvariantCulture),
                current.Decorations.ToString("F2", CultureInfo.InvariantCulture),
                former.Decorations.ToString("F2", CultureInfo.InvariantCulture),
                current.Enemies.ToString("F2", CultureInfo.InvariantCulture),
                former.Enemies.ToString("F2", CultureInfo.InvariantCulture),
                current.Fish.ToString("F2", CultureInfo.InvariantCulture),
                former.Fish.ToString("F2", CultureInfo.InvariantCulture),
                current.Loot.ToString("F2", CultureInfo.InvariantCulture),
                former.Loot.ToString("F2", CultureInfo.InvariantCulture),
                current.Rubble.ToString("F2", CultureInfo.InvariantCulture),
                former.Rubble.ToString("F2", CultureInfo.InvariantCulture),
                IsContentEligible(window.CurrentPixel) ? "1" : "0",
                IsContentEligible(window.FormerPixel) ? "1" : "0",
                runtime.LoadGateActive.ToString("F2", CultureInfo.InvariantCulture),
                runtime.LoadGateCount.ToString("F2", CultureInfo.InvariantCulture),
                runtime.LoadGateAge.ToString("F2", CultureInfo.InvariantCulture),
                runtime.TerrainUpdateActive.ToString("F2", CultureInfo.InvariantCulture),
                runtime.LoadGraceActive.ToString("F2", CultureInfo.InvariantCulture),
                runtime.HeavyWorkBlocked.ToString("F2", CultureInfo.InvariantCulture),
                runtime.HeavyWorkResumeIn.ToString("F2", CultureInfo.InvariantCulture),
                runtime.PostRefreshPending.ToString("F2", CultureInfo.InvariantCulture),
                runtime.DecorQueue.ToString("F2", CultureInfo.InvariantCulture),
                runtime.DecorQueuedTerrains.ToString("F2", CultureInfo.InvariantCulture),
                runtime.LocationSkippedLast.ToString("F2", CultureInfo.InvariantCulture),
                runtime.LocationDeferred.ToString("F2", CultureInfo.InvariantCulture),
                player.x.ToString("F2", CultureInfo.InvariantCulture),
                player.y.ToString("F2", CultureInfo.InvariantCulture),
                player.z.ToString("F2", CultureInfo.InvariantCulture),
                FloatCell(probe.OceanY),
                probe.ColumnPresent ? "1" : "0",
                FloatCell(probe.ColumnDepth),
                FloatCell(probe.RenderedSeafloorY),
                probe.CarvedPresent ? "1" : "0",
                FloatCell(probe.CarvedSeafloorY),
                FloatCell(probe.DownHitY),
                FloatCell(probe.DownHitNormalY),
                probe.DownHitShore ? "1" : "0",
                probe.PlayerSwimming ? "1" : "0",
                probe.ControllerGrounded ? "1" : "0",
                FloatCell(probe.CameraYaw),
                FloatCell(probe.CameraPitch),
                FloatCell(probe.CameraTransformYaw),
                FloatCell(probe.CameraForwardX),
                FloatCell(probe.CameraForwardZ),
                FloatCell(probe.PlayerTransformYaw),
                FloatCell(probe.WorldCompensation.x),
                FloatCell(probe.WorldCompensation.y),
                FloatCell(probe.WorldCompensation.z),
                FloatCell(probe.GpsWorldX),
                FloatCell(probe.GpsWorldZ),
                probe.TerrainPixel,
                FloatCell(probe.LocalFracX),
                FloatCell(probe.LocalFracZ),
                probe.TileValue >= 0 ? probe.TileValue.ToString(CultureInfo.InvariantCulture) : string.Empty,
                probe.TileIndex >= 0 ? probe.TileIndex.ToString(CultureInfo.InvariantCulture) : string.Empty,
                FloatCell(probe.HeightSample),
                probe.LocalPointWater ? "1" : "0",
                probe.BakedWater ? "1" : "0",
                probe.CarvedWater ? "1" : "0",
                probe.RawFineWater ? "1" : "0",
                probe.LocalMissedByFineBake ? "1" : "0",
                probe.OceanConnected ? "1" : "0",
                FloatCell(horizontalSpeed),
                FloatCell(verticalSpeed),
                OutdoorSwimDriver.DiagnosticWaterColliderGateActive ? "1" : "0",
                OutdoorSwimDriver.DiagnosticDisabledWaterColliderCount.ToString(CultureInfo.InvariantCulture),
                OutdoorSwimDriver.DiagnosticDesiredWaterColliderCount.ToString(CultureInfo.InvariantCulture)));
            writer.Flush();
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private static string FloatCell(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? string.Empty
                : value.ToString("F2", CultureInfo.InvariantCulture);
        }

        private static PlayerProbe SamplePlayerProbe(Vector3 player)
        {
            PlayerProbe probe = new PlayerProbe
            {
                OceanY = float.NaN,
                ColumnDepth = float.NaN,
                RenderedSeafloorY = float.NaN,
                CarvedSeafloorY = float.NaN,
                DownHitY = float.NaN,
                DownHitNormalY = float.NaN,
                CameraYaw = float.NaN,
                CameraPitch = float.NaN,
                CameraTransformYaw = float.NaN,
                CameraForwardX = float.NaN,
                CameraForwardZ = float.NaN,
                PlayerTransformYaw = float.NaN,
                WorldCompensation = new Vector3(float.NaN, float.NaN, float.NaN),
                GpsWorldX = float.NaN,
                GpsWorldZ = float.NaN,
                TerrainPixel = string.Empty,
                LocalFracX = float.NaN,
                LocalFracZ = float.NaN,
                TileValue = -1,
                TileIndex = -1,
                HeightSample = float.NaN,
                TerrainSampleWorldY = float.NaN
            };

            float oceanY;
            if (DeepWaterWorld.TryGetOceanSurfaceWorldY(out oceanY))
                probe.OceanY = oceanY;

            DeepWaterColumn column;
            if (DeepWaterWorld.TryGetWaterColumn(player.x, player.z, out column))
            {
                probe.ColumnPresent = true;
                probe.ColumnDepth = column.Depth;

                float renderedY;
                if (DeepWaterWorld.TryGetRenderedSeafloorWorldY(column, player.x, player.z, out renderedY))
                    probe.RenderedSeafloorY = renderedY;
            }

            float carvedY;
            if (DeepWaterWorld.TryGetCarvedSeafloorWorldY(player.x, player.z, out carvedY))
            {
                probe.CarvedPresent = true;
                probe.CarvedSeafloorY = carvedY;
            }

            RaycastHit hit;
            if (Physics.Raycast(player + Vector3.up * 3f, Vector3.down, out hit, 30f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                probe.DownHitY = hit.point.y;
                probe.DownHitNormalY = hit.normal.y;
                probe.DownHitShore = !float.IsNaN(probe.OceanY) && OutdoorShoreExitAssist.IsValidShoreStandingHit(hit, probe.OceanY);
            }

            GameManager gameManager = GameManager.Instance;
            if (gameManager != null)
            {
                probe.PlayerSwimming = gameManager.PlayerEnterExit != null && gameManager.PlayerEnterExit.IsPlayerSwimming;
                CharacterController controller = gameManager.PlayerObject != null
                    ? gameManager.PlayerObject.GetComponent<CharacterController>()
                    : null;
                probe.ControllerGrounded = controller != null && controller.isGrounded;

                PlayerMouseLook mouseLook = gameManager.PlayerMouseLook;
                if (mouseLook != null)
                {
                    probe.CameraYaw = mouseLook.Yaw;
                    probe.CameraPitch = mouseLook.Pitch;
                }

                Camera mainCamera = gameManager.MainCamera;
                if (mainCamera != null)
                {
                    Transform cameraTransform = mainCamera.transform;
                    Vector3 forward = cameraTransform.forward;
                    probe.CameraTransformYaw = cameraTransform.eulerAngles.y;
                    probe.CameraForwardX = forward.x;
                    probe.CameraForwardZ = forward.z;
                }

                if (gameManager.PlayerObject != null)
                    probe.PlayerTransformYaw = gameManager.PlayerObject.transform.eulerAngles.y;

                if (gameManager.StreamingWorld != null)
                    probe.WorldCompensation = gameManager.StreamingWorld.WorldCompensation;

                if (gameManager.PlayerGPS != null)
                {
                    probe.GpsWorldX = gameManager.PlayerGPS.WorldX;
                    probe.GpsWorldZ = gameManager.PlayerGPS.WorldZ;
                }
            }

            DaggerfallTerrain dfTerrain;
            Terrain terrain;
            if (DeepWaterTerrainLookup.TryGetByWorldPosition(player.x, player.z, out dfTerrain, out terrain) &&
                dfTerrain != null)
            {
                float tileWorldSize = DeepWaterWorld.TileWorldSize;
                float fracX = tileWorldSize > 0f ? (player.x - dfTerrain.transform.position.x) / tileWorldSize : float.NaN;
                float fracZ = tileWorldSize > 0f ? (player.z - dfTerrain.transform.position.z) / tileWorldSize : float.NaN;
                probe.TerrainPixel = dfTerrain.MapPixelX + ":" + dfTerrain.MapPixelY;
                probe.LocalFracX = fracX;
                probe.LocalFracZ = fracZ;
                probe.LocalPointWater = DeepWaterWaterClassification.IsLocalPointWater(dfTerrain.MapData, fracX, fracZ);

                DeepWaterTileData tile = dfTerrain.GetComponent<DeepWaterTileData>();
                if (tile != null)
                {
                    probe.BakedWater = tile.IsBakedWater(player.x, player.z);
                    probe.CarvedWater = tile.IsCarvedWater(player.x, player.z);
                    probe.RawFineWater = DeepWaterDistanceBake.IsCarvedWater(dfTerrain.MapPixelX, dfTerrain.MapPixelY, fracX, fracZ);
                    probe.LocalMissedByFineBake = probe.LocalPointWater && !probe.RawFineWater;
                    probe.OceanConnected = tile.IsOceanConnected;
                }

                if (terrain != null)
                    probe.TerrainSampleWorldY = terrain.SampleHeight(player) + terrain.transform.position.y;

                byte[,] tilemap = dfTerrain.MapData.tilemapSamples;
                if (tilemap != null)
                {
                    int rows = tilemap.GetLength(0);
                    int cols = tilemap.GetLength(1);
                    int x = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(fracX) * cols), 0, cols - 1);
                    int y = Mathf.Clamp(Mathf.FloorToInt(Mathf.Clamp01(fracZ) * rows), 0, rows - 1);
                    probe.TileValue = tilemap[y, x];
                    probe.TileIndex = probe.TileValue & 0x3f;
                }

                float[,] heights = dfTerrain.MapData.heightmapSamples;
                if (heights != null)
                {
                    int rows = heights.GetLength(0);
                    int cols = heights.GetLength(1);
                    int x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(fracX) * (cols - 1)), 0, cols - 1);
                    int y = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(fracZ) * (rows - 1)), 0, rows - 1);
                    probe.HeightSample = heights[y, x];
                }
            }

            return probe;
        }

        private static string PixelString(DFPosition pixel)
        {
            return pixel.X + ":" + pixel.Y;
        }

        private static bool IsContentEligible(DFPosition pixel)
        {
            if (!DeepWaterDistanceBake.IsLoaded)
                return false;

            if (DeepWaterDistanceBake.HasFineWaterMask)
                return DeepWaterDistanceBake.MapPixelHasFineWaterCells(pixel.X, pixel.Y);

            return DeepWaterDistanceBake.MapPixelHasWaterCells(pixel.X, pixel.Y);
        }

        private static Counts CountPixelAverage(DFPosition pixel)
        {
            Counts counts = new Counts();
            CountDecorationRenderers(pixel, ref counts);
            CountEnemies(pixel, ref counts);
            CountFish(pixel, ref counts);
            CountLoot(pixel, ref counts);
            return counts;
        }

        private static RuntimeState SampleRuntimeState()
        {
            RuntimeState state = new RuntimeState();
            state.LoadGateCount = DeepWaterRuntime.ActiveLocationLoadCount;
            state.LoadGateActive = state.LoadGateCount > 0f ? 1f : 0f;
            state.LoadGateAge = DeepWaterRuntime.ActiveLocationLoadAgeSeconds;
            state.TerrainUpdateActive = DeepWaterRuntime.IsTerrainUpdateActive ? 1f : 0f;
            state.LoadGraceActive = DeepWaterRuntime.IsLoadGraceActive ? 1f : 0f;
            state.HeavyWorkBlocked = DeepWaterRuntime.CanRunHeavyRuntimeWork ? 0f : 1f;
            state.HeavyWorkResumeIn = DeepWaterRuntime.HeavyWorkResumeInSeconds;
            state.PostRefreshPending = DeepWaterRuntime.IsPostTransitionRefreshPending ? 1f : 0f;
            state.DecorQueue = UnderwaterDecorations.PendingWorkCount;
            state.DecorQueuedTerrains = UnderwaterDecorations.QueuedTerrainCount;
            state.LocationSkippedLast = DeepWaterRuntime.LastLocationSkippedCount;
            state.LocationDeferred = DeepWaterRuntime.DeferredLocationCount;
            return state;
        }

        private static void CountDecorationRenderers(DFPosition pixel, ref Counts counts)
        {
            MeshRenderer[] renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                    continue;

                bool isDecoration = IsUnderTransformNamed(renderer.transform, UnderwaterDecorationBatchFactory.GroupName);
                bool isRubble = IsUnderTransformNamed(renderer.transform, "DeepWaters_LootRubbleBatch");
                if (!isDecoration && !isRubble)
                    continue;

                if (!IsInPixel(renderer.transform, pixel))
                    continue;

                int count = CountBillboards(renderer);
                if (isRubble)
                    counts.Rubble += count;
                else
                    counts.Decorations += count;
            }
        }

        private static void CountEnemies(DFPosition pixel, ref Counts counts)
        {
            DaggerfallEnemy[] enemies = UnityEngine.Object.FindObjectsOfType<DaggerfallEnemy>();
            for (int i = 0; i < enemies.Length; i++)
            {
                DaggerfallEnemy enemy = enemies[i];
                if (enemy != null && IsInPixel(enemy.transform, pixel) && IsInWater(enemy.transform.position))
                    counts.Enemies++;
            }
        }

        private static void CountFish(DFPosition pixel, ref Counts counts)
        {
            PassiveFishBehaviour[] fish = UnityEngine.Object.FindObjectsOfType<PassiveFishBehaviour>();
            for (int i = 0; i < fish.Length; i++)
            {
                if (fish[i] != null && IsInPixel(fish[i].transform, pixel))
                    counts.Fish++;
            }
        }

        private static void CountLoot(DFPosition pixel, ref Counts counts)
        {
            DaggerfallLoot[] loot = UnityEngine.Object.FindObjectsOfType<DaggerfallLoot>();
            for (int i = 0; i < loot.Length; i++)
            {
                DaggerfallLoot item = loot[i];
                if (item == null || item.GetComponent<PassiveFishBehaviour>() != null)
                    continue;

                if (IsInPixel(item.transform, pixel) && IsInWater(item.transform.position))
                    counts.Loot++;
            }
        }

        private static bool IsInWater(Vector3 position)
        {
            DeepWaterColumn column;
            return DeepWaterWorld.TryGetWaterColumn(position.x, position.z, out column) &&
                   position.y <= column.OceanWorldY + 3f;
        }

        private static bool IsInPixel(Transform transform, DFPosition pixel)
        {
            DaggerfallTerrain terrain = transform.GetComponentInParent<DaggerfallTerrain>();
            if (terrain == null)
            {
                Terrain unityTerrain;
                DeepWaterTerrainLookup.TryGetByWorldPosition(transform.position.x, transform.position.z, out terrain, out unityTerrain);
            }

            return terrain != null && terrain.MapPixelX == pixel.X && terrain.MapPixelY == pixel.Y;
        }

        private static bool IsUnderTransformNamed(Transform transform, string name)
        {
            while (transform != null)
            {
                if (transform.name == name)
                    return true;

                transform = transform.parent;
            }

            return false;
        }

        private static int CountBillboards(MeshRenderer renderer)
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh != null && mesh.vertexCount >= 4)
                return Mathf.Max(1, mesh.vertexCount / 4);

            return 1;
        }

        private struct Counts
        {
            public float Decorations;
            public float Enemies;
            public float Fish;
            public float Loot;
            public float Rubble;

            public void Add(Counts other)
            {
                Decorations += other.Decorations;
                Enemies += other.Enemies;
                Fish += other.Fish;
                Loot += other.Loot;
                Rubble += other.Rubble;
            }

            public Counts Average(int samples)
            {
                if (samples <= 0)
                    return this;

                float inv = 1f / samples;
                Decorations *= inv;
                Enemies *= inv;
                Fish *= inv;
                Loot *= inv;
                Rubble *= inv;
                return this;
            }
        }

        private struct RuntimeState
        {
            public float LoadGateActive;
            public float LoadGateCount;
            public float LoadGateAge;
            public float TerrainUpdateActive;
            public float LoadGraceActive;
            public float HeavyWorkBlocked;
            public float HeavyWorkResumeIn;
            public float PostRefreshPending;
            public float DecorQueue;
            public float DecorQueuedTerrains;
            public float LocationSkippedLast;
            public float LocationDeferred;

            public void Add(RuntimeState other)
            {
                LoadGateActive += other.LoadGateActive;
                LoadGateCount += other.LoadGateCount;
                LoadGateAge += other.LoadGateAge;
                TerrainUpdateActive += other.TerrainUpdateActive;
                LoadGraceActive += other.LoadGraceActive;
                HeavyWorkBlocked += other.HeavyWorkBlocked;
                HeavyWorkResumeIn += other.HeavyWorkResumeIn;
                PostRefreshPending += other.PostRefreshPending;
                DecorQueue += other.DecorQueue;
                DecorQueuedTerrains += other.DecorQueuedTerrains;
                LocationSkippedLast += other.LocationSkippedLast;
                LocationDeferred += other.LocationDeferred;
            }

            public RuntimeState Average(int samples)
            {
                if (samples <= 0)
                    return this;

                float inv = 1f / samples;
                LoadGateActive *= inv;
                LoadGateCount *= inv;
                LoadGateAge *= inv;
                TerrainUpdateActive *= inv;
                LoadGraceActive *= inv;
                HeavyWorkBlocked *= inv;
                HeavyWorkResumeIn *= inv;
                PostRefreshPending *= inv;
                DecorQueue *= inv;
                DecorQueuedTerrains *= inv;
                LocationSkippedLast *= inv;
                LocationDeferred *= inv;
                return this;
            }
        }

        private struct PlayerProbe
        {
            public float OceanY;
            public bool ColumnPresent;
            public float ColumnDepth;
            public float RenderedSeafloorY;
            public bool CarvedPresent;
            public float CarvedSeafloorY;
            public float DownHitY;
            public float DownHitNormalY;
            public bool DownHitShore;
            public bool PlayerSwimming;
            public bool ControllerGrounded;
            public float CameraYaw;
            public float CameraPitch;
            public float CameraTransformYaw;
            public float CameraForwardX;
            public float CameraForwardZ;
            public float PlayerTransformYaw;
            public Vector3 WorldCompensation;
            public float GpsWorldX;
            public float GpsWorldZ;
            public string TerrainPixel;
            public float LocalFracX;
            public float LocalFracZ;
            public int TileValue;
            public int TileIndex;
            public float HeightSample;
            public float TerrainSampleWorldY;
            public bool LocalPointWater;
            public bool BakedWater;
            public bool CarvedWater;
            public bool RawFineWater;
            public bool LocalMissedByFineBake;
            public bool OceanConnected;
        }

        private struct SavedPlayerPose
        {
            public Vector3 Position;
            public Vector3 WorldCompensation;
            public float Yaw;
            public float Pitch;
        }

        private sealed class MetricWindow
        {
            public readonly string SaveName;
            public readonly string Phase;
            public readonly string EventName;
            public readonly DFPosition CurrentPixel;
            public readonly DFPosition FormerPixel;

            private readonly float startTime;
            private float nextSampleTime;
            private int frames;
            private int samples;
            private Counts currentSum;
            private Counts formerSum;
            private RuntimeState runtimeSum;

            public MetricWindow(string saveName, string phase, string eventName, DFPosition currentPixel, DFPosition formerPixel)
            {
                SaveName = saveName;
                Phase = phase;
                EventName = eventName;
                CurrentPixel = currentPixel;
                FormerPixel = formerPixel;
                startTime = Time.realtimeSinceStartup;
                nextSampleTime = startTime;
            }

            public bool Update(DeepWaterDiagnosticsRunner owner)
            {
                frames++;
                float now = Time.realtimeSinceStartup;
                if (now >= nextSampleTime)
                {
                    currentSum.Add(CountPixelAverage(CurrentPixel));
                    formerSum.Add(CountPixelAverage(FormerPixel));
                    runtimeSum.Add(SampleRuntimeState());
                    samples++;
                    nextSampleTime = now + 1f;
                }

                float elapsed = now - startTime;
                if (elapsed < TransitionWindowSeconds)
                    return false;

                float fps = frames / Mathf.Max(0.001f, elapsed);
                owner.WriteWindow(this, elapsed, fps, currentSum.Average(samples), formerSum.Average(samples), runtimeSum.Average(samples));
                return true;
            }
        }
    }
}
