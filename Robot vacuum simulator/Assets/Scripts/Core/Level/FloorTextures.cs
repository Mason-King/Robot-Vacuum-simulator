using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>
    /// Seamless textures for each <see cref="FloorPattern"/>, generated on first use so the simulation
    /// needs no art assets. They are light greyscale and tinted by the floor colour, except
    /// <see cref="FloorPattern.Hazard"/>, which carries its own yellow and black.
    /// </summary>
    public static class FloorTextures
    {
        const int Size = 256;

        static readonly Color32 HazardYellow = new Color32(242, 184, 48, 255);
        static readonly Color32 HazardDark = new Color32(38, 38, 42, 255);
        static readonly Dictionary<FloorPattern, Texture2D> cache = new Dictionary<FloorPattern, Texture2D>();

        /// <summary>Metres of floor covered by one repeat of the pattern's texture.</summary>
        public static float TileMetres(FloorPattern pattern) => pattern switch
        {
            FloorPattern.Planks => FloorPatterns.PlankLength,
            FloorPattern.Tiles => FloorPatterns.TileSize * 2f,
            FloorPattern.Carpet => 0.6f,
            FloorPattern.Shag => 0.6f,
            FloorPattern.Rug => 1.2f,
            _ => 1f,
        };

        /// <summary>True when the texture should be drawn untinted.</summary>
        public static bool IsSelfColoured(FloorPattern pattern) => pattern == FloorPattern.Hazard;

        /// <summary>The generated texture for a pattern, or null for <see cref="FloorPattern.Plain"/>.</summary>
        public static Texture2D Get(FloorPattern pattern)
        {
            if (pattern == FloorPattern.Plain) return null;
            if (cache.TryGetValue(pattern, out var existing) && existing != null) return existing;

            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, true)
            {
                name = $"Floor {pattern} (generated)",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    pixels[y * Size + x] = Shade(pattern, x, y);

            texture.SetPixels32(pixels);
            texture.Apply(true, false);

            cache[pattern] = texture;
            return texture;
        }

        static Color32 Shade(FloorPattern pattern, int x, int y)
        {
            float u = (x + 0.5f) / Size;
            float v = (y + 0.5f) / Size;

            switch (pattern)
            {
                case FloorPattern.Planks: return Grey(Planks(u, v));
                case FloorPattern.Tiles: return Grey(Tiles(u, v));
                case FloorPattern.Carpet: return Grey(0.8f + 0.12f * Hash(x, y, 3) + 0.08f * Noise(u, v, 12, 12, 5));
                case FloorPattern.Shag: return Grey(0.66f + 0.26f * Noise(u, v, 40, 40, 7) + 0.08f * Hash(x, y, 9));
                case FloorPattern.Rug: return Grey(Rug(u, v, x, y));
                case FloorPattern.Hazard: return Mathf.FloorToInt((u + v) * 4f) % 2 == 0 ? HazardYellow : HazardDark;
                default: return Grey(1f);
            }
        }

        // One texture repeat is 1.2 m: six 20 cm plank rows, alternate rows shifted half a plank.
        static float Planks(float u, float v)
        {
            const int rows = 6;
            float rowPosition = v * rows;
            int row = Mathf.FloorToInt(rowPosition);
            float across = rowPosition - row;
            float along = Mathf.Repeat(u + ((row & 1) == 1 ? 0.5f : 0f), 1f);

            float tone = 0.86f + 0.12f * Hash(row, row & 1, 11);
            float value = tone * (0.9f + 0.1f * Noise(u, v, 3, 60, 13));

            if (across < 0.04f || along < 0.008f) value *= 0.62f;
            return value;
        }

        // One repeat is 2 × 2 tiles with grout lines.
        static float Tiles(float u, float v)
        {
            const float grout = 0.03f;
            if (Mathf.Repeat(u * 2f, 1f) < grout || Mathf.Repeat(v * 2f, 1f) < grout) return 0.66f;

            int ix = Mathf.FloorToInt(u * 2f), iy = Mathf.FloorToInt(v * 2f);
            return 0.93f + 0.05f * Hash(ix, iy, 17) + 0.02f * Noise(u, v, 16, 16, 19);
        }

        // A diamond lattice with alternating tones.
        static float Rug(float u, float v, int x, int y)
        {
            float a = (u + v) * 4f, b = (u - v) * 4f;
            bool line = Mathf.Abs(Mathf.Repeat(a, 1f) - 0.5f) > 0.44f || Mathf.Abs(Mathf.Repeat(b, 1f) - 0.5f) > 0.44f;
            bool alternate = (Mathf.FloorToInt(a) + Mathf.FloorToInt(b)) % 2 == 0;

            float value = line ? 0.64f : alternate ? 0.92f : 0.8f;
            return value * (0.94f + 0.06f * Hash(x, y, 23));
        }

        static Color32 Grey(float value)
        {
            byte level = (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255f), 0, 255);
            return new Color32(level, level, level, 255);
        }

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        /// <summary>Smooth value noise that wraps at the texture edge, so the tile stays seamless.</summary>
        static float Noise(float u, float v, int cellsX, int cellsY, int seed)
        {
            float fx = u * cellsX, fy = v * cellsY;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);

            int x1 = (x0 + 1) % cellsX, y1 = (y0 + 1) % cellsY;
            x0 %= cellsX;
            y0 %= cellsY;

            float bottom = Mathf.Lerp(Hash(x0, y0, seed), Hash(x1, y0, seed), tx);
            float top = Mathf.Lerp(Hash(x0, y1, seed), Hash(x1, y1, seed), tx);
            return Mathf.Lerp(bottom, top, ty);
        }
    }
}
