using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Draws an External Model into a texture: one pixel per cell, coloured by <see cref="HeatmapColors"/> for
    /// the chosen <see cref="HeatmapMode"/>. It works from <see cref="ExternalModelData"/>, a plain saved
    /// copy, so it needs no live grid and no scene: a finished run, a saved file loaded later by the analysis
    /// screen and a preview of a run in progress all go through the same code. (A thin overload takes the live
    /// grid and copies it first.)
    ///
    /// Unlike the in-scene overlay these pictures are fully opaque, since they are exported as PNGs: untouched
    /// floor is drawn as <see cref="Untouched"/>, floor under blocking furniture as <see cref="Blocked"/>, and
    /// everything outside the rooms is left clear so the plan can be made out.
    /// </summary>
    public static class CoverageImage
    {
        /// <summary>Longest edge in pixels. A grid bigger than this is sampled down to fit.</summary>
        public const int MaxSize = 512;

        /// <summary>Floor nothing has happened on yet.</summary>
        public static readonly Color32 Untouched = new Color32(32, 36, 45, 255);

        /// <summary>Floor it can never reach, under blocking furniture.</summary>
        public static readonly Color32 Blocked = new Color32(60, 66, 79, 255);

        /// <summary>Outside the floor plan: not part of the picture at all.</summary>
        public static readonly Color32 Outside = new Color32(0, 0, 0, 0);

        /// <summary>A new image of <paramref name="data"/>, or null if there is nothing valid to draw.</summary>
        public static Texture2D Create(ExternalModelData data, HeatmapMode mode)
        {
            if (data == null || !data.IsValid) return null;

            var texture = new Texture2D(Width(data), Height(data), TextureFormat.RGBA32, false)
            {
                name = "Coverage " + mode,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            Repaint(texture, data, mode);
            return texture;
        }

        /// <summary>
        /// Refreshes an image in place, e.g. to preview a run still going. False when the texture no longer
        /// matches the data, so the caller knows to build a new one.
        /// </summary>
        public static bool Repaint(Texture2D texture, ExternalModelData data, HeatmapMode mode)
        {
            if (texture == null || data == null || !data.IsValid) return false;

            int width = Width(data);
            int height = Height(data);
            if (texture.width != width || texture.height != height) return false;

            var palette = HeatmapColors.Palette(mode);
            int step = Step(data);
            var pixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                int row = Mathf.Min(y * step, data.rows - 1);

                for (int x = 0; x < width; x++)
                {
                    int cellIndex = data.Index(row, Mathf.Min(x * step, data.cols - 1));
                    int pixel = y * width + x;

                    if (data.outsideRooms[cellIndex])
                    {
                        pixels[pixel] = Outside;
                    }
                    else if (data.nonCleanable[cellIndex])
                    {
                        pixels[pixel] = Blocked;
                    }
                    else
                    {
                        float t = HeatmapColors.Normalize(mode, data.dwellSeconds[cellIndex], data.cleanliness[cellIndex], data.targetMaxClean);
                        int index = HeatmapColors.PaletteIndex(t);

                        // Palette entry 0 is clear for the overlay; here it is bare floor.
                        if (index == 0)
                        {
                            pixels[pixel] = Untouched;
                        }
                        else
                        {
                            Color32 color = palette[index];
                            color.a = 255;
                            pixels[pixel] = color;
                        }
                    }
                }
            }

            // Grid row 0 and texture row 0 are both the bottom, so rows map straight across.
            texture.SetPixels32(pixels);
            texture.Apply();
            return true;
        }

        /// <summary>A new image of a live grid, copying it first. Prefer the data overload when you already have one.</summary>
        public static Texture2D Create(ExternalModelGrid grid, HeatmapMode mode = HeatmapMode.FloorCleanliness) =>
            grid == null ? null : Create(grid.ToData(), mode);

        static int Step(ExternalModelData data) =>
            Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(data.rows, data.cols) / (float)MaxSize));

        static int Width(ExternalModelData data) => Mathf.CeilToInt(data.cols / (float)Step(data));

        static int Height(ExternalModelData data) => Mathf.CeilToInt(data.rows / (float)Step(data));
    }
}
