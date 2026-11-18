using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RobotVacuum.Headless;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;
using RobotVacuum.Sim.Control;
using RobotVacuum.Sim.Sensors;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>The geometry of the three body shapes (3.3), on their own: no physics.</summary>
    public class VacuumShapeGeometryTests
    {
        const float R = VacuumShape.DefaultHalfWidth;

        [Test]
        public void EveryShapeIsAsWideAsTheDisc_AndTheRoundOneIsTheDisc()
        {
            foreach (VacuumShapeKind kind in System.Enum.GetValues(typeof(VacuumShapeKind)))
                Assert.AreEqual(2f * R, VacuumShape.For(kind).Width, 1e-6f, kind.ToString());

            var round = VacuumShape.Round();
            Assert.IsTrue(round.IsRound);
            Assert.AreEqual(R, round.TurnRadius, 1e-6f);
            Assert.AreEqual(R, round.EdgeDistance(37f), 0f, "round: exactly the radius at any bearing");
            Assert.AreEqual(0.3f - R, round.SignedDistance(new Vector2(0.3f, 0f)), 1e-6f);
        }

        [Test]
        public void DShape_SquareFront_RoundBack()
        {
            var d = VacuumShape.For(VacuumShapeKind.DShaped);
            Assert.AreEqual(0.34f, d.Length, 1e-6f);
            Assert.AreEqual(Mathf.Sqrt(2f) * (R - VacuumShape.DCornerRadius) + VacuumShape.DCornerRadius, d.TurnRadius, 1e-5f);

            Assert.IsTrue(d.Contains(new Vector2(0.16f, 0.16f)), "a front corner the disc doesn't reach");
            Assert.IsFalse(VacuumShape.Round().Contains(new Vector2(0.16f, 0.16f)));
            Assert.IsFalse(d.Contains(new Vector2(0.16f, -0.16f)), "the back is round");
            Assert.IsTrue(d.Contains(new Vector2(0.169f, 0.05f)), "flat sides from the axle forward");

            Assert.AreEqual(R, d.EdgeDistance(0f), 1e-4f, "front edge");
            Assert.AreEqual(R, d.EdgeDistance(90f), 1e-4f, "side");
            Assert.AreEqual(R, d.EdgeDistance(180f), 1e-4f, "back");
            Assert.Greater(d.EdgeDistance(45f), 0.2f, "towards a front corner");
        }

        [Test]
        public void Rectangle_LongAxisFrontToBack()
        {
            var box = VacuumShape.For(VacuumShapeKind.Rectangular);
            Assert.AreEqual(VacuumShape.RectangleLength, box.Length, 1e-6f);
            Assert.Greater(box.Length, box.Width, "the long axis runs front to back");
            Assert.AreEqual(box.Length * 0.5f, box.EdgeDistance(0f), 1e-4f);
            Assert.AreEqual(box.Length * 0.5f, box.EdgeDistance(180f), 1e-4f);
            Assert.AreEqual(R, box.EdgeDistance(-90f), 1e-4f);
            Assert.IsTrue(box.Contains(new Vector2(0.16f, 0.19f)));
            Assert.IsFalse(box.Contains(new Vector2(0.17f, 0.2f)), "the corner is rounded");
        }

        [Test]
        public void OutlinesLieOnTheEdge_AndRunCounterClockwise()
        {
            foreach (VacuumShapeKind kind in System.Enum.GetValues(typeof(VacuumShapeKind)))
            {
                var shape = VacuumShape.For(kind);
                var outline = shape.Outline(8);
                float area = 0f;
                for (int i = 0; i < outline.Count; i++)
                {
                    Assert.AreEqual(0f, shape.SignedDistance(outline[i]), 1e-4f, $"{kind} point {i}");
                    var a = outline[i];
                    var b = outline[(i + 1) % outline.Count];
                    area += a.x * b.y - b.x * a.y;
                }
                Assert.Greater(area, 0f, $"{kind} runs counter-clockwise");
            }
        }

        [Test]
        public void TheRobotFrame_MatchesTheHeadingConvention()
        {
            // Heading 90 faces -x (degrees CCW, 0 = +y): straight ahead in the robot's frame is +y.
            TestLevels.AssertNear(RobotPose.DirectionOf(90f), VacuumShape.ToWorld(Vector2.up, 90f));
            TestLevels.AssertNear(Vector2.up, VacuumShape.ToLocal(RobotPose.DirectionOf(90f), 90f));

            var p = new Vector2(0.3f, -0.7f);
            TestLevels.AssertNear(p, VacuumShape.ToWorld(VacuumShape.ToLocal(p, 123f), 123f));
        }

        [Test]
        public void JobFileNames_ParseLikeThePythonChecker()
        {
            void Check(string text, VacuumShapeKind expected)
            {
                Assert.IsTrue(VacuumShape.TryParse(text, out var kind), text);
                Assert.AreEqual(expected, kind, text);
            }

            Check(null, VacuumShapeKind.Round);
            Check("", VacuumShapeKind.Round);
            Check("Round", VacuumShapeKind.Round);
            Check("DShaped", VacuumShapeKind.DShaped);
            Check("d", VacuumShapeKind.DShaped);
            Check("D-shaped", VacuumShapeKind.DShaped);
            Check("Rectangular", VacuumShapeKind.Rectangular);
            Check("rectangle", VacuumShapeKind.Rectangular);
            Assert.IsFalse(VacuumShape.TryParse("triangle", out _));
        }
    }

    /// <summary>
    /// The body in real 2D physics (3.2, 3.3), stepped by hand at the simulation's fixed step. Each test builds a
    /// bare <see cref="VacuumRobot"/> (no controller in edit mode) and drives it through the same
    /// <see cref="VacuumRobot.ApplyMotion"/> its FixedUpdate uses.
    /// </summary>
    public class BodyShapePhysicsTests
    {
        const float Dt = 0.02f;

        /// <summary>Colliders may overlap by the solver's slop and skin; anything deeper is a real penetration.</summary>
        const float OverlapTolerance = 0.015f;

        readonly List<GameObject> created = new List<GameObject>();
        SimulationMode2D savedMode;

        [SetUp]
        public void SetUp()
        {
            savedMode = Physics2D.simulationMode;
            Physics2D.simulationMode = SimulationMode2D.Script;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in created) if (go != null) Object.DestroyImmediate(go);
            created.Clear();
            Physics2D.simulationMode = savedMode;
        }

        VacuumRobot Robot(VacuumShapeKind shape, Vector2 position, float heading)
        {
            var go = new GameObject($"{shape} body");
            created.Add(go);
            var robot = go.AddComponent<VacuumRobot>();
            robot.ShapeKind = shape;
            robot.Body.position = position;
            robot.Body.rotation = heading;
            go.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, 0f, heading));
            Physics2D.SyncTransforms();
            return robot;
        }

        BoxCollider2D Wall(Vector2 centre, Vector2 size)
        {
            var go = new GameObject("Wall");
            created.Add(go);
            go.transform.position = centre;
            var wall = go.AddComponent<BoxCollider2D>();
            wall.size = size;
            Physics2D.SyncTransforms();
            return wall;
        }

        static IEnumerable<Collider2D> BodyColliders(VacuumRobot robot) =>
            robot.GetComponents<Collider2D>().Where(c => c.enabled);

        /// <summary>The deepest overlap between the body and any wall: negative = overlapping.</summary>
        static float Clearance(VacuumRobot robot, IEnumerable<Collider2D> walls)
        {
            float nearest = float.PositiveInfinity;
            foreach (var part in BodyColliders(robot))
                foreach (var wall in walls)
                    nearest = Mathf.Min(nearest, part.Distance(wall).distance);
            return nearest;
        }

        static void Step(VacuumRobot robot, float forward, float turn)
        {
            robot.ApplyMotion(forward, turn, Dt);
            Physics2D.Simulate(Dt);
        }

        // ---- the hitbox -----------------------------------------------------------------------------------------

        [Test]
        public void RoundBody_IsTheOriginalDisc()
        {
            var robot = Robot(VacuumShapeKind.Round, Vector2.zero, 0f);
            Assert.IsNull(robot.GetComponent<BoxCollider2D>(), "no box is ever added to a round body");
            Assert.IsTrue(robot.GetComponent<CircleCollider2D>().enabled);
            Assert.IsTrue(robot.Body.freezeRotation, "a round body turns by MoveRotation, as before");
        }

        [TestCase(VacuumShapeKind.DShaped)]
        [TestCase(VacuumShapeKind.Rectangular)]
        public void Colliders_AreExactlyTheShape_AtTheBodysHeading(VacuumShapeKind kind)
        {
            const float SkinBand = 0.02f; // anything this far outside the outline must be clear of the body
            var robot = Robot(kind, new Vector2(1f, 2f), 30f);
            var shape = robot.Shape;
            Assert.IsFalse(robot.Body.freezeRotation, "a shaped body rotates under the solver");

            // A pin-head probe: a distance query counts the box's rounded corners (a point test would not).
            var probeObject = new GameObject("probe");
            created.Add(probeObject);
            var probeBody = probeObject.AddComponent<Rigidbody2D>();
            probeBody.bodyType = RigidbodyType2D.Kinematic;
            var probe = probeObject.AddComponent<CircleCollider2D>();
            probe.radius = 0.0005f;

            int checkedPoints = 0;
            for (float x = -0.3f; x <= 0.3f; x += 0.01f)
            {
                for (float y = -0.3f; y <= 0.3f; y += 0.01f)
                {
                    var local = new Vector2(x, y);
                    float edge = shape.SignedDistance(local);

                    // Box2D counts colliders as touching within the contact offset (Physics 2D settings, 1 cm): a skin
                    // every body has, the round one included. So skip the edge itself and that skin just outside it.
                    if (edge > -0.004f && edge < SkinBand) continue;

                    probeBody.position = robot.Body.position + VacuumShape.ToWorld(local, 30f);
                    bool hit = BodyColliders(robot).Any(c => c.Distance(probe).isOverlapped);
                    Assert.AreEqual(edge < 0f, hit, $"{kind} at local {local:F2}");
                    checkedPoints++;
                }
            }
            Assert.Greater(checkedPoints, 3000);
        }

        [TestCase(VacuumShapeKind.DShaped)]
        [TestCase(VacuumShapeKind.Rectangular)]
        public void InOpenSpace_ItTurnsAboutItsAxle_AtTheCommandedRate(VacuumShapeKind kind)
        {
            var robot = Robot(kind, new Vector2(3f, -1f), 10f);
            for (int i = 0; i < 50; i++) Step(robot, 0f, 320f);

            Assert.AreEqual(10f + 320f, robot.Body.rotation, 0.05f, "one second at 320°/s, undamped");
            TestLevels.AssertNear(new Vector2(3f, -1f), robot.Body.position, 1e-3f);
        }

        // ---- AT-3.3 ---------------------------------------------------------------------------------------------

        /// <summary>
        /// AT-3.3: a gap the rectangle fits through lengthwise (34 cm wide) but not across (40 cm long). Held side-on
        /// and pushed at the gap, the body is stopped at the gap's mouth and never overlaps the walls; lined up, it
        /// drives through on its own wheels.
        /// </summary>
        [Test]
        public void AT_3_3_Rectangle_PassesTheGapAligned_ButNotAcross()
        {
            const float gap = 0.38f, wallY = 0.5f, thickness = 0.1f;
            var walls = new Collider2D[]
            {
                Wall(new Vector2(-(gap * 0.5f + 0.5f), wallY), new Vector2(1f, thickness)),
                Wall(new Vector2(gap * 0.5f + 0.5f, wallY), new Vector2(1f, thickness)),
            };
            float beyond = wallY + thickness * 0.5f + VacuumShape.RectangleLength * 0.5f;

            // Across: heading 90 puts the long axis across the gap. Pushed straight at it, wheels holding the heading.
            var across = Robot(VacuumShapeKind.Rectangular, Vector2.zero, 90f);
            for (int i = 0; i < 150; i++)
            {
                across.ApplyMotion(0f, 0f, Dt);
                across.Body.linearVelocity = Vector2.up * 0.5f;
                Physics2D.Simulate(Dt);
                Assert.Greater(Clearance(across, walls), -OverlapTolerance, $"step {i}: the body is inside a wall");
            }
            Assert.Less(across.Body.position.y, wallY, "misaligned, the walls stop it at the mouth of the gap");
            Object.DestroyImmediate(across.gameObject);
            Physics2D.SyncTransforms();

            // Lined up: it drives through on its own.
            var aligned = Robot(VacuumShapeKind.Rectangular, Vector2.zero, 0f);
            for (int i = 0; i < 150; i++)
            {
                Step(aligned, 0.5f, 0f);
                Assert.Greater(Clearance(aligned, walls), -OverlapTolerance, $"step {i}: the body is inside a wall");
            }
            Assert.Greater(aligned.Body.position.y, beyond, "aligned, it passes through");
        }

        [Test]
        public void AT_3_3_DShape_PassesTheSameGapStraightOn()
        {
            const float gap = 0.38f, wallY = 0.5f, thickness = 0.1f;
            var walls = new Collider2D[]
            {
                Wall(new Vector2(-(gap * 0.5f + 0.5f), wallY), new Vector2(1f, thickness)),
                Wall(new Vector2(gap * 0.5f + 0.5f, wallY), new Vector2(1f, thickness)),
            };

            var robot = Robot(VacuumShapeKind.DShaped, Vector2.zero, 0f);
            for (int i = 0; i < 150; i++)
            {
                Step(robot, 0.5f, 0f);
                Assert.Greater(Clearance(robot, walls), -OverlapTolerance, $"step {i}");
            }
            Assert.Greater(robot.Body.position.y, wallY + thickness * 0.5f + VacuumShape.DefaultHalfWidth);
        }

        // ---- rotational collision response ------------------------------------------------------------------------

        /// <summary>
        /// A D with its flat front 5 mm from a wall, told to spin. Its corner meets the wall at once: the turn is cut
        /// short on that step, the contact pushes the body off the wall instead of letting it rotate through, and it
        /// never overlaps the wall while it carries on turning.
        /// </summary>
        [Test]
        public void DShape_TurningAgainstAWall_IsStoppedOrPushedOff_NeverRotatesThrough()
        {
            float front = VacuumShape.DefaultHalfWidth;
            var wall = Wall(new Vector2(0f, front + 0.005f + 0.05f), new Vector2(2f, 0.1f));
            var walls = new Collider2D[] { wall };
            var robot = Robot(VacuumShapeKind.DShaped, Vector2.zero, 0f);

            Step(robot, 0f, 320f);
            Assert.Less(robot.Body.rotation, 320f * Dt - 0.4f, "the wall cut the first step's turn short");

            for (int i = 0; i < 50; i++)
            {
                Step(robot, 0f, 320f);
                Assert.Greater(Clearance(robot, walls), -OverlapTolerance, $"step {i}: rotated into the wall");
            }

            Assert.Less(robot.Body.position.y, -0.03f, "the corner pushed the body back off the wall");
            Assert.Greater(robot.Body.rotation, 90f, "and it kept turning as it came free");
        }

        [Test]
        public void Rectangle_SideOnToAWall_TurnsFreeOfIt_WithoutOverlap()
        {
            var wall = Wall(new Vector2(VacuumShape.DefaultHalfWidth + 0.005f + 0.05f, 0f), new Vector2(0.1f, 2f));
            var walls = new Collider2D[] { wall };
            var robot = Robot(VacuumShapeKind.Rectangular, Vector2.zero, 0f);

            for (int i = 0; i < 100; i++)
            {
                Step(robot, 0f, -320f);
                Assert.Greater(Clearance(robot, walls), -OverlapTolerance, $"step {i}");
            }
            Assert.Less(robot.Body.position.x, -0.03f, "pushed away from the wall by its own corners");
        }

        /// <summary>3.2 with a shaped body: the fastest job-file drive (5 m/s, 10 cm a step), at a 2 cm wall's corner.</summary>
        [TestCase(VacuumShapeKind.DShaped)]
        [TestCase(VacuumShapeKind.Rectangular)]
        public void FullSpeed_IntoAThinWallsCorner_NeverTunnels(VacuumShapeKind kind)
        {
            var wall = Wall(new Vector2(0.5f, 1f), new Vector2(1f, 0.02f)); // its left end is at x = 0
            var walls = new Collider2D[] { wall };
            var robot = Robot(kind, new Vector2(0.05f, 0f), 0f);

            for (int i = 0; i < 60; i++)
            {
                Step(robot, 5f, 0f);
                Assert.Greater(Clearance(robot, walls), -0.03f, $"step {i}: inside the wall");
                if (robot.Body.position.x > 0.05f && robot.Body.position.x < 0.9f)
                    Assert.Less(robot.Body.position.y, 1f, $"step {i}: through the wall");
            }
        }

        /// <summary>3.2 for rotation: the fastest job-file turn (2000°/s, 40° a step) with the front 5 mm from a wall.</summary>
        [TestCase(VacuumShapeKind.DShaped)]
        [TestCase(VacuumShapeKind.Rectangular)]
        public void FullTurnRate_AgainstAWall_NeverRotatesThrough(VacuumShapeKind kind)
        {
            var robot = Robot(kind, Vector2.zero, 0f);
            float front = robot.Shape.front;
            var walls = new Collider2D[] { Wall(new Vector2(0f, front + 0.005f + 0.01f), new Vector2(2f, 0.02f)) };

            for (int i = 0; i < 100; i++)
            {
                Step(robot, 0f, 2000f);
                Assert.Greater(Clearance(robot, walls), -0.03f, $"step {i}: inside the wall");
                Assert.Less(robot.Body.position.y, front, $"step {i}: through the wall");
            }
        }

        [TestCase(VacuumShapeKind.DShaped)]
        [TestCase(VacuumShapeKind.Rectangular)]
        public void TheSameCommands_GiveTheSameRun(VacuumShapeKind kind)
        {
            (Vector2 position, float rotation) Run()
            {
                var corner = new Collider2D[]
                {
                    Wall(new Vector2(0f, 1.05f), new Vector2(2f, 0.1f)),
                    Wall(new Vector2(1.05f, 0f), new Vector2(0.1f, 2f)),
                };
                var robot = Robot(kind, new Vector2(0.2f, 0.1f), -30f);
                for (int i = 0; i < 300; i++) Step(robot, i % 60 < 40 ? 0.65f : -0.2f, i % 90 < 30 ? 150f : -40f);
                var result = (robot.Body.position, robot.Body.rotation);

                Object.DestroyImmediate(robot.gameObject);
                foreach (var wall in corner) Object.DestroyImmediate(wall.gameObject);
                Physics2D.SyncTransforms();
                return result;
            }

            var first = Run();
            var second = Run();
            Assert.AreEqual(first.position.x, second.position.x, 0f);
            Assert.AreEqual(first.position.y, second.position.y, 0f);
            Assert.AreEqual(first.rotation, second.rotation, 0f);
        }

        // ---- sensors ------------------------------------------------------------------------------------------------

        /// <summary>The bump look-ahead sweeps the body's own front: the rectangle's reaches 3 cm further than the disc's.</summary>
        [Test]
        public void Whisker_LooksAheadFromTheBodysOwnFront()
        {
            Wall(new Vector2(0f, 0.27f + 0.05f), new Vector2(2f, 0.1f)); // 7 cm ahead of the box, 10 cm ahead of the disc

            bool Bumped(VacuumShapeKind kind)
            {
                var robot = Robot(kind, Vector2.zero, 0f);
                var context = new SensorContext(robot.Body.position, robot.Body.rotation, robot.Shape, robot.Body, null,
                    false, Vector2.zero, 0f, false, 0f, new RandomProvider(1));
                var frame = new SensorFrame();
                new WhiskerSensor().Sense(context, frame);
                Object.DestroyImmediate(robot.gameObject);
                Physics2D.SyncTransforms();
                return frame.bumped;
            }

            Assert.IsFalse(Bumped(VacuumShapeKind.Round), "the disc's front is 10 cm away, beyond the look-ahead");
            Assert.IsTrue(Bumped(VacuumShapeKind.Rectangular), "the box's front is 7 cm away");
        }

        [Test]
        public void ProbeDistance_IsMeasuredFromTheBodysEdge()
        {
            Wall(new Vector2(0f, 1.05f), new Vector2(2f, 0.1f));
            var robot = Robot(VacuumShapeKind.Rectangular, Vector2.zero, 0f);
            Assert.AreEqual(1f - VacuumShape.RectangleLength * 0.5f, robot.ProbeDistance(Vector2.up, 3f), 1e-3f);
        }
    }

    /// <summary>The shape setting, from the Configuration screen and job files to the run.</summary>
    public class BodyShapeSettingsTests
    {
        [Test]
        public void RoundIsTheDefault_Everywhere()
        {
            Assert.AreEqual(VacuumShapeKind.Round, VacuumSettings.Defaults().shape);
            Assert.AreEqual(VacuumShapeKind.Round, new RunSettings().shape);
            Assert.AreEqual(VacuumShapeKind.Round, new VacuumSpec().TryShape(out var kind) ? kind : (VacuumShapeKind)(-1));
            Assert.AreEqual(VacuumShapeKind.Round, JsonUtility.FromJson<VacuumSettings>("{\"driveSpeed\":0.5}").shape,
                "a configuration saved before shapes existed loads as Round");
        }

        [Test]
        public void TheShape_ReachesTheRunsBuildOptions_AndConfigJson()
        {
            var settings = new RunSettings { shape = VacuumShapeKind.DShaped, randomSeed = false };
            var resolved = settings.Resolved();
            Assert.AreEqual(VacuumShapeKind.DShaped, resolved.shape);
            Assert.AreEqual(VacuumShapeKind.DShaped, SimulationRunner.Options.From(resolved, headless: true).shape);

            var copy = JsonUtility.FromJson<RunSettings>(JsonUtility.ToJson(resolved));
            Assert.AreEqual(VacuumShapeKind.DShaped, copy.shape);

            var vacuum = JsonUtility.FromJson<VacuumSettings>(JsonUtility.ToJson(new VacuumSettings { shape = VacuumShapeKind.Rectangular }));
            Assert.AreEqual(VacuumShapeKind.Rectangular, vacuum.shape);
        }

        [Test]
        public void ARobot_RecordsTheShapeItHas()
        {
            var go = new GameObject("robot");
            try
            {
                var robot = go.AddComponent<VacuumRobot>();
                robot.ShapeKind = VacuumShapeKind.Rectangular;
                Assert.AreEqual(VacuumShapeKind.Rectangular, VacuumSettings.From(robot, null).shape);

                var settings = new RunSettings();
                settings.RecordVacuumFrom(robot, null);
                Assert.AreEqual(VacuumShapeKind.Rectangular, settings.shape);

                new VacuumSettings { shape = VacuumShapeKind.DShaped, driveSpeed = 0.5f, reverseSpeed = 0.3f, turnSpeed = 200f }.ApplyTo(robot, null);
                Assert.AreEqual(VacuumShapeKind.Rectangular, robot.ShapeKind, "mid-run settings never change the body");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        const string Room = @"{ ""name"": ""Box"", ""floor"": ""Hardwood"",
            ""outline"": [ {""x"":0,""y"":0}, {""x"":4,""y"":0}, {""x"":4,""y"":3}, {""x"":0,""y"":3} ] }";

        static string Job(string configurations) => @"{
            ""schemaVersion"": 1,
            ""campaignName"": ""shapes"",
            ""floorPlans"": [ { ""name"": ""Plan A"", ""rooms"": [ " + Room + @" ], ""robotSpawn"": {""x"":1,""y"":1} } ],
            ""sweep"": { ""algorithms"": [""RandomBounce""], ""seedStart"": 1, ""seedCount"": 1 },
            ""configurations"": " + configurations + "}";

        [Test]
        public void JobFiles_ChooseTheShape_RoundWhenMissing_RejectUnknownNames()
        {
            var job = HeadlessJob.Parse(Job(@"[ { ""name"": ""plain"" }, { ""name"": ""d"", ""vacuum"": { ""shape"": ""D-shaped"" } },
                { ""name"": ""box"", ""vacuum"": { ""shape"": ""rectangular"" } } ]"), out var errors);
            Assert.IsNotNull(job, string.Join("\n", errors));
            Assert.AreEqual(VacuumShapeKind.Round, job.configurations[0].ToVacuumSettings().shape);
            Assert.AreEqual(VacuumShapeKind.DShaped, job.configurations[1].ToVacuumSettings().shape);
            Assert.AreEqual(VacuumShapeKind.Rectangular, job.configurations[2].ToVacuumSettings().shape);

            Assert.IsNull(HeadlessJob.Parse(Job(@"[ { ""name"": ""bad"", ""vacuum"": { ""shape"": ""triangle"" } } ]"), out errors));
            Assert.IsTrue(errors.Any(e => e.Contains("vacuum.shape")), string.Join("\n", errors));
        }
    }
}
