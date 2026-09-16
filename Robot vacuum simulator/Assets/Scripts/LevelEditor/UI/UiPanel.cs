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
        /// The window size the UI is designed against. Everything is laid out in pixels at this size and
        /// scaled from there, so a smaller window shrinks the whole UI instead of running out of room.
        /// </summary>
        public static readonly Vector2Int ReferenceResolution = new Vector2Int(1600, 900);

        /// <summary>
        /// Adds a child of <paramref name="owner"/> holding a <see cref="UIDocument"/> and returns its styled root.
        /// The owner is responsible for destroying <paramref name="settings"/>.
        /// </summary>
        public static VisualElement Create(Transform owner, string name, float scale, out PanelSettings settings)
        {
            settings = ScriptableObject.CreateInstance<PanelSettings>();
            settings.name = name;
            settings.themeStyleSheet = Resources.Load<ThemeStyleSheet>(ThemePath);
            // Scale with the window rather than with the screen's DPI: a physical-size panel keeps the
            // toolbars and the 312px inspector at the same pixel size on every screen, which leaves them
            // tiny on a big display and overflowing a small one.
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = ReferenceResolution;
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 0.5f; // split the difference, so neither a wide nor a tall window crops the UI
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
