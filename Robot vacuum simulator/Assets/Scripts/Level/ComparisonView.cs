using System;
using System.Collections.Generic;
using System.IO;
using RobotVacuum.Level;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The Analysis page's comparison of picked runs and batches on one floor plan: everything needed to decide which
    /// algorithm is best, in sections that can each be shown or hidden. A verdict on the chosen measure, a ranking, a
    /// scorecard of every measure, head-to-head results on shared seeds, the spread of each group's runs, coverage
    /// over time, rooms, floor types, heatmaps side by side, and every run. The choices are remembered between visits.
    /// </summary>
    public sealed class ComparisonView : VisualElement
    {
        enum Section { Verdict, Ranking, Scorecard, HeadToHead, Spread, Curves, Rooms, Floors, Maps, Runs, Picks }

        static readonly (Section section, string label, string tooltip)[] Sections =
        {
            (Section.Verdict, "Verdict", "Which group leads on the ranking measure, and whether the lead is more than chance"),
            (Section.Ranking, "Ranking", "Each group's mean on the ranking measure, with its 95% confidence interval"),
            (Section.Scorecard, "Scorecard", "Every measure side by side, with the best group on each marked"),
            (Section.HeadToHead, "Head to head", "Each pair of groups on the seeds both ran: wins, ties, losses and the paired difference"),
            (Section.Spread, "Spread", "Every run's value as a dot over a box of the middle half: how consistent each group is"),
            (Section.Curves, "Coverage over time", "Mean coverage over simulated time, with a band of one standard deviation"),
            (Section.Rooms, "Rooms", "Each room's coverage, battery or time per group"),
            (Section.Floors, "Floor types", "Coverage of each floor covering per group"),
            (Section.Maps, "Heatmaps", "Each group's mean coverage map, side by side"),
            (Section.Runs, "Every run", "Every run in the comparison, best first, to open any one"),
            (Section.Picks, "Picks", "What's in the comparison, with a way to take each pick out"),
        };

        const string PrefsKey = "RobotVacuum.Analysis.Compare";
        const int MaxListedRuns = 200;
        const int MaxMaps = 8;

        static readonly HashSet<Section> hidden = new HashSet<Section> { Section.Runs };
        static CompareMetric metric = CompareMetric.Coverage;
        static CompareGrouping grouping = CompareGrouping.Algorithm;
        static int roomMeasure;
        static bool prefsLoaded;

        readonly OverlayLayer overlay;
        readonly FloorPalette palette;
        readonly List<CompareSource> sources;
        readonly Func<IEnumerable<RunReport>, LevelSnapshot> loadLevel;
        List<CompareGroup> groups = new List<CompareGroup>();

        /// <summary>A pick was taken out of the comparison, by id.</summary>
        public event Action<string> PickRemoved;

        /// <summary>A run was chosen to open in full.</summary>
        public event Action<RunReport> RunOpened;

        public ComparisonView(OverlayLayer overlay, FloorPalette palette, List<CompareSource> sources,
            Func<IEnumerable<RunReport>, LevelSnapshot> loadLevel)
        {
            this.overlay = overlay;
            this.palette = palette;
            this.sources = sources;
            this.loadLevel = loadLevel;
            AddToClassList("le-compare");
            LoadPrefs();
            Rebuild();
        }

        Color ColorOf(CompareGroup group) => CompareChart.ColorOf(groups.IndexOf(group));

        void Rebuild()
        {
            Clear();
            groups = RunComparison.Build(sources, grouping, metric);
            BuildToolbar(Ui.Div(this, "le-panel"));

            if (groups.Count == 0)
            {
                Ui.Text(Ui.Div(this, "le-panel"), "None of the picked runs can be grouped by algorithm: they all switched algorithm part-way. Try grouping by pick.",
                    "le-empty-hint");
                return;
            }

            foreach (var (section, _, _) in Sections)
            {
                if (hidden.Contains(section)) continue;
                switch (section)
                {
                    case Section.Verdict: BuildVerdict(); break;
                    case Section.Ranking: BuildRanking(); break;
                    case Section.Scorecard: BuildScorecard(); break;
                    case Section.HeadToHead: BuildHeadToHead(); break;
                    case Section.Spread: BuildSpread(); break;
                    case Section.Curves: BuildCurves(); break;
                    case Section.Rooms: BuildRooms(); break;
                    case Section.Floors: BuildFloors(); break;
                    case Section.Maps: BuildMaps(); break;
                    case Section.Runs: BuildRuns(); break;
                    case Section.Picks: BuildPicks(); break;
                }
            }
        }

        // ---------------------------------------------------------------- toolbar

        void BuildToolbar(VisualElement panel)
        {
            int runCount = 0;
            foreach (var source in sources) runCount += source.RunCount;

            var header = Ui.Div(panel, "le-panel__header");
            var titles = Ui.Div(header, "le-panel__titles");
            Ui.Text(titles, $"Comparison · {sources[0].LevelName}", "le-panel__title");
            Ui.Text(titles, $"{sources.Count} pick(s) · {runCount} runs · {groups.Count} group(s)", "le-panel__text");

            var export = Ui.Button(header, "Export CSV", IconKind.Save, ExportCsv, "le-btn--ghost");
            Ui.Tooltip(export, "Every run with every measure, and each group's mean, SD and 95% confidence interval, for a spreadsheet");

            var controls = Ui.Div(panel, "le-compare__controls");

            var group = Ui.Div(controls, "le-compare__control");
            Ui.Text(group, "Group", "le-field-label");
            var groupPicker = new Segmented(new[] { "By algorithm", "By pick" }, (int)grouping);
            groupPicker.SelectionChanged += index =>
            {
                grouping = (CompareGrouping)index;
                SavePrefs();
                Rebuild();
            };
            Ui.Tooltip(groupPicker, "Pool every picked run of the same algorithm, or keep each pick (and each algorithm within a batch) apart");
            group.Add(groupPicker);

            var rank = Ui.Div(controls, "le-compare__control");
            Ui.Text(rank, "Rank by", "le-field-label");
            VisualElement rankButton = null;
            rankButton = Ui.Button(rank, RunComparison.Label(metric), IconKind.ChevronDown, () => ShowMetricMenu(rankButton),
                "le-btn--ghost le-compare__metric");

            var info = RunComparison.Info(metric);
            Ui.Text(panel, info.description + Direction(info.better), "le-empty-hint");

            Ui.Text(panel, "SHOW", "le-section__title le-panel__gap");
            var chips = Ui.Div(panel, "le-chip-row");
            foreach (var (section, label, tooltip) in Sections)
            {
                bool on = !hidden.Contains(section);
                var chip = Ui.Button(chips, label, on ? IconKind.Check : (IconKind?)null, () =>
                {
                    if (!hidden.Remove(section)) hidden.Add(section);
                    SavePrefs();
                    Rebuild();
                }, on ? "le-chip le-chip--on" : "le-chip");
                Ui.Tooltip(chip, tooltip);
            }
        }

        static string Direction(Better better) => better switch
        {
            Better.Higher => " Higher is better.",
            Better.Lower => " Lower is better.",
            _ => " Neither higher nor lower is better on its own.",
        };

        void ShowMetricMenu(VisualElement anchor)
        {
            var entries = new List<MenuEntry> { MenuEntry.Heading("RANK BY") };
            foreach (var m in RunComparison.Metrics)
            {
                var captured = m;
                entries.Add(MenuEntry.Item(RunComparison.Label(m), () =>
                {
                    metric = captured;
                    SavePrefs();
                    Rebuild();
                }, m == metric ? IconKind.Check : (IconKind?)null));
            }
            overlay.ShowMenuBelow(anchor, entries);
        }

        // ---------------------------------------------------------------- verdict and ranking

        void BuildVerdict()
        {
            var panel = Ui.Div(this, "le-panel");
            var verdict = RunComparison.Judge(groups, metric);
            var callout = Ui.Div(panel, verdict.clear ? "le-compare__verdict le-compare__verdict--clear" : "le-compare__verdict");
            Ui.Text(callout, verdict.headline, "le-compare__verdict-title");
            Ui.Text(callout, verdict.detail, "le-compare__verdict-text");

            foreach (string caveat in RunComparison.Caveats(sources, grouping))
                Ui.Text(panel, caveat, "le-analysis__failure");
        }

        void BuildRanking()
        {
            var panel = Ui.Div(this, "le-panel");
            var info = RunComparison.Info(metric);
            Ui.Text(panel, info.label.ToUpperInvariant() + (info.better == Better.Higher ? " · HIGHER IS BETTER"
                : info.better == Better.Lower ? " · LOWER IS BETTER" : string.Empty), "le-section__title");

            // Coverage and rates read against 100%; everything else against the largest interval end.
            bool percent = metric == CompareMetric.Coverage || info.format(0).EndsWith("%");
            double scale = percent ? 100 : 0;
            if (scale <= 0)
                foreach (var group in groups) scale = Math.Max(scale, group.metric.mean + Math.Max(0, group.metric.ci95));
            if (scale <= 0) scale = 1;

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var row = Ui.Div(panel, "le-compare-row");
                var label = Ui.Div(row, "le-compare-row__label le-compare-row__label--wide le-compare__name");
                Ui.Swatch(label, ColorOf(group), "le-compare__swatch");
                Ui.Text(label, group.label, "le-compare__name-text");
                Ui.Tooltip(label, group.label);

                var track = Ui.Div(row, "le-compare-row__track le-compare-row__track--ci");
                if (group.metric.count == 0)
                {
                    Ui.Text(row, "—", "le-compare-row__value");
                    Ui.Text(row, $"no value in its {group.runs.Count} run(s)", "le-compare-row__meta le-compare-row__meta--wide");
                    continue;
                }

                bool best = i == 0 && info.better != Better.Neutral;
                var fill = Ui.Div(track, best ? "le-compare-row__fill le-compare-row__fill--best" : "le-compare-row__fill");
                fill.style.width = Length.Percent(Mathf.Clamp((float)(group.metric.mean / scale * 100), 0f, 100f));
                if (AnalysisView.HasInterval(group.metric)) AnalysisView.ErrorBar(track, group.metric.Low, group.metric.High, scale);

                Ui.Text(row, info.format(group.metric.mean), "le-compare-row__value");
                string spread = AnalysisView.HasInterval(group.metric) ? $"± {info.spread(group.metric.ci95)} · " : string.Empty;
                int counted = group.RunsFor(info).Count;
                string runs = group.metric.count < counted ? $"{group.metric.count} of {counted} runs" : $"{group.metric.count} run(s)";
                Ui.Text(row, spread + runs, "le-compare-row__meta le-compare-row__meta--wide");
            }
            Ui.Text(panel, "Bar: the mean. |—|: its 95% confidence interval. Failed runs are left out of every measure but Succeeded.", "le-empty-hint");
        }

        // ---------------------------------------------------------------- scorecard

        /// <summary>Every measure in rows, every group in columns. The best group on each is marked, with ✓ when its lead is clear.</summary>
        void BuildScorecard()
        {
            var panel = Ui.Div(this, "le-panel");
            Ui.Text(panel, "SCORECARD · MEAN ± 95% CONFIDENCE INTERVAL", "le-section__title");

            var table = Ui.Div(panel, "le-table le-scorecard");
            var head = Ui.Div(table, "le-table__row le-table__row--head");
            Ui.Text(head, "Measure", "le-table__cell le-scorecard__measure");
            foreach (var group in groups) GroupHeading(head, group);

            foreach (var m in RunComparison.Metrics)
            {
                var info = RunComparison.Info(m);
                var ranked = new List<CompareGroup>(groups);
                SortBy(ranked, m);
                var leader = info.better != Better.Neutral && ranked[0].Stat(m).count > 0 ? ranked[0] : null;
                bool clear = leader != null && ranked.Count > 1 && JudgeOn(ranked, m).clear;

                var row = Ui.Div(table, "le-table__row");
                if (m == metric) row.AddToClassList("le-scorecard__row--ranked");
                var name = Ui.Text(row, info.label + (info.better == Better.Higher ? "  ↑" : info.better == Better.Lower ? "  ↓" : ""),
                    "le-table__cell le-table__cell--strong le-scorecard__measure");
                Ui.Tooltip(name, info.description + Direction(info.better));

                foreach (var group in groups)
                {
                    var s = group.Stat(m);
                    string text = s.count == 0 ? "—" : info.format(s.mean) + (s.count > 1 && s.ci95 > 0 ? $" ± {info.spread(s.ci95)}" : string.Empty);
                    var cell = Ui.Text(row, group == leader ? text + (clear ? "  ✓" : "") : text, "le-table__cell");
                    if (group == leader) cell.AddToClassList(clear ? "le-scorecard__best--clear" : "le-scorecard__best");
                }
            }

            Ui.Text(panel, "Highlighted: the best group on that measure. ✓: clearly best, by the same test as the verdict. ↑ higher is better, ↓ lower is better. " +
                           "Click Rank by to put any measure in the verdict and ranking.", "le-empty-hint");
        }

        void GroupHeading(VisualElement row, CompareGroup group)
        {
            var cell = Ui.Div(row, "le-table__cell le-compare__name");
            Ui.Swatch(cell, ColorOf(group), "le-compare__swatch");
            Ui.Text(cell, group.label, "le-compare__name-text");
            Ui.Tooltip(cell, $"{group.label} · {group.runs.Count} run(s)");
        }

        static void SortBy(List<CompareGroup> list, CompareMetric m)
        {
            var better = RunComparison.Info(m).better;
            list.Sort((a, b) =>
            {
                var sa = a.Stat(m);
                var sb = b.Stat(m);
                if (sa.count == 0 || sb.count == 0) return sb.count.CompareTo(sa.count);
                return better == Better.Lower ? sa.mean.CompareTo(sb.mean) : sb.mean.CompareTo(sa.mean);
            });
        }

        /// <summary>The verdict on another measure, without disturbing the groups' ranking measure.</summary>
        static CompareVerdict JudgeOn(List<CompareGroup> ranked, CompareMetric m)
        {
            var saved = new Summary[ranked.Count];
            for (int i = 0; i < ranked.Count; i++)
            {
                saved[i] = ranked[i].metric;
                ranked[i].metric = ranked[i].Stat(m);
            }
            var verdict = RunComparison.Judge(ranked, m);
            for (int i = 0; i < ranked.Count; i++) ranked[i].metric = saved[i];
            return verdict;
        }

        // ---------------------------------------------------------------- head to head

        void BuildHeadToHead()
        {
            var panel = Ui.Div(this, "le-panel");
            var info = RunComparison.Info(metric);
            Ui.Text(panel, $"HEAD TO HEAD ON SHARED SEEDS · {info.label.ToUpperInvariant()}", "le-section__title");

            if (groups.Count < 2)
            {
                Ui.Text(panel, "Pick runs of at least two algorithms to see them head to head.", "le-empty-hint");
                return;
            }

            bool anyShared = false;
            var table = Ui.Div(panel, "le-table le-h2h");
            var head = Ui.Div(table, "le-table__row le-table__row--head");
            Ui.Text(head, "Row vs column", "le-table__cell le-h2h__corner");
            foreach (var group in groups) GroupHeading(head, group);

            foreach (var a in groups)
            {
                var row = Ui.Div(table, "le-table__row");
                GroupHeading(row, a);
                row[0].AddToClassList("le-h2h__corner");
                foreach (var b in groups)
                {
                    var cell = Ui.Div(row, "le-table__cell le-h2h__cell");
                    if (a == b)
                    {
                        cell.AddToClassList("le-h2h__cell--self");
                        continue;
                    }

                    var result = RunComparison.Compare(a, b, metric);
                    if (result.shared == 0)
                    {
                        Ui.Text(cell, "no shared seeds", "le-h2h__note");
                        continue;
                    }

                    anyShared = true;
                    Ui.Text(cell, $"{result.wins}–{result.ties}–{result.losses}", "le-h2h__score");
                    string sign = result.difference.mean >= 0 ? "+" : "−";
                    string diff = $"{sign}{info.spread(Math.Abs(result.difference.mean))}" +
                                  (result.difference.count > 1 ? $" ± {info.spread(result.difference.ci95)}" : string.Empty);
                    Ui.Text(cell, diff, "le-h2h__note");
                    if (info.better != Better.Neutral)
                    {
                        if (result.ClearlyBetter) cell.AddToClassList("le-h2h__cell--better");
                        else if (result.ClearlyWorse) cell.AddToClassList("le-h2h__cell--worse");
                    }
                    Ui.Tooltip(cell, $"{a.label} vs {b.label} on {result.shared} shared seed(s): better on {result.wins}, tied on {result.ties}, " +
                                     $"worse on {result.losses}. Mean paired difference {diff}.");
                }
            }

            Ui.Text(panel, anyShared
                    ? "Each cell is the row's result against the column's, seed by seed: wins–ties–losses, then the mean difference (positive = the row did better) " +
                      "with its paired 95% confidence interval. Green: clearly better; red: clearly worse. Same seed means same floor and same luck, so this is the fairest test."
                    : "No two groups ran the same seeds. Run batches with the same first seed and number of seeds to compare like for like.",
                "le-empty-hint");
        }

        // ---------------------------------------------------------------- spread

        void BuildSpread()
        {
            var panel = Ui.Div(this, "le-panel");
            var info = RunComparison.Info(metric);
            Ui.Text(panel, $"SPREAD OF {info.label.ToUpperInvariant()} ACROSS RUNS", "le-section__title");

            var rows = new List<(string label, Color color, List<double> values)>();
            foreach (var group in groups)
                rows.Add((group.label, ColorOf(group), group.Values(metric).ConvertAll(v => v.value)));
            panel.Add(new SpreadChart(rows, info.format, info.format(0).EndsWith("%")));

            Ui.Text(panel, "Each dot is one run. The box spans the middle half of the runs, the line in it is the median, and the whiskers reach the best and worst run. " +
                           "A narrow box means the algorithm behaves the same whatever the seed.", "le-empty-hint");
        }

        // ---------------------------------------------------------------- curves

        void BuildCurves()
        {
            var panel = Ui.Div(this, "le-panel");
            Ui.Text(panel, "MEAN COVERAGE OVER TIME", "le-section__title");

            float duration = 0f;
            foreach (var group in groups) duration = Mathf.Max(duration, group.CurveDuration);
            if (duration <= 0f)
            {
                Ui.Text(panel, "None of these runs recorded a coverage timeline.", "le-empty-hint");
                return;
            }

            var series = new List<CompareChart.Series>();
            foreach (var group in groups)
            {
                var mean = group.Curve(BatchArchive.CurvePoints, duration, out var spread);
                series.Add(new CompareChart.Series { label = group.label, color = ColorOf(group), values = mean, spread = spread });
            }
            panel.Add(new CompareChart(series, duration));
            Ui.Text(panel, "Line: mean coverage. Band: one standard deviation either side, across runs (batches saved before this existed have no band). " +
                           "A run that has ended holds its final coverage, so every line runs to the end. Hover to read every line at once.", "le-empty-hint");
        }

        // ---------------------------------------------------------------- rooms and floors

        void BuildRooms()
        {
            var panel = Ui.Div(this, "le-panel");
            var header = Ui.Div(panel, "le-panel__header");
            Ui.Text(Ui.Div(header, "le-panel__titles"), "ROOMS · MEAN PER GROUP", "le-section__title");
            var picker = new Segmented(new[] { "Coverage", "Battery", "Time" }, roomMeasure);
            picker.SelectionChanged += index =>
            {
                roomMeasure = index;
                SavePrefs();
                Rebuild();
            };
            header.Add(picker);

            Func<RunReport, int, float> measure = roomMeasure switch
            {
                1 => (run, i) => run.roomBattery[i],
                2 => (run, i) => run.roomSeconds[i],
                _ => (run, i) => run.roomCoverage[i],
            };
            Func<double, string> format = roomMeasure switch
            {
                1 => v => $"{v:0} units",
                2 => v => VacuumSettings.FormatDuration((float)v),
                _ => v => $"{v:0}%",
            };

            var rows = RunComparison.Rooms(groups, measure);
            if (rows.Count == 0)
            {
                Ui.Text(panel, "None of these runs recorded per-room results.", "le-empty-hint");
                return;
            }

            // Coverage: higher is better. Battery and time in a room depend on the route, so neither is marked.
            AreaTable(panel, rows, format, roomMeasure == 0 ? Better.Higher : Better.Neutral, true);
            Ui.Text(panel, roomMeasure == 0
                    ? "Coverage of each room on its own. A room an algorithm keeps missing shows up here first."
                    : roomMeasure == 1 ? "Battery spent while in each room. Time in a doorway counts toward the room the vacuum came from."
                    : "Simulated time spent in each room.", "le-empty-hint");
        }

        void BuildFloors()
        {
            var panel = Ui.Div(this, "le-panel");
            Ui.Text(panel, "FLOOR TYPES · MEAN COVERAGE PER GROUP", "le-section__title");
            var rows = RunComparison.Floors(groups);
            if (rows.Count == 0)
            {
                Ui.Text(panel, "None of these runs recorded coverage per floor type (older runs don't).", "le-empty-hint");
                return;
            }

            AreaTable(panel, rows, v => $"{v:0}%", Better.Higher, false);
            Ui.Text(panel, "Coverage of each floor covering on its own, graded against the target like overall coverage. Harder floors take more passes.",
                "le-empty-hint");
        }

        void AreaTable(VisualElement panel, List<AreaRow> rows, Func<double, string> format, Better better, bool showArea)
        {
            var table = Ui.Div(panel, "le-table le-scorecard");
            var head = Ui.Div(table, "le-table__row le-table__row--head");
            Ui.Text(head, showArea ? "Room" : "Floor", "le-table__cell le-scorecard__measure");
            foreach (var group in groups) GroupHeading(head, group);

            foreach (var area in rows)
            {
                var row = Ui.Div(table, "le-table__row");
                Ui.Text(row, showArea && area.area > 0f ? $"{area.name} · {Ui.FormatArea(area.area)}" : area.name,
                    "le-table__cell le-table__cell--strong le-scorecard__measure");

                int best = -1;
                if (better != Better.Neutral)
                    for (int g = 0; g < area.perGroup.Length; g++)
                    {
                        if (area.perGroup[g].count == 0) continue;
                        if (best < 0 || (better == Better.Higher ? area.perGroup[g].mean > area.perGroup[best].mean : area.perGroup[g].mean < area.perGroup[best].mean))
                            best = g;
                    }

                for (int g = 0; g < area.perGroup.Length; g++)
                {
                    var s = area.perGroup[g];
                    var cell = Ui.Text(row, s.count == 0 ? "—" : format(s.mean), "le-table__cell");
                    if (g == best && groups.Count > 1) cell.AddToClassList("le-scorecard__best");
                }
            }
        }

        // ---------------------------------------------------------------- heatmaps

        void BuildMaps()
        {
            var panel = Ui.Div(this, "le-panel");
            Ui.Text(panel, "MEAN COVERAGE MAPS", "le-section__title");

            var grid = Ui.Div(panel, "le-compare__maps");
            int shown = 0;
            LevelSnapshot level = null;
            foreach (var group in groups)
            {
                if (shown >= MaxMaps) break;
                var map = group.Map(out int width, out int height, out Rect rect);
                if (map == null) continue;
                level ??= loadLevel(group.runs);
                if (level == null) break;

                var card = Ui.Div(grid, "le-compare__map-card");
                var title = Ui.Div(card, "le-compare__name");
                Ui.Swatch(title, ColorOf(group), "le-compare__swatch");
                Ui.Text(title, $"{group.label} · {group.Stat(CompareMetric.Coverage).mean:0.0}%", "le-compare__name-text");

                var view = new FloorMapView(level, palette);
                view.AddToClassList("le-compare__map");
                view.SetCoverage(map, width, height, rect);
                card.Add(view);
                shown++;
            }

            if (shown == 0) Ui.Text(panel, "No coverage maps were saved for these runs.", "le-empty-hint");
            else Ui.Text(panel, "Blue is floor cleaned in every run; red is floor left dirty. Look for the same corner left red in one group but not another." +
                                (groups.Count > MaxMaps ? $" The first {MaxMaps} groups are shown." : string.Empty), "le-empty-hint");
        }

        // ---------------------------------------------------------------- every run and picks

        void BuildRuns()
        {
            var panel = Ui.Div(this, "le-panel");
            var info = RunComparison.Info(metric);
            Ui.Text(panel, $"EVERY RUN · BY {info.label.ToUpperInvariant()}", "le-section__title");

            var all = new List<(CompareGroup group, RunReport run, double? value)>();
            foreach (var group in groups)
                foreach (var run in group.runs)
                    all.Add((group, run, RunComparison.Value(run, metric)));

            all.Sort((a, b) =>
            {
                if (!a.value.HasValue || !b.value.HasValue) return b.value.HasValue.CompareTo(a.value.HasValue);
                return info.better == Better.Lower ? a.value.Value.CompareTo(b.value.Value) : b.value.Value.CompareTo(a.value.Value);
            });

            var table = Ui.Div(panel, "le-table");
            var head = Ui.Div(table, "le-table__row le-table__row--head");
            foreach (string heading in new[] { "Group", "Seed", info.label, "Coverage", "Run length", "Battery", "Ended" })
                Ui.Text(head, heading, "le-table__cell");

            int listed = 0;
            foreach (var (group, run, value) in all)
            {
                if (listed++ >= MaxListedRuns) break;
                var row = Ui.Div(table, "le-table__row le-compare__run");
                var name = Ui.Div(row, "le-table__cell le-compare__name");
                Ui.Swatch(name, ColorOf(group), "le-compare__swatch");
                Ui.Text(name, group.label, "le-compare__name-text");
                Ui.Text(row, run.seed.ToString(), "le-table__cell");
                Ui.Text(row, value.HasValue ? info.format(value.Value) : "—", "le-table__cell le-table__cell--strong");
                Ui.Text(row, $"{run.coveragePercent:0.0}%", "le-table__cell");
                Ui.Text(row, VacuumSettings.FormatDuration(run.durationSeconds), "le-table__cell");
                Ui.Text(row, $"{run.batteryUsed:0}", "le-table__cell");
                Ui.Text(row, BatchStats.Succeeded(run) ? run.endReason : "Failed", "le-table__cell");

                var captured = run;
                row.AddManipulator(new Clickable(() => RunOpened?.Invoke(captured)));
                Ui.Tooltip(row, "Open this run: its replay, timeline and rooms");
            }

            if (all.Count > MaxListedRuns)
                Ui.Text(panel, $"The top {MaxListedRuns} of {all.Count} runs are listed. Export CSV has every one.", "le-empty-hint");
        }

        void BuildPicks()
        {
            var panel = Ui.Div(this, "le-panel");
            Ui.Text(panel, "PICKED", "le-section__title");
            foreach (var source in sources)
            {
                var row = Ui.Div(panel, "le-run-row");
                var text = Ui.Div(row, "le-run-row__text");
                if (source.single != null)
                {
                    var run = source.single;
                    Ui.Text(text, $"{run.levelName} · {run.AlgorithmName}", "le-run-row__title");
                    Ui.Text(text, $"Single run · seed {run.seed} · {run.FinishedAt:d MMM HH:mm}", "le-run-row__meta");
                }
                else
                {
                    var batch = source.batch;
                    Ui.Text(text, batch.Title, "le-run-row__title");
                    Ui.Text(text, $"{batch.algorithms.Count} algorithm(s) · {batch.trials.Count} runs · seeds {batch.MinSeed}–{batch.MaxSeed} · {batch.FinishedAt:d MMM HH:mm}",
                        "le-run-row__meta");
                }

                string id = source.Id;
                Ui.IconButton(row, IconKind.Close, "Take out of the comparison", () => PickRemoved?.Invoke(id), "le-btn--ghost le-btn--small");
            }
        }

        // ---------------------------------------------------------------- export and preferences

        void ExportCsv()
        {
            try
            {
                string path = FloorPlanFiles.PickSavePath("Export comparison", $"comparison {sources[0].LevelName}", "csv");
                if (path == null) return;
                File.WriteAllText(path, RunComparison.ToCsv(groups));
                overlay.Toast($"Saved to {path}", "Show", () => FloorPlanFiles.Reveal(Path.GetDirectoryName(path)));
            }
            catch (Exception exception)
            {
                overlay.Toast($"Couldn't export: {exception.Message}");
            }
        }

        // Saved as "metric|grouping|roomMeasure|hidden,sections", so the comparison opens the way it was left.
        static void LoadPrefs()
        {
            if (prefsLoaded) return;
            prefsLoaded = true;

            string[] parts = PlayerPrefs.GetString(PrefsKey, string.Empty).Split('|');
            if (parts.Length < 4) return;
            if (Enum.TryParse(parts[0], out CompareMetric m)) metric = m;
            if (Enum.TryParse(parts[1], out CompareGrouping g)) grouping = g;
            if (int.TryParse(parts[2], out int r)) roomMeasure = Mathf.Clamp(r, 0, 2);

            hidden.Clear();
            foreach (string name in parts[3].Split(','))
                if (Enum.TryParse(name, out Section section)) hidden.Add(section);
        }

        static void SavePrefs()
        {
            var names = new List<string>();
            foreach (var section in hidden) names.Add(section.ToString());
            PlayerPrefs.SetString(PrefsKey, $"{metric}|{grouping}|{roomMeasure}|{string.Join(",", names)}");
        }

        // Statics survive entering Play Mode when domain reload is off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => prefsLoaded = false;
    }

    /// <summary>
    /// Each group's runs on one measure: a dot per run over a box of the middle half, with the median and whiskers
    /// to the best and worst run, all on one shared axis.
    /// </summary>
    public sealed class SpreadChart : VisualElement
    {
        const float LabelWidth = 180f;
        const float RowHeight = 30f;
        const int MaxDots = 400;

        readonly List<(string label, Color color, List<double> values)> rows;
        readonly VisualElement plot;
        readonly double min;
        readonly double max;

        public SpreadChart(List<(string label, Color color, List<double> values)> rows, Func<double, string> format, bool percent)
        {
            this.rows = rows;
            AddToClassList("le-spread");
            style.height = rows.Count * RowHeight + 22f;

            min = double.MaxValue;
            max = double.MinValue;
            foreach (var (_, _, values) in rows)
                foreach (double v in values)
                {
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                }
            if (min > max) min = max = 0;
            if (percent)
            {
                min = Math.Min(min, 0);
                max = Math.Max(max, 100);
            }
            if (max - min < 1e-9) max = min + 1;

            for (int i = 0; i < rows.Count; i++)
            {
                var label = Ui.Text(this, rows[i].label, "le-spread__label");
                label.style.top = i * RowHeight + 6f;
                label.style.width = LabelWidth - 10f;
                Ui.Tooltip(label, rows[i].label);
            }

            plot = Ui.Div(this, "le-spread__plot");
            plot.style.left = LabelWidth;
            plot.style.height = rows.Count * RowHeight;
            plot.generateVisualContent += Draw;

            var low = Ui.Text(this, format(min), "le-chart__x-label");
            low.style.left = LabelWidth;
            var high = Ui.Text(this, format(max), "le-chart__x-label");
            high.style.right = 0f;
        }

        void Draw(MeshGenerationContext context)
        {
            var rect = plot.contentRect;
            if (rect.width < 4f) return;
            var painter = context.painter2D;

            float X(double v) => rect.xMin + rect.width * (float)((v - min) / (max - min));

            for (int r = 0; r < rows.Count; r++)
            {
                var (_, color, values) = rows[r];
                float mid = r * RowHeight + RowHeight * 0.5f;

                painter.lineWidth = 1f;
                painter.strokeColor = new Color(1f, 1f, 1f, 0.06f);
                painter.BeginPath();
                painter.MoveTo(new Vector2(rect.xMin, mid));
                painter.LineTo(new Vector2(rect.xMax, mid));
                painter.Stroke();
                if (values.Count == 0) continue;

                var (lo, q1, median, q3, hi) = RunComparison.Quartiles(values);

                // Whiskers, box and median.
                painter.strokeColor = new Color(color.r, color.g, color.b, 0.8f);
                painter.lineWidth = 1.5f;
                painter.BeginPath();
                painter.MoveTo(new Vector2(X(lo), mid));
                painter.LineTo(new Vector2(X(hi), mid));
                painter.MoveTo(new Vector2(X(lo), mid - 5f));
                painter.LineTo(new Vector2(X(lo), mid + 5f));
                painter.MoveTo(new Vector2(X(hi), mid - 5f));
                painter.LineTo(new Vector2(X(hi), mid + 5f));
                painter.Stroke();

                painter.fillColor = new Color(color.r, color.g, color.b, 0.22f);
                painter.BeginPath();
                float left = X(q1), right = Mathf.Max(X(q3), left + 1.5f);
                painter.MoveTo(new Vector2(left, mid - 8f));
                painter.LineTo(new Vector2(right, mid - 8f));
                painter.LineTo(new Vector2(right, mid + 8f));
                painter.LineTo(new Vector2(left, mid + 8f));
                painter.ClosePath();
                painter.Fill();
                painter.Stroke();

                painter.lineWidth = 2.5f;
                painter.strokeColor = color;
                painter.BeginPath();
                painter.MoveTo(new Vector2(X(median), mid - 9f));
                painter.LineTo(new Vector2(X(median), mid + 9f));
                painter.Stroke();

                // One dot per run, spread up and down a little so equal values stay visible. Evenly thinned past MaxDots.
                painter.fillColor = new Color(color.r, color.g, color.b, 0.75f);
                int step = Mathf.Max(1, Mathf.CeilToInt(values.Count / (float)MaxDots));
                for (int i = 0; i < values.Count; i += step)
                {
                    float jitter = ((i * 7919) % 13 - 6) * 0.9f;
                    painter.BeginPath();
                    painter.Arc(new Vector2(X(values[i]), mid + jitter), 2.2f, 0f, 360f);
                    painter.Fill();
                }
            }
        }
    }

    /// <summary>
    /// Several coverage curves on one time axis, one colour each with a legend, and a translucent band of one standard
    /// deviation around each where there is one. Hovering reads every line's value at that moment.
    /// </summary>
    public sealed class CompareChart : VisualElement
    {
        const float AxisWidth = 40f;
        const float AxisHeight = 20f;

        public sealed class Series
        {
            public string label;
            public Color color;
            public float[] values;
            public float[] spread;
        }

        // Distinct on the dark surface and in the same family as the app's accents. Past eight they repeat.
        static readonly Color[] Palette =
        {
            new Color(0.357f, 0.553f, 0.937f), // blue, --accent
            new Color(0.235f, 0.796f, 0.541f), // green
            new Color(0.949f, 0.702f, 0.298f), // amber
            new Color(0.937f, 0.353f, 0.404f), // red
            new Color(0.690f, 0.506f, 0.957f), // violet
            new Color(0.298f, 0.812f, 0.871f), // cyan
            new Color(0.957f, 0.522f, 0.776f), // pink
            new Color(0.780f, 0.800f, 0.380f), // olive
        };

        readonly List<Series> series;
        readonly float duration;
        readonly VisualElement legend;
        readonly VisualElement plot;
        readonly VisualElement crosshair;
        readonly Label readout;
        readonly float maxPercent;

        public static Color ColorOf(int index) => Palette[Mathf.Max(0, index) % Palette.Length];

        public CompareChart(List<Series> series, float duration)
        {
            this.series = series;
            this.duration = duration;
            AddToClassList("le-chart");
            AddToClassList("le-chart--compare");

            legend = Ui.Div(this, "le-chart__legend le-chart__legend--wrap");
            foreach (var s in series)
            {
                var item = Ui.Div(legend, "le-chart__legend-item");
                var key = Ui.Div(item, "le-chart__key");
                key.style.backgroundColor = s.color;
                Ui.Text(item, s.label, "le-chart__legend-label");
            }

            plot = Ui.Div(this, "le-chart__plot");
            plot.style.left = AxisWidth;
            plot.style.bottom = AxisHeight;
            plot.generateVisualContent += Draw;

            float peak = 0f;
            foreach (var s in series)
                for (int i = 0; i < s.values.Length; i++)
                    peak = Mathf.Max(peak, s.values[i] + (s.spread != null && i < s.spread.Length ? s.spread[i] : 0f));
            maxPercent = Mathf.Clamp(Mathf.Ceil(peak / 25f) * 25f, 25f, 100f);

            for (int i = 0; i <= 4; i++)
            {
                var tick = Ui.Text(this, $"{maxPercent * i / 4f:0}%", "le-chart__y-label");
                tick.userData = i / 4f;
            }

            var start = Ui.Text(this, "0:00", "le-chart__x-label");
            start.style.left = AxisWidth;
            var end = Ui.Text(this, VacuumSettings.FormatDuration(duration), "le-chart__x-label");
            end.style.right = 0f;

            crosshair = Ui.Div(plot, "le-chart__crosshair");
            crosshair.pickingMode = PickingMode.Ignore;
            readout = Ui.Text(plot, string.Empty, "le-chart__readout");
            readout.pickingMode = PickingMode.Ignore;
            Ui.SetVisible(crosshair, false);
            Ui.SetVisible(readout, false);

            plot.RegisterCallback<PointerMoveEvent>(evt => ShowHover(evt.localPosition.x));
            plot.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                Ui.SetVisible(crosshair, false);
                Ui.SetVisible(readout, false);
            });
            RegisterCallback<GeometryChangedEvent>(_ => PlaceTicks());
            plot.RegisterCallback<GeometryChangedEvent>(_ => PlaceTicks());
        }

        /// <summary>The legend wraps to as many lines as it needs; the plot starts below it and the y ticks follow.</summary>
        void PlaceTicks()
        {
            float legendHeight = legend.layout.height;
            if (float.IsNaN(legendHeight)) return;

            float top = legendHeight + 8f;
            if (!Mathf.Approximately(plot.resolvedStyle.top, top))
            {
                plot.style.top = top;
                return; // the plot's new size raises another geometry change, which places the ticks
            }

            float height = plot.layout.height;
            if (float.IsNaN(height)) return;
            foreach (var child in Children())
                if (child.userData is float t) child.style.top = top + height * (1f - t) - 7f;
        }

        void ShowHover(float x)
        {
            var rect = plot.contentRect;
            if (rect.width <= 0f || duration <= 0f) return;

            float fraction = Mathf.Clamp01(x / rect.width);
            var parts = new List<string> { VacuumSettings.FormatDuration(duration * fraction) };
            foreach (var s in series)
            {
                if (s.values.Length < 2) continue;
                float position = fraction * (s.values.Length - 1);
                int index = Mathf.FloorToInt(position);
                int next = Mathf.Min(index + 1, s.values.Length - 1);
                parts.Add($"{s.label} {Mathf.Lerp(s.values[index], s.values[next], position - index):0.0}%");
            }

            crosshair.style.left = x;
            readout.text = string.Join("\n", parts);
            readout.style.left = x < rect.width * 0.6f ? x + 8f : x - 8f - readout.resolvedStyle.width;
            Ui.SetVisible(crosshair, true);
            Ui.SetVisible(readout, true);
        }

        void Draw(MeshGenerationContext context)
        {
            var rect = plot.contentRect;
            if (rect.width < 4f || rect.height < 4f) return;

            var painter = context.painter2D;
            painter.lineWidth = 1f;
            painter.strokeColor = new Color(1f, 1f, 1f, 0.07f);
            painter.BeginPath();
            for (int i = 0; i <= 4; i++)
            {
                float y = rect.yMax - rect.height * i / 4f;
                painter.MoveTo(new Vector2(rect.xMin, y));
                painter.LineTo(new Vector2(rect.xMax, y));
            }
            painter.Stroke();

            Vector2 Point(float[] values, int i, float offset) => new Vector2(
                rect.xMin + rect.width * i / (values.Length - 1),
                rect.yMax - rect.height * Mathf.Clamp01((values[i] + offset) / maxPercent));

            // Bands first, so every line sits on top of them.
            foreach (var s in series)
            {
                if (s.values.Length < 2 || s.spread == null || s.spread.Length != s.values.Length) continue;
                bool any = false;
                foreach (float sd in s.spread) any |= sd > 0.01f;
                if (!any) continue;

                painter.fillColor = new Color(s.color.r, s.color.g, s.color.b, 0.12f);
                painter.BeginPath();
                painter.MoveTo(Point(s.values, 0, s.spread[0]));
                for (int i = 1; i < s.values.Length; i++) painter.LineTo(Point(s.values, i, s.spread[i]));
                for (int i = s.values.Length - 1; i >= 0; i--) painter.LineTo(Point(s.values, i, -s.spread[i]));
                painter.ClosePath();
                painter.Fill();
            }

            painter.lineJoin = LineJoin.Round;
            painter.lineCap = LineCap.Round;
            painter.lineWidth = 2f;

            // Drawn last-first, so the leader's line ends up on top.
            for (int s = series.Count - 1; s >= 0; s--)
            {
                var values = series[s].values;
                if (values.Length < 2) continue;

                painter.strokeColor = series[s].color;
                painter.BeginPath();
                painter.MoveTo(Point(values, 0, 0f));
                for (int i = 1; i < values.Length; i++) painter.LineTo(Point(values, i, 0f));
                painter.Stroke();
            }
        }
    }
}
