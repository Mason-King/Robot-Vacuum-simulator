using System;
using RobotVacuum.Level;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The page between choosing to run a floor plan and the run starting: the plan, how it will run
    /// (normal or headless, preselected by the button that opened the page), the settings this run will use
    /// (the global ones until changed here), and Start. Opened from a card's Run and Headless buttons, and
    /// from the editor's (without the Edit button).
    /// </summary>
    public sealed class RunSetupView : VisualElement
    {
        readonly RunSettingsForm form;
        readonly Label status;
        readonly VisualElement resetButton;
        readonly VisualElement startButton;
        readonly Label modeHint;
        RunMode mode;

        public event Action BackRequested;
        public event Action EditRequested;
        public event Action<RunMode> RunRequested;

        public RunSetupView(LevelSnapshot level, FloorPalette palette, bool fromEditor, RunMode initialMode)
        {
            mode = initialMode;

            AddToClassList("le-screen");
            AddToClassList("le-setup");

            var header = Ui.Div(this, "le-topbar le-library__header");
            Ui.IconButton(header, IconKind.Back, fromEditor ? "Back to the editor" : "All floor plans",
                () => BackRequested?.Invoke(), "le-btn--ghost");
            var titles = Ui.Div(header, "le-library__title-block");
            Ui.Text(titles, level.name, "le-library__title");
            int rooms = level.ValidRoomCount;
            Ui.Text(titles, $"{(rooms == 1 ? "1 room" : $"{rooms} rooms")} · {Ui.FormatArea(level.TotalFloorArea())}", "le-library__subtitle");

            Ui.Div(header, "le-topbar__spacer");
            if (!fromEditor)
            {
                var edit = Ui.Button(header, "Edit floor plan", IconKind.Pen, () => EditRequested?.Invoke(), "le-btn--ghost");
                Ui.Tooltip(edit, "Open this floor plan in the level editor. You can run it from there too.");
            }
            var modes = new Segmented(new[] { "Normal", "Headless" }, mode == RunMode.Headless ? 1 : 0);
            modes.AddToClassList("le-setup__mode");
            modes.SelectionChanged += index =>
            {
                mode = index == 1 ? RunMode.Headless : RunMode.Watch;
                RefreshMode();
            };
            header.Add(modes);

            startButton = Ui.Button(header, "Start", IconKind.Play, () => RunRequested?.Invoke(mode), "le-btn--primary");

            var content = new ScrollView(ScrollViewMode.Vertical);
            content.AddToClassList("le-setup__content");
            content.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            Add(content);

            var columns = Ui.Div(content, "le-setup__columns");

            var preview = Ui.Div(columns, "le-setup__preview");
            var thumbnail = new LevelThumbnail(level, palette);
            thumbnail.AddToClassList("le-setup__thumbnail");
            preview.Add(thumbnail);
            modeHint = Ui.Text(preview, string.Empty, "le-form__hint");

            var panel = Ui.Div(columns, "le-setup__settings");
            var panelHeader = Ui.Div(panel, "le-setup__settings-header");
            var panelTitles = Ui.Div(panelHeader, "le-form__head");
            Ui.Text(panelTitles, "Run settings", "le-form__title");
            status = Ui.Text(panelTitles, string.Empty, "le-form__text");
            Ui.Div(panelHeader, "le-topbar__spacer");
            resetButton = Ui.Button(panelHeader, "Use global settings", IconKind.Undo, ResetToGlobal, "le-btn--ghost le-btn--small");
            Ui.Tooltip(resetButton, "Throw away this run's changes and use the defaults from Settings");

            form = new RunSettingsForm(RunPreferences.Run);
            form.Changed += () =>
            {
                RunPreferences.MarkRunCustomized();
                RefreshStatus();
            };
            panel.Add(form);

            RefreshStatus();
            RefreshMode();
        }

        void RefreshMode()
        {
            bool headless = mode == RunMode.Headless;
            startButton.Q<Label>().text = headless ? "Start headless run" : "Start run";
            startButton.Q<IconElement>().Kind = headless ? IconKind.Headless : IconKind.Play;
            modeHint.text = headless
                ? "Headless: nothing is drawn. The run plays as fast as it can, then shows its results and saves its files."
                : "Normal: watch the vacuum clean, with speed, pause and live settings. The run's files are saved when it ends.";
        }

        void ResetToGlobal()
        {
            RunPreferences.ResetRunToGlobal();
            form.Refresh();
            RefreshStatus();
        }

        void RefreshStatus()
        {
            bool custom = RunPreferences.IsRunCustomized;
            status.text = custom
                ? "Customized for this run. The global defaults on the Settings page are unchanged."
                : "Using the global settings. Change anything here to customize this run only.";
            Ui.SetVisible(resetButton, custom);
        }
    }
}
