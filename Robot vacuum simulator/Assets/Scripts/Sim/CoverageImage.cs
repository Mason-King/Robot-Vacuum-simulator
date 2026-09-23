using RobotVacuum.Level;
using RobotVacuumSim.EnvironmentModel;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Draws a coverage grid into a texture the UI can show: one pixel per cell, on the same red-to-green
    /// scale as the in-scene heatmap. It is how a headless run — which builds no heatmap, no meshes and no
    /// camera — can still show what it managed to clean, and how a run in progress can be previewed.
    ///
    /// The grid is a rectangle around the whole plan, so cells outside every room are drawn clear: without
    /// that the picture is one filled rectangle and the rooms cannot be made out at all.
    /// </summary>
    public static class CoverageImage
    {
        /// <summary>Longest edge in pixels. A grid bigger than this is sampled down to fit.</summary>
        public const int MaxSize = 512;

        const int PaletteSize = 256;

        /// <summary>Floor the vacuum has never touched.</summary>
        public static readonly Color32 Untouched = new Color32(32, 36, 45, 255);

        /// <summary>Floor it can never reach, under blocking furniture.</summary>
        public static readonly Color32 Blocked = new Color32(60, 66, 79, 255);

        /// <summary>Outside the floor plan: not part of the picture at all.</summary>
        public static readonly Color32 Outside = new Color32(0, 0, 0, 0);

        static Color32[] palette;

        // Which pixels fall outside every room. Rooms cannot move mid-run, so this is worked out once per
        // grid rather than per repaint: the preview would otherwise test every pixel several times a second.
        static ExternalModelGrid maskedGrid;
        static bool[] outsideMask;

        /// <summary>
        /// A new image sized to <paramref name="grid"/>, or null if there is no grid to draw. Pass the level
        /// and its renderer to draw everything outside the rooms clear.
        /// </summary>
        public static Texture2D Create(ExternalModelGrid grid, LevelData level = null, LevelRenderer renderer = null)
        {
            if (grid == null || grid.Rows <= 0 || grid.Cols <= 0) return null;

            var texture = new Texture2D(Width(grid), Height(grid), TextureFormat.RGBA32, false)
            {
                name = "Coverage",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            Repaint(texture, grid, level, renderer);
            return texture;
        }

        /// <summary>
        /// Refreshes an image in place, for previewing a run still going. False when the texture no longer
        /// matches the grid, so the caller knows to build a new one.
        /// </summary>
        public static bool Repaint(Texture2D texture, ExternalModelGrid grid, LevelData level = null, LevelRenderer renderer = null)
        {
            if (texture == null || grid == null || grid.Rows <= 0 || grid.Cols <= 0) return false;

            int width = Width(grid);
            int height = Height(grid);
            if (texture.width != width || texture.height != height) return false;

            if (palette == null) BuildPalette();
            var mask = OutsideMask(grid, level, renderer, width, height);

            int step = Step(grid);
            var pixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
            {
                int row = Mathf.Min(y * step, grid.Rows - 1);

                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (mask != null && mask[index])
                    {
                        pixels[index] = Outside;
                        continue;
                    }

                    var cell = grid.GetCell(row, Mathf.Min(x * step, grid.Cols - 1));

                    // Grid row 0 and texture row 0 are both the bottom, so rows map straight across.
                    pixels[index] = cell.isNonCleanable
                        ? Blocked
                        : palette[Mathf.Clamp(Mathf.RoundToInt((1f - cell.Dirtiness) * (PaletteSize - 1)), 0, PaletteSize - 1)];
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return true;
        }

        /// <summary>Forgets the cached room mask, for a level whose rooms have been edited.</summary>
        public static void Forget()
        {
            maskedGrid = null;
            outsideMask = null;
        }

        static bool[] OutsideMask(ExternalModelGrid grid, LevelData level, LevelRenderer renderer, int width, int height)
        {
            if (level == null) return null;
            if (ReferenceEquals(maskedGrid, grid) && outsideMask != null && outsideMask.Length == width * height)
                return outsideMask;

            int step = Step(grid);
            var mask = new bool[width * height];

            for (int y = 0; y < height; y++)
            {
                int row = Mathf.Min(y * step, grid.Rows - 1);

                for (int x = 0; x < width; x++)
                {
                    Vector2 world = grid.GridIndexToWorldCenter(row, Mathf.Min(x * step, grid.Cols - 1));
                    Vector2 point = renderer != null ? renderer.WorldToLevel(world) : world;
                    mask[y * width + x] = level.RoomIndexAt(point) < 0;
                }
            }

            maskedGrid = grid;
            outsideMask = mask;
            return mask;
        }

        static int Step(ExternalModelGrid grid) =>
            Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(grid.Rows, grid.Cols) / (float)MaxSize));

        static int Width(ExternalModelGrid grid) => Mathf.CeilToInt(grid.Cols / (float)Step(grid));

        static int Height(ExternalModelGrid grid) => Mathf.CeilToInt(grid.Rows / (float)Step(grid));

        static void BuildPalette()
        {
            var colors = DefaultColors();
            palette = new Color32[PaletteSize];
            for (int i = 0; i < PaletteSize; i++) palette[i] = colors.Evaluate(i / (float)(PaletteSize - 1));

            // Nothing cleaned yet reads as bare floor rather than the faintest red.
            palette[0] = Untouched;
        }

        /// <summary>Barely touched red, through orange and yellow, to fully clean green.</summary>
        public static Gradient DefaultColors()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.90f, 0.26f, 0.29f), 0f),
                    new GradientColorKey(new Color(0.96f, 0.58f, 0.22f), 0.35f),
                    new GradientColorKey(new Color(0.96f, 0.84f, 0.29f), 0.6f),
                    new GradientColorKey(new Color(0.24f, 0.80f, 0.54f), 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }
    }
}
