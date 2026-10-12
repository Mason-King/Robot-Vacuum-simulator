using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// The views of the External Model the heatmap can show. All three read the same saved per-cell data
    /// (dwell and cleanliness); only the way it is turned into colour differs. More modes can be added
    /// here later: add a value, then teach <see cref="HeatmapColors"/> what it measures and how it looks.
    /// </summary>
    public enum HeatmapMode
    {
        /// <summary>Cleanliness over 0 to target_max_clean, one cool-to-warm spectrum.</summary>
        FloorCleanliness = 0,

        /// <summary>Dwell over 0 to target_max_dwell. Cool up to 1x target_max_clean, warm beyond it.</summary>
        FloorCoverage = 1,

        /// <summary>Cleanliness over the same range as Coverage. Cool up to 1x target_max_clean, warm beyond it.</summary>
        FloorOverCoverage = 2,
    }

    /// <summary>
    /// EVERY TWEAKABLE HEATMAP / CLEANING VALUE LIVES IN THIS ONE FILE. Edit here and they apply everywhere
    /// (live overlay, exported PNGs, and the analysis side, since all of them go through
    /// <see cref="HeatmapColors"/>). Only the heatmap mode is meant to be changed during a run; target_max_clean is set before a run and fixed for it.
    /// </summary>
    public static class HeatmapDefaults
    {
        // ---- Cleaning goalposts ------------------------------------------------------------------------

        /// <summary>
        /// target_max_clean: the cleanliness a cell is aiming for. The client's number; settable before a
        /// run (GUI, Inspector or config) and fixed once it starts. This is only the default. With <see cref="BaseVacuumPower"/> and a floor
        /// resistance of 1 it equals the seconds of dwell needed, so 2 reproduces the old two-second clean.
        /// </summary>
        public const float TargetMaxClean = 2f;

        /// <summary>target_max_dwell = this scalar x target_max_clean. Sets the top of the Coverage range.</summary>
        public const float DwellTargetScalar = 2f;

        /// <summary>Cleanliness gained per second of dwell on a floor with a cleaning effort of 1.</summary>
        public const float BaseVacuumPower = 1f;

        // ---- Opacity -----------------------------------------------------------------------------------

        /// <summary>Opacity just above zero: very see-through. A value of exactly 0 is always fully clear.</summary>
        public const float AlphaLow = 0.10f;

        /// <summary>Opacity at the top of the range: mostly solid, but never fully opaque.</summary>
        public const float AlphaHigh = 0.85f;

        /// <summary>
        /// UNDECIDED. False: opacity keeps rising across the whole range. True: it stops rising once the
        /// value reaches 1x target_max_clean, so everything past the target is equally opaque.
        /// </summary>
        public const bool OpacityPlateausAtTarget = false;

        // ---- Colours (evenly spaced stops, left = low value, right = high value) -----------------------

        /// <summary>Floor Cleanliness: one spectrum from cool to warm.</summary>
        public static readonly Color[] SingleSpectrum =
        {
            new Color(0.20f, 0.35f, 0.90f), // blue
            new Color(0.15f, 0.80f, 0.85f), // cyan
            new Color(0.96f, 0.84f, 0.29f), // yellow
            new Color(0.90f, 0.26f, 0.29f), // red
        };

        /// <summary>Coverage and OverCoverage, from 0 up to 1x target_max_clean.</summary>
        public static readonly Color[] CoolSpectrum =
        {
            new Color(0.20f, 0.35f, 0.90f), // blue
            new Color(0.15f, 0.80f, 0.85f), // cyan
        };

        /// <summary>Coverage and OverCoverage, from 1x target_max_clean up to the top of the range.</summary>
        public static readonly Color[] WarmSpectrum =
        {
            new Color(0.96f, 0.84f, 0.29f), // yellow
            new Color(0.96f, 0.58f, 0.22f), // orange
            new Color(0.90f, 0.26f, 0.29f), // red
        };

        /// <summary>The label to show in a mode picker.</summary>
        public static string DisplayName(HeatmapMode mode)
        {
            switch (mode)
            {
                case HeatmapMode.FloorCoverage: return "Floor Coverage";
                case HeatmapMode.FloorOverCoverage: return "Floor OverCoverage";
                default: return "Floor Cleanliness";
            }
        }
    }
}
