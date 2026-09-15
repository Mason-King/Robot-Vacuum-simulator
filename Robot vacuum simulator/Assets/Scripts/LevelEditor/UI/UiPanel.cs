using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>Creates a runtime UI Toolkit panel that uses the level editor's theme and style sheet.</summary>
    public static class UiPanel
    {
        const string StyleSheetPath = "LevelEditor/LevelEditor";
        const string ThemePath = "LevelEditor/LevelEditorTheme";

        /// <summary>
        /// Adds a child of <paramref name="owner"/> holding a <see cref="UIDocument"/> and returns its styled root.
        /// The owner is responsible for destroying <paramref name="settings"/>.
        /// </summary>
        public static VisualElement Create(Transform owner, string name, float scale, out PanelSettings settings)
        {
            settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.name = name;
            settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>(ThemePath);
            settings.scaleMode = PanelScaleMode.ConstantPhysicalSize;
            settings.referenceDpi = 96f;
            settings.fallbackDpi = 96f;
            settings.scale = scale;

            if (settings.themeStyleSheet == null)
                Debug.LogWarning($"UI: theme not found at Resources/{ThemePath}. Text fields may look unstyled.");

            var host = new GameObject(name);
            host.transform.SetParent(owner, false);
            host.SetActive(false);
            var document = host.AddComponent<UIDocument>();
            document.panelSettings = settings;
            host.SetActive(true);

            var root = document.rootVisualElement;
            root.style.flexGrow = 1f;

            var styleSheet = Resources.Load<StyleSheet>(StyleSheetPath);
            if (styleSheet != null) root.styleSheets.Add(styleSheet);
            else Debug.LogError($"UI: style sheet not found at Resources/{StyleSheetPath}.");

            return root;
        }
    }
}
