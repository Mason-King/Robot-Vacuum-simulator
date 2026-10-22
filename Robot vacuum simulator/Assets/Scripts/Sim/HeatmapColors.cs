using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// The one place per-cell numbers become colour. Works on plain values (dwell, cleanliness, target), so
    /// the live overlay, the exported PNGs and the analysis screen all colour a cell identically. Every
    /// number it uses comes from <see cref="HeatmapDefaults"/>.
    /// </summary>
    public static class HeatmapColors
    {
        const int PaletteSize = 256;
        const int ModeCount = 3;

        static readonly Color32[][] palettes = new Color32[ModeCount][];

        /// <summary>What a mode measures: dwell for Coverage, cleanliness for the other two.</summary>
        public static float Value(HeatmapMode mode, float dwellSeconds, float cleanliness) =>
            mode == HeatmapMode.FloorCoverage ? dwellSeconds : cleanliness;

        /// <summary>The value at the top of a mode's colour range.</summary>
        public static float Range(HeatmapMode mode, float targetMaxClean) =>
            mode == HeatmapMode.FloorCleanliness ? targetMaxClean : targetMaxClean * HeatmapDefaults.DwellTargetScalar;

        /// <summary>Where 1x target_max_clean sits along a mode's range, 0 to 1.</summary>
        public static float TargetPosition(HeatmapMode mode) =>
            mode == HeatmapMode.FloorCleanliness ? 1f : Mathf.Min(1f, 1f / Mathf.Max(HeatmapDefaults.DwellTargetScalar, 1e-6f));

        /// <summary>A cell's position along the mode's range, 0 to 1. Values past the top stay at 1.</summary>
        public static float Normalize(HeatmapMode mode, float dwellSeconds, float cleanliness, float targetMaxClean)
        {
            float value = Value(mode, dwellSeconds, cleanliness);
            if (value <= 0f) return 0f;

            float range = Range(mode, targetMaxClean);
            return range > 0f ? Mathf.Clamp01(value / range) : 1f;
        }

        /// <summary>The colour for a cell. Fully clear when the mode's value is zero.</summary>
        public static Color32 Evaluate(HeatmapMode mode, float dwellSeconds, float cleanliness, float targetMaxClean) =>
            Palette(mode)[PaletteIndex(Normalize(mode, dwellSeconds, cleanliness, targetMaxClean))];

        /// <summary>Index into <see cref="Palette"/> for a normalised position.</summary>
        public static int PaletteIndex(float normalized) =>
            Mathf.Clamp(Mathf.RoundToInt(normalized * (PaletteSize - 1)), 0, PaletteSize - 1);

        /// <summary>The mode's 256 colours, alpha included. Entry 0 is always fully clear.</summary>
        public static Color32[] Palette(HeatmapMode mode)
        {
            int slot = Mathf.Clamp((int)mode, 0, ModeCount - 1);
            if (palettes[slot] == null) palettes[slot] = BuildPalette((HeatmapMode)slot);
            return palettes[slot];
        }

        /// <summary>Throws away the cached palettes. For tests, or after editing the colours at run time.</summary>
        public static void Rebuild()
        {
            for (int i = 0; i < ModeCount; i++) palettes[i] = null;
        }

        static Color32[] BuildPalette(HeatmapMode mode)
        {
            var palette = new Color32[PaletteSize];
            float targetAt = TargetPosition(mode);

            for (int i = 0; i < PaletteSize; i++)
            {
                float t = i / (float)(PaletteSize - 1);

                Color color;
                if (mode == HeatmapMode.FloorCleanliness)
                    color = Sample(HeatmapDefaults.SingleSpectrum, t);
                else if (t <= targetAt)
                    color = Sample(HeatmapDefaults.CoolSpectrum, targetAt > 0f ? t / targetAt : 1f);
                else
                    color = Sample(HeatmapDefaults.WarmSpectrum, (t - targetAt) / (1f - targetAt));

                float alphaT = HeatmapDefaults.OpacityPlateausAtTarget && targetAt > 0f ? Mathf.Min(t, targetAt) / targetAt : t;
                color.a = Mathf.Lerp(HeatmapDefaults.AlphaLow, HeatmapDefaults.AlphaHigh, alphaT);
                palette[i] = color;
            }

            // Nothing there yet stays see-through, so the real floor shows from the start.
            palette[0] = default;
            return palette;
        }

        // Linear blend along evenly spaced colour stops; t is 0 to 1.
        static Color Sample(Color[] stops, float t)
        {
            if (stops == null || stops.Length == 0) return Color.white;
            if (stops.Length == 1) return stops[0];

            float scaled = Mathf.Clamp01(t) * (stops.Length - 1);
            int low = Mathf.Min((int)scaled, stops.Length - 2);
            return Color.Lerp(stops[low], stops[low + 1], scaled - low);
        }
    }
}
