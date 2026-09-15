using System;
using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    public enum ObstacleKind { Sofa, Bed, Table, Chair, Cabinet, Box }

    /// <summary>
    /// A piece of furniture standing on the floor, as a rotated rectangle. Furniture that blocks the
    /// vacuum gets a collider and the floor beneath it drops out of coverage; furniture the vacuum
    /// passes under (a table, a raised bed) is drawn but driven through and cleaned beneath.
    /// </summary>
    [Serializable]
    public class Obstacle
    {
        public const float MinSize = 0.1f;

        public string name = "Box";
        public ObstacleKind kind = ObstacleKind.Box;
        public Vector2 center;
        public Vector2 size = new Vector2(0.6f, 0.6f);

        [Tooltip("Degrees counter-clockwise.")]
        public float rotation;

        [Tooltip("False when the vacuum fits underneath, like a table or a raised bed.")]
        public bool blocksVacuum = true;

        public List<Vector2> Corners() => LevelData.RectangleOutline(center, size, rotation);
        public bool Contains(Vector2 point) => Poly2D.ContainsPoint(Corners(), point);
        public Rect Bounds => Poly2D.Bounds(Corners());

        public Obstacle Clone() => (Obstacle)MemberwiseClone();

        // Signs of each corner in Corners() order, in the obstacle's own frame.
        static readonly Vector2[] CornerSigns = { new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(1f, 1f), new Vector2(-1f, 1f) };

        static void Axes(float rotation, out Vector2 x, out Vector2 y)
        {
            float radians = rotation * Mathf.Deg2Rad;
            x = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
            y = new Vector2(-x.y, x.x);
        }

        /// <summary>
        /// Moves corner <paramref name="corner"/> (in <see cref="Corners"/> order) to <paramref name="point"/>,
        /// keeping the opposite corner and the rotation fixed. The shape never turns inside out.
        /// </summary>
        public static void ResizeFromCorner(Vector2 center, Vector2 size, float rotation, int corner, Vector2 point,
            out Vector2 newCenter, out Vector2 newSize)
        {
            Axes(rotation, out Vector2 x, out Vector2 y);
            Vector2 sign = CornerSigns[corner];
            Vector2 opposite = center - x * (sign.x * size.x * 0.5f) - y * (sign.y * size.y * 0.5f);

            Vector2 delta = point - opposite;
            newSize = new Vector2(
                Mathf.Max(Vector2.Dot(delta, x) * sign.x, MinSize),
                Mathf.Max(Vector2.Dot(delta, y) * sign.y, MinSize));
            newCenter = opposite + x * (sign.x * newSize.x * 0.5f) + y * (sign.y * newSize.y * 0.5f);
        }

        /// <summary>
        /// Moves side <paramref name="side"/> to <paramref name="point"/>, keeping the opposite side fixed. Sides
        /// run between corner <c>side</c> and the next: 0 is −y, 1 is +x, 2 is +y, 3 is −x in the obstacle's frame.
        /// </summary>
        public static void ResizeFromSide(Vector2 center, Vector2 size, float rotation, int side, Vector2 point,
            out Vector2 newCenter, out Vector2 newSize)
        {
            Axes(rotation, out Vector2 x, out Vector2 y);
            bool acrossY = side % 2 == 0;
            Vector2 axis = acrossY ? y : x;
            float sign = side == 0 || side == 3 ? -1f : 1f;
            float length = acrossY ? size.y : size.x;

            Vector2 opposite = center - axis * (sign * length * 0.5f);
            float next = Mathf.Max(Vector2.Dot(point - opposite, axis) * sign, MinSize);

            newCenter = opposite + axis * (sign * next * 0.5f);
            newSize = acrossY ? new Vector2(size.x, next) : new Vector2(next, size.y);
        }

        public static string DefaultName(ObstacleKind kind) => kind.ToString();

        /// <summary>A typical footprint for the kind, in metres.</summary>
        public static Vector2 DefaultSize(ObstacleKind kind) => kind switch
        {
            ObstacleKind.Sofa => new Vector2(2f, 0.9f),
            ObstacleKind.Bed => new Vector2(1.6f, 2f),
            ObstacleKind.Table => new Vector2(1.4f, 0.8f),
            ObstacleKind.Chair => new Vector2(0.5f, 0.5f),
            ObstacleKind.Cabinet => new Vector2(1f, 0.45f),
            _ => new Vector2(0.6f, 0.6f),
        };

        /// <summary>Whether the vacuum usually can't get under this kind of furniture.</summary>
        public static bool DefaultBlocks(ObstacleKind kind) =>
            kind != ObstacleKind.Table && kind != ObstacleKind.Bed && kind != ObstacleKind.Chair;

        public static Color ColorOf(ObstacleKind kind) => kind switch
        {
            ObstacleKind.Sofa => new Color(0.47f, 0.42f, 0.56f),
            ObstacleKind.Bed => new Color(0.46f, 0.56f, 0.68f),
            ObstacleKind.Table => new Color(0.56f, 0.40f, 0.27f),
            ObstacleKind.Chair => new Color(0.66f, 0.50f, 0.33f),
            ObstacleKind.Cabinet => new Color(0.40f, 0.33f, 0.28f),
            _ => new Color(0.58f, 0.56f, 0.52f),
        };
    }
}
