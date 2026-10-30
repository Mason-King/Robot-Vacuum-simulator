using System;
using System.Collections.Generic;
using System.IO;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using RobotVacuum.UI;
using UnityEditor;
using IconKind = RobotVacuum.LevelEditor.IconKind;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using F = RobotVacuum.EditorTools.UI.UiFactory;

namespace RobotVacuum.EditorTools.UI
{
    /// <summary>
    /// Generates the start screen as uGUI prefabs and puts it in its scene. Run it once: after that the
    /// prefabs are the source of truth and are edited like any Unity UI. Running it again keeps existing
    /// prefabs unless you choose to rebuild them, which throws away edits made to them.
    /// </summary>
    public static class StartScreenBuilder
    {
        public const string PrefabFolder = "Assets/UI/Prefabs";
        public const string FormPrefabPath = PrefabFolder + "/RunSettingsForm.prefab";
        public const string StartPrefabPath = PrefabFolder + "/StartScreen.prefab";

        [MenuItem("Robot Vacuum/UI/Build Start Screen")]
        public static void BuildStartScreen()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            bool rebuild = false;
            if (File.Exists(StartPrefabPath) || File.Exists(FormPrefabPath))
            {
                int choice = EditorUtility.DisplayDialogComplex("Build Start Screen",
                    "The start screen prefabs already exist. Keep them and just place them in the scene, or rebuild them from scratch? Rebuilding throws away changes made to them.",
                    "Keep and place", "Cancel", "Rebuild");
                if (choice == 1) return;
                rebuild = choice == 2;
            }

            Directory.CreateDirectory(PrefabFolder);
            var form = EnsureFormPrefab(rebuild);
            var start = rebuild || !File.Exists(StartPrefabPath) ? BuildStartPrefab(form) : AssetDatabase.LoadAssetAtPath<GameObject>(StartPrefabPath);
            PlaceInScene(SimLauncher.StartScenePath, start, typeof(StartScreenApp));
        }

        /// <summary>The shared run settings form, used by the Settings page and every Run setup page.</summary>
        public static GameObject EnsureFormPrefab(bool rebuild = false)
        {
            if (!rebuild && File.Exists(FormPrefabPath)) return AssetDatabase.LoadAssetAtPath<GameObject>(FormPrefabPath);

            var root = F.Go("Run Settings Form", null);
            F.Column(root, 14f);
            var view = root.AddComponent<RunSettingsFormView>();

            var movement = F.Card(root.transform, "Movement", "How the vacuum chooses where to go, and the seed behind its random choices.");
            F.FieldLabel(movement, "Algorithm");
            var algorithm = F.Segmented(movement, MovementBrain.Labels, "Algorithm");
            var random = F.Switch(movement, "New random seed every run", "Random Seed");
            var seedRow = F.Go("Seed Row", movement);
            F.Row(seedRow, 10f);
            var seedLabel = F.Label(seedRow.transform, "Seed", 12, UiTheme.Text3);
            F.Size(seedLabel.gameObject, 60f);
            var seed = F.Input(seedRow.transform, 140f, "1", "Seed");
            seed.contentType = InputField.ContentType.IntegerNumber;
            F.Hint(movement, "The same floor plan, settings and seed always give the same run.");

            var vacuum = F.Card(root.transform, "Vacuum", "Drive speeds and how much work a full battery holds.");
            var drive = F.Slider(vacuum, "Drive speed");
            var reverse = F.Slider(vacuum, "Reverse speed");
            var turn = F.Slider(vacuum, "Turn speed");
            var battery = F.Slider(vacuum, "Battery capacity");

            var cleaning = F.Card(root.transform, "Cleaning", "How the floor is measured. Set before a run and fixed for it.");
            var cell = F.Slider(cleaning, "Grid cell size");
            var power = F.Slider(cleaning, "Vacuum power");
            var target = F.Slider(cleaning, "Target cleanliness");
            F.Hint(cleaning, "A cell counts as fully clean once its cleanliness reaches the target. Power is cleanliness gained per second over the easiest floor.");
            F.FieldLabel(cleaning, "Heatmap the run opens with");
            var modes = new List<string>();
            foreach (HeatmapMode mode in Enum.GetValues(typeof(HeatmapMode))) modes.Add(HeatmapDefaults.DisplayName(mode));
            var heatmap = F.Segmented(cleaning, modes, "Heatmap");

            Wire.Set(view, "algorithm", algorithm);
            Wire.Set(view, "randomSeed", random);
            Wire.Set(view, "seed", seed);
            Wire.Set(view, "driveSpeed", drive);
            Wire.Set(view, "reverseSpeed", reverse);
            Wire.Set(view, "turnSpeed", turn);
            Wire.Set(view, "batteryCapacity", battery);
            Wire.Set(view, "cellSize", cell);
            Wire.Set(view, "vacuumPower", power);
            Wire.Set(view, "targetMaxClean", target);
            Wire.Set(view, "heatmap", heatmap);

            return Save(root, FormPrefabPath);
        }

        static GameObject BuildStartPrefab(GameObject formPrefab)
        {
            var root = F.Canvas("Start Screen");
            var app = root.AddComponent<StartScreenApp>();

            var background = F.Go("Background", root.transform);
            F.Stretch(background);
            F.Box(background, UiTheme.Bg);

            // ---------------------------------------------------------------- home
            var home = F.Go("Home Page", root.transform);
            F.Stretch(home);
            F.Column(home, 0f, alignment: TextAnchor.MiddleCenter, expandWidth: false);

            F.Icon(home.transform, IconKind.Logo, 64f, UiTheme.Accent);
            Gap(home.transform, 20f);
            F.Label(home.transform, "Robot Vacuum Simulator", 28, UiTheme.Text, FontStyle.Bold, TextAnchor.MiddleCenter, name: "Title");
            Gap(home.transform, 8f);
            var lead = F.Label(home.transform, "Pick a floor plan, set up the run, then watch the vacuum clean or run it headless.",
                13, UiTheme.Text2, alignment: TextAnchor.MiddleCenter, wrap: true, name: "Lead");
            F.Size(lead.gameObject, 420f);
            Gap(home.transform, 28f);

            var actions = F.Go("Actions", home.transform);
            F.Row(actions, 12f, alignment: TextAnchor.MiddleCenter);
            var startButton = F.Button(actions.transform, "Start simulation", IconKind.Play, ButtonKind.Primary, 40f);
            var analysisButton = F.Button(actions.transform, "Analysis", IconKind.Grid, ButtonKind.Ghost, 40f);
            var settingsButton = F.Button(actions.transform, "Settings", IconKind.Spawn, ButtonKind.Ghost, 40f);
            var quitButton = F.Button(actions.transform, "Quit", IconKind.Close, ButtonKind.Ghost, 40f);

            // ---------------------------------------------------------------- settings
            var settings = F.Go("Settings Page", root.transform);
            F.Stretch(settings);
            F.Column(settings, 0f);
            F.TopBar(settings.transform, "Settings", "The defaults every run starts from. Any run can change them for itself on its Run setup page.",
                out var settingsBack, out var settingsActions);
            var reset = F.Button(settingsActions, "Reset to defaults", IconKind.Undo, ButtonKind.Ghost);

            F.Scroll(settings.transform, out var content);
            F.Column(content.gameObject, 0f, F.Pad(16, 16, 24, 32), TextAnchor.UpperCenter, expandWidth: false);
            var column = F.Go("Column", content);
            F.Size(column, 620f);
            F.Column(column, 14f);

            var general = F.Card(column.transform, "General", "Applies across the whole app.");
            F.FieldLabel(general, "Units");
            var units = F.Segmented(general, new[] { "Imperial (ft)", "Metric (m)" }, "Units");

            var formInstance = (GameObject)PrefabUtility.InstantiatePrefab(formPrefab, column.transform);
            var form = formInstance.GetComponent<RunSettingsFormView>();

            // ---------------------------------------------------------------- analysis
            var analysis = F.Go("Analysis Page", root.transform);
            F.Stretch(analysis);
            F.Column(analysis, 0f);
            F.TopBar(analysis.transform, "Analysis", "Results of past runs", out var analysisBack, out _);
            var empty = F.Go("Empty State", analysis.transform);
            F.Size(empty, flexWidth: 1f, flexHeight: 1f);
            F.Box(empty, UiTheme.Canvas);
            F.Column(empty, 8f, alignment: TextAnchor.MiddleCenter, expandWidth: false);
            F.Icon(empty.transform, IconKind.Grid, 40f, UiTheme.Text3);
            F.Label(empty.transform, "Analysis isn't connected yet", 16, UiTheme.Text, FontStyle.Bold, TextAnchor.MiddleCenter, name: "Title");
            var emptyText = F.Label(empty.transform, "Runs are still saved: each one writes its config, results and heatmaps to its run folder.",
                13, UiTheme.Text2, alignment: TextAnchor.MiddleCenter, wrap: true, name: "Text");
            F.Size(emptyText.gameObject, 420f);

            settings.SetActive(false);
            analysis.SetActive(false);

            Wire.Set(app, "homePage", home);
            Wire.Set(app, "settingsPage", settings);
            Wire.Set(app, "analysisPage", analysis);
            Wire.Set(app, "startButton", startButton);
            Wire.Set(app, "analysisButton", analysisButton);
            Wire.Set(app, "settingsButton", settingsButton);
            Wire.Set(app, "quitButton", quitButton);
            Wire.Set(app, "settingsBackButton", settingsBack);
            Wire.Set(app, "resetButton", reset);
            Wire.Set(app, "units", units);
            Wire.Set(app, "form", form);
            Wire.Set(app, "analysisBackButton", analysisBack);

            return Save(root, StartPrefabPath);
        }

        static void Gap(Transform parent, float height) => F.Size(F.Go("Gap", parent), height: height);

        static GameObject Save(GameObject root, string path)
        {
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            return prefab;
        }

        /// <summary>
        /// Opens <paramref name="scenePath"/>, swaps whatever screen object it had for an instance of
        /// <paramref name="prefab"/>, adds an EventSystem if missing, and saves it.
        /// </summary>
        public static void PlaceInScene(string scenePath, GameObject prefab, Type controllerType)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            // The old UI Toolkit screen lived on a plain GameObject with this same component; replace it.
            foreach (var old in UnityEngine.Object.FindObjectsByType(controllerType, FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var go = ((Component)old).gameObject;
                if (PrefabUtility.GetCorrespondingObjectFromSource(go) == prefab) continue;
                UnityEngine.Object.DestroyImmediate(go);
            }

            bool placed = false;
            foreach (var existing in UnityEngine.Object.FindObjectsByType(controllerType, FindObjectsInactive.Include, FindObjectsSortMode.None))
                placed |= PrefabUtility.GetCorrespondingObjectFromSource(((Component)existing).gameObject) == prefab;
            if (!placed) PrefabUtility.InstantiatePrefab(prefab, scene);

            EnsureEventSystem();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"UI builder: {prefab.name} placed in {scenePath}. Edit it at {AssetDatabase.GetAssetPath(prefab)}.");
        }

        static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null) return;

            var go = new GameObject("EventSystem", typeof(EventSystem));
            var module = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (module != null) go.AddComponent(module);
            else go.AddComponent<StandaloneInputModule>();
        }
    }
}
