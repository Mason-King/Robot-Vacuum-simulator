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
    /// The start screen's Analysis page. On the left, every recorded run, with batch experiments grouped into
    /// one entry. On the right, the selection's results. A single run shows its metrics, a replay you can
    /// scrub through, coverage over time, per-room coverage and the log of settings changed mid-run. A batch
    /// shows the same results interface aggregated over its trials: each algorithm's mean, variance and 95%
    /// confidence interval on identical seeds, an averaged heatmap and an averaged coverage curve.
    /// </summary>
    public sealed class AnalysisView : VisualElement
    {
        readonly OverlayLayer overlay;
        readonly FloorPalette palette;
        readonly VisualElement body;
        readonly Label subtitle;
        readonly VisualElement clearButton;
        List<RunReport> runs = new List<RunReport>();
        RunReport selectedRun;
        string selectedBatch;
        MovementPattern batchPattern;
        bool compareAllLevels;

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
                () => FloorPlanFiles.Reveal(Path.GetDirectoryName(RunHistory.DataFolderFor("x"))), "le-btn--ghost");
            Ui.Tooltip(folder, "Open the folder holding every run's telemetry CSVs, replays and summaries");
            clearButton = Ui.Button(header, "Clear history", IconKind.Trash, ConfirmClear, "le-btn--ghost");
            Ui.Button(header, "Start simulation", IconKind.Play, () => SimulateRequested?.Invoke(), "le-btn--primary");

            body = Ui.Div(this, "le-analysis");
            Reload(focusedId);
        }

        void Reload(string focusId)
        {
            runs = RunHistory.LoadAll();
            selectedRun = null;
            selectedBatch = null;

            if (!string.IsNullOrEmpty(focusId) && runs.Exists(run => run.batchId == focusId)) SelectBatch(focusId);
            else selectedRun = runs.Find(run => run.id == focusId);

            if (selectedRun == null && selectedBatch == null && runs.Count > 0)
            {
                if (runs[0].IsBatch) SelectBatch(runs[0].batchId);
                else selectedRun = runs[0];
            }

            int batches = 0;
            var seen = new HashSet<string>();
            foreach (var run in runs)
                if (run.IsBatch && seen.Add(run.batchId)) batches++;
            subtitle.text = $"{(runs.Count == 1 ? "1 recorded run" : $"{runs.Count} recorded runs")}" +
                            (batches > 0 ? $" · {batches} batch{(batches == 1 ? "" : "es")}" : string.Empty);
            Ui.SetVisible(clearButton, runs.Count > 0);
            Rebuild();
        }

        void SelectBatch(string batchId)
        {
            selectedBatch = batchId;
            selectedRun = null;
            var members = BatchRuns(batchId);
            var summaries = BatchStats.Summarise(members);
            if (summaries.Count > 0 && !summaries.Exists(s => s.pattern == batchPattern)) batchPattern = summaries[0].pattern;
        }

        List<RunReport> BatchRuns(string batchId) => runs.FindAll(run => run.batchId == batchId);

        void Rebuild()
        {
            body.Clear();

            if (runs.Count == 0)
            {
                var empty = Ui.Div(body, "le-empty le-analysis__empty");
                var icon = new IconElement(IconKind.Chart);
                icon.AddToClassList("le-empty__icon");
                empty.Add(icon);
                Ui.Text(empty, "No runs yet", "le-empty__title");
                Ui.Text(empty, "Run a floor plan in the simulator and press Finish, or start a batch experiment from a floor plan's card. Results land here.",
                    "le-empty__text");
                Ui.Button(Ui.Div(empty, "le-empty__actions"), "Start simulation", IconKind.Play,
                    () => SimulateRequested?.Invoke(), "le-btn--primary");
                return;
            }

            var list = new ScrollView(ScrollViewMode.Vertical);
            list.AddToClassList("le-analysis__list");
            list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            body.Add(list);

            var listedBatches = new HashSet<string>();
            foreach (var run in runs)
            {
                if (!run.IsBatch) list.Add(BuildRunRow(run));
                else if (listedBatches.Add(run.batchId)) list.Add(BuildBatchRow(run.batchId));
            }

            var detail = new ScrollView(ScrollViewMode.Vertical);
            detail.AddToClassList("le-analysis__detail");
            detail.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            body.Add(detail);

            var column = Ui.Div(detail, "le-page__column le-page__column--wide");
            if (selectedBatch != null)
            {
                BuildBatchDetail(column, BatchRuns(selectedBatch));
            }
            else if (selectedRun != null)
            {
                BuildRunDetail(column, selectedRun);
                BuildComparison(Ui.Div(column, "le-panel"));
            }
        }

        // ---------------------------------------------------------------- list

        VisualElement BuildRunRow(RunReport run)
        {
            var row = Ui.Div(null, "le-run-row");
            if (run == selectedRun) row.AddToClassList("le-run-row--selected");

            var text = Ui.Div(row, "le-run-row__text");
            Ui.Text(text, run.levelName, "le-run-row__title");
            Ui.Text(text, $"{run.PatternLabel} · {VacuumSettings.FormatDuration(run.durationSeconds)} · {FormatWhen(run.FinishedAt)}",
                "le-run-row__meta");
            Ui.Text(row, $"{run.coveragePercent:0.0}%", $"le-run-row__value le-tone-text--{CoverageTone(run.coveragePercent)}");

            row.AddManipulator(new Clickable(() =>
            {
                selectedRun = run;
                selectedBatch = null;
                Rebuild();
            }));
            return row;
        }

        VisualElement BuildBatchRow(string batchId)
        {
            var members = BatchRuns(batchId);
            var summaries = BatchStats.Summarise(members);
            var first = members[0];

            var row = Ui.Div(null, "le-run-row le-run-row--batch");
            if (batchId == selectedBatch) row.AddToClassList("le-run-row--selected");

            var icon = new IconElement(IconKind.Chart);
            icon.AddToClassList("le-run-row__icon");
            row.Add(icon);

            var text = Ui.Div(row, "le-run-row__text");
            Ui.Text(text, $"Batch · {first.levelName}", "le-run-row__title");
            Ui.Text(text, $"{summaries.Count} algorithm(s) · {members.Count} runs · {FormatWhen(first.FinishedAt)}", "le-run-row__meta");
            if (summaries.Count > 0)
                Ui.Text(row, $"{summaries[0].coverage.mean:0.0}%",
                    $"le-run-row__value le-tone-text--{CoverageTone((float)summaries[0].coverage.mean)}");

            row.AddManipulator(new Clickable(() =>
            {
                SelectBatch(batchId);
                Rebuild();
            }));
            return row;
        }

        // ---------------------------------------------------------------- batch

        void BuildBatchDetail(VisualElement column, List<RunReport> members)
        {
            if (members.Count == 0) return;
            var first = members[0];
            var summaries = BatchStats.Summarise(members);

            int minSeed = int.MaxValue, maxSeed = int.MinValue;
            foreach (var run in members)
            {
                minSeed = Math.Min(minSeed, run.seed);
                maxSeed = Math.Max(maxSeed, run.seed);
            }

            var panel = Ui.Div(column, "le-panel");
            var header = Ui.Div(panel, "le-panel__header");
            var titles = Ui.Div(header, "le-panel__titles");
            Ui.Text(titles, $"Batch experiment · {first.levelName}", "le-panel__title");
            Ui.Text(titles, $"{first.FinishedAt:d MMM yyyy, HH:mm} · {members.Count} runs · seeds {minSeed}–{maxSeed}" +
                            (first.headless ? " · headless" : string.Empty), "le-panel__text");
            if (!string.IsNullOrEmpty(first.levelPath) && File.Exists(first.levelPath))
                Ui.IconButton(header, IconKind.Pen, "Open in level editor", () => EditRequested?.Invoke(first.levelPath),
                    "le-btn--ghost le-btn--small");
            Ui.IconButton(header, IconKind.Trash, "Delete this batch", () => ConfirmDelete(selectedBatch, $"the batch on {first.levelName}"),
                "le-btn--ghost le-btn--small");

            Ui.Text(panel, "ALGORITHMS ON IDENTICAL SEEDS · MEAN ± 95% CONFIDENCE INTERVAL", "le-section__title");
            BuildStatsTable(panel, summaries);

            // Coverage bars with the confidence interval as a whisker, so overlaps read at a glance.
            Ui.Text(panel, "COVERAGE", "le-section__title le-analysis__chart-title");
            foreach (var summary in summaries)
            {
                var row = Ui.Div(panel, "le-compare-row");
                Ui.Text(row, summary.Label, "le-compare-row__label");
                var track = Ui.Div(row, "le-compare-row__track le-compare-row__track--ci");
                var fill = Ui.Div(track, "le-compare-row__fill");
                fill.style.width = Length.Percent(Mathf.Clamp((float)summary.coverage.mean, 0f, 100f));
                var whisker = Ui.Div(track, "le-compare-row__ci");
                float low = Mathf.Clamp((float)(summary.coverage.mean - summary.coverage.ci95), 0f, 100f);
                float high = Mathf.Clamp((float)(summary.coverage.mean + summary.coverage.ci95), 0f, 100f);
                whisker.style.left = Length.Percent(low);
                whisker.style.width = Length.Percent(Mathf.Max(0.4f, high - low));
                Ui.Text(row, $"{summary.coverage.mean:0.0}%", "le-compare-row__value");
            }

            // Averaged heatmap and coverage curve for one algorithm at a time.
            var mapPanel = Ui.Div(column, "le-panel");
            var mapHeader = Ui.Div(mapPanel, "le-panel__header");
            var mapTitles = Ui.Div(mapHeader, "le-panel__titles");
            Ui.Text(mapTitles, "Averaged across seeds", "le-panel__title");
            var chosen = summaries.Find(s => s.pattern == batchPattern) ?? summaries[0];
            Ui.Text(mapTitles, $"{chosen.Label}: mean coverage map and curve over {chosen.runs.Count} runs", "le-panel__text");

            var labels = summaries.ConvertAll(s => s.Label).ToArray();
            var picker = new Segmented(labels, summaries.IndexOf(chosen));
            picker.SelectionChanged += index =>
            {
                batchPattern = summaries[index].pattern;
                Rebuild();
            };
            mapPanel.Add(picker);

            var level = LoadLevelFor(chosen.runs);
            var averaged = BatchStats.AverageMap(chosen.runs, out var shape);
            if (level != null && averaged != null)
            {
                var map = new FloorMapView(level, palette);
                map.AddToClassList("le-analysis__map");
                map.SetCoverage(averaged, shape.mapWidth, shape.mapHeight, shape.mapRect);
                mapPanel.Add(map);
                Ui.Text(mapPanel, "Blue is floor cleaned in every run; red is floor left dirty.", "le-empty-hint");
            }
            else Ui.Text(mapPanel, "No coverage maps were saved for these runs.", "le-empty-hint");

            var curve = BatchStats.AverageCurve(chosen.runs, 120, out float duration);
            Ui.Text(mapPanel, "MEAN COVERAGE OVER TIME", "le-section__title le-analysis__chart-title");
            mapPanel.Add(new CoverageChart(curve, duration));

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
                row.AddManipulator(new Clickable(() =>
                {
                    selectedRun = captured;
                    selectedBatch = null;
                    Rebuild();
                }));
            }
        }

        void BuildStatsTable(VisualElement parent, List<AlgorithmSummary> summaries)
        {
            var table = Ui.Div(parent, "le-table");
            var head = Ui.Div(table, "le-table__row le-table__row--head");
            foreach (string heading in new[] { "Algorithm", "Runs", "Coverage", "SD", "Time", "Battery used", "Path length", "Revisits", "To 90%" })
                Ui.Text(head, heading, "le-table__cell");

            foreach (var s in summaries)
            {
                var row = Ui.Div(table, "le-table__row");
                Ui.Text(row, s.Label, "le-table__cell le-table__cell--strong");
                Ui.Text(row, s.runs.Count.ToString(), "le-table__cell");
                Ui.Text(row, $"{s.coverage.mean:0.0} ± {s.coverage.ci95:0.0}%", "le-table__cell");
                Ui.Text(row, $"{s.coverage.StandardDeviation:0.0}", "le-table__cell");
                Ui.Text(row, $"{VacuumSettings.FormatDuration((float)s.seconds.mean)} ± {s.seconds.ci95:0}s", "le-table__cell");
                Ui.Text(row, $"{s.batteryUsed.mean:0} ± {s.batteryUsed.ci95:0}", "le-table__cell");
                Ui.Text(row, $"{Ui.FormatLength((float)s.distance.mean)}", "le-table__cell");
                Ui.Text(row, $"{s.revisits.mean:0}", "le-table__cell");
                Ui.Text(row, s.timeTo90.count > 0 ? $"{VacuumSettings.FormatDuration((float)s.timeTo90.mean)} ({s.timeTo90.count}/{s.runs.Count})" : "never",
                    "le-table__cell");
            }

            Ui.Text(parent, "± is the 95% confidence interval of the mean (Student's t). SD is the standard deviation across seeds; variance is its square. Battery used is in work units.",
                "le-empty-hint");
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

            if (run.IsBatch)
                Ui.Button(header, "Batch", IconKind.Back, () =>
                {
                    batchPattern = run.pattern;
                    SelectBatch(run.batchId);
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
                    $"Final settings: drive {v.FormatSpeed(v.driveSpeed)} · reverse {v.FormatSpeed(v.reverseSpeed)} · turn {v.turnSpeed:0} °/s · " +
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

        // ---------------------------------------------------------------- comparison

        void BuildComparison(VisualElement panel)
        {
            var header = Ui.Div(panel, "le-panel__header");
            var titles = Ui.Div(header, "le-panel__titles");
            Ui.Text(titles, "Algorithm comparison", "le-panel__title");
            Ui.Text(titles, "Average coverage per movement algorithm over single runs. Runs that switched algorithm part-way are left out. " +
                            "For a like-for-like test on identical seeds, run a batch experiment.", "le-panel__text");

            string levelName = selectedRun != null ? selectedRun.levelName : null;
            if (levelName != null)
            {
                var scope = new Segmented(new[] { "This floor plan", "All floor plans" }, compareAllLevels ? 1 : 0);
                scope.SelectionChanged += index =>
                {
                    compareAllLevels = index == 1;
                    Rebuild();
                };
                header.Add(scope);
            }

            var singles = runs.FindAll(run => !run.IsBatch);
            var groups = ComparePatterns(singles, compareAllLevels ? null : selectedRun?.levelPath, compareAllLevels ? null : levelName);
            if (groups.Count == 0)
            {
                Ui.Text(panel, "No single-algorithm runs to compare yet.", "le-empty-hint");
                return;
            }

            foreach (var group in groups)
            {
                var row = Ui.Div(panel, "le-compare-row");
                Ui.Text(row, MovementAlgorithms.LabelOf(group.pattern), "le-compare-row__label");

                var track = Ui.Div(row, "le-compare-row__track");
                var fill = Ui.Div(track, "le-compare-row__fill");
                fill.style.width = Length.Percent(Mathf.Clamp(group.averageCoverage, 0f, 100f));

                Ui.Text(row, $"{group.averageCoverage:0.0}%", "le-compare-row__value");
                Ui.Text(row,
                    $"{(group.runs == 1 ? "1 run" : $"{group.runs} runs")} · {group.coveragePerMinute:0.0}%/min · {VacuumSettings.FormatDuration(group.averageSeconds)} avg",
                    "le-compare-row__meta");
            }
        }

        public readonly struct PatternSummary
        {
            public readonly MovementPattern pattern;
            public readonly int runs;
            public readonly float averageCoverage;
            public readonly float averageSeconds;
            public readonly float coveragePerMinute;

            public PatternSummary(MovementPattern pattern, int runs, float averageCoverage, float averageSeconds, float coveragePerMinute)
            {
                this.pattern = pattern;
                this.runs = runs;
                this.averageCoverage = averageCoverage;
                this.averageSeconds = averageSeconds;
                this.coveragePerMinute = coveragePerMinute;
            }
        }

        /// <summary>
        /// Averages single-algorithm runs per movement pattern, best average coverage first. Given a level,
        /// only its runs count: matched by file when the run has one, by name otherwise.
        /// </summary>
        public static List<PatternSummary> ComparePatterns(IEnumerable<RunReport> runs, string levelPath = null, string levelName = null)
        {
            var totals = new Dictionary<MovementPattern, (int count, float coverage, float seconds)>();
            foreach (var run in runs)
            {
                if (run.mixedPatterns) continue;
                if (levelName != null)
                {
                    bool samePlan = !string.IsNullOrEmpty(levelPath) && !string.IsNullOrEmpty(run.levelPath)
                        ? run.levelPath == levelPath
                        : run.levelName == levelName;
                    if (!samePlan) continue;
                }

                totals.TryGetValue(run.pattern, out var t);
                totals[run.pattern] = (t.count + 1, t.coverage + run.coveragePercent, t.seconds + run.durationSeconds);
            }

            var result = new List<PatternSummary>();
            foreach (var pair in totals)
            {
                var t = pair.Value;
                float perMinute = t.seconds > 0f ? t.coverage / (t.seconds / 60f) : 0f;
                result.Add(new PatternSummary(pair.Key, t.count, t.coverage / t.count, t.seconds / t.count, perMinute));
            }

            result.Sort((a, b) => b.averageCoverage.CompareTo(a.averageCoverage));
            return result;
        }

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

        void ConfirmClear()
        {
            overlay.ShowDialog("Clear all runs?", "Every recorded run and batch will be deleted, with their saved data. Floor plans and configuration are kept.", null,
                new DialogButton("Cancel", ButtonStyle.Ghost, null),
                new DialogButton("Clear history", ButtonStyle.Danger, () =>
                {
                    RunHistory.Clear();
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
            map.SetPath(path, index + 1, replay.heading[index], replay.robotRadius);

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
}
