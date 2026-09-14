using System;
using System.Collections.Generic;
using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Tests
{
    /// <summary>
    /// Shared fixtures. Levels are ScriptableObjects, so everything created here is torn down with
    /// DestroyImmediate: Object.Destroy only logs an error in edit mode, which fails the test.
    /// </summary>
    static class TestLevels
    {
        public const float Tolerance = 1e-4f;

        public static LevelData NewLevel(Action<LevelData> populate = null)
        {
            var level = ScriptableObject.CreateInstance<LevelData>();
            level.hideFlags = HideFlags.DontSave;
            populate?.Invoke(level);
            return level;
        }

        public static LevelSnapshot Snapshot(string name, Action<LevelData> populate)
        {
            var scratch = NewLevel(populate);
            try
            {
                return LevelSnapshot.FromLevel(scratch, name);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scratch);
            }
        }

        /// <summary>Axis-aligned, counter-clockwise square with its lower-left corner at <paramref name="origin"/>.</summary>
        public static List<Vector2> Square(Vector2 origin, float size = 1f) => new List<Vector2>
        {
            origin,
            origin + new Vector2(size, 0f),
            origin + new Vector2(size, size),
            origin + new Vector2(0f, size),
        };

        public static void AssertNear(Vector2 expected, Vector2 actual, float tolerance = Tolerance)
        {
            Assert.That(Vector2.Distance(expected, actual), Is.LessThanOrEqualTo(tolerance),
                $"Expected {expected:F4} but was {actual:F4}");
        }
    }
}
