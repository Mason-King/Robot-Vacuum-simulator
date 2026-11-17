using NUnit.Framework;
using RobotVacuum.AlgorithmApi;
using RobotVacuum.Sim.Control;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Driving beside walls with a physical body: the walls are solid squares filling their cells right up to the
    /// cell edges (the worst case for where a mapped wall's face really is). The body is a disc that slides along
    /// what it touches, and the whisker reports the contact's bearing, as in the simulator.
    /// </summary>
    public class WallSkirtingTests
    {
        const float Radius = 0.17f;
        const float Dt = 0.02f;

        InternalModelWriter writer;
        InternalModelView view;
        PathPlanner planner;
        float cell;

        [SetUp]
        public void SetUp()
        {
            writer = new InternalModelWriter(new InternalModelGrid(Vector2.zero, 0.05f));
            view = new InternalModelView(writer);
            planner = new PathPlanner(view, Radius + ControllerConstants.ClearanceMarginMeters);
            cell = view.CellSizeMeters;
        }

        void Wall(Vector2 a, Vector2 b)
        {
            bool okA = view.TryWorldToCell(a, out var ca);
            bool okB = view.TryWorldToCell(b, out var cb);
            Assert.IsTrue(okA && okB);
            foreach (var c in GridLine.Cells(ca.row, ca.col, cb.row, cb.col)) writer.SetTraversability(c.row, c.col, 0f);
        }

        void Box(float minX, float minY, float maxX, float maxY)
        {
            Wall(new Vector2(minX, minY), new Vector2(maxX, minY));
            Wall(new Vector2(maxX, minY), new Vector2(maxX, maxY));
            Wall(new Vector2(maxX, maxY), new Vector2(minX, maxY));
            Wall(new Vector2(minX, maxY), new Vector2(minX, minY));
        }

        /// <summary>The nearest point of any blocked cell's square to <paramref name="p"/>, and its distance.</summary>
        float NearestWall(Vector2 p, out Vector2 nearest)
        {
            nearest = p;
            float best = float.PositiveInfinity;
            view.TryWorldToCell(p, out var centre);
            int reach = Mathf.CeilToInt((Radius + 0.1f) / cell) + 1;
            for (int r = centre.row - reach; r <= centre.row + reach; r++)
                for (int c = centre.col - reach; c <= centre.col + reach; c++)
                {
                    if (!view.IsBlocked(r, c)) continue;
                    Vector2 cc = view.CellCenter(r, c);
                    var q = new Vector2(Mathf.Clamp(p.x, cc.x - cell / 2f, cc.x + cell / 2f), Mathf.Clamp(p.y, cc.y - cell / 2f, cc.y + cell / 2f));
                    float d = Vector2.Distance(p, q);
                    if (d < best)
                    {
                        best = d;
                        nearest = q;
                    }
                }
            return best;
        }

        bool Penetrates(Vector2 p) => NearestWall(p, out _) < Radius - 1e-4f;

        /// <summary>Drives the disc under the driver until the goal ends or time runs out. Returns the steps spent in contact.</summary>
        GoalStatus Drive(WaypointDriver driver, ref RobotPose pose, float seconds, out int contactSteps)
        {
            contactSteps = 0;
            float t = 0f;
            while (t < seconds && driver.Status == GoalStatus.InProgress)
            {
                t += Dt;
                bool touching = NearestWall(pose.position, out var touch) <= Radius + 0.002f;
                float bearing = touching ? pose.BearingTo(touch) : 0f;
                if (touching) contactSteps++;

                var input = new DriveInput(pose, t, Dt, touching, bearing, view, Radius, 0.65f, 0.35f, 320f);
                driver.Step(input, out float forward, out float turn);

                float heading = pose.heading + turn * Dt;
                Vector2 move = RobotPose.DirectionOf(heading) * forward * Dt;
                Vector2 next = pose.position + move;

                // Solid walls: slide along them (keep whichever part of the move fits).
                if (Penetrates(next))
                {
                    var alongX = pose.position + new Vector2(move.x, 0f);
                    var alongY = pose.position + new Vector2(0f, move.y);
                    next = !Penetrates(alongX) ? alongX : !Penetrates(alongY) ? alongY : pose.position;
                }
                pose = new RobotPose(next, heading);
            }
            return driver.Status;
        }

        [Test]
        public void ShallowApproach_ToAGoalBesideAWall_SlidesAlongIt_WithoutReRouting()
        {
            // A long wall at y = 1.00 (its cells' faces at 1.00), approached at about 16 degrees, to a goal whose
            // centre would leave the body overlapping the wall by 1.5 cm (as a naive algorithm might pick).
            Box(-1f, -1f, 4f, 1f);
            var driver = new WaypointDriver { Planner = planner };
            var pose = new RobotPose(Vector2.zero, -75f);
            var goal = new Vector2(3f, 0.845f);
            driver.SetGoal(goal, pose, 0f, view);

            var status = Drive(driver, ref pose, 30f, out int contactSteps);

            Assert.AreEqual(GoalStatus.Reached, status, $"ended at {pose.position}, re-routes {driver.Reroutes}, contact steps {contactSteps}");
            Assert.AreEqual(0, driver.Reroutes, "a graze along the wall is not a collision to route around");
        }

        [Test]
        public void RoundingAWallEnd_KeepsTheBodyOffTheCorner()
        {
            // A wall from the left ending at x = 1.05, between the robot and its goal: the path rounds the end.
            Box(-2f, -2f, 2.5f, 2f);
            Wall(new Vector2(-2f, 0.5f), new Vector2(1f, 0.5f));
            var driver = new WaypointDriver { Planner = planner };
            var pose = new RobotPose(new Vector2(0.3f, 0f), 0f);
            driver.SetGoal(new Vector2(0.3f, 1f), pose, 0f, view);

            var status = Drive(driver, ref pose, 40f, out int contactSteps);

            Assert.AreEqual(GoalStatus.Reached, status, $"ended at {pose.position}, re-routes {driver.Reroutes}");
            Assert.AreEqual(0, contactSteps, "the planned path keeps a real gap from the wall's corner");
        }
    }
}
