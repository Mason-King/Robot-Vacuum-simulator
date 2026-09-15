using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>The app's first screen. Drop this on an empty GameObject in the start scene.</summary>
    [DisallowMultipleComponent]
    public sealed class StartScreenApp : MonoBehaviour
    {
        [Tooltip("Multiplies the UI size on top of the screen's DPI scaling.")]
        [SerializeField, Range(0.6f, 2f)] float uiScale = 1f;

        PanelSettings panelSettings;

        void Start()
        {
            var root = UiPanel.Create(transform, "Start Screen UI", uiScale, out panelSettings);
            var screen = Ui.Div(Ui.Div(root, "le-app"), "le-screen le-start");

            var mark = new IconElement(IconKind.Logo);
            mark.AddToClassList("le-start__mark");
            screen.Add(mark);

            Ui.Text(screen, "Robot Vacuum Simulator", "le-start__title");
            Ui.Text(screen, "Draw a floor plan, then drop the vacuum in and watch it clean.", "le-start__text");

            var actions = Ui.Div(screen, "le-start__actions");
            Ui.Button(actions, "Level editor", IconKind.Pen, () => SimLauncher.OpenLevelEditor(), "le-btn--primary le-start__button");

            // Quitting does nothing inside the Unity Editor, so only offer it in builds.
            if (!Application.isEditor)
                Ui.Button(actions, "Quit", IconKind.Close, Application.Quit, "le-btn--ghost le-start__button");
        }

        void OnDestroy()
        {
            if (panelSettings != null) Destroy(panelSettings);
        }
    }
}
