using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The app's first screen: start a simulation (which goes to the floor plan list), open Analysis, or
    /// change the global settings every run starts from. Drop this on an empty GameObject in the start scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StartScreenApp : MonoBehaviour
    {
        [Tooltip("Multiplies the UI size on top of the screen's DPI scaling.")]
        [SerializeField, Range(0.6f, 2f)] float uiScale = 1f;

        PanelSettings panelSettings;
        VisualElement app;

        void Start()
        {
            var root = UiPanel.Create(transform, "Start Screen UI", uiScale, out panelSettings);
            app = Ui.Div(root, "le-app");
            ShowHome();
        }

        void ShowHome()
        {
            app.Clear();
            var screen = Ui.Div(app, "le-screen le-start");

            var mark = new IconElement(IconKind.Logo);
            mark.AddToClassList("le-start__mark");
            screen.Add(mark);

            Ui.Text(screen, "Robot Vacuum Simulator", "le-start__title");
            Ui.Text(screen, "Pick a floor plan, set up the run, then watch the vacuum clean or run it headless.", "le-start__text");

            var actions = Ui.Div(screen, "le-start__actions");
            Ui.Button(actions, "Start simulation", IconKind.Play, () => SimLauncher.OpenFloorPlans(), "le-btn--primary le-start__button");
            var analysis = Ui.Button(actions, "Analysis", IconKind.Grid, ShowAnalysis, "le-btn--ghost le-start__button");
            Ui.Tooltip(analysis, "Results of past runs");
            var settings = Ui.Button(actions, "Settings", IconKind.Spawn, ShowSettings, "le-btn--ghost le-start__button");
            Ui.Tooltip(settings, "The defaults every run starts from");

            // Quitting does nothing inside the Unity Editor, so only offer it in builds.
            if (!Application.isEditor)
                Ui.Button(actions, "Quit", IconKind.Close, Application.Quit, "le-btn--ghost le-start__button");
        }

        /// <summary>
        /// The Analysis page. Placeholder until the analysis work from its own branch is merged in: replace
        /// this body with that page (it was opened from here, with a back button to <see cref="ShowHome"/>).
        /// </summary>
        void ShowAnalysis()
        {
            app.Clear();
            var screen = Ui.Div(app, "le-screen le-setup");

            var header = Ui.Div(screen, "le-topbar le-library__header");
            Ui.IconButton(header, IconKind.Back, "Start screen", ShowHome, "le-btn--ghost");
            var titles = Ui.Div(header, "le-library__title-block");
            Ui.Text(titles, "Analysis", "le-library__title");
            Ui.Text(titles, "Results of past runs", "le-library__subtitle");

            var empty = Ui.Div(screen, "le-empty");
            var icon = new IconElement(IconKind.Grid);
            icon.AddToClassList("le-empty__icon");
            empty.Add(icon);
            Ui.Text(empty, "Analysis isn't connected yet", "le-empty__title");
            Ui.Text(empty, "Runs are still saved: each one writes its config, results and heatmaps to its run folder.", "le-empty__text");
        }

        /// <summary>The global settings: the defaults every run starts from, saved between sessions.</summary>
        void ShowSettings()
        {
            app.Clear();
            var screen = Ui.Div(app, "le-screen le-setup");

            var header = Ui.Div(screen, "le-topbar le-library__header");
            Ui.IconButton(header, IconKind.Back, "Start screen", ShowHome, "le-btn--ghost");
            var titles = Ui.Div(header, "le-library__title-block");
            Ui.Text(titles, "Settings", "le-library__title");
            Ui.Text(titles, "The defaults every run starts from. Any run can change them for itself on its Run setup page.",
                "le-library__subtitle");
            Ui.Div(header, "le-topbar__spacer");

            var reset = Ui.Button(header, "Reset to defaults", IconKind.Undo, () =>
            {
                RunPreferences.ResetGlobalToDefaults();
                ShowSettings();
            }, "le-btn--ghost");
            Ui.Tooltip(reset, "Put every setting back to the built-in default");

            var content = new ScrollView(ScrollViewMode.Vertical);
            content.AddToClassList("le-setup__content");
            content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            screen.Add(content);

            var column = Ui.Div(content, "le-settings__column");

            var general = Ui.Div(column, "le-form__group");
            var head = Ui.Div(general, "le-form__head");
            Ui.Text(head, "General", "le-form__title");
            Ui.Text(head, "Applies across the whole app.", "le-form__text");
            var body = Ui.Div(general, "le-form__body");
            Ui.Text(body, "Units", "le-field-label");

            var form = new RunSettingsForm(RunPreferences.Global);
            form.Changed += RunPreferences.SaveGlobal;

            var units = new Segmented(new[] { "Imperial (ft)", "Metric (m)" }, RunPreferences.UseFeet ? 0 : 1);
            units.SelectionChanged += index =>
            {
                RunPreferences.UseFeet = index == 0;
                form.Refresh(); // the read-outs switch unit too
            };
            body.Add(units);
            column.Add(form);
        }

        void OnDestroy()
        {
            if (panelSettings != null) Destroy(panelSettings);
        }
    }
}
