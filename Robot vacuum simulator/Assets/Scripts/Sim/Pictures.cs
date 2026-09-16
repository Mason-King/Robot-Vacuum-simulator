using System;
using System.IO;
using UnityEngine;

namespace RobotVacuum.Sim
{
    public enum PictureKind { Heart, Smiley, Invader, Image }

    /// <summary>
    /// A small shaded bitmap for the Picture movement pattern. Each pixel's shade runs from 0 (leave the
    /// floor dirty) to 1 (clean it completely); the coverage heatmap turns those shades into colour.
    /// </summary>
    public sealed class PixelPicture
    {
        readonly float[] shades; // row 0 is the top

        PixelPicture(string name, int width, int height, float[] shades)
        {
            Name = name;
            Width = width;
            Height = height;
            this.shades = shades;
        }

        public string Name { get; }
        public int Width { get; }
        public int Height { get; }

        public float this[int x, int y] => shades[y * Width + x];

        /// <summary>
        /// Builds a picture from rows of text, top row first. <c>#</c> is fully clean, <c>+</c> 0.6,
        /// <c>o</c> 0.4, <c>.</c> 0.15, and anything else leaves the floor alone. Short rows are padded.
        /// </summary>
        public static PixelPicture Parse(string name, params string[] rows)
        {
            int width = 0;
            foreach (var row in rows) width = Mathf.Max(width, row.Length);

            var shades = new float[width * rows.Length];
            for (int y = 0; y < rows.Length; y++)
                for (int x = 0; x < rows[y].Length; x++)
                    shades[y * width + x] = ShadeOf(rows[y][x]);

            return new PixelPicture(name, width, rows.Length, shades);
        }

        /// <summary>
        /// Builds a picture from an image: brighter pixels come out cleaner (greener on the heatmap) and
        /// transparent ones leave the floor alone. Scaled down so the longer side is at most <paramref name="maxSize"/>.
        /// </summary>
        public static PixelPicture FromTexture(string name, Texture2D texture, int maxSize = 256)
        {
            float scale = Mathf.Min(1f, maxSize / (float)Mathf.Max(texture.width, texture.height));
            int width = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));

            var shades = new float[width * height];
            for (int y = 0; y < height; y++)
            {
                float v = 1f - (y + 0.5f) / height; // row 0 is the top; textures count from the bottom
                for (int x = 0; x < width; x++)
                {
                    Color pixel = texture.GetPixelBilinear((x + 0.5f) / width, v);
                    float luminance = 0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b;
                    shades[y * width + x] = Mathf.Clamp01(luminance * pixel.a);
                }
            }

            return new PixelPicture(name, width, height, shades);
        }

        static float ShadeOf(char symbol) => symbol switch
        {
            '#' => 1f,
            '+' => 0.6f,
            'o' => 0.4f,
            '.' => 0.15f,
            _ => 0f,
        };

        /// <summary>The shade under <paramref name="point"/> with the picture stretched over <paramref name="canvas"/>, or 0 outside it.</summary>
        public float Sample(Rect canvas, Vector2 point)
        {
            if (Width == 0 || Height == 0 || !canvas.Contains(point)) return 0f;

            int x = Mathf.Clamp(Mathf.FloorToInt((point.x - canvas.xMin) / canvas.width * Width), 0, Width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((canvas.yMax - point.y) / canvas.height * Height), 0, Height - 1);
            return this[x, y];
        }
    }

    /// <summary>The pictures the vacuum knows how to draw. <see cref="Names"/> follows <see cref="PictureKind"/>.</summary>
    public static class Pictures
    {
        public static readonly string[] Names = { "Heart", "Smiley", "Invader", "Image…" };

        /// <summary>The image last loaded with <see cref="LoadImage"/>, or null.</summary>
        public static PixelPicture CustomImage { get; private set; }

        /// <summary>A picture by kind. <see cref="PictureKind.Image"/> falls back to the heart until an image is loaded.</summary>
        public static PixelPicture Get(PictureKind kind) => kind switch
        {
            PictureKind.Smiley => Smiley,
            PictureKind.Invader => Invader,
            PictureKind.Image => CustomImage ?? Heart,
            _ => Heart,
        };

        /// <summary>Image file extensions the app will read.</summary>
        public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

        /// <summary>Where a picture asked for by name is looked up: what ships with the app, then what the player added.</summary>
        public static string[] SearchFolders =>
            new[] { Application.streamingAssetsPath, Path.Combine(Application.persistentDataPath, "Images") };

        static readonly System.Collections.Generic.Dictionary<string, PixelPicture> named =
            new System.Collections.Generic.Dictionary<string, PixelPicture>();

        /// <summary>
        /// A picture that comes with the app, by file name without its extension (James's photo, say).
        /// Looked up in <see cref="SearchFolders"/> and kept after the first load; null when there is no such file.
        /// </summary>
        public static PixelPicture LoadNamed(string baseName)
        {
            if (named.TryGetValue(baseName, out var cached)) return cached;

            PixelPicture picture = null;
            foreach (string folder in SearchFolders)
            {
                string path = FindNamedFile(folder, baseName);
                if (path == null) continue;

                picture = LoadImageFile(path);
                if (picture != null) break;
            }

            named[baseName] = picture;
            return picture;
        }

        /// <summary>
        /// An image in <paramref name="folder"/> called <paramref name="baseName"/>, whatever its capitalisation
        /// or image extension (James.JPEG counts as james), or null when there is none.
        /// </summary>
        public static string FindNamedFile(string folder, string baseName)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;

            foreach (string path in Directory.GetFiles(folder))
            {
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), baseName, StringComparison.OrdinalIgnoreCase)) continue;

                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (Array.IndexOf(Extensions, extension) >= 0) return path;
            }

            return null;
        }

        /// <summary>Forgets images loaded by name, so a replaced file is read again.</summary>
        public static void ClearNamedImages() => named.Clear();

        /// <summary>
        /// Reads a PNG or JPG and makes it the <see cref="PictureKind.Image"/> picture. Returns null and changes
        /// nothing when the file can't be read as an image.
        /// </summary>
        public static PixelPicture LoadImage(string path)
        {
            var picture = LoadImageFile(path);
            if (picture != null) CustomImage = picture;
            return picture;
        }

        static PixelPicture LoadImageFile(string path)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception)
            {
                return null;
            }

            var texture = new Texture2D(2, 2);
            try
            {
                if (!texture.LoadImage(bytes)) return null;

                return PixelPicture.FromTexture(Path.GetFileNameWithoutExtension(path), texture);
            }
            finally
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
                else UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        public static void ClearCustomImage() => CustomImage = null;

        static readonly PixelPicture Heart = PixelPicture.Parse("Heart",
            "  ...     ...  ",
            " .....   ..... ",
            "...+... .......",
            "..++...........",
            "..+............",
            "...............",
            " ............. ",
            "  ...........  ",
            "   .........   ",
            "    .......    ",
            "     .....     ",
            "      ...      ",
            "       .       ");

        static readonly PixelPicture Smiley = PixelPicture.Parse("Smiley",
            "     oooooo     ",
            "   oo++++++oo   ",
            "  o++++++++++o  ",
            " o++++++++++++o ",
            " o+++  ++  +++o ",
            "o++++  ++  ++++o",
            "o++++++++++++++o",
            "o++++++++++++++o",
            "o++ ++++++++ ++o",
            "o+++ ++++++ +++o",
            " o+++      +++o ",
            " o++++++++++++o ",
            "  o++++++++++o  ",
            "   oo++++++oo   ",
            "     oooooo     ");

        static readonly PixelPicture Invader = PixelPicture.Parse("Invader",
            "  #     #  ",
            "   #   #   ",
            "  #######  ",
            " ## ### ## ",
            "###########",
            "# ####### #",
            "# #     # #",
            "   ## ##   ");
    }
}
