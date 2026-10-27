using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>A coarse coverage map at one moment of a run, encoded like <see cref="RunReport.coverageMap"/>.</summary>
    [Serializable]
    public sealed class ReplayKeyframe
    {
        public float time;
        public string map;
    }

    /// <summary>
    /// Everything needed to play a finished run back: the floor plan, the vacuum's path and state over time,
    /// and coverage keyframes. Saved as replay.json in the run's data folder.
    /// </summary>
    [Serializable]
    public sealed class RunReplay
    {
        public string levelJson;
        public float sampleInterval;
        public List<float> x = new List<float>();
        public List<float> y = new List<float>();
        public List<float> heading = new List<float>();
        public List<float> battery = new List<float>();
        public List<float> coverage = new List<float>();
        public List<ReplayKeyframe> keyframes = new List<ReplayKeyframe>();
        public int mapWidth;
        public int mapHeight;
        public Rect mapRect;
        public float robotRadius;

        public int SampleCount => x.Count;

        public float Duration => Mathf.Max(0f, (SampleCount - 1) * sampleInterval);

        public static string PathIn(string folder) => Path.Combine(folder, "replay.json");

        public static RunReplay Load(string folder)
        {
            try
            {
                string path = PathIn(folder);
                return File.Exists(path) ? JsonUtility.FromJson<RunReplay>(File.ReadAllText(path)) : null;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Replay: could not read '{folder}': {exception.Message}");
                return null;
            }
        }

        /// <summary>The last keyframe at or before <paramref name="time"/>, or null.</summary>
        public ReplayKeyframe KeyframeAt(float time)
        {
            ReplayKeyframe found = null;
            foreach (var keyframe in keyframes)
            {
                if (keyframe.time > time + 1e-4f) break;
                found = keyframe;
            }
            return found;
        }
    }

    /// <summary>
    /// The simulation pieces of one scene, found once, and the one way every run starts, so a single run, a
    /// restart and every batch trial begin from an identical state for a given seed.
    /// </summary>
    public sealed class SimWorld
    {
        public SimEngine Engine { get; }
        public LevelData Level { get; }
        public LevelRenderer Renderer { get; }
        public VacuumRobot Robot { get; }
        public Battery Battery { get; }
        public VacuumCleaningController Cleaning { get; }
        public List<MovingCat> Cats { get; }

        public SimWorld(SimEngine engine, LevelData level, LevelRenderer renderer)
        {
            Engine = engine;
            Level = level;
            Renderer = renderer;
            Robot = UnityEngine.Object.FindAnyObjectByType<VacuumRobot>();
            Battery = Robot != null ? Robot.GetComponent<Battery>() : null;
            Cleaning = UnityEngine.Object.FindAnyObjectByType<VacuumCleaningController>();
            Cats = new List<MovingCat>(UnityEngine.Object.FindObjectsByType<MovingCat>(FindObjectsSortMode.InstanceID));

            var order = new List<ISimStepper>();
            if (Robot != null) order.Add(Robot);
            order.AddRange(Cats);
            if (Cleaning != null) order.Add(Cleaning);
            engine.SetSteppers(order);
        }

        /// <summary>
        /// Starts a run from scratch: seeds all randomness, puts the vacuum and cats back, applies the
        /// configured vacuum and grid resolution, and restarts the clock.
        /// </summary>
        public void BeginRun(int seed, MovementPattern pattern, PictureKind picture)
        {
            SimRandom.Reseed(seed);

            // Toggling simulation drops any contacts left from the last run, so physics starts clean too.
            SetBodiesSimulated(false);

            if (Robot != null)
            {
                SimConfig.Vacuum.ApplyTo(Robot, Battery);
                Robot.Pattern = pattern;
                Robot.Picture = picture;
                Robot.ResetToSpawn();
            }

            foreach (var cat in Cats)
                if (cat != null) cat.ResetToSpawn(SimRandom.NextSeed());

            if (Cleaning != null)
            {
                if (!Mathf.Approximately(Cleaning.CellSizeMeters, SimConfig.CellSize)) Cleaning.SetCellSize(SimConfig.CellSize);
                else Cleaning.ResetCoverage();
            }

            SetBodiesSimulated(true);
            Physics2D.SyncTransforms();
            Engine.ResetClock();
        }

        void SetBodiesSimulated(bool simulated)
        {
            if (Robot != null && Robot.TryGetComponent(out Rigidbody2D robotBody)) robotBody.simulated = simulated;
            foreach (var cat in Cats)
                if (cat != null && cat.TryGetComponent(out Rigidbody2D catBody)) catBody.simulated = simulated;
        }

        public float Coverage => Cleaning != null && Cleaning.Grid != null ? Cleaning.CoveragePercent : 0f;

        public bool OffFloor => Robot != null && LevelGeometry.IsOffFloor(Level, Robot.LevelPosition);
    }

    /// <summary>
    /// Watches one run through the engine's step hook and turns it into results: the summary metrics, the
    /// coverage timeline, time-series telemetry at the configured resolution, per-cell cleaning events,
    /// replay keyframes, and a log of every setting changed mid-run. <see cref="Finish"/> saves it all.
    /// </summary>
    public sealed class RunRecorder
    {
        const int MapSize = 64;
        const int MaxReplaySamples = 4000;
        const int MaxKeyframes = 60;

        readonly SimWorld world;
        readonly RunReport report;
        readonly float telemetryInterval;
        readonly StringBuilder telemetry = new StringBuilder();
        readonly List<CellEvent> cellEvents = new List<CellEvent>();
        readonly RunReplay replay = new RunReplay();
        ExternalModelGrid hookedGrid;
        float nextTelemetry;
        float nextReplaySample;
        float nextCoverageSample;
        float keyframeInterval = 5f;
        float nextKeyframe;
        float batteryAtStart;
        bool detached;

        // Per-room tallies, indexed like level.Rooms.
        float[] roomBattery;
        float[] roomSeconds;
        float[] roomDistance;
        int[] roomVisits;
        int currentRoom = -1;
        float lastCharge;
        float lastDistance;
        float lastTime;

        struct CellEvent
        {
            public float time;
            public int row;
            public int col;
            public float dirtiness;
        }

        public RunRecorder(SimWorld world, int seed, string batchId = null, int trial = 0, bool headless = false)
        {
            this.world = world;
            telemetryInterval = SimConfig.TelemetryInterval;

            report = new RunReport
            {
                id = Guid.NewGuid().ToString("N"),
                levelName = world.Level != null ? world.Level.name : "Floor plan",
                levelPath = SimLauncher.SimulatedPath,
                pattern = world.Robot != null ? world.Robot.Pattern : SimConfig.Pattern,
                picture = SimConfig.Picture,
                seed = seed,
                batchId = batchId,
                trial = trial,
                headless = headless,
                cellSize = SimConfig.CellSize,
                telemetryInterval = telemetryInterval,
            };
            report.dataFolder = RunHistory.DataFolderFor(report.id);

            replay.sampleInterval = telemetryInterval;
            replay.robotRadius = world.Robot != null ? world.Robot.Radius : 0.17f;
            if (world.Level != null)
                replay.levelJson = LevelSnapshot.FromLevel(world.Level, report.levelName).ToJson(false);

            telemetry.AppendLine("time_s,x_m,y_m,heading_deg,speed_mps,battery_units,coverage_pct");
            batteryAtStart = world.Battery != null ? world.Battery.Capacity : 0f;

            int roomCount = world.Level != null ? world.Level.Rooms.Count : 0;
            roomBattery = new float[roomCount];
            roomSeconds = new float[roomCount];
            roomDistance = new float[roomCount];
            roomVisits = new int[roomCount];
            lastCharge = world.Battery != null ? world.Battery.CurrentCharge : 0f;
            lastDistance = world.Robot != null ? world.Robot.DistanceTravelled : 0f;

            world.Engine.Stepped += OnStepped;
            HookGrid();
            Sample(0f);
        }

        public RunReport Report => report;

        public float Elapsed => world.Engine.SimTime;

        /// <summary>Stops listening, without saving. Safe to call twice.</summary>
        public void Detach()
        {
            if (detached) return;
            detached = true;
            world.Engine.Stepped -= OnStepped;
            if (hookedGrid != null) hookedGrid.CellStateLogged -= OnCellLogged;
        }

        /// <summary>
        /// Notes a setting changed mid-run. Repeated changes to one setting within the same simulated second
        /// (a slider being dragged) collapse into one event that runs from the first value to the last.
        /// </summary>
        public void LogParameter(string parameter, string from, string to)
        {
            if (from == to) return;

            float now = Elapsed;
            var events = report.parameterEvents;
            var last = events.Count > 0 ? events[events.Count - 1] : null;
            if (last != null && last.parameter == parameter && now - last.time < 1f)
            {
                last.to = to;
                if (last.from == last.to) events.RemoveAt(events.Count - 1);
                return;
            }

            events.Add(new ParameterEvent { time = now, parameter = parameter, from = from, to = to });
        }

        public void MarkMixedPatterns() => report.mixedPatterns = true;

        void HookGrid()
        {
            var grid = world.Cleaning != null ? world.Cleaning.Grid : null;
            if (grid == hookedGrid) return;

            if (hookedGrid != null) hookedGrid.CellStateLogged -= OnCellLogged;
            hookedGrid = grid;
            if (hookedGrid != null) hookedGrid.CellStateLogged += OnCellLogged;
        }

        void OnCellLogged(int row, int col, float dirtiness, float time)
        {
            if (!string.IsNullOrEmpty(report.batchId)) return; // batches don't write per-cell files
            cellEvents.Add(new CellEvent { time = Elapsed, row = row, col = col, dirtiness = dirtiness });
        }

        void OnStepped(SimEngine engine) => Sample(engine.SimTime);

        void Sample(float time)
        {
            HookGrid();
            TrackRoom(time);
            float coverage = world.Coverage;

            if (report.timeTo50 < 0f && coverage >= 50f) report.timeTo50 = time;
            if (report.timeTo75 < 0f && coverage >= 75f) report.timeTo75 = time;
            if (report.timeTo90 < 0f && coverage >= 90f) report.timeTo90 = time;

            if (time + 1e-4f >= nextCoverageSample)
            {
                report.AddSample(coverage);
                nextCoverageSample += report.sampleInterval;
            }

            if (time + 1e-4f >= nextTelemetry)
            {
                WriteTelemetry(time, coverage);
                nextTelemetry += telemetryInterval;
            }

            if (time + 1e-4f >= nextKeyframe)
            {
                AddKeyframe(time);
                nextKeyframe += keyframeInterval;
            }
        }

        /// <summary>
        /// Charges the battery, time and distance since the last step to the room the vacuum is in. In a
        /// doorway or wall gap it is in no room, so the step counts toward the room it came from.
        /// </summary>
        void TrackRoom(float time)
        {
            var robot = world.Robot;
            if (robot == null || world.Level == null) return;

            float charge = world.Battery != null ? world.Battery.CurrentCharge : 0f;
            float distance = robot.DistanceTravelled;

            int room = world.Level.RoomIndexAt(robot.LevelPosition);
            if (room < 0) room = currentRoom;
            else if (room != currentRoom)
            {
                currentRoom = room;
                if (room < roomVisits.Length) roomVisits[room]++;
            }

            if (room >= 0 && room < roomBattery.Length)
            {
                roomBattery[room] += Mathf.Max(0f, lastCharge - charge);
                roomSeconds[room] += Mathf.Max(0f, time - lastTime);
                roomDistance[room] += Mathf.Max(0f, distance - lastDistance);
            }

            lastCharge = charge;
            lastDistance = distance;
            lastTime = time;
        }

        void WriteTelemetry(float time, float coverage)
        {
            var robot = world.Robot;
            Vector2 position = robot != null ? robot.LevelPosition : Vector2.zero;
            float heading = robot != null ? robot.Heading : 0f;
            float speed = robot != null ? robot.SpeedMetersPerSecond : 0f;
            float charge = world.Battery != null ? world.Battery.CurrentCharge : 0f;

            telemetry.Append(F(time)).Append(',').Append(F(position.x)).Append(',').Append(F(position.y)).Append(',')
                .Append(F(heading)).Append(',').Append(F(speed)).Append(',').Append(F(charge)).Append(',')
                .Append(F(coverage)).Append('\n');

            // The replay keeps a bounded copy: when full it halves, then only takes every other sample.
            if (time + 1e-4f < nextReplaySample) return;
            if (replay.SampleCount >= MaxReplaySamples)
            {
                ThinReplay();
                nextReplaySample = replay.SampleCount * replay.sampleInterval;
                if (time + 1e-4f < nextReplaySample) return;
            }
            nextReplaySample += replay.sampleInterval;

            replay.x.Add(position.x);
            replay.y.Add(position.y);
            replay.heading.Add(heading);
            replay.battery.Add(charge);
            replay.coverage.Add(coverage);
        }

        /// <summary>Halves the replay's resolution, so a long run keeps a bounded file.</summary>
        void ThinReplay()
        {
            static List<float> Halve(List<float> values)
            {
                var kept = new List<float>(values.Count / 2 + 1);
                for (int i = 0; i < values.Count; i += 2) kept.Add(values[i]);
                return kept;
            }

            replay.x = Halve(replay.x);
            replay.y = Halve(replay.y);
            replay.heading = Halve(replay.heading);
            replay.battery = Halve(replay.battery);
            replay.coverage = Halve(replay.coverage);
            replay.sampleInterval *= 2f;
        }

        void AddKeyframe(float time)
        {
            if (!TryEncodeMap(out string map, out _, out _, out _)) return;

            if (replay.keyframes.Count >= MaxKeyframes)
            {
                var kept = new List<ReplayKeyframe>();
                for (int i = 0; i < replay.keyframes.Count; i += 2) kept.Add(replay.keyframes[i]);
                replay.keyframes = kept;
                keyframeInterval *= 2f;
                nextKeyframe = time + keyframeInterval;
            }

            replay.keyframes.Add(new ReplayKeyframe { time = time, map = map });
        }

        bool TryEncodeMap(out string map, out int width, out int height, out Rect rect)
        {
            map = null;
            width = height = 0;
            rect = default;

            var grid = world.Cleaning != null ? world.Cleaning.Grid : null;
            if (grid == null) return false;

            var values = grid.Downsample(MapSize, out width, out height);
            int block = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(grid.Rows, grid.Cols) / (float)MapSize));
            float blockMetres = block * grid.CellSizeMeters;

            Vector2 origin = world.Renderer != null ? world.Renderer.WorldToLevel(grid.OriginWorld) : grid.OriginWorld;
            rect = new Rect(origin, new Vector2(width * blockMetres, height * blockMetres));

            var bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++)
                bytes[i] = values[i] < 0f ? (byte)255 : (byte)Mathf.RoundToInt(Mathf.Clamp01(values[i]) * 250f);
            map = Convert.ToBase64String(bytes);
            return true;
        }

        /// <summary>Fills in the results, writes the run's files and saves it to the history. Saving again later updates it.</summary>
        public RunReport Finish(string reason, bool save = true)
        {
            float time = Elapsed;
            var robot = world.Robot;

            report.endReason = reason;
            report.durationSeconds = time;
            report.coveragePercent = world.Coverage;
            report.vacuum = JsonUtility.FromJson<VacuumSettings>(JsonUtility.ToJson(SimConfig.Vacuum));
            report.picture = SimConfig.Picture;
            report.distanceMeters = robot != null ? robot.DistanceTravelled : 0f;
            report.batteryUsed = world.Battery != null ? Mathf.Max(0f, batteryAtStart - world.Battery.CurrentCharge) : 0f;
            report.batteryEmptied = world.Battery != null && !world.Battery.CanOperate;
            report.failure = robot != null ? robot.FailureReason : null;
            report.floorArea = world.Level != null ? world.Level.TotalFloorArea() : 0f;

            var cleaning = world.Cleaning;
            if (cleaning != null && cleaning.Grid != null)
            {
                report.blockedArea = cleaning.Grid.NonCleanableAreaSquareMeters;
                report.revisitCount = cleaning.RevisitCount;

                report.roomNames.Clear();
                report.roomCoverage.Clear();
                report.roomBattery.Clear();
                report.roomSeconds.Clear();
                report.roomDistance.Clear();
                report.roomVisits.Clear();
                report.roomArea.Clear();
                var perRoom = cleaning.RoomCoveragePercent();
                for (int i = 0; i < perRoom.Length; i++)
                {
                    var room = world.Level.GetRoom(i);
                    if (room == null || !room.IsValid) continue;
                    report.roomNames.Add(string.IsNullOrEmpty(room.name) ? $"Room {i + 1}" : room.name);
                    report.roomCoverage.Add(perRoom[i]);
                    bool tracked = i < roomBattery.Length;
                    report.roomBattery.Add(tracked ? roomBattery[i] : 0f);
                    report.roomSeconds.Add(tracked ? roomSeconds[i] : 0f);
                    report.roomDistance.Add(tracked ? roomDistance[i] : 0f);
                    report.roomVisits.Add(tracked ? roomVisits[i] : 0);
                    report.roomArea.Add(room.Area);
                }
            }

            // The final point on the timeline and in the replay is the run's end state.
            report.AddSample(report.coveragePercent);
            if (TryEncodeMap(out string map, out int width, out int height, out Rect rect))
            {
                report.coverageMap = map;
                report.mapWidth = width;
                report.mapHeight = height;
                report.mapRect = rect;
                replay.mapWidth = width;
                replay.mapHeight = height;
                replay.mapRect = rect;
                replay.keyframes.Add(new ReplayKeyframe { time = time, map = map });
            }

            if (save)
            {
                WriteFiles();
                RunHistory.Add(report);
            }
            return report;
        }

        /// <summary>
        /// Writes the run's data folder: the summary, telemetry and per-cell CSVs for offline analysis, the
        /// parameter log, the configuration used, and the replay. A failure here never loses the summary.
        /// </summary>
        public void WriteFiles()
        {
            try
            {
                Directory.CreateDirectory(report.dataFolder);
                string folder = report.dataFolder;

                File.WriteAllText(Path.Combine(folder, "summary.json"), JsonUtility.ToJson(report, true));
                File.WriteAllText(Path.Combine(folder, "configuration.json"), SimConfig.ToJson());
                File.WriteAllText(Path.Combine(folder, "telemetry.csv"), telemetry.ToString());
                File.WriteAllText(RunReplay.PathIn(folder), JsonUtility.ToJson(replay));

                var parameters = new StringBuilder("time_s,parameter,from,to\n");
                foreach (var change in report.parameterEvents)
                    parameters.Append(F(change.time)).Append(',').Append(Csv(change.parameter)).Append(',')
                        .Append(Csv(change.from)).Append(',').Append(Csv(change.to)).Append('\n');
                File.WriteAllText(Path.Combine(folder, "parameters.csv"), parameters.ToString());

                if (report.HasRoomEnergy)
                {
                    var rooms = new StringBuilder("room,area_m2,coverage_pct,battery_units,seconds,distance_m,entries\n");
                    for (int i = 0; i < report.roomNames.Count; i++)
                        rooms.Append(Csv(report.roomNames[i])).Append(',').Append(F(report.roomArea[i])).Append(',')
                            .Append(F(report.roomCoverage[i])).Append(',').Append(F(report.roomBattery[i])).Append(',')
                            .Append(F(report.roomSeconds[i])).Append(',').Append(F(report.roomDistance[i])).Append(',')
                            .Append(report.roomVisits[i]).Append('\n');
                    File.WriteAllText(Path.Combine(folder, "rooms.csv"), rooms.ToString());
                }

                // Per-cell files run to megabytes at fine grids, so a batch's many trials keep only the
                // telemetry, replay and summary; a single run gets the full per-cell record too.
                if (string.IsNullOrEmpty(report.batchId))
                {
                    var events = new StringBuilder("time_s,row,col,dirtiness\n");
                    foreach (var e in cellEvents)
                        events.Append(F(e.time)).Append(',').Append(e.row).Append(',').Append(e.col).Append(',')
                            .Append(F(e.dirtiness)).Append('\n');
                    File.WriteAllText(Path.Combine(folder, "cell_events.csv"), events.ToString());

                    WriteFinalCells(Path.Combine(folder, "cells_final.csv"));
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Run recorder: could not write '{report.dataFolder}': {exception.Message}");
            }
        }

        void WriteFinalCells(string path)
        {
            var grid = world.Cleaning != null ? world.Cleaning.Grid : null;
            if (grid == null) return;

            using var writer = new StreamWriter(path, false, Encoding.UTF8);
            writer.WriteLine($"# cell_size_m={F(grid.CellSizeMeters)}");
            writer.WriteLine("row,col,x_m,y_m,dirtiness,counts_toward_coverage");
            for (int row = 0; row < grid.Rows; row++)
            {
                for (int col = 0; col < grid.Cols; col++)
                {
                    var cell = grid.GetCell(row, col);
                    if (cell.isOffFloor) continue;

                    Vector2 world2 = grid.GridIndexToWorldCenter(row, col);
                    Vector2 point = world.Renderer != null ? world.Renderer.WorldToLevel(world2) : world2;
                    writer.Write(row); writer.Write(','); writer.Write(col); writer.Write(',');
                    writer.Write(F(point.x)); writer.Write(','); writer.Write(F(point.y)); writer.Write(',');
                    writer.Write(F(cell.Dirtiness)); writer.Write(',');
                    writer.WriteLine(cell.CountsTowardCoverage ? "1" : "0");
                }
            }
        }

        static string F(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        static string Csv(string value)
        {
            value ??= string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\n' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
