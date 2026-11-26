using System;
using System.Collections.Generic;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The start screen's Analysis page. On the left, every recorded run and batch experiment, each with a
    /// box to pick it for comparison. On the right, the selection's results, or the comparison of the picked
    /// runs. A single run shows its metrics, a replay you can scrub through, coverage over time, per-room
    /// coverage and the log of settings changed mid-run. A batch shows each algorithm's mean, variance and
    /// 95% confidence interval on identical seeds, an averaged heatmap and an averaged coverage curve. The
    /// comparison pools the picked runs per algorithm (or keeps each pick apart), ranks them on a chosen
    /// metric and says whether the leader is clearly best. Cloud and local headless campaigns are brought in with
    /// Import results (<see cref="CampaignImport"/>) and shown, and compared, the same way as batches.
    /// </summary>
    public sealed class AnalysisView : VisualElement
    {
        readonly OverlayLayer overlay;
        readonly FloorPalette palette;
        readonly VisualElement body;
        readonly Label subtitle;
        readonly VisualElement clearButton;
        List<RunReport> runs = new List<RunReport>();
        List<BatchResult> batches = new List<BatchResult>();
        RunReport selectedRun;
        BatchResult selectedBatch;
        string batchAlgorithm;

        // Runs and batches picked for comparison, by id, and how the comparison is shown.
        readonly HashSet<string> picked = new HashSet<string>();
        bool comparing;
        CompareMetric compareMetric = CompareMetric.Coverage;
        CompareGrouping compareGrouping = CompareGrouping.Algorithm;

        public event Action BackRequested;
        public event Action SimulateRequested;

        /// <summary>Run this floor plan again with the current configuration.</summary>
        public event Action<RunReport> RerunRequested;

        public event Action<string> EditRequested;

        public AnalysisView(OverlayLayer overlay, FloorPalette palette, string focusedId)
        {
            this.overlay = overlay;
            this.palette = palette;
            AddToClassList("le-screen");

            var header = Ui.PageHeader(this, "Analysis", "Start screen", () => BackRequested?.Invoke(), out subtitle);
            var folder = Ui.Button(header, "Data folder", IconKind.Save,
                () => FloorPlanFiles.Reveal(Application.persistentDataPath), "le-btn--ghost");
            Ui.Tooltip(folder, "Open the folder holding every run's telemetry CSVs and replays, and each batch's results file and CSV");
            var import = Ui.Button(header, "Import results", IconKind.Plus, () => CampaignImportView.Show(overlay, Reload), "le-btn--ghost");
            Ui.Tooltip(import, "Bring in a cloud campaign fetched with vacuumctl, or a local headless campaign");
            clearButton = Ui.Button(header, "Clear history", IconKind.Trash, ConfirmClear, "le-btn--ghost");
            Ui.Button(header, "Start simulation", IconKind.Play, () => SimulateRequested?.Invoke(), "le-btn--primary");

            body = Ui.Div(this, "le-analysis");
            Reload(focusedId);
        }

        void Reload(string focusId)
        {
            runs = RunHistory.LoadAll();
            batches = BatchArchive.LoadAll();
            selectedRun = null;
            selectedBatch = null;
            picked.RemoveWhere(id => !runs.Exists(run => run.id == id) && !batches.Exists(batch => batch.batchId == id));
            if (picked.Count == 0) comparing = false;

            var focusedBatch = batches.Find(batch => batch.batchId == focusId);
            if (focusedBatch != null) SelectBatch(focusedBatch);
            else selectedRun = runs.Find(run => run.id == focusId);

            if (selectedRun == null && selectedBatch == null)
            {
                var newest = Entries();
                if (newest.Count > 0)
                {
                    if (newest[0].batch != null) SelectBatch(newest[0].batch);
                    else selectedRun = newest[0].single;
                }
            }

            int trials = 0;
            foreach (var batch in batches) trials += batch.trials.Count;
            subtitle.text = $"{(runs.Count == 1 ? "1 run" : $"{runs.Count} runs")}" +
                            (batches.Count > 0 ? $" · {batches.Count} batch{(batches.Count == 1 ? "" : "es")} of {trials} trials" : string.Empty) +
                            " · tick runs on one floor plan to compare them";
            Ui.SetVisible(clearButton, runs.Count > 0 || batches.Count > 0);
            Rebuild();
        }

        /// <summary>Single runs and batches together, newest first.</summary>
        List<CompareSource> Entries()
        {
            var entries = new List<CompareSource>(runs.Count + batches.Count);
            foreach (var run in runs) entries.Add(CompareSource.Of(run));
            foreach (var batch in batches) entries.Add(CompareSource.Of(batch));
            entries.Sort((a, b) => FinishedAt(b).CompareTo(FinishedAt(a)));
            return entries;
        }

        static DateTime FinishedAt(CompareSource source) => source.single != null ? source.single.FinishedAt : source.batch.FinishedAt;

        void SelectBatch(BatchResult batch)
        {
            selectedBatch = batch;
            selectedRun = null;
            if (batch.algorithms.Count > 0 && batch.Algorithm(batchAlgorithm) == null)
                batchAlgorithm = BatchStats.Summarise(batch.Reports())[0].algorithmId;
        }

        void Rebuild()
        {
            body.Clear();

            if (runs.Count == 0 && batches.Count == 0)
            {
                var empty = Ui.Div(body, "le-empty le-analysis__empty");
                var icon = new IconElement(IconKind.Chart);
                icon.AddToClassList("le-empty__icon");
                empty.Add(icon);
                Ui.Text(empty, "No runs yet", "le-empty__title");
                Ui.Text(empty, "Run a floor plan in the simulator and press Finish, start a batch experiment from a floor plan's card, " +
                               "or import a cloud or headless campaign with Import results. Results land here.",
                    "le-empty__text");
                Ui.Button(Ui.Div(empty, "le-empty__actions"), "Start simulation", IconKind.Play,
                    () => SimulateRequested?.Invoke(), "le-btn--primary");
                return;
            }

            var side = Ui.Div(body, "le-analysis__list");
            BuildCompareBar(side);

            var list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("le-analysis__scroll");
            list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            side.Add(list);

            foreach (var entry in Entries())
                list.Add(entry.batch != null ? BuildBatchRow(entry.batch) : BuildRunRow(entry.single));

            var detail = new ScrollView(ScrollViewMode.Vertical);
            detail.AddToClassList("le-analysis__detail");
            detail.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            body.Add(detail);

            var column = Ui.Div(detail, "le-page__column le-page__column--wide");
            if (comparing && picked.Count > 0) BuildCompare(column);
            else if (selectedBatch != null) BuildBatchDetail(column, selectedBatch);
            else if (selectedRun != null) BuildRunDetail(column, selectedRun);
        }

        // ---------------------------------------------------------------- list

        /// <summary>The bar above the list: which floor plan is being compared, how much is ticked, and Compare.</summary>
        void BuildCompareBar(VisualElement side)
        {
            var bar = Ui.Div(side, "le-analysis__compare-bar");
            var sources = PickedSources();
            var text = Ui.Div(bar, "le-analysis__compare-text");

            if (sources.Count == 0)
            {
                Ui.Text(text, "Tick runs or batches on one floor plan to compare them", "le-analysis__compare-hint");
                return;
            }

            int runCount = 0;
            foreach (var source in sources) runCount += source.RunCount;
            Ui.Text(text, sources[0].LevelName, "le-analysis__compare-plan");
            Ui.Text(text, $"{sources.Count} picked · {(runCount == 1 ? "1 run" : $"{runCount} runs")}", "le-analysis__compare-hint");

            Ui.IconButton(bar, IconKind.Close, "Untick everything", () =>
            {
                picked.Clear();
                comparing = false;
                Rebuild();
            }, "le-btn--ghost le-btn--small");

            if (!comparing)
            {
                var compare = Ui.Button(bar, "Compare", IconKind.Chart, () =>
                {
                    comparing = true;
                    Rebuild();
                }, "le-btn--primary le-analysis__compare-button");
                Ui.Tooltip(compare, "Rank the ticked runs and batches, per algorithm");
            }
        }

        /// <summary>The floor plan the current picks are on, or null when nothing is picked.</summary>
        string PickedPlan()
        {
            var sources = PickedSources();
            return sources.Count > 0 ? sources[0].PlanKey : null;
        }

        /// <summary>The box that ticks a row for comparison, without opening it. Rows on another floor plan than the picks can't be ticked.</summary>
        VisualElement PickBox(VisualElement row, CompareSource source)
        {
            string id = source.Id;
            bool on = picked.Contains(id);
            string plan = PickedPlan();
            bool allowed = on || plan == null || plan == source.PlanKey;

            var box = Ui.Div(row, "le-run-row__check");
            if (on)
            {
                box.AddToClassList("le-run-row__check--on");
                box.Add(new IconElement(IconKind.Check));
            }
            if (!allowed) box.AddToClassList("le-run-row__check--off");
            Ui.Tooltip(box, on ? "Take out of the comparison"
                : allowed ? "Add to the comparison"
                : "Only runs on the same floor plan can be compared. Untick the current picks to compare this plan instead.");

            // Handled here and stopped, so ticking doesn't also open the row.
            box.RegisterCallback<PointerDownEvent>(evt =>
            {
                evt.StopPropagation();
                if (!allowed) return;
                if (!picked.Remove(id)) picked.Add(id);
                if (picked.Count == 0) comparing = false;
                Rebuild();
            });
            box.RegisterCallback<PointerUpEvent>(evt => evt.StopPropagation());
            return box;
        }

        VisualElement BuildRunRow(RunReport run)
        {
            var row = Ui.Div(null, "le-run-row");
            if (!comparing && run == selectedRun) row.AddToClassList("le-run-row--selected");
            PickBox(row, CompareSource.Of(run));

            var text = Ui.Div(row, "le-run-row__text");
            Ui.Text(text, run.levelName, "le-run-row__title");
            Ui.Text(text, $"{run.PatternLabel} · {VacuumSettings.FormatDuration(run.durationSeconds)} · {FormatWhen(run.FinishedAt)}",
                "le-run-row__meta");
            Ui.Text(row, $"{run.coveragePercent:0.0}%", $"le-run-row__value le-tone-text--{CoverageTone(run.coveragePercent)}");

            row.AddManipulator(new Clickable(() =>
            {
                selectedRun = run;
                selectedBatch = null;
                comparing = false;
                Rebuild();
            }));
            return row;
        }

        VisualElement BuildBatchRow(BatchResult batch)
        {
            var row = Ui.Div(null, "le-run-row le-run-row--batch");
            if (!comparing && batch == selectedBatch) row.AddToClassList("le-run-row--selected");
            PickBox(row, CompareSource.Of(batch));

            var icon = new IconElement(IconKind.Chart);
            icon.AddToClassList("le-run-row__icon");
            row.Add(icon);

            var summaries = BatchStats.Summarise(batch.Reports());
            var text = Ui.Div(row, "le-run-row__text");
            Ui.Text(text, batch.Title, "le-run-row__title");
            Ui.Text(text, (batch.IsImported ? $"{batch.levelName} · {batch.configuration} · " : string.Empty) +
                          $"{summaries.Count} algorithm(s) · {batch.trials.Count} runs · {FormatWhen(batch.FinishedAt)}", "le-run-row__meta");
            if (summaries.Count > 0)
                Ui.Text(row, $"{summaries[0].coverage.mean:0.0}%",
                    $"le-run-row__value le-tone-text--{CoverageTone((float)summaries[0].coverage.mean)}");

            row.AddManipulator(new Clickable(() =>
            {
                SelectBatch(batch);
                comparing = false;
                Rebuild();
            }));
            return row;
        }

        // ---------------------------------------------------------------- comparison

        /// <summary>The picked runs and batches, in list order.</summary>
        List<CompareSource> PickedSources() => Entries().FindAll(entry => picked.Contains(entry.Id));

        void BuildCompare(VisualElement column)
        {
            var sources = PickedSources();
            var groups = RunComparison.Build(sources, compareGrouping, compareMetric);

            var panel = Ui.Div(column, "le-panel");
            var header = Ui.Div(panel, "le-panel__header");
            var titles = Ui.Div(header, "le-panel__titles");
            int runCount = 0;
            foreach (var source in sources) runCount += source.RunCount;
            Ui.Text(titles, $"Comparison · {sources[0].LevelName}", "le-panel__title");
            Ui.Text(titles, $"{sources.Count} pick(s) · {runCount} runs · {groups.Count} group(s)", "le-panel__text");

            var grouping = new Segmented(new[] { "By algorithm", "By pick" }, (int)compareGrouping);
            grouping.SelectionChanged += index =>
            {
                compareGrouping = (CompareGrouping)index;
                Rebuild();
            };
            Ui.Tooltip(grouping, "Pool every picked run of the same algorithm, or keep each pick (and each algorithm within a batch) apart");
            header.Add(grouping);

            Ui.Text(panel, "RANK BY", "le-section__title");
            var metricLabels = Array.ConvertAll(RunComparison.Metrics, RunComparison.Label);
            var metric = new Segmented(metricLabels, Array.IndexOf(RunComparison.Metrics, compareMetric));
            metric.SelectionChanged += index =>
            {
                compareMetric = RunComparison.Metrics[index];
                Rebuild();
            };
            panel.Add(metric);

            if (groups.Count == 0)
            {
                Ui.Text(panel, "None of the picked runs can be grouped by algorithm: they all switched algorithm part-way. Try grouping by pick.",
                    "le-empty-hint");
                return;
            }

            var verdict = RunComparison.Judge(groups, compareMetric);
            var callout = Ui.Div(panel, verdict.clear ? "le-compare__verdict le-compare__verdict--clear" : "le-compare__verdict");
            Ui.Text(callout, verdict.headline, "le-compare__verdict-title");
            Ui.Text(callout, verdict.detail, "le-compare__verdict-text");

            foreach (string caveat in RunComparison.Caveats(sources, compareGrouping))
                Ui.Text(panel, caveat, "le-analysis__failure");

            BuildMetricBars(panel, groups);

            Ui.Text(panel, "EVERY MEASURE · MEAN ± 95% CONFIDENCE INTERVAL", "le-section__title le-analysis__chart-title");
            BuildStatsTable(panel, groups.ConvertAll(g => g.stats));

            // Mean coverage over time, one line per group, on one time axis.
            var curvePanel = Ui.Div(column, "le-panel");
            Ui.Text(curvePanel, "MEAN COVERAGE OVER TIME", "le-section__title");
            float duration = 0f;
            foreach (var group in groups) duration = Mathf.Max(duration, group.CurveDuration);
            var series = new List<(string, float[])>();
            foreach (var group in groups) series.Add((group.label, group.Curve(BatchArchive.CurvePoints, duration)));
            curvePanel.Add(new CompareChart(series, duration));
            Ui.Text(curvePanel, "A run that has ended holds its final coverage, so every line runs to the end.", "le-empty-hint");

            // What's in the comparison, with a way to take each pick out.
            var picks = Ui.Div(column, "le-panel");
            Ui.Text(picks, "PICKED", "le-section__title");
            foreach (var source in sources)
            {
                var row = Ui.Div(picks, "le-run-row");
                var text = Ui.Div(row, "le-run-row__text");
                if (source.single != null)
                {
                    var run = source.single;
                    Ui.Text(text, $"{run.levelName} · {run.PatternLabel}", "le-run-row__title");
                    Ui.Text(text, $"Single run · seed {run.seed} · {FormatWhen(run.FinishedAt)}", "le-run-row__meta");
                }
                else
                {
                    var batch = source.batch;
                    Ui.Text(text, $"Batch · {batch.levelName}", "le-run-row__title");
                    Ui.Text(text, $"{batch.algorithms.Count} algorithm(s) · {batch.trials.Count} runs · seeds {batch.MinSeed}–{batch.MaxSeed} · {FormatWhen(batch.FinishedAt)}",
                        "le-run-row__meta");
                }

                string id = source.Id;
                Ui.IconButton(row, IconKind.Close, "Take out of the comparison", () =>
                {
                    picked.Remove(id);
                    if (picked.Count == 0) comparing = false;
                    Rebuild();
                }, "le-btn--ghost le-btn--small");
            }
        }

        /// <summary>One bar per group on the chosen metric, with its 95% interval as a whisker, best first.</summary>
        void BuildMetricBars(VisualElement panel, List<CompareGroup> groups)
        {
            Ui.Text(panel, RunComparison.Label(compareMetric).ToUpperInvariant() +
                           (RunComparison.HigherIsBetter(compareMetric) ? " · HIGHER IS BETTER" : " · LOWER IS BETTER"),
                "le-section__title le-analysis__chart-title");

            // Coverage reads against 100%; everything else against the largest interval end.
            double scale = compareMetric == CompareMetric.Coverage ? 100 : 0;
            if (scale <= 0)
                foreach (var group in groups) scale = Math.Max(scale, group.metric.mean + group.metric.ci95);
            if (scale <= 0) scale = 1;

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var row = Ui.Div(panel, "le-compare-row");
                var label = Ui.Text(row, group.label, "le-compare-row__label le-compare-row__label--wide");
                Ui.Tooltip(label, group.label);

                var track = Ui.Div(row, "le-compare-row__track le-compare-row__track--ci");
                if (group.metric.count == 0)
                {
                    Ui.Text(row, "—", "le-compare-row__value");
                    Ui.Text(row, MissingNote(group), "le-compare-row__meta le-compare-row__meta--wide");
                    continue;
                }

                var fill = Ui.Div(track, i == 0 ? "le-compare-row__fill le-compare-row__fill--best" : "le-compare-row__fill");
                fill.style.width = Length.Percent(Mathf.Clamp((float)(group.metric.mean / scale * 100), 0f, 100f));
                if (HasInterval(group.metric)) ErrorBar(track, group.metric.Low, group.metric.High, scale);

                Ui.Text(row, RunComparison.Format(compareMetric, group.metric.mean), "le-compare-row__value");
                string spread = HasInterval(group.metric) ? $"± {RunComparison.FormatSpread(compareMetric, group.metric.ci95)} · " : string.Empty;
                string counted = group.metric.count < group.runs.Count ? $"{group.metric.count} of {group.runs.Count} runs" : $"{group.runs.Count} run(s)";
                Ui.Text(row, spread + counted, "le-compare-row__meta le-compare-row__meta--wide");
            }
        }

        string MissingNote(CompareGroup group) => compareMetric == CompareMetric.TimeTo90
            ? $"none of its {group.runs.Count} run(s) reached 90%"
            : $"no value in its {group.runs.Count} run(s)";

        // ---------------------------------------------------------------- batch

        void BuildBatchDetail(VisualElement column, BatchResult batch)
        {
            var members = batch.Reports();
            if (members.Count == 0) return;
            var summaries = BatchStats.Summarise(members);

            var panel = Ui.Div(column, "le-panel");
            var header = Ui.Div(panel, "le-panel__header");
            var titles = Ui.Div(header, "le-panel__titles");
            if (batch.IsImported)
            {
                Ui.Text(titles, $"{batch.SourceLabel} campaign · {batch.campaignName}", "le-panel__title");
                Ui.Text(titles, $"{batch.levelName} · configuration {batch.configuration} · {batch.FinishedAt:d MMM yyyy, HH:mm} · " +
                                $"{members.Count} runs · seeds {batch.MinSeed}–{batch.MaxSeed}", "le-panel__text");
            }
            else
            {
                Ui.Text(titles, $"Batch experiment · {batch.levelName}", "le-panel__title");
                Ui.Text(titles, $"{batch.FinishedAt:d MMM yyyy, HH:mm} · {members.Count} runs · seeds {batch.MinSeed}–{batch.MaxSeed}" +
                                (batch.headless ? " · headless" : string.Empty), "le-panel__text");
            }

            var compare = Ui.Button(header, "Compare", IconKind.Chart, () =>
            {
                // A comparison is on one floor plan, so picks on another plan make way for this batch.
                if (PickedPlan() != CompareSource.Of(batch).PlanKey) picked.Clear();
                picked.Add(batch.batchId);
                comparing = true;
                Rebuild();
            }, "le-btn--ghost");
            Ui.Tooltip(compare, "Add this batch to the comparison and open it. Tick other runs or batches to compare against them.");
            if (!string.IsNullOrEmpty(batch.levelPath) && File.Exists(batch.levelPath))
                Ui.IconButton(header, IconKind.Pen, "Open in level editor", () => EditRequested?.Invoke(batch.levelPath),
                    "le-btn--ghost le-btn--small");
            Ui.IconButton(header, IconKind.Save, "Export every trial and the statistics as CSV, to check or analyse them elsewhere",
                () => ExportCsv(batch), "le-btn--ghost le-btn--small");
            Ui.IconButton(header, IconKind.Trash, "Delete this batch", () => ConfirmDeleteBatch(batch),
                "le-btn--ghost le-btn--small");

            Ui.Text(panel, "PER-ALGORITHM STATISTICS · IDENTICAL SEEDS", "le-section__title");
            BuildStatsTable(panel, summaries);

            // Coverage bars with the confidence interval as an error bar (|—|), so overlaps read at a glance. No error bar
            // when there is no interval: one run, or every run identical (a deterministic algorithm on fixed seeds).
            Ui.Text(panel, "COVERAGE", "le-section__title le-analysis__chart-title");
            foreach (var summary in summaries)
            {
                var row = Ui.Div(panel, "le-compare-row");
                var name = Ui.Text(row, summary.Label, "le-compare-row__label");
                Ui.Tooltip(name, summary.Label);
                var track = Ui.Div(row, "le-compare-row__track le-compare-row__track--ci");
                var fill = Ui.Div(track, "le-compare-row__fill");
                double mean = summary.coverage.mean, ci = summary.coverage.ci95;
                fill.style.width = Length.Percent(Mathf.Clamp((float)mean, 0f, 100f));

                bool hasInterval = HasInterval(summary.coverage) && ci >= 0.05;
                if (hasInterval) ErrorBar(track, mean - ci, mean + ci, 100);

                Ui.Text(row, hasInterval ? $"{mean:0.0}% ± {ci:0.0}" : $"{mean:0.0}%", "le-compare-row__value le-compare-row__value--ci");
            }
            Ui.Text(panel, "Bar: mean coverage. |—|: its 95% confidence interval (none when every run came out the same). Few seeds give wide intervals.",
                "le-empty-hint le-analysis__chart-note");

            // Averaged heatmap and coverage curve for one algorithm at a time.
            var mapPanel = Ui.Div(column, "le-panel");
            var mapHeader = Ui.Div(mapPanel, "le-panel__header");
            var mapTitles = Ui.Div(mapHeader, "le-panel__titles");
            Ui.Text(mapTitles, "Averaged across seeds", "le-panel__title");
            var chosen = summaries.Find(s => s.algorithmId == batchAlgorithm) ?? summaries[0];
            Ui.Text(mapTitles, $"{chosen.Label}: mean coverage map and curve over {chosen.runs.Count} runs", "le-panel__text");

            var labels = summaries.ConvertAll(s => s.Label).ToArray();
            var picker = new Segmented(labels, summaries.IndexOf(chosen));
            picker.SelectionChanged += index =>
            {
                batchAlgorithm = summaries[index].algorithmId;
                Rebuild();
            };
            mapPanel.Add(picker);

            var averages = batch.Algorithm(chosen.algorithmId);
            var level = LoadLevelFor(chosen.runs);
            var map = averages != null ? BatchStats.DecodeMap(averages.map, batch.mapWidth * batch.mapHeight) : null;
            if (level != null && map != null)
            {
                var view = new FloorMapView(level, palette);
                view.AddToClassList("le-analysis__map");
                view.SetCoverage(map, batch.mapWidth, batch.mapHeight, batch.mapRect);
                mapPanel.Add(view);
                Ui.Text(mapPanel, "Blue is floor cleaned in every run; red is floor left dirty.", "le-empty-hint");
            }
            else Ui.Text(mapPanel, "No coverage maps were saved for these runs.", "le-empty-hint");

            Ui.Text(mapPanel, "MEAN COVERAGE OVER TIME", "le-section__title le-analysis__chart-title");
            if (averages != null && averages.curve.Count > 1) mapPanel.Add(new CoverageChart(averages.curve, averages.curveDuration));
            else Ui.Text(mapPanel, "These runs were imported without their coverage timelines.", "le-empty-hint");

            BuildBatchRoomTable(column, chosen);

            // Every trial, so any one can be opened and replayed in full.
            var trials = Ui.Div(column, "le-panel");
            Ui.Text(trials, $"{chosen.Label.ToUpperInvariant()} TRIALS", "le-section__title");
            foreach (var run in chosen.runs)
            {
                var row = Ui.Div(trials, "le-run-row");
                var text = Ui.Div(row, "le-run-row__text");
                Ui.Text(text, $"Seed {run.seed}", "le-run-row__title");
                Ui.Text(text, $"{VacuumSettings.FormatDuration(run.durationSeconds)} · {run.endReason}" +
                              (string.IsNullOrEmpty(run.failure) ? string.Empty : " · algorithm failed"), "le-run-row__meta");
                Ui.Text(row, $"{run.coveragePercent:0.0}%", $"le-run-row__value le-tone-text--{CoverageTone(run.coveragePercent)}");
                var captured = run;
                row.AddManipulator(new Clickable(() => OpenTrial(captured)));
            }
        }

        /// <summary>Opens one batch trial in full, from the report saved in its data folder.</summary>
        void OpenTrial(RunReport trial)
        {
            var full = BatchArchive.LoadTrial(trial.dataFolder);
            if (full == null)
            {
                overlay.Toast("This trial's saved data is gone, so it can't be opened.");
                return;
            }

            // The batch's record of what was run wins: an imported trial's own summary.json predates the import.
            full.id = trial.id;
            full.batchId = trial.batchId;
            full.algorithmId = trial.algorithmId;
            full.algorithmName = trial.algorithmName;
            full.seed = trial.seed;
            full.failure = trial.failure;
            full.source = trial.source;
            full.campaignName = trial.campaignName;
            full.configuration = trial.configuration;
            if (string.IsNullOrEmpty(full.levelName)) full.levelName = trial.levelName;
            selectedRun = full;
            selectedBatch = null;
            comparing = false;
            Rebuild();
        }

        /// <summary>
        /// Requirement 2.8: for each algorithm (or compared group), the mean, sample variance, standard deviation and
        /// 95% confidence interval of coverage, time and battery used, over the trials that succeeded. Then the other measures.
        /// </summary>
        void BuildStatsTable(VisualElement parent, List<AlgorithmSummary> summaries)
        {
            var table = Ui.Div(parent, "le-table");
            var head = Ui.Div(table, "le-table__row le-table__row--head");
            foreach (string heading in new[] { "Algorithm", "Measure", "Runs", "Mean", "Variance", "SD", "95% CI" })
                Ui.Text(head, heading, "le-table__cell");

            foreach (var s in summaries)
            {
                string runs = s.failed > 0 ? $"{s.coverage.count} (+{s.failed} failed)" : s.coverage.count.ToString();
                StatsRow(table, s.Label, "Coverage (%)", runs, s.coverage, "0.00");
                StatsRow(table, string.Empty, "Time (s)", string.Empty, s.seconds, "0.0");
                StatsRow(table, string.Empty, "Battery used", string.Empty, s.batteryUsed, "0.0");
            }

            Ui.Text(parent, "Variance is the sample variance (n − 1) across seeds and SD its square root. The 95% confidence interval " +
                            "is for the mean, using Student's t with n − 1 degrees of freedom. Time is simulated seconds; battery is " +
                            "in work units. Failed runs are listed but left out of the statistics.", "le-empty-hint");

            var more = Ui.Div(parent, "le-table le-panel__gap");
            var moreHead = Ui.Div(more, "le-table__row le-table__row--head");
            foreach (string heading in new[] { "Algorithm", "Path length", "Revisits", "To 90%" })
                Ui.Text(moreHead, heading, "le-table__cell");
            foreach (var s in summaries)
            {
                var row = Ui.Div(more, "le-table__row");
                var name = Ui.Text(row, s.Label, "le-table__cell le-table__cell--strong");
                Ui.Tooltip(name, s.Label);
                Ui.Text(row, s.distance.count > 0 ? Ui.FormatLength((float)s.distance.mean) : "—", "le-table__cell");
                Ui.Text(row, s.revisits.count > 0 ? $"{s.revisits.mean:0}" : "—", "le-table__cell");
                Ui.Text(row, s.timeTo90.count > 0 ? $"{VacuumSettings.FormatDuration((float)s.timeTo90.mean)} ({s.timeTo90.count}/{s.succeeded.Count})" : "never",
                    "le-table__cell");
            }
        }

        static void StatsRow(VisualElement table, string algorithm, string measure, string runs, Summary summary, string format)
        {
            var row = Ui.Div(table, "le-table__row");
            var name = Ui.Text(row, algorithm, "le-table__cell le-table__cell--strong");
            if (!string.IsNullOrEmpty(algorithm)) Ui.Tooltip(name, algorithm);
            Ui.Text(row, measure, "le-table__cell");
            Ui.Text(row, runs, "le-table__cell");
            if (summary.count == 0)
            {
                for (int i = 0; i < 4; i++) Ui.Text(row, "—", "le-table__cell");
                return;
            }
            Ui.Text(row, summary.mean.ToString(format), "le-table__cell le-table__cell--strong");
            Ui.Text(row, summary.variance.ToString(format), "le-table__cell");
            Ui.Text(row, summary.StandardDeviation.ToString(format), "le-table__cell");
            Ui.Text(row, summary.count > 1 ? $"{summary.Low.ToString(format)} – {summary.High.ToString(format)}" : "needs 2+ runs", "le-table__cell");
        }

        /// <summary>True when a summary has a confidence interval worth drawing: two or more runs that didn't all come out the same.</summary>
        static bool HasInterval(Summary summary) =>
            summary.count >= 2 && !double.IsNaN(summary.ci95) && !double.IsInfinity(summary.ci95) && summary.ci95 > 0;

        /// <summary>An error bar (|—|) from <paramref name="low"/> to <paramref name="high"/> on a track that spans 0 to <paramref name="scale"/>.</summary>
        static void ErrorBar(VisualElement track, double low, double high, double scale)
        {
            var whisker = Ui.Div(track, "le-compare-row__ci");
            Ui.Div(whisker, "le-compare-row__ci-line");
            float from = Mathf.Clamp((float)(low / scale * 100), 0f, 100f);
            float to = Mathf.Clamp((float)(high / scale * 100), 0f, 100f);
            whisker.style.left = Length.Percent(from);
            whisker.style.width = Length.Percent(Mathf.Max(0.4f, to - from));
        }

        void ExportCsv(BatchResult batch)
        {
            string name = batch.IsImported ? $"{batch.campaignName} {batch.levelName} {batch.configuration}" : $"batch {batch.levelName}";
            try
            {
                string path = FloorPlanFiles.PickSavePath("Export batch results", name, "csv");
                if (path == null) return;
                File.WriteAllText(path, BatchStats.ToCsv(batch.Reports()));
                overlay.Toast($"Saved to {path}", "Show", () => FloorPlanFiles.Reveal(Path.GetDirectoryName(path)));
            }
            catch (Exception exception)
            {
                overlay.Toast($"Couldn't export: {exception.Message}");
            }
        }

        LevelSnapshot LoadLevelFor(IEnumerable<RunReport> group)
        {
            foreach (var run in group)
            {
                if (string.IsNullOrEmpty(run.dataFolder)) continue;
                var replay = RunReplay.Load(run.dataFolder);
                var level = replay != null ? LevelSnapshot.Parse(replay.levelJson) : null;
                if (level != null) return level;
            }
            return null;
        }

        // ---------------------------------------------------------------- single run

        void BuildRunDetail(VisualElement column, RunReport run)
        {
            var panel = Ui.Div(column, "le-panel");
            var header = Ui.Div(panel, "le-panel__header");
            var titles = Ui.Div(header, "le-panel__titles");
            Ui.Text(titles, $"{run.levelName} · {run.PatternLabel}", "le-panel__title");
            Ui.Text(titles, $"{run.FinishedAt:d MMM yyyy, HH:mm} · {run.endReason} · seed {run.seed}", "le-panel__text");

            var parentBatch = run.IsBatch ? batches.Find(batch => batch.batchId == run.batchId) : null;
            if (parentBatch != null)
                Ui.Button(header, "Batch", IconKind.Back, () =>
                {
                    batchAlgorithm = run.AlgorithmKey;
                    SelectBatch(parentBatch);
                    Rebuild();
                }, "le-btn--ghost");

            bool levelExists = !string.IsNullOrEmpty(run.levelPath) && File.Exists(run.levelPath);
            if (levelExists)
            {
                var again = Ui.Button(header, "Run again", IconKind.Play, () => RerunRequested?.Invoke(run), "le-btn--ghost");
                Ui.Tooltip(again, "Run this floor plan with the current configuration");
                Ui.IconButton(header, IconKind.Pen, "Open in level editor", () => EditRequested?.Invoke(run.levelPath),
                    "le-btn--ghost le-btn--small");
            }
            if (!string.IsNullOrEmpty(run.dataFolder) && Directory.Exists(run.dataFolder))
                Ui.IconButton(header, IconKind.Save, "Open this run's data: telemetry.csv, cell_events.csv, cells_final.csv, parameters.csv, replay.json",
                    () => FloorPlanFiles.Reveal(run.dataFolder), "le-btn--ghost le-btn--small");
            if (!run.IsBatch)
                Ui.IconButton(header, IconKind.Trash, "Delete this run", () => ConfirmDelete(run.id, $"the {run.PatternLabel} run on {run.levelName}"),
                    "le-btn--ghost le-btn--small");

            if (!string.IsNullOrEmpty(run.failure))
                Ui.Text(panel, $"The algorithm was stopped: {run.failure}", "le-analysis__failure");

            // Headline numbers, each with a bar where a fraction makes sense.
            var kpis = Ui.Div(panel, "le-kpis");
            Kpi(kpis, "Coverage", $"{run.coveragePercent:0.0}%", $"{Ui.FormatArea(run.CleanedArea)} of {Ui.FormatArea(run.CleanableArea)} cleaned",
                run.coveragePercent / 100f, CoverageTone(run.coveragePercent));
            Kpi(kpis, "Sim time", VacuumSettings.FormatDuration(run.durationSeconds), $"{run.CoveragePerMinute:0.0}% per minute");
            Kpi(kpis, "Battery used", $"{run.BatteryUsedPercent:0}%",
                run.batteryEmptied ? $"{run.batteryUsed:0} units · ran flat" : $"{run.batteryUsed:0} units",
                run.BatteryUsedPercent / 100f, run.batteryEmptied ? "low" : "battery");
            Kpi(kpis, "Path length", Ui.FormatLength(run.distanceMeters), $"{Ui.FormatLength(run.AverageSpeed)}/s average");
            if (kpis.childCount > 0) kpis[kpis.childCount - 1].AddToClassList("le-kpi--last"); // USS has no :last-child

            // How quickly the run got there.
            var milestones = Ui.Div(panel, "le-milestones");
            Milestone(milestones, "50%", run.timeTo50, run.durationSeconds);
            Milestone(milestones, "75%", run.timeTo75, run.durationSeconds);
            Milestone(milestones, "90%", run.timeTo90, run.durationSeconds);

            var metrics = Ui.Div(panel, "le-metrics");
            Metric(metrics, "Area per battery unit", run.batteryUsed > 0f ? Ui.FormatArea(run.CleanedPerBatteryUnit) : "—",
                "Floor cleaned for each battery work unit spent. Higher means the charge goes further.");
            Metric(metrics, "Revisits", run.revisitCount.ToString("N0"),
                "Times the vacuum went back over floor it had already covered and left.");
            Metric(metrics, "Revisits per metre", run.distanceMeters > 0f ? $"{run.revisitCount / run.distanceMeters:0.0}" : "—",
                "Revisits per metre driven: how much of the path retraced old ground.");
            Metric(metrics, "Floor area", Ui.FormatArea(run.floorArea), "Total floor area of every room.");
            Metric(metrics, "Under furniture", Ui.FormatArea(run.blockedArea),
                "Floor the vacuum can't reach because furniture blocks it. Left out of coverage.");
            Metric(metrics, "Battery left", run.vacuum != null ? $"{Mathf.Max(0f, run.vacuum.batteryCapacity - run.batteryUsed):0} units" : "—",
                "Charge remaining when the run ended.");

            var replay = !string.IsNullOrEmpty(run.dataFolder) ? RunReplay.Load(run.dataFolder) : null;
            var replayPanel = Ui.Div(column, "le-panel");
            Ui.Text(replayPanel, "REPLAY", "le-section__title");
            if (replay != null && replay.SampleCount > 1) replayPanel.Add(new ReplayPlayer(replay, palette));
            else Ui.Text(replayPanel, "No replay was saved for this run.", "le-empty-hint");

            Ui.Text(replayPanel, replay != null ? "COVERAGE AND BATTERY OVER TIME" : "COVERAGE OVER TIME",
                "le-section__title le-analysis__chart-title");
            replayPanel.Add(new CoverageChart(run, replay));

            if (run.roomCoverage.Count > 0) BuildRoomTable(Ui.Div(column, "le-panel"), run);

            var log = Ui.Div(column, "le-panel");
            Ui.Text(log, "SETTINGS CHANGED DURING THE RUN", "le-section__title");
            if (run.parameterEvents.Count == 0) Ui.Text(log, "None. The run used one set of settings throughout.", "le-empty-hint");
            foreach (var change in run.parameterEvents)
            {
                var row = Ui.Div(log, "le-log-row");
                Ui.Text(row, VacuumSettings.FormatDuration(change.time), "le-log-row__time");
                Ui.Text(row, change.parameter, "le-log-row__name");
                Ui.Text(row, $"{change.from} → {change.to}", "le-log-row__change");
            }

            if (run.vacuum != null)
            {
                var v = run.vacuum;
                Ui.Text(log,
                    $"Final settings: {VacuumShape.Label(v.shape).ToLowerInvariant()} body · drive {v.FormatSpeed(v.driveSpeed)} · reverse {v.FormatSpeed(v.reverseSpeed)} · turn {v.turnSpeed:0} °/s · " +
                    $"battery {VacuumSettings.FormatCapacity(v.batteryCapacity)} · grid {Ui.FormatSmallLength(run.cellSize)} · " +
                    $"telemetry every {run.telemetryInterval:0.##} s",
                    "le-empty-hint");
            }
        }

        /// <summary>
        /// Each room's coverage, time and battery. The cost per square metre shows which rooms are expensive
        /// to clean for their size: thick carpet, clutter or awkward shapes.
        /// </summary>
        void BuildRoomTable(VisualElement panel, RunReport run)
        {
            int count = Mathf.Min(run.roomNames.Count, run.roomCoverage.Count);
            bool energy = run.HasRoomEnergy;

            var header = Ui.Div(panel, "le-panel__header");
            var titles = Ui.Div(header, "le-panel__titles");
            Ui.Text(titles, "Rooms", "le-panel__title");

            float totalBattery = 0f;
            int hungriest = -1;
            if (energy)
                for (int i = 0; i < count; i++)
                {
                    totalBattery += run.roomBattery[i];
                    if (hungriest < 0 || run.roomBattery[i] > run.roomBattery[hungriest]) hungriest = i;
                }

            Ui.Text(titles, energy && hungriest >= 0 && totalBattery > 0f
                    ? $"Most battery went to {run.roomNames[hungriest]}: {run.roomBattery[hungriest] / totalBattery * 100f:0}% of the charge used."
                    : "Coverage of each room on its own.",
                "le-panel__text");

            var table = Ui.Div(panel, "le-table");
            var head = Ui.Div(table, "le-table__row le-table__row--head");
            Ui.Text(head, "Room", "le-table__cell le-table__cell--name");
            Ui.Text(head, "Coverage", "le-table__cell le-table__cell--bar");
            if (energy)
            {
                Ui.Text(head, "Battery", "le-table__cell le-table__cell--bar");
                Ui.Text(head, "Per m²", "le-table__cell le-table__cell--num");
                Ui.Text(head, "Time", "le-table__cell le-table__cell--num");
                Ui.Text(head, "Entries", "le-table__cell le-table__cell--num");
            }

            for (int i = 0; i < count; i++)
            {
                var row = Ui.Div(table, "le-table__row");
                Ui.Text(row, run.roomNames[i], "le-table__cell le-table__cell--name le-table__cell--strong");
                BarCell(row, run.roomCoverage[i] / 100f, $"{run.roomCoverage[i]:0}%", CoverageTone(run.roomCoverage[i]));
                if (!energy) continue;

                float share = totalBattery > 0f ? run.roomBattery[i] / totalBattery : 0f;
                BarCell(row, share, $"{run.roomBattery[i]:0} · {share * 100f:0}%", "battery");
                Ui.Text(row, run.roomArea[i] > 0f ? $"{run.roomBattery[i] / run.roomArea[i]:0.0}" : "—", "le-table__cell le-table__cell--num");
                Ui.Text(row, VacuumSettings.FormatDuration(run.roomSeconds[i]), "le-table__cell le-table__cell--num");
                Ui.Text(row, run.roomVisits[i].ToString(), "le-table__cell le-table__cell--num");
            }

            Ui.Text(panel, energy
                    ? "Battery is in work units and its share is of everything this run used. Per m² is battery units per square metre of the room. Time in a doorway counts toward the room the vacuum came from."
                    : "This run was recorded before per-room battery and time were tracked. Run it again to see them.",
                "le-empty-hint");
        }

        /// <summary>The batch version of the room table: each room's mean coverage, battery and time over one algorithm's trials.</summary>
        void BuildBatchRoomTable(VisualElement column, AlgorithmSummary summary)
        {
            var tracked = summary.runs.FindAll(run => run.HasRoomEnergy);
            if (tracked.Count == 0) return;

            var names = tracked[0].roomNames;
            tracked = tracked.FindAll(run => run.roomNames.Count == names.Count);

            int rooms = names.Count;
            var coverage = new double[rooms];
            var battery = new double[rooms];
            var seconds = new double[rooms];
            foreach (var run in tracked)
                for (int i = 0; i < rooms; i++)
                {
                    coverage[i] += run.roomCoverage[i];
                    battery[i] += run.roomBattery[i];
                    seconds[i] += run.roomSeconds[i];
                }

            double total = 0;
            for (int i = 0; i < rooms; i++)
            {
                coverage[i] /= tracked.Count;
                battery[i] /= tracked.Count;
                seconds[i] /= tracked.Count;
                total += battery[i];
            }

            var panel = Ui.Div(column, "le-panel");
            var header = Ui.Div(panel, "le-panel__header");
            var titles = Ui.Div(header, "le-panel__titles");
            Ui.Text(titles, "Rooms", "le-panel__title");
            Ui.Text(titles, $"{summary.Label}: mean per room over {tracked.Count} run{(tracked.Count == 1 ? "" : "s")}", "le-panel__text");

            var table = Ui.Div(panel, "le-table");
            var head = Ui.Div(table, "le-table__row le-table__row--head");
            Ui.Text(head, "Room", "le-table__cell le-table__cell--name");
            Ui.Text(head, "Coverage", "le-table__cell le-table__cell--bar");
            Ui.Text(head, "Battery", "le-table__cell le-table__cell--bar");
            Ui.Text(head, "Per m²", "le-table__cell le-table__cell--num");
            Ui.Text(head, "Time", "le-table__cell le-table__cell--num");

            for (int i = 0; i < rooms; i++)
            {
                float area = tracked[0].roomArea[i];
                float share = total > 0 ? (float)(battery[i] / total) : 0f;
                var row = Ui.Div(table, "le-table__row");
                Ui.Text(row, names[i], "le-table__cell le-table__cell--name le-table__cell--strong");
                BarCell(row, (float)coverage[i] / 100f, $"{coverage[i]:0}%", CoverageTone((float)coverage[i]));
                BarCell(row, share, $"{battery[i]:0} · {share * 100f:0}%", "battery");
                Ui.Text(row, area > 0f ? $"{battery[i] / area:0.0}" : "—", "le-table__cell le-table__cell--num");
                Ui.Text(row, VacuumSettings.FormatDuration((float)seconds[i]), "le-table__cell le-table__cell--num");
            }

            if (tracked.Count < summary.runs.Count)
                Ui.Text(panel, $"{summary.runs.Count - tracked.Count} older run(s) without per-room data are left out.", "le-empty-hint");
        }

        static void BarCell(VisualElement row, float fraction, string text, string tone)
        {
            var cell = Ui.Div(row, "le-table__cell le-table__cell--bar le-bar-cell");
            var track = Ui.Div(cell, "le-bar-cell__track");
            var fill = Ui.Div(track, $"le-bar-cell__fill le-tone--{tone}");
            fill.style.width = Length.Percent(Mathf.Clamp01(fraction) * 100f);
            Ui.Text(cell, text, "le-bar-cell__value");
        }

        static void Kpi(VisualElement parent, string label, string value, string note, float? fraction = null, string tone = null)
        {
            var tile = Ui.Div(parent, "le-kpi");
            Ui.Text(tile, label, "le-kpi__label");
            var valueLabel = Ui.Text(tile, value, "le-kpi__value");
            if (tone == "low") valueLabel.AddToClassList("le-kpi__value--low");

            if (fraction.HasValue)
            {
                var track = Ui.Div(tile, "le-kpi__track");
                var fill = Ui.Div(track, $"le-kpi__fill le-tone--{tone ?? "accent"}");
                fill.style.width = Length.Percent(Mathf.Clamp01(fraction.Value) * 100f);
            }

            if (!string.IsNullOrEmpty(note)) Ui.Text(tile, note, "le-kpi__note");
        }

        static void Milestone(VisualElement parent, string label, float seconds, float duration)
        {
            var item = Ui.Div(parent, "le-milestone");
            if (seconds < 0f) item.AddToClassList("le-milestone--missed");
            var dot = Ui.Div(item, "le-milestone__dot");
            dot.pickingMode = PickingMode.Ignore;
            var text = Ui.Div(item, "le-milestone__text");
            Ui.Text(text, $"{label} clean", "le-milestone__label");
            Ui.Text(text, seconds >= 0f
                ? $"{VacuumSettings.FormatDuration(seconds)}{(duration > 0f ? $"  ·  {seconds / duration * 100f:0}% of the run" : string.Empty)}"
                : "never reached", "le-milestone__value");
        }

        static void Metric(VisualElement parent, string label, string value, string tooltip)
        {
            var row = Ui.Div(parent, "le-metric");
            Ui.Text(row, label, "le-metric__label");
            Ui.Text(row, value, "le-metric__value");
            Ui.Tooltip(row, tooltip);
        }

        /// <summary>Colour band for a coverage percentage: good, middling or poor.</summary>
        static string CoverageTone(float percent) => percent >= 90f ? "good" : percent >= 60f ? "mid" : "low";

        // ---------------------------------------------------------------- actions

        void ConfirmDelete(string id, string what)
        {
            overlay.ShowDialog("Delete?", $"{char.ToUpperInvariant(what[0])}{what.Substring(1)} will be removed from the history, with its saved data.", null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Delete", ButtonStyle.Danger, () =>
                {
                    RunHistory.Delete(id);
                    Reload(null);
                }));
        }

        void ConfirmDeleteBatch(BatchResult batch)
        {
            overlay.ShowDialog("Delete batch?", $"The batch on {batch.levelName} and its {batch.trials.Count} runs will be removed, with their saved data.", null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Delete", ButtonStyle.Danger, () =>
                {
                    BatchArchive.Delete(batch.batchId);
                    Reload(null);
                }));
        }

        void ConfirmClear()
        {
            overlay.ShowDialog("Clear all runs?", "Every recorded run and batch will be deleted, with their saved data. Floor plans and configuration are kept.", null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Clear history", ButtonStyle.Danger, () =>
                {
                    RunHistory.Clear();
                    BatchArchive.Clear();
                    Reload(null);
                }));
        }

        static string FormatWhen(DateTime time)
        {
            if (time == DateTime.MinValue) return "unknown";
            var span = DateTime.Now - time;
            if (span.TotalMinutes < 1) return "just now";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
            return time.ToString("d MMM");
        }
    }

    /// <summary>
    /// Plays a finished run back from its saved replay: drag the slider to scrub forward or backward through
    /// simulated time, or press play. The map shows the coverage at the nearest saved moment and the exact
    /// path and position of the vacuum.
    /// </summary>
    public sealed class ReplayPlayer : VisualElement
    {
        /// <summary>Playing from start to end takes this many real seconds, whatever the run's length.</summary>
        const float PlaybackSeconds = 20f;

        readonly RunReplay replay;
        readonly FloorMapView map;
        readonly ValueSlider slider;
        readonly Label readout;
        readonly List<Vector2> path = new List<Vector2>();
        readonly VisualElement playButton;
        IVisualElementScheduledItem ticker;
        ReplayKeyframe shownKeyframe;
        float time;
        bool playing;

        public ReplayPlayer(RunReplay replay, FloorPalette palette)
        {
            this.replay = replay;
            AddToClassList("le-replay");

            for (int i = 0; i < replay.SampleCount; i++) path.Add(new Vector2(replay.x[i], replay.y[i]));

            var level = LevelSnapshot.Parse(replay.levelJson) ?? new LevelSnapshot();
            map = new FloorMapView(level, palette);
            map.AddToClassList("le-analysis__map");
            Add(map);

            var controls = Ui.Div(this, "le-replay__controls");
            playButton = Ui.IconButton(controls, IconKind.Play, "Play or pause the replay", TogglePlay, "le-btn--ghost");
            Ui.IconButton(controls, IconKind.Undo, "Back to the start", () => Seek(0f), "le-btn--ghost");

            float duration = Mathf.Max(0.01f, replay.Duration);
            slider = new ValueSlider(0f, duration, duration, VacuumSettings.FormatDuration);
            slider.AddToClassList("le-replay__slider");
            slider.ValueChanged += Seek;
            controls.Add(slider);
            readout = Ui.Text(controls, string.Empty, "le-replay__readout");

            RegisterCallback<DetachFromPanelEvent>(_ => ticker?.Pause());
            Seek(duration);
        }

        void TogglePlay()
        {
            playing = !playing;
            playButton.Q<IconElement>().Kind = playing ? IconKind.Pause : IconKind.Play;
            if (!playing)
            {
                ticker?.Pause();
                return;
            }

            if (time >= replay.Duration - 1e-3f) Seek(0f);
            ticker ??= schedule.Execute(Tick).Every(33);
            ticker.Resume();
        }

        void Tick()
        {
            if (!playing) return;

            float next = time + replay.Duration / PlaybackSeconds * 0.033f;
            if (next >= replay.Duration)
            {
                next = replay.Duration;
                TogglePlay();
            }
            Seek(next);
        }

        void Seek(float t)
        {
            time = Mathf.Clamp(t, 0f, replay.Duration);
            slider.SetValueWithoutNotify(time);

            int index = Mathf.Clamp(Mathf.RoundToInt(time / Mathf.Max(1e-4f, replay.sampleInterval)), 0, replay.SampleCount - 1);
            map.SetPath(path, index + 1, replay.heading[index], replay.robotRadius, replay.robotShape);

            var keyframe = replay.KeyframeAt(time);
            if (keyframe != null && keyframe != shownKeyframe)
            {
                shownKeyframe = keyframe;
                map.SetCoverage(BatchStats.DecodeMap(keyframe.map, replay.mapWidth * replay.mapHeight),
                    replay.mapWidth, replay.mapHeight, replay.mapRect);
            }

            readout.text = $"{VacuumSettings.FormatDuration(time)} · {replay.coverage[index]:0.0}% clean · battery {replay.battery[index]:0}";
        }
    }

    /// <summary>
    /// A line chart of a run's coverage against simulated time, drawn with Painter2D, optionally with the
    /// battery charge as a second line and markers where coverage first reached 50, 75 and 90%. Hovering
    /// shows the values at that moment.
    /// </summary>
    public sealed class CoverageChart : VisualElement
    {
        const float AxisWidth = 40f;
        const float AxisHeight = 20f;
        const float LegendHeight = 24f;

        static readonly Color Accent = new Color(0.357f, 0.553f, 0.937f);  // --accent
        static readonly Color BatteryColor = new Color(0.949f, 0.702f, 0.298f); // --warning

        readonly IReadOnlyList<float> samples;
        readonly IReadOnlyList<float> battery;
        readonly float duration;
        readonly VisualElement plot;
        readonly VisualElement crosshair;
        readonly Label readout;
        readonly List<(float time, Label label)> markers = new List<(float, Label)>();
        float maxPercent = 100f;

        public CoverageChart(RunReport run) : this(run, null) { }

        /// <summary>A run's coverage, plus its battery from the replay when there is one.</summary>
        public CoverageChart(RunReport run, RunReplay replay)
            : this(run.coverageSamples, run.durationSeconds, BatteryPercent(run, replay), new[]
            {
                (run.timeTo50, "50%"), (run.timeTo75, "75%"), (run.timeTo90, "90%"),
            })
        {
        }

        /// <summary>
        /// Evenly spaced coverage percentages from time zero to <paramref name="duration"/> seconds, with an
        /// optional evenly spaced battery percentage series over the same span and coverage milestones.
        /// </summary>
        public CoverageChart(IReadOnlyList<float> samples, float duration, IReadOnlyList<float> battery = null,
            IEnumerable<(float time, string label)> milestones = null)
        {
            this.samples = samples ?? Array.Empty<float>();
            this.battery = battery != null && battery.Count > 1 ? battery : null;
            this.duration = duration;
            AddToClassList("le-chart");

            float top = 8f;
            if (this.battery != null)
            {
                AddToClassList("le-chart--legend");
                var legend = Ui.Div(this, "le-chart__legend");
                LegendItem(legend, "Coverage", "le-chart__key--coverage");
                LegendItem(legend, "Battery remaining", "le-chart__key--battery");
                top = LegendHeight;
            }

            plot = Ui.Div(this, "le-chart__plot");
            plot.style.left = AxisWidth;
            plot.style.bottom = AxisHeight;
            plot.style.top = top;
            plot.generateVisualContent += Draw;

            // Fit the y axis to the run, rounded up to a 25% step, so a short run still shows its curve. With
            // battery on the same axis it stays at 100%.
            float peak = 0f;
            foreach (float sample in this.samples) peak = Mathf.Max(peak, sample);
            maxPercent = this.battery != null ? 100f : Mathf.Clamp(Mathf.Ceil(peak / 25f) * 25f, 25f, 100f);

            for (int i = 0; i <= 4; i++)
            {
                float t = i / 4f;
                var tick = Ui.Text(this, $"{maxPercent * t:0}%", "le-chart__y-label");
                tick.userData = t;
            }

            var start = Ui.Text(this, "0:00", "le-chart__x-label");
            start.style.left = AxisWidth;
            var end = Ui.Text(this, VacuumSettings.FormatDuration(duration), "le-chart__x-label le-chart__x-label--end");
            end.style.right = 0f;

            if (milestones != null && duration > 0f)
            {
                foreach (var (time, label) in milestones)
                {
                    if (time < 0f || time > duration) continue;
                    var marker = Ui.Text(plot, label, "le-chart__marker");
                    marker.pickingMode = PickingMode.Ignore;
                    markers.Add((time, marker));
                }
            }

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
        }

        static IReadOnlyList<float> BatteryPercent(RunReport run, RunReplay replay)
        {
            float capacity = run.vacuum != null ? run.vacuum.batteryCapacity : 0f;
            if (replay == null || replay.battery.Count < 2 || capacity <= 0f) return null;

            var percent = new List<float>(replay.battery.Count);
            foreach (float charge in replay.battery) percent.Add(Mathf.Clamp(charge / capacity * 100f, 0f, 100f));
            return percent;
        }

        static void LegendItem(VisualElement legend, string text, string keyClass)
        {
            var item = Ui.Div(legend, "le-chart__legend-item");
            Ui.Div(item, $"le-chart__key {keyClass}");
            Ui.Text(item, text, "le-chart__legend-label");
        }

        void PlaceTicks()
        {
            float height = plot.layout.height;
            float width = plot.layout.width;
            if (float.IsNaN(height) || float.IsNaN(width)) return;

            float top = plot.resolvedStyle.top;
            foreach (var child in Children())
            {
                if (child.userData is float t) child.style.top = top + height * (1f - t) - 7f;
            }

            foreach (var (time, label) in markers) label.style.left = width * time / duration + 3f;
        }

        static float At(IReadOnlyList<float> series, float fraction) =>
            series[Mathf.Clamp(Mathf.RoundToInt(fraction * (series.Count - 1)), 0, series.Count - 1)];

        void ShowHover(float x)
        {
            float width = plot.layout.width;
            if (samples.Count < 2 || width <= 1f) return;

            int index = Mathf.Clamp(Mathf.RoundToInt(x / width * (samples.Count - 1)), 0, samples.Count - 1);
            float fraction = (float)index / (samples.Count - 1);
            float px = fraction * width;
            float seconds = fraction * duration;

            crosshair.style.left = px;
            readout.text = $"{VacuumSettings.FormatDuration(seconds)} · {samples[index]:0.0}% clean" +
                           (battery != null ? $" · battery {At(battery, fraction):0}%" : string.Empty);
            readout.style.left = Mathf.Clamp(px + 8f, 0f, Mathf.Max(0f, width - (battery != null ? 230f : 130f)));
            Ui.SetVisible(crosshair, true);
            Ui.SetVisible(readout, true);
        }

        void Draw(MeshGenerationContext context)
        {
            var rect = plot.contentRect;
            if (rect.width < 4f || rect.height < 4f) return;

            var painter = context.painter2D;

            // Recessive grid: one hairline per tick.
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

            // Milestone markers: a faint vertical rule where coverage first crossed each threshold.
            if (markers.Count > 0)
            {
                painter.strokeColor = new Color(1f, 1f, 1f, 0.18f);
                painter.BeginPath();
                foreach (var (time, _) in markers)
                {
                    float x = rect.xMin + rect.width * time / duration;
                    painter.MoveTo(new Vector2(x, rect.yMin));
                    painter.LineTo(new Vector2(x, rect.yMax));
                }
                painter.Stroke();
            }

            if (samples.Count < 2) return;

            Vector2 Point(IReadOnlyList<float> series, int i) => new Vector2(
                rect.xMin + rect.width * i / (series.Count - 1),
                rect.yMax - rect.height * Mathf.Clamp01(series[i] / maxPercent));

            // Soft area under the coverage line, then the 2px line itself.
            painter.fillColor = new Color(Accent.r, Accent.g, Accent.b, 0.14f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin, rect.yMax));
            for (int i = 0; i < samples.Count; i++) painter.LineTo(Point(samples, i));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.ClosePath();
            painter.Fill();

            painter.lineJoin = LineJoin.Round;
            painter.lineCap = LineCap.Round;

            if (battery != null)
            {
                painter.lineWidth = 1.5f;
                painter.strokeColor = BatteryColor;
                painter.BeginPath();
                painter.MoveTo(Point(battery, 0));
                for (int i = 1; i < battery.Count; i++) painter.LineTo(Point(battery, i));
                painter.Stroke();
            }

            painter.lineWidth = 2f;
            painter.strokeColor = Accent;
            painter.BeginPath();
            painter.MoveTo(Point(samples, 0));
            for (int i = 1; i < samples.Count; i++) painter.LineTo(Point(samples, i));
            painter.Stroke();
        }
    }

    /// <summary>
    /// Several coverage curves on one time axis, one colour each with a legend: the comparison's mean
    /// coverage over time per group. Hovering reads every line's value at that moment.
    /// </summary>
    public sealed class CompareChart : VisualElement
    {
        const float AxisWidth = 40f;
        const float AxisHeight = 20f;

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

        readonly List<(string label, float[] values)> series;
        readonly float duration;
        readonly VisualElement legend;
        readonly VisualElement plot;
        readonly VisualElement crosshair;
        readonly Label readout;
        readonly float maxPercent;

        public static Color ColorOf(int index) => Palette[index % Palette.Length];

        public CompareChart(List<(string label, float[] values)> series, float duration)
        {
            this.series = series;
            this.duration = duration;
            AddToClassList("le-chart");
            AddToClassList("le-chart--compare");

            legend = Ui.Div(this, "le-chart__legend le-chart__legend--wrap");
            for (int i = 0; i < series.Count; i++)
            {
                var item = Ui.Div(legend, "le-chart__legend-item");
                var key = Ui.Div(item, "le-chart__key");
                key.style.backgroundColor = ColorOf(i);
                Ui.Text(item, series[i].label, "le-chart__legend-label");
            }

            plot = Ui.Div(this, "le-chart__plot");
            plot.style.left = AxisWidth;
            plot.style.bottom = AxisHeight;
            plot.generateVisualContent += Draw;

            float peak = 0f;
            foreach (var (_, values) in series)
                foreach (float value in values) peak = Mathf.Max(peak, value);
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
            foreach (var (label, values) in series)
            {
                if (values.Length < 2) continue;
                float position = fraction * (values.Length - 1);
                int index = Mathf.FloorToInt(position);
                int next = Mathf.Min(index + 1, values.Length - 1);
                parts.Add($"{label} {Mathf.Lerp(values[index], values[next], position - index):0.0}%");
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

            painter.lineJoin = LineJoin.Round;
            painter.lineCap = LineCap.Round;
            painter.lineWidth = 2f;

            // Drawn last-first, so the leader's line ends up on top.
            for (int s = series.Count - 1; s >= 0; s--)
            {
                var values = series[s].values;
                if (values.Length < 2) continue;

                painter.strokeColor = ColorOf(s);
                painter.BeginPath();
                for (int i = 0; i < values.Length; i++)
                {
                    var point = new Vector2(rect.xMin + rect.width * i / (values.Length - 1),
                        rect.yMax - rect.height * Mathf.Clamp01(values[i] / maxPercent));
                    if (i == 0) painter.MoveTo(point);
                    else painter.LineTo(point);
                }
                painter.Stroke();
            }
        }
    }
}
