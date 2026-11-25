using System.Linq;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
using UnityEditor;
using UnityEngine;

namespace RobotVacuum.EditorTools
{
    /// <summary>
    /// The Algorithm dropdown: every algorithm in the catalog by name (plugins also show their file), with the id
    /// stored behind it. An empty id shows as the default algorithm (Random). An id that is no longer in the catalog
    /// stays selected and shows in red as "missing".
    /// </summary>
    [CustomPropertyDrawer(typeof(AlgorithmIdAttribute))]
    public sealed class AlgorithmIdDrawer : PropertyDrawer
    {
        const float ButtonWidth = 64f;

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.String)
            {
                EditorGUI.LabelField(position, label.text, "[AlgorithmId] needs a string field.");
                return;
            }

            // The id in force: the field, or (empty) the default.
            string id = AlgorithmCatalog.OrDefault(property.stringValue);

            var entries = AlgorithmCatalog.Entries;
            var labels = entries.Select(e => e.BuiltIn ? e.Name : $"{e.Name}   ({e.Origin})").ToList();
            int selected = entries.ToList().FindIndex(e => e.Id == id);
            bool missing = selected < 0;
            if (missing)
            {
                labels.Add($"missing: {id}");
                selected = labels.Count - 1;
            }

            label = new GUIContent("Algorithm", label.tooltip);
            var popupRect = new Rect(position.x, position.y, position.width - ButtonWidth - 4f, position.height);
            var buttonRect = new Rect(position.xMax - ButtonWidth, position.y, ButtonWidth, position.height);

            EditorGUI.BeginProperty(position, label, property);
            var previousColor = GUI.color;
            if (missing) GUI.color = new Color(1f, 0.45f, 0.45f);

            EditorGUI.BeginChangeCheck();
            int chosen = EditorGUI.Popup(popupRect, label, selected, labels.Select(l => new GUIContent(l)).ToArray());
            GUI.color = previousColor;

            if (EditorGUI.EndChangeCheck() && chosen >= 0 && chosen < entries.Count)
            {
                var entry = entries[chosen];
                property.stringValue = entry.Id;
            }
            EditorGUI.EndProperty();

            if (GUI.Button(buttonRect, new GUIContent("Rescan", "Read the Algorithms folder again (not during a run).")))
            {
                var report = AlgorithmCatalog.Rescan();
                if (report.refused || report.FailedUnits > 0) Debug.LogWarning(report.Text());
                else Debug.Log(report.Text());
            }
        }
    }
}
