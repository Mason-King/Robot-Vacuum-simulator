using System.Collections.Generic;
using NUnit.Framework;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.Tests
{
    public class Poly2DTests
    {
        // An L-shape: a 2 × 2 square with the top-right 1 × 1 quadrant missing. Area 3.
        static List<Vector2> LShape() => new List<Vector2>
        {
            new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 1),
            new Vector2(1, 1), new Vector2(1, 2), new Vector2(0, 2),
        };

        [Test]
        public void SignedArea_IsPositiveCounterClockwise_NegativeClockwise()
        {
            var square = TestLevels.Square(Vector2.zero, 2f);
            Assert.AreEqual(4f, Poly2D.SignedArea(square), TestLevels.Tolerance);

            square.Reverse();
            Assert.AreEqual(-4f, Poly2D.SignedArea(square), TestLevels.Tolerance);
            Assert.AreEqual(4f, Poly2D.Area(square), TestLevels.Tolerance);
        }

        [Test]
        public void Area_OfDegenerateInput_IsZero()
        {
            Assert.AreEqual(0f, Poly2D.Area(null));
            Assert.AreEqual(0f, Poly2D.Area(new List<Vector2> { Vector2.zero, Vector2.one }));
        }

        [Test]
        public void Area_OfConcaveOutline()
        {
            Assert.AreEqual(3f, Poly2D.Area(LShape()), TestLevels.Tolerance);
        }

        [Test]
        public void Centroid_OfSquare_IsItsMiddle()
        {
            TestLevels.AssertNear(new Vector2(4f, 6f), Poly2D.Centroid(TestLevels.Square(new Vector2(3f, 5f), 2f)));
        }

        [Test]
        public void Centroid_FallsBackToAverage_ForFewOrCollinearPoints()
        {
            Assert.AreEqual(Vector2.zero, Poly2D.Centroid(new List<Vector2>()));
            TestLevels.AssertNear(new Vector2(1f, 0f),
                Poly2D.Centroid(new List<Vector2> { Vector2.zero, new Vector2(2f, 0f) }));
            TestLevels.AssertNear(new Vector2(1f, 0f),
                Poly2D.Centroid(new List<Vector2> { Vector2.zero, new Vector2(1f, 0f), new Vector2(2f, 0f) }));
        }

        [Test]
        public void ContainsPoint_RespectsConcavity()
        {
            var shape = LShape();

            Assert.IsTrue(Poly2D.ContainsPoint(shape, new Vector2(0.5f, 0.5f)));
            Assert.IsTrue(Poly2D.ContainsPoint(shape, new Vector2(0.5f, 1.5f)));
            Assert.IsFalse(Poly2D.ContainsPoint(shape, new Vector2(1.5f, 1.5f)), "the notch is outside");
            Assert.IsFalse(Poly2D.ContainsPoint(shape, new Vector2(-1f, 0.5f)));
            Assert.IsFalse(Poly2D.ContainsPoint(new List<Vector2> { Vector2.zero, Vector2.one }, Vector2.zero));
        }

        [Test]
        public void DistanceToSegment_ReportsDistanceAndClampedPosition()
        {
            Vector2 a = Vector2.zero, b = new Vector2(4f, 0f);

            Assert.AreEqual(2f, Poly2D.DistanceToSegment(new Vector2(1f, 2f), a, b, out float t), TestLevels.Tolerance);
            Assert.AreEqual(0.25f, t, TestLevels.Tolerance);

            Assert.AreEqual(5f, Poly2D.DistanceToSegment(new Vector2(-3f, 4f), a, b, out t), TestLevels.Tolerance);
            Assert.AreEqual(0f, t);

            Assert.AreEqual(1f, Poly2D.DistanceToSegment(new Vector2(5f, 0f), a, b, out t), TestLevels.Tolerance);
            Assert.AreEqual(1f, t);
        }

        [Test]
        public void DistanceToSegment_OfZeroLengthSegment_IsPointDistance()
        {
            Assert.AreEqual(5f, Poly2D.DistanceToSegment(new Vector2(3f, 4f), Vector2.zero, Vector2.zero, out float t), TestLevels.Tolerance);
            Assert.AreEqual(0f, t);
        }

        [Test]
        public void Bounds_EnclosesEveryPoint()
        {
            var bounds = Poly2D.Bounds(new List<Vector2> { new Vector2(-1f, 3f), new Vector2(2f, -2f), new Vector2(0.5f, 1f) });

            Assert.AreEqual(new Rect(-1f, -2f, 3f, 5f), bounds);
            Assert.AreEqual(new Rect(), Poly2D.Bounds(null));
        }

        [Test]
        public void IsSimple_DetectsSelfIntersection()
        {
            Assert.IsTrue(Poly2D.IsSimple(TestLevels.Square(Vector2.zero)));
            Assert.IsTrue(Poly2D.IsSimple(LShape()));
            Assert.IsTrue(Poly2D.IsSimple(new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up }));

            var bowtie = new List<Vector2> { new Vector2(0, 0), new Vector2(2, 2), new Vector2(2, 0), new Vector2(0, 2) };
            Assert.IsFalse(Poly2D.IsSimple(bowtie));

            Assert.IsFalse(Poly2D.IsSimple(new List<Vector2> { Vector2.zero, Vector2.one }));
            Assert.IsFalse(Poly2D.IsSimple(null));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Triangulate_ConcaveOutline_CoversExactlyItsArea(bool clockwise)
        {
            var shape = LShape();
            if (clockwise) shape.Reverse();

            int[] triangles = Poly2D.Triangulate(shape);

            Assert.AreEqual((shape.Count - 2) * 3, triangles.Length);

            float total = 0f;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                var triangle = new List<Vector2> { shape[triangles[i]], shape[triangles[i + 1]], shape[triangles[i + 2]] };
                float signed = Poly2D.SignedArea(triangle);

                Assert.Greater(signed, 0f, "triangles should come out counter-clockwise");
                total += signed;
            }

            Assert.AreEqual(3f, total, TestLevels.Tolerance);
        }

        [Test]
        public void Triangulate_TooFewPoints_ReturnsEmpty()
        {
            Assert.IsEmpty(Poly2D.Triangulate(null));
            Assert.IsEmpty(Poly2D.Triangulate(new List<Vector2> { Vector2.zero, Vector2.one }));
        }

        [Test]
        public void PointInTriangle_IncludesEdges()
        {
            Vector2 a = Vector2.zero, b = new Vector2(2f, 0f), c = new Vector2(0f, 2f);

            Assert.IsTrue(Poly2D.PointInTriangle(new Vector2(0.5f, 0.5f), a, b, c));
            Assert.IsTrue(Poly2D.PointInTriangle(new Vector2(1f, 0f), a, b, c));
            Assert.IsFalse(Poly2D.PointInTriangle(new Vector2(2f, 2f), a, b, c));
        }
    }
}
