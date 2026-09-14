using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEditor;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    [CustomEditor(typeof(LevelData))]
    public class LevelDataInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            var level = (LevelData)target;

            if (GUILayout.Button("Open in Level Editor", GUILayout.Height(26f)))
                LevelEditorWindow.Open(level);

            if (GUILayout.Button("Set Up 2D Scene"))
            {
                Selection.activeObject = level;
                LevelAssetFactory.SetUp2DScene();
            }

            EditorGUILayout.Space(6f);
            DrawDefaultInspector();

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Summary", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Rooms", level.Rooms.Count.ToString());
            EditorGUILayout.LabelField("Doorways", level.Doorways.Count.ToString());
            EditorGUILayout.LabelField("Total floor area", $"{level.TotalFloorArea():0.0} m²");

            if (level.Palette == null)
                EditorGUILayout.HelpBox("Assign a Floor Palette before editing this level.", MessageType.Warning);
        }
    }

    [CustomEditor(typeof(LevelRenderer))]
    public class LevelRendererInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space(6f);

            var renderer = (LevelRenderer)target;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild", GUILayout.Height(24f)))
                    renderer.Rebuild();

                using (new EditorGUI.DisabledScope(renderer.Level == null))
                {
                    if (GUILayout.Button("Open Level Editor", GUILayout.Height(24f)))
                        LevelEditorWindow.Open(renderer.Level);
                }
            }
        }
    }

    [CustomEditor(typeof(VacuumRobot))]
    public class VacuumRobotInspector : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space(6f);

            var robot = (VacuumRobot)target;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild Visual")) robot.BuildVisual();
                if (GUILayout.Button("Reset To Spawn")) robot.ResetToSpawn();
            }

            if (!Application.isPlaying) return;

            var floor = robot.CurrentFloor;
            EditorGUILayout.LabelField("Current surface", floor != null ? floor.Label : "outside any room");
            EditorGUILayout.LabelField("Distance travelled", $"{robot.DistanceTravelled:0.0} m");
            Repaint();
        }
    }
}
