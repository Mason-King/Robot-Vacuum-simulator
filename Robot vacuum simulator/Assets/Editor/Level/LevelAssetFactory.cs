using System.Collections.Generic;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEditor;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>Creates the starter palette, sample floor plans, and a ready-to-play 2D scene.</summary>
    public static class LevelAssetFactory
    {
        public const string LevelsFolder = "Assets/Levels";
        const string DefaultPalettePath = LevelsFolder + "/DefaultFloorPalette.asset";

        public static FloorPalette GetOrCreateDefaultPalette()
        {
            var palette = AssetDatabase.LoadAssetAtPath<FloorPalette>(DefaultPalettePath);
            if (palette != null) return palette;

            EnsureFolder(LevelsFolder);
            palette = FloorPalette.CreateDefault();
            AssetDatabase.CreateAsset(palette, DefaultPalettePath);

            foreach (var floor in palette.Entries)
                AssetDatabase.AddObjectToAsset(floor, palette);

            EditorUtility.SetDirty(palette);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(DefaultPalettePath);
            return palette;
        }

        /// <summary>Prompts for a path and creates a level seeded with the sample apartment.</summary>
        public static LevelData CreateLevelAsset(bool includeSampleRooms)
        {
            EnsureFolder(LevelsFolder);
            string path = EditorUtility.SaveFilePanelInProject(
                "New Level", "NewLevel", "asset", "Where should the level asset go?", LevelsFolder);
            if (string.IsNullOrEmpty(path)) return null;

            var level = ScriptableObject.CreateInstance<LevelData>();
            level.Palette = GetOrCreateDefaultPalette();

            if (includeSampleRooms) PopulateSampleApartment(level);
            else
            {
                level.Rooms.Add(new Room
                {
                    name = "Room 1",
                    outline = LevelData.RectangleOutline(Vector2.zero, new Vector2(4f, 3f)),
                    floorIndex = 0,
                });
                level.RobotSpawn = Vector2.zero;
            }

            AssetDatabase.CreateAsset(level, path);
            EditorUtility.SetDirty(level);
            AssetDatabase.SaveAssets();
            return level;
        }

        /// <summary>
        /// Four rooms, two of them deliberately off-axis, wired together with doorways.
        /// The bedroom shares an exact edge with the hall so its doorway cuts both walls.
        /// </summary>
        public static void PopulateSampleApartment(LevelData level) => SampleLevels.PopulateApartment(level);

        /// <summary>Drops a <see cref="LevelRenderer"/> for this level into the open scene.</summary>
        public static LevelRenderer SpawnRenderer(LevelData level)
        {
            var existing = LevelRenderer.FindFor(level);
            if (existing != null)
            {
                existing.Rebuild();
                return existing;
            }

            var go = new GameObject(level.name);
            Undo.RegisterCreatedObjectUndo(go, "Create Level Renderer");

            var renderer = go.AddComponent<LevelRenderer>();
            renderer.Level = level;
            Selection.activeGameObject = go;
            return renderer;
        }

        public static VacuumRobot SpawnRobot(LevelRenderer renderer)
        {
            var existing = Object.FindAnyObjectByType<VacuumRobot>(FindObjectsInactive.Include);
            if (existing == null)
            {
                var go = new GameObject("Vacuum Robot");
                Undo.RegisterCreatedObjectUndo(go, "Create Vacuum Robot");
                existing = go.AddComponent<VacuumRobot>();
                existing.BuildVisual();
            }

            var so = new SerializedObject(existing);
            so.FindProperty("levelRenderer").objectReferenceValue = renderer;
            so.ApplyModifiedPropertiesWithoutUndo();

            existing.ResetToSpawn();
            return existing;
        }

        /// <summary>Builds the level, adds the robot, and points an orthographic camera at it.</summary>
        [MenuItem("Tools/Robot Vacuum/Set Up 2D Scene", false, 30)]
        public static void SetUp2DScene()
        {
            var level = Selection.activeObject as LevelData;
            if (level == null)
            {
                var guids = AssetDatabase.FindAssets("t:LevelData");
                if (guids.Length > 0)
                    level = AssetDatabase.LoadAssetAtPath<LevelData>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            if (level == null)
            {
                EditorUtility.DisplayDialog(
                    "No level found",
                    "Create a level first: Tools ▸ Robot Vacuum ▸ Level Editor, then New.",
                    "OK");
                return;
            }

            var renderer = SpawnRenderer(level);
            SpawnRobot(renderer);
            FrameCameraOn(level);
            Force2DSceneView(level);

            Debug.Log($"Robot Vacuum: scene set up for '{level.name}'. Press Play to run the vacuum.", renderer);
        }

        /// <summary>
        /// Puts the scene view into real 2D mode looking straight down the Z axis. Without
        /// this the URP 3D template opens in perspective and a flat floor plan reads as 3D.
        /// </summary>
        static void Force2DSceneView(LevelData level)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null) return;

            var bounds = level.Bounds();

            sceneView.in2DMode = true;
            sceneView.orthographic = true;
            sceneView.LookAt(
                new Vector3(bounds.center.x, bounds.center.y, 0f),
                Quaternion.identity,
                Mathf.Max(bounds.width, bounds.height) * 0.6f + 1f);

            sceneView.Repaint();
        }

        static void FrameCameraOn(LevelData level)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                Undo.RegisterCreatedObjectUndo(go, "Create Camera");
                camera = go.AddComponent<Camera>();
            }

            var bounds = level.Bounds();
            camera.orthographic = true;
            camera.orthographicSize = Mathf.Max(bounds.height, bounds.width / Mathf.Max(0.1f, camera.aspect)) * 0.6f + 0.5f;
            camera.transform.position = new Vector3(bounds.center.x, bounds.center.y, -10f);
            camera.transform.rotation = Quaternion.identity;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.09f, 0.10f, 0.12f);
        }

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent)) return;

            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        static Color ParseHex(string hex) =>
            ColorUtility.TryParseHtmlString("#" + hex, out var color) ? color : Color.magenta;
    }
}
