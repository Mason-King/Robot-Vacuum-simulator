using System;
using System.Collections.Generic;
using System.IO;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.Headless
{
    /// <summary>
    /// Carries out one shard of a headless job: each run goes through a <see cref="SimulationSession"/> started
    /// with headless = true, exactly the way the GUI starts a run, so a headless run and a rendered run of the
    /// same configuration and seed are built and recorded by the same code (2.17). A <see cref="RunRecorder"/>
    /// adds the per-run metrics and telemetry (2.4, 2.19).
    ///
    /// Output folder layout:
    /// <code>
    ///   results.json            ShardResults: every finished run's outcome (rewritten as it goes)
    ///   heartbeat.json          progress, every ~2 real seconds
    ///   runs/&lt;guid&gt;/           one folder per run: config.json, result.json, summary.json, telemetry.csv,
    ///                           replay.json, external_model.json, heatmap PNGs (same as a GUI run)
    /// </code>
    /// One run failing (bad floor plan, algorithm crash, wall-clock limit) is recorded and the shard moves on (4.5, 2.11).
    /// </summary>
    public sealed class HeadlessJobRunner : MonoBehaviour
    {
        const float HeartbeatInterval = 2f;

        HeadlessArgs args;
        HeadlessJob job;
        string outputFolder;
        FloorPalette palette;
        ShardResults results;
        readonly List<RunSpec> queue = new List<RunSpec>();
        int nextInQueue;
        int sinceReport;
        bool done;
        float nextHeartbeat;

        // The run in progress
        RunSpec currentRun;
        RunConfiguration currentConfig;
        RunOutcome currentOutcome;
        LevelData currentLevel;
        SimulationSession session;
        RunRecorder recorder;
        bool sessionEnded;
        float runStartedRealtime;

        public void Begin(HeadlessArgs launchArgs)
        {
            args = launchArgs;
            Application.runInBackground = true;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            try
            {
                BeginJob();
            }
            catch (Exception ex)
            {
                Crash(ex);
            }
        }

        void BeginJob()
        {
            job = HeadlessJob.Load(args.configPath, out var errors);
            outputFolder = ResolveOutputFolder(job?.campaignId);
            Directory.CreateDirectory(outputFolder);

            if (job == null)
            {
                Reject(errors);
                return;
            }

            int shardCount = args.shardCount > 0 ? args.shardCount : job.execution.shardCount;
            int shardIndex = Mathf.Max(0, args.shardIndex);
            if (shardIndex >= shardCount)
            {
                Reject(new List<string> { $"Shard index {shardIndex} is outside the {shardCount} shard(s) of this job." });
                return;
            }

            var mine = job.RunsForShard(shardIndex, shardCount);
            Log($"Job \"{job.campaignName}\" ({job.campaignId}) is valid: {job.runs.Count} run(s) in total, {mine.Count} in shard {shardIndex + 1} of {shardCount}.");

            if (args.validateOnly)
            {
                Quit(HeadlessBootstrap.ExitOk);
                return;
            }

            // Every run folder goes under this node's output folder, ready to upload as one tree.
            RunArtifacts.RootOverride = Path.Combine(outputFolder, "runs");
            RunArtifacts.SaveModelFile = job.execution.saveExternalModel;
            RunArtifacts.SaveImages = job.execution.saveHeatmapImages;

            // SimConfig loads lazily and, on first load, resets FloorTuning from the GUI's saved settings. Load it
            // now so that can't happen in the middle of a run and undo the job's floor efforts. Nothing here
            // writes SimConfig, so a local headless run never changes the GUI's saved configuration.
            _ = SimConfig.CellSize;

            palette = job.BuildPalette();

            results = new ShardResults
            {
                campaignId = job.campaignId,
                campaignName = job.campaignName,
                shardIndex = shardIndex,
                shardCount = shardCount,
                startedUtc = DateTime.UtcNow.ToString("o"),
                host = HostInfo.Current(),
                totalRuns = mine.Count,
            };

            // Resume: a retried node (Spot interruption, crash) keeps the runs it already finished.
            var alreadyFinished = new HashSet<int>();
            var previous = args.resume ? ShardResults.TryRead(outputFolder) : null;
            if (previous != null && previous.campaignId == job.campaignId && previous.shardIndex == shardIndex &&
                previous.shardCount == shardCount && previous.runs != null)
            {
                foreach (var outcome in previous.runs)
                    if (outcome != null && alreadyFinished.Add(outcome.runIndex)) results.runs.Add(outcome);
                results.startedUtc = previous.startedUtc ?? results.startedUtc;
                Log($"Resuming: {results.runs.Count} run(s) already finished on an earlier attempt.");
            }
            RecountResults();

            foreach (var run in mine)
                if (!alreadyFinished.Contains(run.runIndex)) queue.Add(run);

            results.WriteTo(outputFolder);
            StartNextRun();
        }

        // ---------------------------------------------------------------- frame loop

        void Update()
        {
            if (done) return;

            try
            {
                if (Time.realtimeSinceStartup >= nextHeartbeat)
                {
                    WriteHeartbeat("running");
                    nextHeartbeat = Time.realtimeSinceStartup + HeartbeatInterval;
                }

                if (session == null) return;

                if (sessionEnded)
                {
                    CompleteRun(null);
                    return;
                }

                float limit = job.execution.maxRealSecondsPerRun;
                if (limit > 0f && Time.realtimeSinceStartup - runStartedRealtime > limit)
                    CompleteRun($"The run took longer than the {limit:0} s wall-clock limit (execution.maxRealSecondsPerRun) and was stopped.");
            }
            catch (Exception ex)
            {
                Crash(ex);
            }
        }

        // ---------------------------------------------------------------- runs

        void StartNextRun()
        {
            while (nextInQueue < queue.Count)
            {
                if (TryStartRun(queue[nextInQueue++])) return;
            }

            FinishJob();
        }

        /// <summary>Starts one run. False when it couldn't start; its failure has been recorded.</summary>
        bool TryStartRun(RunSpec run)
        {
            currentRun = run;
            currentConfig = job.configurations[run.configuration];
            var plan = job.floorPlans[run.floorPlan];

            currentOutcome = new RunOutcome
            {
                runIndex = run.runIndex,
                floorPlanIndex = run.floorPlan,
                floorPlan = plan != null ? plan.name : null,
                configurationIndex = run.configuration,
                configuration = currentConfig.name,
                algorithm = run.algorithm,
                seed = run.seed,
                endReason = "NotStarted",
                batteryCapacity = currentConfig.battery.capacity,
                targetMaxClean = currentConfig.simulation.targetMaxClean,
                cellSizeMeters = currentConfig.simulation.cellSizeMeters,
            };

            try
            {
                run.TryPattern(out var pattern);
                currentConfig.simulation.TryHeatmapMode(out var heatmapMode);
                currentOutcome.algorithm = pattern.ToString();

                // A private copy of the floor plan, coverings resolved by name against the job's palette.
                var snapshot = LevelSnapshot.Parse(plan.ToJson(false));
                FloorPlanFiles.ResolveFloorNames(snapshot, palette);
                currentLevel = SimLauncher.BuildLevel(snapshot, palette);
                currentOutcome.floorAreaSquareMeters = currentLevel.TotalFloorArea();

                if (LevelGeometry.HasFurnitureWallCollision(currentLevel))
                    return FailToStart("Furniture overlaps a wall in this floor plan, so it can't be simulated. Move the furniture clear of the walls.");

                // Same pre-run warnings the GUI shows (1.2.4, 1.2.5). As in the GUI, the run still goes ahead.
                currentOutcome.warnings.AddRange(RunChecks.Problems(currentLevel));

                ApplyFloorEfforts(currentConfig);

                var settings = new RunSettings
                {
                    pattern = pattern,
                    randomSeed = false,
                    seed = run.seed,
                    targetMaxClean = currentConfig.simulation.targetMaxClean,
                    heatmapMode = heatmapMode,
                    driveSpeed = currentConfig.vacuum.driveSpeed,
                    reverseSpeed = currentConfig.vacuum.reverseSpeed,
                    turnSpeed = currentConfig.vacuum.turnSpeed,
                    batteryCapacity = currentConfig.battery.capacity,
                    cellSizeMeters = currentConfig.simulation.cellSizeMeters,
                    vacuumPower = currentConfig.vacuum.vacuumPower,
                    maxSimulatedSeconds = currentConfig.simulation.maxSimulatedSeconds,
                };

                sessionEnded = false;
                if (!SimulationSession.TryStart(currentLevel, settings, headless: true, OnRunBegan, out session, out string error))
                {
                    session = null;
                    return FailToStart(error);
                }

                session.Finished += OnSessionFinished;
                runStartedRealtime = Time.realtimeSinceStartup;
                Log($"Run {run.runIndex}: {currentOutcome.floorPlan} / {currentConfig.name} / {pattern} / seed {run.seed} started.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                CloseSession();
                return FailToStart($"Could not start the run: {ex.Message}");
            }
        }

        // Called by the session once the robot exists and before it first moves.
        void OnRunBegan(SimulationSession started)
        {
            var battery = started.Runner.Battery;
            if (battery != null)
            {
                var b = currentConfig.battery;
                battery.ConfigureDrain(b.distanceEnergyPerMeter, b.turnEnergyPerDegree, b.operatingEnergyPerSecond, b.stalledEnergyPerSecond);
            }

            recorder = new RunRecorder(new SimWorld(started), job.campaignId, currentRun.runIndex, headless: true,
                telemetryIntervalOverride: currentConfig.simulation.telemetryInterval,
                vacuumOverride: currentConfig.ToVacuumSettings());
        }

        // The session ended the run on its own: battery empty, time cap, or the algorithm failed. The run is
        // frozen in place and its config.json/result.json/model are saved; the rest happens in Update.
        void OnSessionFinished(SimulationSession ended)
        {
            if (ended != session) return;
            recorder?.Freeze();
            sessionEnded = true;
        }

        void CompleteRun(string forcedError)
        {
            var outcome = currentOutcome;

            // The recorder reads the live grid, so it finishes before the session tears the run down.
            RunReport report = null;
            if (recorder != null)
            {
                recorder.Freeze();
                string reason = forcedError != null ? "Wall-clock limit"
                    : SimWorld.Describe(session.Result != null ? session.Result.endReason : RunEndReason.None);
                report = recorder.Finish(reason, save: false);
                recorder.WriteFiles();
                recorder.Detach();
                recorder = null;
            }

            string runFolder = session.RunFolder;
            session.Finished -= OnSessionFinished;
            session.Close(); // a run still going is ended and saved as stopped; a finished one is just torn down
            var result = session.Result;
            session = null;

            outcome.succeeded = forcedError == null && result != null && result.succeeded;
            outcome.error = forcedError ?? result?.errorMessage ?? report?.failure;
            outcome.endReason = forcedError != null ? RunEndReason.Error.ToString()
                : result != null ? result.endReason.ToString() : RunEndReason.None.ToString();

            if (result != null)
            {
                outcome.coveragePercent = result.coveragePercent;
                outcome.fullyCleanedPercent = result.fullyCleanedPercent;
                outcome.simulatedSeconds = result.simulatedSeconds;
                outcome.realSeconds = result.realSeconds;
                outcome.blockedAreaSquareMeters = result.blockedAreaSquareMeters;
                outcome.targetMaxClean = result.targetMaxClean;
            }

            if (report != null)
            {
                outcome.batteryUsed = report.batteryUsed;
                outcome.batteryEmptied = report.batteryEmptied;
                outcome.distanceMeters = report.distanceMeters;
                outcome.revisitCount = report.revisitCount;
                outcome.timeTo50 = report.timeTo50;
                outcome.timeTo75 = report.timeTo75;
                outcome.timeTo90 = report.timeTo90;
                outcome.cellSizeMeters = report.cellSize;

                for (int i = 0; i < report.roomNames.Count; i++)
                {
                    outcome.rooms.Add(new RoomOutcome
                    {
                        name = report.roomNames[i],
                        coveragePercent = At(report.roomCoverage, i),
                        areaSquareMeters = At(report.roomArea, i),
                        seconds = At(report.roomSeconds, i),
                        distanceMeters = At(report.roomDistance, i),
                        batteryUsed = At(report.roomBattery, i),
                        entries = i < report.roomVisits.Count ? report.roomVisits[i] : 0,
                    });
                }
            }

            outcome.runFolder = Relative(runFolder);
            Log($"Run {outcome.runIndex}: {outcome.endReason}, coverage {outcome.coveragePercent:0.00}%, " +
                $"{outcome.simulatedSeconds:0} s simulated in {outcome.realSeconds:0.0} s." +
                (outcome.succeeded ? string.Empty : $" FAILED: {outcome.error}"));

            ReleaseLevel();
            Record(outcome);
            StartNextRun();
        }

        bool FailToStart(string message)
        {
            currentOutcome.succeeded = false;
            currentOutcome.error = message;
            currentOutcome.endReason = "NotStarted";
            Debug.LogWarning($"[headless] Run {currentOutcome.runIndex} could not start: {message}");

            ReleaseLevel();
            Record(currentOutcome);
            return false;
        }

        void Record(RunOutcome outcome)
        {
            results.runs.Add(outcome);
            RecountResults();

            if (++sinceReport >= job.execution.reportEveryNRuns)
            {
                sinceReport = 0;
                results.WriteTo(outputFolder);
            }
        }

        void RecountResults()
        {
            results.completedRuns = results.runs.Count;
            results.failedRuns = 0;
            foreach (var r in results.runs) if (!r.succeeded) results.failedRuns++;
        }

        void ApplyFloorEfforts(RunConfiguration config)
        {
            FloorTuning.ClearAll();
            foreach (var entry in config.floorEfforts)
            {
                // FloorTuning is keyed by the covering's exact label; the job matches names without regard to case.
                for (int i = 0; i < palette.Count; i++)
                    if (string.Equals(palette.LabelOf(i), entry.floor, StringComparison.OrdinalIgnoreCase))
                        FloorTuning.SetCleaningEffort(palette.LabelOf(i), entry.effort);
            }
        }

        // ---------------------------------------------------------------- end of job

        void FinishJob()
        {
            if (done) return;
            done = true;

            results.status = "completed";
            results.finishedUtc = DateTime.UtcNow.ToString("o");
            results.WriteTo(outputFolder);
            WriteHeartbeat("completed");
            CleanUp();

            Log($"Shard finished: {results.completedRuns} run(s), {results.failedRuns} failed. Results: {Path.Combine(outputFolder, ShardResults.FileName)}");
            Quit(results.failedRuns > 0 ? HeadlessBootstrap.ExitSomeRunsFailed : HeadlessBootstrap.ExitOk);
        }

        void Reject(List<string> errors)
        {
            done = true;
            foreach (string error in errors) Debug.LogError($"[headless] Invalid job: {error}");

            var invalid = new ShardResults
            {
                status = "invalid",
                errors = errors,
                startedUtc = DateTime.UtcNow.ToString("o"),
                finishedUtc = DateTime.UtcNow.ToString("o"),
                host = HostInfo.Current(),
            };
            if (job != null)
            {
                invalid.campaignId = job.campaignId;
                invalid.campaignName = job.campaignName;
            }

            try { invalid.WriteTo(outputFolder); }
            catch (Exception ex) { Debug.LogWarning($"[headless] Could not write results: {ex.Message}"); }

            Quit(HeadlessBootstrap.ExitInvalid);
        }

        void Crash(Exception ex)
        {
            Debug.LogException(ex);
            if (done) return;
            done = true;

            try
            {
                if (results != null && outputFolder != null)
                {
                    results.status = "failed";
                    results.errors.Add($"The job crashed: {ex.Message}");
                    results.finishedUtc = DateTime.UtcNow.ToString("o");
                    results.WriteTo(outputFolder);
                    WriteHeartbeat("failed");
                }
            }
            catch (Exception inner)
            {
                Debug.LogException(inner);
            }

            CleanUp();
            Quit(HeadlessBootstrap.ExitCrashed);
        }

        void CleanUp()
        {
            CloseSession();
            ReleaseLevel();
            FloorTuning.ClearAll();
            HeadlessJob.DestroyPalette(palette);
            palette = null;
        }

        void CloseSession()
        {
            recorder?.Detach();
            recorder = null;
            if (session == null) return;
            session.Finished -= OnSessionFinished;
            session.Close();
            session = null;
        }

        void ReleaseLevel()
        {
            if (currentLevel != null) Destroy(currentLevel);
            currentLevel = null;
        }

        void OnDestroy() => CloseSession();

        // ---------------------------------------------------------------- helpers

        void WriteHeartbeat(string status)
        {
            if (outputFolder == null || results == null) return;
            new Heartbeat
            {
                status = status,
                currentRunIndex = session != null && currentRun != null ? currentRun.runIndex : -1,
                completedRuns = results.completedRuns,
                totalRuns = results.totalRuns,
                currentSimulatedSeconds = session != null ? session.SimulatedSeconds : 0f,
                currentCoveragePercent = session?.Runner != null ? session.Runner.CoveragePercent : 0f,
            }.WriteTo(outputFolder);
        }

        string ResolveOutputFolder(string campaignId)
        {
            if (!string.IsNullOrWhiteSpace(args.outputPath)) return Path.GetFullPath(args.outputPath);
            return Path.Combine(Application.persistentDataPath, "HeadlessJobs", string.IsNullOrEmpty(campaignId) ? "invalid" : campaignId);
        }

        string Relative(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try { return Path.GetRelativePath(outputFolder, folder).Replace('\\', '/'); }
            catch (Exception) { return folder; }
        }

        static float At(List<float> values, int index) => index < values.Count ? values[index] : 0f;

        static void Log(string message) => Debug.Log($"[headless] {message}");

        static void Quit(int code)
        {
            Debug.Log($"[headless] Exiting with code {code}.");
            Application.Quit(code);
        }
    }
}
