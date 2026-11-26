using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>
    /// A ginger tabby seen from above, in metres, nose toward +y. <see cref="Silhouette"/> is the cat's solid
    /// outline (its collider in the simulation); every other layer is drawn inside it, back to front.
    /// The simulation builds meshes from <see cref="Layers"/>; the floor-plan editor paints the same layers.
    /// </summary>
    public static class CatShape
    {
        public struct Layer
        {
            public string name;
            public Color color;
            public Vector2[] vertices;
            /// <summary>Counter-clockwise triangles into <see cref="vertices"/>.</summary>
            public int[] triangles;
        }

        public const string SilhouetteLayerName = "Cat silhouette";

        public static readonly Color OutlineColor = new Color(0.30f, 0.17f, 0.10f);
        public static readonly Color FurColor = new Color(0.88f, 0.54f, 0.27f);
        public static readonly Color StripeColor = new Color(0.66f, 0.34f, 0.14f);
        public static readonly Color CreamColor = new Color(0.99f, 0.87f, 0.71f);
        public static readonly Color PinkColor = new Color(0.94f, 0.58f, 0.60f);
        public static readonly Color EyeColor = new Color(0.60f, 0.82f, 0.28f);
        public static readonly Color PupilColor = new Color(0.13f, 0.09f, 0.07f);

        const float OutlineWidth = 0.007f;

        // Right half of the head and body, nose to rump. The left half is its mirror image.
        static readonly Vector2[] RightSide =
        {
            new Vector2(0.026f, 0.233f), new Vector2(0.045f, 0.221f),
            new Vector2(0.052f, 0.214f), new Vector2(0.082f, 0.226f), new Vector2(0.066f, 0.180f), // ear
            new Vector2(0.064f, 0.160f), new Vector2(0.055f, 0.137f), new Vector2(0.046f, 0.120f), // cheek to neck
            new Vector2(0.057f, 0.100f), new Vector2(0.078f, 0.066f), new Vector2(0.092f, 0.019f),
            new Vector2(0.095f, -0.033f), new Vector2(0.086f, -0.083f), new Vector2(0.067f, -0.126f),
            new Vector2(0.040f, -0.156f),
        };

        static readonly Vector2 Nose = new Vector2(0f, 0.238f);
        static readonly Vector2[] RightEar = { RightSide[2], RightSide[3], RightSide[4] };

        // The tail leaves the rump and curls to the cat's left.
        static readonly Vector2[] TailSpine =
        {
            new Vector2(0f, -0.160f), new Vector2(-0.005f, -0.205f), new Vector2(-0.025f, -0.240f),
            new Vector2(-0.060f, -0.265f), new Vector2(-0.100f, -0.272f), new Vector2(-0.135f, -0.262f),
            new Vector2(-0.155f, -0.245f),
        };
        const float TailBaseWidth = 0.032f;
        const float TailTipWidth = 0.022f;

        // Body ellipse the back stripes are fitted to.
        static readonly Vector2 BodyCenter = new Vector2(0f, -0.02f);
        static readonly Vector2 BodyRadii = new Vector2(0.095f, 0.15f);

        public static readonly Vector2[] Silhouette = BuildSilhouette();

        static Layer[] layers;
        public static IReadOnlyList<Layer> Layers => layers ??= BuildLayers();

        static Vector2 Mirror(Vector2 point) => new Vector2(-point.x, point.y);

        static float TailWidth(int index) =>
            Mathf.Lerp(TailBaseWidth, TailTipWidth, index / (float)(TailSpine.Length - 1));

        static Vector2 TailTangent(int index)
        {
            Vector2 previous = TailSpine[Mathf.Max(0, index - 1)];
            Vector2 next = TailSpine[Mathf.Min(TailSpine.Length - 1, index + 1)];
            return (next - previous).normalized;
        }

        // Toward the cat's right as you walk down the tail from the rump.
        static Vector2 TailRight(int index)
        {
            Vector2 t = TailTangent(index);
            return new Vector2(-t.y, t.x);
        }

        static Vector2[] BuildSilhouette()
        {
            var points = new List<Vector2> { Nose };
            points.AddRange(RightSide);

            // Down the tail's right edge, round the tip, back up its left edge.
            for (int i = 0; i < TailSpine.Length; i++)
                points.Add(TailSpine[i] + TailRight(i) * (TailWidth(i) * 0.5f));
            int last = TailSpine.Length - 1;
            points.Add(TailSpine[last] + TailTangent(last) * (TailWidth(last) * 0.5f));
            for (int i = last; i >= 0; i--)
                points.Add(TailSpine[i] - TailRight(i) * (TailWidth(i) * 0.5f));

            for (int i = RightSide.Length - 1; i >= 0; i--)
                points.Add(Mirror(RightSide[i]));

            return points.ToArray();
        }

        static Layer[] BuildLayers()
        {
            var result = new List<Layer>();

            result.Add(new Layer
            {
                name = SilhouetteLayerName,
                color = OutlineColor,
                vertices = Silhouette,
                triangles = CounterClockwise(Silhouette, Poly2D.Triangulate(Silhouette)),
            });

            Vector2[] fur = Inset(Silhouette, OutlineWidth);
            result.Add(new Layer
            {
                name = "Cat fur",
                color = FurColor,
                vertices = fur,
                triangles = CounterClockwise(fur, Poly2D.Triangulate(fur)),
            });

            var stripes = new Builder();
            stripes.Ribbon(new Vector2(0f, 0.085f), new Vector2(0f, -0.130f), 0.012f); // along the spine
            foreach (float y in new[] { 0.045f, -0.005f, -0.055f, -0.100f })
            {
                float halfWidth = BodyHalfWidth(y - 0.018f) - 0.025f;
                stripes.Ribbon(new Vector2(-halfWidth, y - 0.018f), new Vector2(-0.006f, y), 0.013f);
                stripes.Ribbon(new Vector2(0.006f, y), new Vector2(halfWidth, y - 0.018f), 0.013f);
            }
            stripes.Ribbon(new Vector2(0f, 0.160f), new Vector2(0f, 0.192f), 0.008f); // forehead "M"
            stripes.Ribbon(new Vector2(-0.020f, 0.166f), new Vector2(-0.014f, 0.190f), 0.007f);
            stripes.Ribbon(new Vector2(0.020f, 0.166f), new Vector2(0.014f, 0.190f), 0.007f);
            for (int i = 2; i <= 4; i++)
            {
                // Rounded caps add half the band's width at each end, so stop short to stay on the fur.
                const float ringWidth = 0.007f;
                float reach = Mathf.Max(0.001f, TailWidth(i) * 0.5f - OutlineWidth - ringWidth * 0.5f);
                stripes.Ribbon(TailSpine[i] - TailRight(i) * reach, TailSpine[i] + TailRight(i) * reach, ringWidth);
            }
            result.Add(stripes.ToLayer("Cat stripes", StripeColor));

            var cream = new Builder();
            cream.Ellipse(new Vector2(-0.012f, 0.218f), new Vector2(0.015f, 0.010f), 14); // muzzle
            cream.Ellipse(new Vector2(0.012f, 0.218f), new Vector2(0.015f, 0.010f), 14);
            cream.Ellipse(TailSpine[TailSpine.Length - 1], new Vector2(0.007f, 0.007f), 10); // tail tip
            result.Add(cream.ToLayer("Cat markings", CreamColor));

            var pink = new Builder();
            AddInnerEar(pink, RightEar);
            AddInnerEar(pink, new[] { Mirror(RightEar[0]), Mirror(RightEar[1]), Mirror(RightEar[2]) });
            pink.Triangle(new Vector2(-0.008f, 0.231f), new Vector2(0.008f, 0.231f), new Vector2(0f, 0.222f)); // nose
            result.Add(pink.ToLayer("Cat ears and nose", PinkColor));

            var eyes = new Builder();
            eyes.Ellipse(new Vector2(-0.025f, 0.203f), new Vector2(0.011f, 0.008f), 16);
            eyes.Ellipse(new Vector2(0.025f, 0.203f), new Vector2(0.011f, 0.008f), 16);
            result.Add(eyes.ToLayer("Cat eyes", EyeColor));

            var pupils = new Builder();
            pupils.Ellipse(new Vector2(-0.025f, 0.203f), new Vector2(0.0035f, 0.0072f), 12);
            pupils.Ellipse(new Vector2(0.025f, 0.203f), new Vector2(0.0035f, 0.0072f), 12);
            result.Add(pupils.ToLayer("Cat pupils", PupilColor));

            return result.ToArray();
        }

        static float BodyHalfWidth(float y)
        {
            float t = (y - BodyCenter.y) / BodyRadii.y;
            return BodyRadii.x * Mathf.Sqrt(Mathf.Max(0f, 1f - t * t));
        }

        static void AddInnerEar(Builder builder, Vector2[] ear)
        {
            Vector2 center = (ear[0] + ear[1] + ear[2]) / 3f;
            builder.Triangle(
                center + (ear[0] - center) * 0.5f,
                center + (ear[1] - center) * 0.5f,
                center + (ear[2] - center) * 0.5f);
        }

        /// <summary>The outline moved inward by <paramref name="distance"/>, with sharp corners' mitres capped.</summary>
        static Vector2[] Inset(Vector2[] outline, float distance)
        {
            int n = outline.Length;
            float sign = Poly2D.SignedArea(outline) > 0f ? 1f : -1f;
            var result = new Vector2[n];

            for (int i = 0; i < n; i++)
            {
                Vector2 previous = outline[(i + n - 1) % n];
                Vector2 current = outline[i];
                Vector2 next = outline[(i + 1) % n];

                Vector2 inA = InwardNormal(current - previous, sign);
                Vector2 inB = InwardNormal(next - current, sign);
                Vector2 direction = inA + inB;
                if (direction.sqrMagnitude < 1e-8f) direction = inA;
                direction.Normalize();

                float cos = Mathf.Max(0.2f, Vector2.Dot(direction, inA));
                float miter = Mathf.Min(distance / cos, distance * 3f);
                result[i] = current + direction * miter;
            }

            return result;
        }

        static Vector2 InwardNormal(Vector2 edge, float sign)
        {
            edge.Normalize();
            return new Vector2(-edge.y, edge.x) * sign;
        }

        static int[] CounterClockwise(Vector2[] vertices, int[] triangles)
        {
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                if (Poly2D.Cross(vertices[triangles[i + 1]] - vertices[triangles[i]],
                                 vertices[triangles[i + 2]] - vertices[triangles[i]]) < 0f)
                    (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
            }
            return triangles;
        }

        sealed class Builder
        {
            readonly List<Vector2> vertices = new List<Vector2>();
            readonly List<int> triangles = new List<int>();

            public Layer ToLayer(string name, Color color)
            {
                var points = vertices.ToArray();
                return new Layer { name = name, color = color, vertices = points, triangles = CounterClockwise(points, triangles.ToArray()) };
            }

            public void Triangle(Vector2 a, Vector2 b, Vector2 c)
            {
                int first = vertices.Count;
                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);
                triangles.Add(first);
                triangles.Add(first + 1);
                triangles.Add(first + 2);
            }

            public void Ellipse(Vector2 center, Vector2 radii, int sides)
            {
                int first = vertices.Count;
                vertices.Add(center);
                for (int i = 0; i < sides; i++)
                {
                    float angle = i * Mathf.PI * 2f / sides;
                    vertices.Add(center + new Vector2(Mathf.Cos(angle) * radii.x, Mathf.Sin(angle) * radii.y));
                }

                for (int i = 0; i < sides; i++)
                {
                    triangles.Add(first);
                    triangles.Add(first + 1 + i);
                    triangles.Add(first + 1 + (i + 1) % sides);
                }
            }

            /// <summary>A straight band of <paramref name="width"/> from start to end, with rounded caps.</summary>
            public void Ribbon(Vector2 start, Vector2 end, float width)
            {
                Vector2 along = (end - start).normalized;
                Vector2 side = new Vector2(-along.y, along.x) * (width * 0.5f);
                int first = vertices.Count;
                vertices.Add(start + side);
                vertices.Add(start - side);
                vertices.Add(end - side);
                vertices.Add(end + side);
                triangles.Add(first);
                triangles.Add(first + 1);
                triangles.Add(first + 2);
                triangles.Add(first);
                triangles.Add(first + 2);
                triangles.Add(first + 3);

                Ellipse(start, Vector2.one * (width * 0.5f), 8);
                Ellipse(end, Vector2.one * (width * 0.5f), 8);
            }
        }
    }
}
