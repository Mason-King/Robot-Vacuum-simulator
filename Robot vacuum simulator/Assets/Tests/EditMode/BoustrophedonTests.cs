using System;
using System.Collections.Generic;
using NUnit.Framework;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Algorithms;
using RobotVacuum.Sim.Control;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>The map analysis's distance transform, and Boustrophedon Sweep and Crawl on simple rooms (no physics: an ideal follower).</summary>
    public class BoustrophedonTests
    {
        const float Radius = 0.17f;

        InternalModelWriter writer;
        InternalModelView view;
        PathPlanner planner;
        BeliefMapAdapter map;

        [SetUp]
        public void SetUp()
        {
            writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, 0.05f));
            view = new InternalModelView(writer);
            planner = new PathPlanner(view, Radius + ControllerConstants.ClearanceMarginMeters);
            map = new BeliefMapAdapter(view, planner);
            sweptSlowly = 0;
        }

        void Wall(Vector2 a, Vector2 b)
        {
            bool okA = view.TryWorldToCell(a, out var ca);
            bool okB = view.TryWorldToCell(b, out var cb);
            Assert.IsTrue(okA && okB);
            foreach (var c in GridLine.Cells(ca.row, ca.col, cb.row, cb.col)) writer.SetTraversability(c.row, c.col, 0f);
        }

        void Polygon(params Vector2[] corners)
        {
            for (int i = 0; i < corners.Length; i++) Wall(corners[i], corners[(i + 1) % corners.Length]);
        }

        void Block(float minX, float minY, float maxX, float maxY)
        {
            for (float x = minX; x <= maxX; x += 0.025f)
                for (float y = minY; y <= maxY; y += 0.025f)
                    if (view.TryWorldToCell(new Vector2(x, y), out var c)) writer.SetTraversability(c.row, c.col, 0f);
        }

        const float Target = 2f;
        DriveRequests drive;

        AlgorithmContext Context(bool slam = false, System.Action<string> log = null)
        {
            drive = new DriveRequests();
            return new AlgorithmContext(new ProviderRandom(new RandomProvider(5u)), Radius, 2f * Radius, 0.05f, slam, 8f, Target, 1f, drive,
                new MapAnalysis(map), log);
        }

        // ---- The distance transform ----------------------------------------------------------------------

        [Test]
        public void DistanceTransform_MatchesBruteForce()
        {
            var random = new System.Random(3);
            Polygon(new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(1f, 0.6f), new Vector2(-1f, 0.6f));
            for (int i = 0; i < 25; i++)
                if (view.TryWorldToCell(new Vector2((float)random.NextDouble() * 1.8f - 0.9f, (float)random.NextDouble() * 1.4f - 0.9f), out var c))
                    writer.SetTraversability(c.row, c.col, 0f);

            var snapshot = new MapSnapshot();
            snapshot.Take(map);
            float halfDiagonal = 0.05f * 0.70710678f;
            var blocked = new List<CellIndex>();
            for (int r = snapshot.MinRow; r < snapshot.MinRow + snapshot.Rows; r++)
                for (int c = snapshot.MinCol; c < snapshot.MinCol + snapshot.Cols; c++)
                    if (view.IsBlocked(r, c)) blocked.Add(new CellIndex(r, c));

            for (int r = snapshot.MinRow; r < snapshot.MinRow + snapshot.Rows; r += 3)
                for (int c = snapshot.MinCol; c < snapshot.MinCol + snapshot.Cols; c += 3)
                {
                    float best = float.PositiveInfinity;
                    foreach (var b in blocked)
                        best = Mathf.Min(best, Mathf.Sqrt((b.row - r) * (b.row - r) + (b.col - c) * (b.col - c)) * 0.05f);
                    float expected = best <= 0f ? 0f : Mathf.Max(0f, best - halfDiagonal);
                    Assert.AreEqual(expected, snapshot.ObstacleDistance(r, c), 1e-4f, $"cell ({r}, {c})");
                }
        }

        // ---- Lane direction ----------------------------------------------------------------------------------

        void AngledRoom()
        {
            // A 3 x 2 m room rotated 30 degrees.
            float a = 30f * Mathf.Deg2Rad;
            Vector2 u = new Vector2(Mathf.Cos(a), Mathf.Sin(a)), v = new Vector2(-u.y, u.x);
            Polygon(-1.5f * u - v, 1.5f * u - v, 1.5f * u + v, -1.5f * u + v);
        }

        static void AssertAlong30(Vec2 direction)
        {
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            float error = Mathf.Abs(Mathf.DeltaAngle(angle, 30f));
            error = Mathf.Min(error, Mathf.Abs(Mathf.DeltaAngle(angle, 210f)));
            Assert.Less(error, 3f, $"lanes at {angle:F1} degrees; the room's long side is at 30");
        }

        [Test]
        public void Sweep_LanesFollowAnAngledRoom_JustOverlapping()
        {
            AngledRoom();
            var algorithm = new BoustrophedonSweep();
            algorithm.Initialize(Context());
            algorithm.NextGoal(map, new Pose2(Vec2.Zero, 0f), GoalStatus.None);
            AssertAlong30(algorithm.LaneDirection);
            Assert.AreEqual(0.306f, algorithm.LaneSpacing, 1e-3f, "lanes 10% overlapping");
        }

        [Test]
        public void Crawl_LanesFollowAnAngledRoom_CloseEnoughForOnePass()
        {
            AngledRoom();
            var algorithm = new BoustrophedonCrawl();
            algorithm.Initialize(Context());
            algorithm.NextGoal(map, new Pose2(Vec2.Zero, 0f), GoalStatus.None);
            AssertAlong30(algorithm.LaneDirection);

            // At the floor speed (0.2 m/s), the worst-covered point between lanes must still reach the target in one pass.
            float need = Target * DriveRequests.SweepSpeedFloor * DriveRequests.SweepSpeedSafety;
            float spacing = algorithm.LaneSpacing;
            Debug.Log($"Crawl spacing on normal floor: {spacing * 100f:F1} cm (needs exposure {need:F3} m, gets {CoverageMath.LaneExposure(spacing, Radius):F3} m)");
            Assert.GreaterOrEqual(CoverageMath.LaneExposure(spacing, Radius), need);
            Assert.Less(spacing, 0.26f, "closer than the widest spacing: at 0.2 m/s that would not reach a target of 2");
        }

        // ---- Coverage ---------------------------------------------------------------------------------------

        /// <summary>
        /// An ideal follower: moves along the planner's path, at the sweep speed the controller would use (power 1,
        /// resistance 1) or full speed, honours pass-through goals, and cleans under its footprint as the real
        /// controller does. Optionally "senses" (calls <paramref name="sense"/>) as it goes.
        /// </summary>
        sealed class Follower
        {
            public Vector2 position;
            public float driven, seconds;
            public bool finished;
            public int goals;
            public readonly List<Vector2> trail = new List<Vector2>();
            public readonly List<Vector2> targets = new List<Vector2>();
        }

        int sweptSlowly;

        Follower Run(ICleaningAlgorithm algorithm, Vector2 start, float maxSeconds, Action<Vector2> sense = null, Func<bool> stopWhen = null)
        {
            const float dt = 0.05f, full = 0.65f;
            var f = new Follower { position = start };
            var status = GoalStatus.None;
            var path = new List<Vec2>();
            int index = 0;
            Vector2 goal = start;
            bool pass = false;
            float speed = full;

            for (; f.seconds < maxSeconds; f.seconds += dt)
            {
                if (stopWhen != null && stopWhen()) break;
                sense?.Invoke(f.position);
                Footprint(f.position, dt);
                if (f.trail.Count == 0 || Vector2.Distance(f.trail[f.trail.Count - 1], f.position) > 0.1f) f.trail.Add(f.position);

                if (status != GoalStatus.InProgress)
                {
                    var next = algorithm.NextGoal(map, new Pose2(f.position.ToApi(), 0f), status);
                    if (drive.Finished)
                    {
                        f.finished = true;
                        break;
                    }
                    if (!next.HasValue)
                    {
                        status = GoalStatus.None;
                        continue;
                    }
                    f.goals++;
                    goal = next.Value.ToUnity();
                    f.targets.Add(goal);
                    pass = drive.PassThrough;
                    if (drive.Mode == DriveMode.Sweep) sweptSlowly++;
                    speed = drive.Mode == DriveMode.Sweep
                        ? Mathf.Min(full, Mathf.Max(ControllerConstants.SweepSpeedFloor,
                            CoverageMath.SweepSpeed(drive.SweepExposureMeters, 1f, 1f, Target) / ControllerConstants.SweepSpeedSafety))
                        : full;
                    if (!map.FindPath(f.position.ToApi(), next.Value, path, out _))
                    {
                        status = GoalStatus.Unreachable;
                        continue;
                    }
                    index = 1;
                    status = GoalStatus.InProgress;
                }

                float budget = speed * dt;
                while (budget > 0f && index < path.Count)
                {
                    Vector2 target = path[index].ToUnity();
                    float d = Vector2.Distance(f.position, target);
                    if (d <= budget)
                    {
                        f.position = target;
                        budget -= d;
                        f.driven += d;
                        index++;
                    }
                    else
                    {
                        f.position += (target - f.position) / d * budget;
                        f.driven += budget;
                        budget = 0f;
                    }
                }
                if (index >= path.Count || (pass && Vector2.Distance(f.position, goal) <= DriveRequests.PassThroughMeters))
                    status = GoalStatus.Reached;
            }
            return f;
        }

        /// <summary>As the controller's footprint: every cell whose centre is under the brush gains dt of dwell and cleanliness (power 1, resistance 1).</summary>
        void Footprint(Vector2 p, float dt)
        {
            if (!view.TryWorldToCell(p - new Vector2(Radius, Radius), out var low) || !view.TryWorldToCell(p + new Vector2(Radius, Radius), out var high)) return;
            for (int r = low.row; r <= high.row; r++)
                for (int c = low.col; c <= high.col; c++)
                    if ((view.CellCenter(r, c) - p).sqrMagnitude <= Radius * Radius)
                        writer.AddDwellAndCleanliness(r, c, dt, dt);
        }

        /// <summary>The share of the floor where the body fits that has reached the target.</summary>
        float Coverage(out float area)
        {
            var snapshot = new MapSnapshot();
            snapshot.Take(map);
            int fit = 0, clean = 0;
            for (int r = snapshot.MinRow; r < snapshot.MinRow + snapshot.Rows; r++)
                for (int c = snapshot.MinCol; c < snapshot.MinCol + snapshot.Cols; c++)
                {
                    if (!snapshot.BodyFits(r, c) || !map.IsReachable(r, c)) continue;
                    fit++;
                    if (snapshot.Cleanliness(r, c) >= BoustrophedonSweep.CleanEnough * Target) clean++;
                }
            area = fit * 0.05f * 0.05f;
            return fit > 0 ? clean / (float)fit : 0f;
        }

        [Test]
        public void Crawl_ARoomWithATable_IsCleanedToTheTarget_WithoutOverCleaning()
        {
            Polygon(new Vector2(-2f, -1.5f), new Vector2(2f, -1.5f), new Vector2(2f, 1.5f), new Vector2(-2f, 1.5f));
            Block(-0.4f, -0.3f, 0.4f, 0.3f); // a table in the middle
            var start = new Vector2(-1.5f, -1f);
            view.SetBelievedPose(new RobotPose(start, 0f));

            var algorithm = new BoustrophedonCrawl();
            algorithm.Initialize(Context());
            var f = Run(algorithm, start, 4000f);

            float coverage = Coverage(out float area);
            float median = MedianCleanliness();
            Debug.Log($"Crawl, room with a table: {coverage:P1} of {area:F2} m² at target, median cleanliness {median:F2} (target {Target}), " +
                      $"lanes {algorithm.LaneSpacing * 100f:F1} cm apart, {f.seconds:F0} s, driven {f.driven:F1} m, goals {f.goals}, finished {f.finished}");
            Assert.IsTrue(f.finished, "it should run out of work and say so");
            Assert.GreaterOrEqual(coverage, 0.95f, "the floor where the body fits reaches the target");
            Assert.Less(median, 2.5f * Target, "one pass, then patches: not pass after pass over the whole room");
            Assert.Greater(sweptSlowly, 0, "Crawl cleans at sweep speed");
        }

        /// <summary>The median believed cleanliness over the floor where the body fits.</summary>
        float MedianCleanliness()
        {
            var snapshot = new MapSnapshot();
            snapshot.Take(map);
            var values = new List<float>();
            for (int r = snapshot.MinRow; r < snapshot.MinRow + snapshot.Rows; r++)
                for (int c = snapshot.MinCol; c < snapshot.MinCol + snapshot.Cols; c++)
                    if (snapshot.BodyFits(r, c) && map.IsReachable(r, c)) values.Add(snapshot.Cleanliness(r, c));
            if (values.Count == 0) return 0f;
            values.Sort();
            return values[values.Count / 2];
        }

        [Test]
        public void Sweep_FirstCoversEverythingAtFullSpeed_ThenPassesUntilClean()
        {
            Polygon(new Vector2(-2f, -1.5f), new Vector2(2f, -1.5f), new Vector2(2f, 1.5f), new Vector2(-2f, 1.5f));
            Block(-0.4f, -0.3f, 0.4f, 0.3f);
            var start = new Vector2(-1.5f, -1f);
            view.SetBelievedPose(new RobotPose(start, 0f));

            var algorithm = new BoustrophedonSweep();
            algorithm.Initialize(Context());
            Assert.AreEqual(0.306f, algorithm.LaneSpacing, 1e-3f, "lanes just overlapping");

            // Run until the first pass ends: by then every place the body fits has been visited, at full speed.
            var f = Run(algorithm, start, 3000f, stopWhen: () => algorithm.Pass > 0);
            float visited = Visited();
            Debug.Log($"Sweep, first pass: {visited:P1} visited in {f.seconds:F0} s, driven {f.driven:F1} m");
            Assert.GreaterOrEqual(visited, 0.97f, "the first pass reaches everywhere before a second begins");
            Assert.IsTrue(sweptSlowly == 0, "Sweep never asks for sweep speed");

            // Then on to the target.
            var rest = Run(algorithm, f.position, 6000f);
            float coverage = Coverage(out _);
            float median = MedianCleanliness();
            Debug.Log($"Sweep, to the target: {coverage:P1} at target after {algorithm.Pass + 1} passes, {f.seconds + rest.seconds:F0} s in all, " +
                      $"median cleanliness {median:F2} (target {Target})");
            Assert.IsTrue(rest.finished);
            Assert.GreaterOrEqual(coverage, 0.95f);
            Assert.Less(median, 2f * Target, "a mostly clean room gets patches, not more whole passes");
        }

        [Test]
        public void AShadedSlamWall_CountsAsAWall_ButASlowFloorDoesNot()
        {
            // SLAM shades a wall it isn't sure of from the floor's ease (probability 0.5) down to 0 (0.75). Below half the
            // ease it is more likely wall than not: the planner and the map analysis must not go through it.
            writer.SetTraversability(0, 0, 0.3f);              // ease 1: shaded below half
            Assert.IsTrue(view.IsBlocked(0, 0), "traversability 0.3 on ease-1 floor is a wall");
            writer.SetTraversability(0, 1, 0.7f);
            Assert.IsFalse(view.IsBlocked(0, 1), "0.7 on ease-1 floor is (probably) floor");

            writer.SetEase(0, 2, 0.4f);                        // a slow floor, fully clear
            writer.SetTraversability(0, 2, 0.4f);
            Assert.IsFalse(view.IsBlocked(0, 2), "a slow floor is not a wall");
            writer.SetTraversability(0, 2, 0.15f);             // the same floor, shaded below half its ease
            Assert.IsTrue(view.IsBlocked(0, 2));
            writer.SetTraversability(0, 3, 0f);
            Assert.IsTrue(view.IsBlocked(0, 3));
        }

        [Test]
        public void Sweep_FirstPass_DoesNotGoBackForLaneEnds()
        {
            // A U-turn starts a radius before the lane's end and its arc covers the rest. If the lane were recorded as
            // swept only to where the turn starts, every lane end would be left as a 15 cm "run" to go back for, one
            // decision at a time, all over the room.
            Polygon(new Vector2(-2f, -1.5f), new Vector2(2f, -1.5f), new Vector2(2f, 1.5f), new Vector2(-2f, 1.5f));
            var start = new Vector2(-1.5f, -1f);
            view.SetBelievedPose(new RobotPose(start, 0f));

            var decisions = new List<string>();
            var algorithm = new BoustrophedonSweep();
            algorithm.Initialize(Context(log: decisions.Add));
            Run(algorithm, start, 3000f, stopWhen: () => algorithm.Pass > 0);

            var lanes = decisions.FindAll(d => d.StartsWith("pass 1, lanes"));
            Debug.Log($"Sweep, empty room, first pass: {lanes.Count} lane decisions:\n{string.Join("\n", lanes)}");
            Assert.LessOrEqual(lanes.Count, 2, "one sweep over an empty room (two at most), not trips back for the lane ends");
        }

        [Test]
        public void EdgePass_CleansRightUpToTheWall()
        {
            // The first two rows of floor along each wall (cell centres 2.5 and 7.5 cm from the wall's cells) must be
            // under the brush after the first pass. Before, the edge run kept the body 2 cm out and drove the edge
            // cells' centres (up to 5 cm further out again), and that strip was never touched. Corners are left out: a
            // round robot can't reach into them.
            Polygon(new Vector2(-2f, -1.5f), new Vector2(2f, -1.5f), new Vector2(2f, 1.5f), new Vector2(-2f, 1.5f));
            var start = new Vector2(-1.5f, -1f);
            view.SetBelievedPose(new RobotPose(start, 0f));

            var algorithm = new BoustrophedonSweep();
            algorithm.Initialize(Context());
            Run(algorithm, start, 3000f, stopWhen: () => algorithm.Pass > 0);

            var snapshot = new MapSnapshot();
            snapshot.Take(map);
            var corners = new[] { new Vector2(-2f, -1.5f), new Vector2(2f, -1.5f), new Vector2(2f, 1.5f), new Vector2(-2f, 1.5f) };
            int strip = 0, touched = 0;
            for (int r = snapshot.MinRow; r < snapshot.MinRow + snapshot.Rows; r++)
                for (int c = snapshot.MinCol; c < snapshot.MinCol + snapshot.Cols; c++)
                {
                    if (snapshot.IsBlocked(r, c) || !map.IsReachable(r, c)) continue;
                    if (snapshot.ObstacleDistance(r, c) > 0.07f) continue; // the first two rows by a wall
                    Vector2 p = view.CellCenter(r, c);
                    if (Mathf.Abs(p.x) > 2f || Mathf.Abs(p.y) > 1.5f) continue; // outside the room
                    bool nearCorner = false;
                    foreach (var corner in corners) nearCorner |= Vector2.Distance(p, corner) < 0.45f;
                    if (nearCorner) continue;
                    strip++;
                    if (snapshot.IsVisited(r, c)) touched++;
                }
            float share = strip > 0 ? touched / (float)strip : 0f;
            Debug.Log($"Edge strip: {touched} of {strip} cells touched ({share:P1}) in the first pass");
            Assert.GreaterOrEqual(share, 0.9f, "the edge run brings the brush right up to the wall");
        }

        /// <summary>The share of the floor where the body fits that has been under the brush at all.</summary>
        float Visited()
        {
            var snapshot = new MapSnapshot();
            snapshot.Take(map);
            int fit = 0, seen = 0;
            for (int r = snapshot.MinRow; r < snapshot.MinRow + snapshot.Rows; r++)
                for (int c = snapshot.MinCol; c < snapshot.MinCol + snapshot.Cols; c++)
                {
                    if (!snapshot.BodyFits(r, c) || !map.IsReachable(r, c)) continue;
                    fit++;
                    if (snapshot.IsVisited(r, c)) seen++;
                }
            return fit > 0 ? seen / (float)fit : 0f;
        }

        [Test]
        public void Crawl_TwoRooms_AreFoundAsRooms_AndCleanedOneAfterTheOther()
        {
            // Two 3 x 2 m rooms joined by a 0.8 m doorway at x = 0.
            Polygon(new Vector2(-3f, -1f), new Vector2(3f, -1f), new Vector2(3f, 1f), new Vector2(-3f, 1f));
            Wall(new Vector2(0f, -1f), new Vector2(0f, -0.4f));
            Wall(new Vector2(0f, 0.4f), new Vector2(0f, 1f));
            var start = new Vector2(-2.5f, -0.5f);
            view.SetBelievedPose(new RobotPose(start, 0f));

            var algorithm = new BoustrophedonCrawl();
            algorithm.Initialize(Context());
            var f = Run(algorithm, start, 6000f);

            // Which room the robot is in, with a band either side of the doorway (x = 0) so cleaning right up to the
            // doorway, which can dip a few centimetres past it, doesn't count as changing rooms.
            int crossings = 0, side = 0;
            var visits = new List<string>();
            for (int i = 0; i < f.trail.Count; i++)
            {
                int now = f.trail[i].x < -0.3f ? -1 : f.trail[i].x > 0.3f ? 1 : side;
                if (now == side) continue;
                if (side != 0) crossings++;
                side = now;
                visits.Add($"{(now < 0 ? "left" : "right")} from {f.trail[i]}");
            }

            float coverage = Coverage(out _);
            Debug.Log($"Crawl, two rooms: {algorithm.RoomCount} rooms, {coverage:P1} at target, {f.seconds:F0} s, room changes {crossings}, finished {f.finished}\n  " +
                      string.Join("\n  ", visits));
            Assert.AreEqual(2, algorithm.RoomCount, "the doorway splits the floor into two rooms");
            Assert.IsTrue(f.finished);
            Assert.GreaterOrEqual(coverage, 0.95f);
            Assert.LessOrEqual(crossings, 1, "one room, then the other: not back and forth");
        }

        [Test]
        public void Slam_Sweep_OnlyPlansOverExploredFloor_AndExploresTheRest() => SlamExploring(new BoustrophedonSweep());

        [Test]
        public void Slam_Crawl_OnlyPlansOverExploredFloor_AndExploresTheRest() => SlamExploring(new BoustrophedonCrawl());

        void SlamExploring(ICleaningAlgorithm algorithm)
        {
            // Only floor within 2 m of where the robot has been counts as explored (a stand-in for the lidar).
            Polygon(new Vector2(-3f, -1f), new Vector2(3f, -1f), new Vector2(3f, 1f), new Vector2(-3f, 1f));
            var known = new HashSet<long>();
            var start = new Vector2(-2.5f, 0f);
            void Sense(Vector2 p)
            {
                if (!view.TryWorldToCell(p, out var centre)) return;
                for (int r = centre.row - 40; r <= centre.row + 40; r++)
                    for (int c = centre.col - 40; c <= centre.col + 40; c++)
                        if ((view.CellCenter(r, c) - p).sqrMagnitude <= 4f) known.Add(CellIndex.Pack(r, c));
            }
            view.SetExploredSource((r, c) => known.Contains(CellIndex.Pack(r, c)));
            view.SetBelievedPose(new RobotPose(start, 0f));
            Sense(start);

            algorithm.Initialize(Context(slam: true));
            var first = algorithm.NextGoal(map, new Pose2(start.ToApi(), 0f), GoalStatus.None);
            Assert.IsTrue(first.HasValue);
            Assert.IsTrue(view.TryWorldToCell(first.Value.ToUnity(), out var firstCell) && known.Contains(CellIndex.Pack(firstCell.row, firstCell.col)),
                "the first goal is on explored floor");

            algorithm.Initialize(Context(slam: true));
            var f = Run(algorithm, start, 6000f, Sense);
            float coverage = Coverage(out _);
            Debug.Log($"{algorithm.Name}, SLAM-style: {coverage:P1} at target, {f.seconds:F0} s, finished {f.finished}");
            Assert.IsTrue(f.finished);
            Assert.GreaterOrEqual(coverage, 0.95f);
        }

        // ---- Rooms from doorways, and floor that only looks reachable ---------------------------------------

        /// <summary>The sample apartment's walls (12 cm thick), with its three doorways left open.</summary>
        void SampleApartment()
        {
            var rooms = new[]
            {
                new[] { new Vector2(-2.4f, -1.8f), new Vector2(1.6f, -1.8f), new Vector2(1.6f, 1.4f), new Vector2(-2.4f, 1.4f) },
                new[] { new Vector2(1.6f, -1.8f), new Vector2(4.6f, -1.2f), new Vector2(4.6f, 1.0f), new Vector2(1.6f, 1.4f) },
                new[] { new Vector2(-2.4f, -1.8f), new Vector2(1.6f, -1.8f), new Vector2(1.2f, -3.2f), new Vector2(-2.0f, -3.4f) },
                new[] { new Vector2(-2.0f, -3.4f), new Vector2(-2.4f, -1.8f), new Vector2(-4.923f, -2.431f), new Vector2(-4.523f, -4.031f) },
            };
            var doors = new[] { (new Vector2(1.6f, 0.2f), 1.0f), (new Vector2(-0.4f, -1.8f), 1.1f), (new Vector2(-2.2f, -2.6f), 0.9f) };

            foreach (var outline in rooms)
                for (int i = 0; i < outline.Length; i++)
                {
                    Vector2 a = outline[i], b = outline[(i + 1) % outline.Length];
                    float length = Vector2.Distance(a, b);
                    for (float t = 0f; t <= length; t += 0.01f)
                    {
                        Vector2 p = Vector2.Lerp(a, b, t / length);
                        bool inDoor = false;
                        foreach (var (centre, width) in doors) inDoor |= Vector2.Distance(p, centre) < width * 0.5f;
                        if (inDoor) continue;
                        for (float x = -0.06f; x <= 0.06f; x += 0.02f)
                            for (float y = -0.06f; y <= 0.06f; y += 0.02f)
                                if (view.TryWorldToCell(p + new Vector2(x, y), out var c)) writer.SetTraversability(c.row, c.col, 0f);
                    }
                }
        }

        [Test]
        public void TheSampleApartment_IsFourRooms_SplitAtItsDoorways()
        {
            SampleApartment();
            var spawn = new Vector2(-0.4f, -0.2f);
            view.SetBelievedPose(new RobotPose(spawn, 0f));

            var algorithm = new BoustrophedonSweep();
            algorithm.Initialize(Context());
            algorithm.NextGoal(map, new Pose2(spawn.ToApi(), 0f), GoalStatus.None);

            var doorways = new List<string>();
            foreach (var (a, b) in algorithm.Doorways) doorways.Add($"{a.ToUnity()}-{b.ToUnity()}");
            Debug.Log($"Sample apartment: {algorithm.RoomCount} rooms; doorway cuts: {string.Join(", ", doorways)}");
            Assert.AreEqual(4, algorithm.RoomCount, "living room, kitchen, hall and bedroom");
        }

        [Test]
        public void FloorGlimpsedThroughAGap_OutsideTheWalls_IsNeverATarget()
        {
            // A room whose top wall has a 10 cm gap (too narrow for the body), and "explored" floor outside it, as when
            // the lidar sees through a half-drawn wall. Unexplored floor reads as open, so before, that outside floor
            // looked reachable through the unknown: it became work, every path to it failed, and the robot stalled.
            Polygon(new Vector2(-2f, -1.5f), new Vector2(2f, -1.5f), new Vector2(2f, 1.5f), new Vector2(-2f, 1.5f));
            for (float x = 0.5f; x < 0.6f; x += 0.02f)
                if (view.TryWorldToCell(new Vector2(x, 1.5f), out var gap)) writer.SetTraversability(gap.row, gap.col, 1f);

            bool Inside(Vector2 p) => p.x >= -2.05f && p.x <= 2.05f && p.y >= -1.55f && p.y <= 1.55f;
            bool Glimpsed(Vector2 p) => p.x >= -1f && p.x <= 1f && p.y >= 1.6f && p.y <= 2.6f;
            view.SetExploredSource((row, col) =>
            {
                var p = view.CellCenter(row, col);
                return Inside(p) || Glimpsed(p);
            });
            var start = new Vector2(-1.5f, -1f);
            view.SetBelievedPose(new RobotPose(start, 0f));

            var algorithm = new BoustrophedonSweep();
            algorithm.Initialize(Context(slam: true));
            var f = Run(algorithm, start, 3000f, stopWhen: () => algorithm.Pass > 0);

            int outside = f.targets.FindAll(p => !Inside(p)).Count;
            Debug.Log($"Glimpsed floor: {f.goals} goals, {outside} outside the room, first pass took {f.seconds:F0} s");
            Assert.AreEqual(0, outside, "no goal outside the walls");
            Assert.Greater(algorithm.Pass, 0, "the first pass finished (it didn't stall)");
        }
    }
}
