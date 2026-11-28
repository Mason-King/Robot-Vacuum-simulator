using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The version history of one document (a floor plan, or the configuration): every saved version, newest first,
    /// with when, its label and the edits it holds. From here a version can be renamed, restored (as an undoable
    /// edit), or compared with any other version, or with the unsaved state.
    /// </summary>
    public static class VersionHistoryDialog
    {
        /// <summary>What the dialog shows and does, for one kind of document.</summary>
        public sealed class Source
        {
            /// <summary>"“Kitchen”" or "Configuration": shown in the titles.</summary>
            public string Name;
            public VersionHistory History;

            /// <summary>The state being edited now, in the same form as a version's content.</summary>
            public Func<string> CurrentContent;

            /// <summary>The edits not in any version yet; empty when there are none.</summary>
            public Func<List<string>> UnsavedEdits;

            /// <summary>Differences between two contents, as lines.</summary>
            public Func<string, string, List<string>> Compare;

            /// <summary>A picture of a content (a floor plan), or null for none.</summary>
            public Func<string, VisualElement> Preview;

            /// <summary>Brings back a version as an undoable edit; returns false if it can't be read.</summary>
            public Func<int, bool> Restore;

            /// <summary>Shown after a restore: what happens next ("Save to keep it as a new version").</summary>
            public string RestoredNote;
        }

        const int CurrentRow = 0; // in the compare selection: the unsaved state

        public static void Show(OverlayLayer overlay, Source source)
        {
            var versions = new List<VersionInfo>(source.History.Versions);
            versions.Reverse(); // newest first
            var unsaved = source.UnsavedEdits?.Invoke() ?? new List<string>();
            bool hasUnsaved = HasUnsaved(source);

            var body = Ui.Div(null, "le-history");
            var selected = new List<int>(); // version numbers, or CurrentRow
            VisualElement compareButton = null;

            void RefreshCompare()
            {
                compareButton?.SetEnabled(selected.Count == 2);
                if (compareButton?.Q<Label>() is Label label)
                    label.text = selected.Count == 2 ? $"Compare {Name(selected[0])} and {Name(selected[1])}" : "Compare (pick two)";
            }

            void Toggle(VisualElement row, int key, bool on)
            {
                if (on)
                {
                    if (selected.Count == 2) selected.RemoveAt(0);
                    selected.Add(key);
                }
                else selected.Remove(key);
                RefreshCompare();
            }

            var list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("le-history__list");
            body.Add(list);

            if (versions.Count == 0 && !hasUnsaved)
                Ui.Text(list, "No saved versions yet. Each save records one.", "le-empty-hint");

            var toggles = new List<(Toggle toggle, int key)>();

            if (hasUnsaved)
            {
                var row = Row(list, "Now · unsaved changes", unsaved.Count > 0 ? string.Join(", ", unsaved) : "Not saved as a version yet", true);
                AddToggle(row, CurrentRow, toggles, Toggle);
                if (versions.Count > 0)
                    Ui.Button(row, "Changes", null, () => ShowCompare(overlay, source, versions[0].number, CurrentRow), "le-btn--ghost");
            }

            for (int i = 0; i < versions.Count; i++)
            {
                var version = versions[i];
                string edits = version.edits != null && version.edits.Count > 0 ? string.Join(", ", version.edits) : "No edits listed";
                var row = Row(list, version.Title, $"{When(version)} · {edits}", false);
                AddToggle(row, version.number, toggles, Toggle);
                int number = version.number;
                if (i + 1 < versions.Count)
                {
                    int previous = versions[i + 1].number;
                    Ui.Button(row, "Changes", null, () => ShowCompare(overlay, source, previous, number), "le-btn--ghost");
                }
                Ui.Button(row, "Rename", null, () => ShowRename(overlay, source, version), "le-btn--ghost");
                Ui.Button(row, "Restore", null, () =>
                {
                    overlay.CancelDialog(); // the list is out of date once the state changes
                    if (source.Restore(number)) overlay.Toast($"Restored v{number}. {source.RestoredNote}", null, null, 4000);
                    else overlay.Toast($"Couldn't read v{number}.");
                }, "le-btn--ghost");
            }

            var footer = Ui.Div(body, "le-history__footer");
            compareButton = Ui.Button(footer, "Compare (pick two)", null, () =>
            {
                if (selected.Count != 2) return;
                int a = selected[0], b = selected[1];
                // Older first; the unsaved state is the newest.
                int Order(int key) => key == CurrentRow ? int.MaxValue : key;
                if (Order(a) > Order(b)) (a, b) = (b, a);
                ShowCompare(overlay, source, a, b);
            }, "le-btn--ghost");
            RefreshCompare();

            string count = versions.Count == 1 ? "1 saved version" : $"{versions.Count} saved versions";
            overlay.ShowDialog($"{source.Name}: version history",
                $"{count}. Tick two to compare them; “Changes” compares a version with the one before it.",
                body, null, new DialogButton("Close", ButtonStyle.Primary, null));
            overlay.DialogCard?.AddToClassList("le-dialog--history");

            static string Name(int key) => key == CurrentRow ? "now" : $"v{key}";
        }

        static bool HasUnsaved(Source source)
        {
            string current = source.CurrentContent?.Invoke();
            var latest = source.History.Latest;
            if (current == null) return false;
            if (latest == null) return true;
            string saved = source.History.Read(latest.number);
            return source.Compare(saved, current).Count > 0;
        }

        static VisualElement Row(VisualElement list, string title, string meta, bool current)
        {
            var row = Ui.Div(list, current ? "le-history__row le-history__row--current" : "le-history__row");
            var main = Ui.Div(row, "le-history__main");
            Ui.Text(main, title, "le-history__title");
            Ui.Text(main, meta, "le-history__meta");
            return row;
        }

        static Toggle AddToggle(VisualElement row, int key, List<(Toggle, int)> toggles, Action<VisualElement, int, bool> changed)
        {
            var toggle = new Toggle();
            toggle.AddToClassList("le-history__pick");
            toggle.tooltip = "Pick for comparing";
            toggle.RegisterValueChangedCallback(evt => changed(row, key, evt.newValue));
            row.Insert(0, toggle);
            toggles.Add((toggle, key));
            return toggle;
        }

        static string When(VersionInfo version)
        {
            var at = version.SavedAt;
            return at == DateTime.MinValue ? "unknown time" : at.ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture);
        }

        // ---- compare ------------------------------------------------------------------------------------------

        /// <summary>Compares <paramref name="older"/> with <paramref name="newer"/> (version numbers; 0 = the unsaved state).</summary>
        public static void ShowCompare(OverlayLayer overlay, Source source, int older, int newer)
        {
            string a = Content(source, older), b = Content(source, newer);
            string nameA = older == CurrentRow ? "now" : source.History.Find(older)?.Title ?? $"v{older}";
            string nameB = newer == CurrentRow ? "now (unsaved)" : source.History.Find(newer)?.Title ?? $"v{newer}";

            var body = Ui.Div(null, "le-history");
            if (a == null || b == null)
            {
                Ui.Text(body, "One of these versions can't be read.", "le-empty-hint");
            }
            else
            {
                if (source.Preview != null)
                {
                    var maps = Ui.Div(body, "le-history__maps");
                    foreach (var (content, caption) in new[] { (a, nameA), (b, nameB) })
                    {
                        var cell = Ui.Div(maps, "le-history__map");
                        Ui.Text(cell, caption, "le-history__meta");
                        var preview = source.Preview(content);
                        if (preview != null)
                        {
                            preview.AddToClassList("le-history__map-view");
                            cell.Add(preview);
                        }
                    }
                }

                var lines = source.Compare(a, b);
                var list = new ScrollView(ScrollViewMode.Vertical);
                list.AddToClassList("le-history__diff");
                body.Add(list);
                if (lines.Count == 0) Ui.Text(list, "No differences.", "le-empty-hint");
                foreach (string line in lines) Ui.Text(list, "• " + line, "le-history__diff-line");
            }

            overlay.ShowDialog($"{source.Name}: {nameA} → {nameB}", null, body, null,
                new DialogButton("Back to history", ButtonStyle.Ghost, () => Show(overlay, source)),
                new DialogButton("Close", ButtonStyle.Primary, null));
            overlay.DialogCard?.AddToClassList("le-dialog--history");
        }

        static string Content(Source source, int key) =>
            key == CurrentRow ? source.CurrentContent?.Invoke() : source.History.Read(key);

        // ---- rename -------------------------------------------------------------------------------------------

        static void ShowRename(OverlayLayer overlay, Source source, VersionInfo version)
        {
            string label = version.label;
            var body = Ui.Div(null, "le-history");
            var field = Ui.TextInput(body, label, value => label = value);
            field.RegisterValueChangedCallback(evt => label = evt.newValue);

            overlay.ShowDialog($"Rename v{version.number}", "A name to find this version by, such as “Before moving the kitchen”.", body,
                () => Show(overlay, source),
                new DialogButton("Cancel", ButtonStyle.Ghost, () => Show(overlay, source)),
                new DialogButton("Rename", ButtonStyle.Primary, () =>
                {
                    try
                    {
                        source.History.Relabel(version.number, label);
                    }
                    catch (Exception exception)
                    {
                        UnityEngine.Debug.LogException(exception);
                        overlay.Toast("Couldn't rename that version.");
                    }
                    Show(overlay, source);
                }));
            field.schedule.Execute(() => field.Focus());
        }
    }
}
