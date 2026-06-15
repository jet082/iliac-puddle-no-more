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
            DeepWaterLocationLoadGate.Install();
            DeepWaterFloorBuilder.Install();
            DeepWaterRuntime.Install();
            DeepWaterLocationUpdateSkipper.Install();
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
            go.AddComponent<CutoutDepthQueueFix>();
            go.AddComponent<UnderwaterDistanceFog>();
            go.AddComponent<UnderwaterWaveShadowFix>();
            go.AddComponent<ArgonianWaterBreathing>();
            go.AddComponent<SwimmingSfxBridge>();
            go.AddComponent<UnderwaterWeatherSuppressor>();
            go.AddComponent<UnderwaterAmbientMuter>();
            DeepWaterDiagnosticsRunner.Install(go);
        }

        private void RegisterCustomItems()
        {
            int[] templateIndices = UnderwaterPassiveFishSpawner.CustomItemTemplateIndices;
            for (int i = 0; i < templateIndices.Length; i++)
            {
                DaggerfallUnity.Instance.ItemHelper.RegisterCustomItem(
                    templateIndices[i],
                    UnderwaterPassiveFishSpawner.FishItemGroup);
            }
        }

        private void WrapTerrainTexturing()
        {
            var inner = DaggerfallUnity.Instance.TerrainTexturing;
            if (inner is DeepWaterTexturing)
                return;

            DaggerfallUnity.Instance.TerrainTexturing = new DeepWaterTexturing(inner);
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

        public static void Install(GameObject host)
        {
            if (!IsEnabled())
                return;

            DeepWaterRuntime.DiagnosticProfiling = true;
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
                yield return RecordInitialWindow(saveName);
                yield return RunTraversal(saveName);
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

            Debug.Log("[DeepWaters.Diagnostics] Loading " + characterName + " / " + saveName);
            saveLoad.Load(key);
            yield return null;

            while (saveLoad.LoadInProgress)
                yield return null;

            CloseBlockingUi();
            yield return null;
            CloseBlockingUi();
            yield return new WaitForSecondsRealtime(2f);
            CloseBlockingUi();
            DisableNoisyRuntimeSystems();
            hasLastPixel = TryGetCurrentPixel(out lastPixel);
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

        private void OpenWriter()
        {
            string dir = Path.Combine(Application.persistentDataPath, "DeepWatersDiagnostics");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "deep-waters-diagnostics-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
            writer = new StreamWriter(path, false, Encoding.UTF8);
            writer.AutoFlush = true;
            writer.WriteLine("utc,save,phase,event,currentPixel,formerPixel,seconds,fps,decorationsCurrent,decorationsFormer,enemiesCurrent,enemiesFormer,fishCurrent,fishFormer,lootCurrent,lootFormer,rubbleCurrent,rubbleFormer,contentEligibleCurrent,contentEligibleFormer,loadGateActive,loadGateCount,loadGateAge,terrainUpdateActive,loadGraceActive,heavyWorkBlocked,heavyWorkResumeIn,postRefreshPending,decorQueue,decorQueuedTerrains,locationSkippedLast,locationDeferred,playerX,playerY,playerZ");
        }

        private void WriteWindow(MetricWindow window, float seconds, float fps, Counts current, Counts former, RuntimeState runtime)
        {
            Vector3 player = Vector3.zero;
            if (GameManager.Instance != null && GameManager.Instance.PlayerObject != null)
                player = GameManager.Instance.PlayerObject.transform.position;

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
                player.z.ToString("F2", CultureInfo.InvariantCulture)));
            writer.Flush();
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
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
            state.LoadGateCount = DeepWaterLocationLoadGate.ActiveLoadCount;
            state.LoadGateActive = state.LoadGateCount > 0f ? 1f : 0f;
            state.LoadGateAge = DeepWaterLocationLoadGate.ActiveLoadAgeSeconds;
            state.TerrainUpdateActive = DeepWaterRuntime.IsTerrainUpdateActive ? 1f : 0f;
            state.LoadGraceActive = DeepWaterRuntime.IsLoadGraceActive ? 1f : 0f;
            state.HeavyWorkBlocked = DeepWaterRuntime.CanRunHeavyRuntimeWork ? 0f : 1f;
            state.HeavyWorkResumeIn = DeepWaterRuntime.HeavyWorkResumeInSeconds;
            state.PostRefreshPending = DeepWaterRuntime.IsPostTransitionRefreshPending ? 1f : 0f;
            state.DecorQueue = UnderwaterDecorations.PendingWorkCount;
            state.DecorQueuedTerrains = UnderwaterDecorations.QueuedTerrainCount;
            state.LocationSkippedLast = DeepWaterLocationUpdateSkipper.LastSkippedCount;
            state.LocationDeferred = DeepWaterLocationUpdateSkipper.DeferredLocationCount;
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
