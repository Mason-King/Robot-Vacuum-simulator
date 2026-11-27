using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Sim.Control;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>The body planner, and the driver following its paths and re-routing around what it runs into.</summary>
    public class PathPlannerTests
    {
        const float Radius = 0.17f;
        const float Clearance = Radius + ControllerConstants.ClearanceMarginMeters;

        InternalModelWriter writer;
        InternalModelView view;
        PathPlanner planner;

        [SetUp]
        public void SetUp()
        {
            writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, 0.05f));
            view = new InternalModelView(writer);
            planner = new PathPlanner(view, Clearance);
        }

        // ---- Map building ----------------------------------------------------------------------------

        void Wall(Vector2 a, Vector2 b)
        {
            bool okA = view.TryWorldToCell(a, out var ca);
            bool okB = view.TryWorldToCell(b, out var cb);
            Assert.IsTrue(okA && okB);
            foreach (var cell in GridLine.Cells(ca.row, ca.col, cb.row, cb.col)) writer.SetTraversability(cell.row, cell.col, 0f);
        }

        void Box(float minX, float minY, float maxX, float maxY)
        {
            Wall(new Vector2(minX, minY), new Vector2(maxX, minY));
            Wall(new Vector2(maxX, minY), new Vector2(maxX, maxY));
            Wall(new Vector2(maxX, maxY), new Vector2(minX, maxY));
            Wall(new Vector2(minX, maxY), new Vector2(minX, minY));
        }

        void Disc(Vector2 centre, float radius)
        {
            for (float x = -radius; x <= radius; x += 0.025f)
                for (float y = -radius; y <= radius; y += 0.025f)
                    if (x * x + y * y <= radius * radius && view.TryWorldToCell(centre + new Vector2(x, y), out var cell))
                        writer.SetTraversability(cell.row, cell.col, 0f);
        }

        [Test]
        public void TheUpdatedClearanceField_IsTheRebuiltOne_ToTheBit()
        {
            // A room, a path planned (the field is built), then wall cells added and removed inside it a few at a time
            // with a path planned after each change (the field is updated, not rebuilt). After every change the field
            // must equal what a new planner builds from scratch, and both must equal the clearance worked out the long
            // way, cell by cell.
            Box(-2f, -2f, 2f, 2f);
            Wall(new Vector2(-0.6f, 0.6f), new Vector2(0.6f, -0.6f)); // across the straight line, so the planner searches
            var from = new Vector2(-1.5f, -1.5f);
            var to = new Vector2(1.5f, 1.5f);
            var path = new List<Vector2>();
            planner.FindPath(from, to, path, out _);
            int rebuilds = planner.FieldRebuilds;

            var rng = new System.Random(7);
            for (int round = 0; round < 12; round++)
            {
                for (int k = 0; k < 6; k++)
                {
                    var spot = new Vector2((float)(rng.NextDouble() * 3.2 - 1.6), (float)(rng.NextDouble() * 3.2 - 1.6));
                    if (Mathf.Abs(spot.x + spot.y) < 0.15f || (spot - from).magnitude < 0.4f || (spot - to).magnitude < 0.4f) continue;
                    Assert.IsTrue(view.TryWorldToCell(spot, out var cell));
                    writer.SetTraversability(cell.row, cell.col, round % 3 == 2 ? 1f : 0f);
                }
                planner.FindPath(from, to, path, out _);

                var fresh = new PathPlanner(view, Clearance);
                fresh.FindPath(from, to, new List<Vector2>(), out _);
                for (float y = -2.4f; y <= 2.4f; y += 0.05f)
                    for (float x = -2.4f; x <= 2.4f; x += 0.05f)
                    {
                        Assert.IsTrue(view.TryWorldToCell(new Vector2(x, y), out var cell));
                        float updated = planner.ClearanceOf(cell.row, cell.col);
                        Assert.AreEqual(fresh.ClearanceOf(cell.row, cell.col), updated, 0f, $"round {round}, cell {cell.row},{cell.col}: rebuilt");
                        Assert.AreEqual(LongWayClearance(cell.row, cell.col), updated, 0f, $"round {round}, cell {cell.row},{cell.col}: the long way");
                    }
            }
            Assert.Greater(planner.FieldUpdates, 0, "the field was updated in place");
            Assert.AreEqual(rebuilds, planner.FieldRebuilds, "and never rebuilt");
        }

        /// <summary>A cell's clearance as the planner defines it: the distance from its centre to the nearest blocked cell's edge, capped.</summary>
        float LongWayClearance(int row, int col)
        {
            float cell = view.CellSizeMeters, half = cell * 0.5f;
            int reach = Mathf.CeilToInt((Clearance + half) / cell) + 1;
            float best = Clearance + cell;
            for (int dr = -reach; dr <= reach; dr++)
                for (int dc = -reach; dc <= reach; dc++)
                {
                    if (!view.IsBlocked(row + dr, col + dc)) continue;
                    float d = 0f;
                    if (dr != 0 || dc != 0)
                    {
                        float dx = Mathf.Max(Math.Abs(dc) * cell - half, 0f), dy = Mathf.Max(Math.Abs(dr) * cell - half, 0f);
                        d = Mathf.Sqrt(dx * dx + dy * dy);
                    }
                    if (d < best) best = d;
                }
            return best;
        }

        void AssertPathKeepsClear(List<Vector2> path)
        {
            for (int i = 1; i < path.Count; i++)
                Assert.IsTrue(planner.SegmentClear(path[i - 1], path[i]), $"leg {i}: {path[i - 1]} -> {path[i]}");
            for (int i = 1; i < path.Count - 1; i++)
            {
                Assert.IsTrue(view.TryWorldToCell(path[i], out var cell));
                Assert.Greater(planner.ClearanceOf(cell.row, cell.col), Clearance, $"corner {path[i]}");
            }
        }

        // ---- The planner -----------------------------------------------------------------------------

        [Test]
        public void OpenFloor_IsOneStraightLeg()
        {
            var path = new List<Vector2>();
            Assert.IsTrue(planner.FindPath(Vector2.zero, new Vector2(2f, 1f), path, out float length));
            Assert.AreEqual(2, path.Count, string.Join(" ", path));
            Assert.AreEqual(Mathf.Sqrt(5f), length, 1e-3f);
        }

        [Test]
        public void AWallInTheWay_IsGoneAround_WithTheBodyClear()
        {
            Box(-2f, -2f, 2f, 2f);
            Wall(new Vector2(-1.5f, 0.5f), new Vector2(1.5f, 0.5f));

            var path = new List<Vector2>();
            Assert.IsTrue(planner.FindPath(Vector2.zero, new Vector2(0f, 1f), path, out float length));
            Assert.Greater(path.Count, 2);
            Assert.Greater(length, 3f, "round the end of the wall and back");
            Assert.Less(length, 5f);
            AssertPathKeepsClear(path);
        }

        [Test]
        public void AGapNarrowerThanTheBody_IsClosed_AWideOneIsOpen()
        {
            Box(-2f, -2f, 2f, 2f);
            Wall(new Vector2(-2f, 0f), new Vector2(-0.15f, 0f));
            Wall(new Vector2(0.15f, 0f), new Vector2(2f, 0f)); // a 0.3 m gap: the body is 0.34 m across
            var path = new List<Vector2>();
            Assert.IsFalse(planner.FindPath(new Vector2(0f, -1f), new Vector2(0f, 1f), path, out _), string.Join(" ", path));

            SetUp();
            Box(-2f, -2f, 2f, 2f);
            Wall(new Vector2(-2f, 0f), new Vector2(-0.35f, 0f));
            Wall(new Vector2(0.35f, 0f), new Vector2(2f, 0f)); // 0.7 m
            Assert.IsTrue(planner.FindPath(new Vector2(0f, -1f), new Vector2(0f, 1f), path, out _));
            AssertPathKeepsClear(path);
        }

        [Test]
        public void ARobotAlreadyCloseToAWall_CanLeave_AndAGoalCloseToOne_CanBeReached()
        {
            Box(-2f, -2f, 2f, 2f);
            var path = new List<Vector2>();

            // 0.1 m from the top wall's centre line: well inside the clearance.
            Assert.IsTrue(planner.FindPath(new Vector2(0f, 1.9f), Vector2.zero, path, out _), "leaving");
            Assert.IsTrue(planner.FindPath(Vector2.zero, new Vector2(1.9f, 0f), path, out _), "arriving");
        }

        [Test]
        public void AGoalInAWall_OrBehindAClosedWall_HasNoPath()
        {
            Box(-2f, -2f, 2f, 2f);
            var path = new List<Vector2>();
            Assert.IsFalse(planner.FindPath(Vector2.zero, new Vector2(2f, 0f), path, out _), "in the wall");
            Assert.IsFalse(planner.FindPath(Vector2.zero, new Vector2(3f, 0f), path, out _), "outside the closed box");
        }

        [Test]
        public void TheApi_OffersThePlanner()
        {
            Box(-2f, -2f, 2f, 2f);
            Wall(new Vector2(-1.5f, 0.5f), new Vector2(1.5f, 0.5f));
            IBeliefMap map = new BeliefMapAdapter(view, planner);

            var path = new List<Vec2>();
            Assert.IsTrue(map.FindPath(Vec2.Zero, new Vec2(0f, 1f), path, out float length));
            Assert.AreEqual(length, map.PathLength(Vec2.Zero, new Vec2(0f, 1f)), 1e-4f);
            Assert.IsTrue(float.IsPositiveInfinity(map.PathLength(Vec2.Zero, new Vec2(3f, 0f))));
            Assert.AreEqual(Clearance, map.BodyClearanceMeters, 1e-6f);
            Assert.IsFalse(map.BodyFitsLeg(Vec2.Zero, new Vec2(0f, 1f)), "straight through the wall");
        }

        // ---- The driver on a planned path ------------------------------------------------------------

        /// <summary>Drives a simple body (no slip) under the driver until the goal ends or time runs out.</summary>
        GoalStatus Drive(WaypointDriver driver, ref RobotPose pose, float seconds,
            Func<RobotPose, float?> bumpBearing = null, Action<float> atTime = null)
        {
            const float dt = 0.02f;
            float t = 0f;
            while (t < seconds && driver.Status == GoalStatus.InProgress)
            {
                t += dt;
                atTime?.Invoke(t);
                float? bump = bumpBearing?.Invoke(pose);
                var input = new DriveInput(pose, t, dt, bump.HasValue, bump ?? 0f, view, Radius, 0.65f, 0.35f, 320f);
                driver.Step(input, out float forward, out float turn);

                float heading = pose.heading + turn * dt;
                if (bump.HasValue && Mathf.Abs(bump.Value) <= ControllerConstants.FrontContactDegrees && forward > 0f) forward = 0f; // it is solid
                var position = pose.position + RobotPose.DirectionOf(heading) * forward * dt;
                Assert.IsFalse(view.IsBlockedAt(position), $"the robot's centre entered a blocked cell at {position}, t = {t:F2}");
                pose = new RobotPose(position, heading);
            }
            return driver.Status;
        }

        [Test]
        public void Driver_FollowsThePath_RoundAWall_ToTheGoal()
        {
            Box(-2f, -2f, 2f, 2f);
            Wall(new Vector2(-1.5f, 0.5f), new Vector2(1.5f, 0.5f));

            var driver = new WaypointDriver { Planner = planner };
            var pose = new RobotPose(Vector2.zero, 0f);
            var goal = new Vector2(0f, 1f);
            driver.SetGoal(goal, pose, 0f, view);
            Assert.Greater(driver.Path.Count, 2);

            Assert.AreEqual(GoalStatus.Reached, Drive(driver, ref pose, 40f));
            Assert.LessOrEqual(Vector2.Distance(pose.position, goal), ControllerConstants.ArrivalToleranceMeters);
            Assert.AreEqual(0, driver.Reroutes);
        }

        [Test]
        public void Driver_ReplansWhenTheMapChangesAhead_WithoutUsingAReroute()
        {
            Box(-2f, -1f, 2f, 4f);
            var driver = new WaypointDriver { Planner = planner };
            var pose = new RobotPose(Vector2.zero, 0f);
            driver.SetGoal(new Vector2(0f, 3f), pose, 0f, view);
            Assert.AreEqual(2, driver.Path.Count, "straight at first");

            bool added = false;
            var status = Drive(driver, ref pose, 40f, atTime: t =>
            {
                if (added || t < 1f) return;
                added = true;
                Wall(new Vector2(-2f, 2f), new Vector2(1f, 2f)); // mapped ahead: the way round is on the right
            });

            Assert.AreEqual(GoalStatus.Reached, status);
            Assert.GreaterOrEqual(driver.Replans, 2);
            Assert.AreEqual(0, driver.Reroutes);
        }

        [Test]
        public void Driver_BumpingIntoSomethingUnmapped_ReroutesRoundIt()
        {
            Box(-2f, -1f, 2f, 4f);
            var pillar = new Vector2(0f, 1.2f);
            var driver = new WaypointDriver { Planner = planner };
            var pose = new RobotPose(Vector2.zero, 0f);
            driver.SetGoal(new Vector2(0f, 2.4f), pose, 0f, view);

            int bumps = 0;
            var status = Drive(driver, ref pose, 60f, bumpBearing: p =>
            {
                if (Vector2.Distance(p.position, pillar) > Radius + 0.03f) return null; // a thin leg, 3 cm radius
                float bearing = p.BearingTo(pillar);
                if (Mathf.Abs(bearing) > ControllerConstants.FrontContactDegrees) return null;
                bumps++;
                Disc(pillar, 0.08f); // the whisker maps what it touched (generously), as ObservationMapper does
                return bearing;
            });

            Assert.AreEqual(GoalStatus.Reached, status, $"bumps {bumps}, re-routes {driver.Reroutes}");
            Assert.Greater(bumps, 0, "it did run into it");
            Assert.LessOrEqual(driver.Reroutes, ControllerConstants.MaxReroutes);
        }

        [Test]
        public void Driver_ATooLongDetour_IsBlocked()
        {
            Box(-6f, -1f, 6f, 3f);
            var driver = new WaypointDriver { Planner = planner };
            var pose = new RobotPose(Vector2.zero, 0f);
            driver.SetGoal(new Vector2(0f, 1.5f), pose, 0f, view);

            bool added = false;
            var status = Drive(driver, ref pose, 40f, atTime: t =>
            {
                if (added || t < 0.5f) return;
                added = true;
                Wall(new Vector2(-6f, 0.9f), new Vector2(5f, 0.9f)); // the only way round is 5+ m to the right and back
            });

            Assert.AreEqual(GoalStatus.Blocked, status, "1.5 m planned; the detour exceeds 2 x 1.5 + 4 m");
        }

        [Test]
        public void Driver_WithoutAPlanner_DrivesStraight_AsBefore()
        {
            var driver = new WaypointDriver();
            driver.SetGoal(new Vector2(0f, 1f), new RobotPose(Vector2.zero, 0f), 0f, view);
            Assert.AreEqual(0, driver.Path.Count);
            Assert.AreEqual(new Vector2(0f, 1f), driver.Target.Value);
        }
    }
}
