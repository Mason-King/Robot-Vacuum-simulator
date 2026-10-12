namespace RobotVacuum.Sim
{
    /// <summary>
    /// Run-wide defaults for the models and the simulation clock, kept in ONE place so they can be tuned
    /// later. (Heatmap and target defaults live in <see cref="HeatmapDefaults"/>.)
    /// </summary>
    public static class SimDefaults
    {
        /// <summary>
        /// Grid cell edge length in metres: 2 inches. Used by both models, which must share it so their cells
        /// line up. Note VacuumCleaningController serializes its own copy for the Inspector, so an object
        /// that already saved an older value (for example in a scene) keeps that value until it is reset there.
        /// </summary>
        public const float CellSizeMeters = 0.0508f;

        /// <summary>
        /// Safety backstop on simulated seconds (24 h). The battery running out is the real stop condition.
        /// SecondaryBootLauncher and TestSimStart both read this, so there is one source.
        /// </summary>
        public const float MaxSimulatedSeconds = 86400f;

        /// <summary>
        /// When the Internal Model has to grow, each side that grows is extended this many cells past the
        /// cell that triggered it, so a robot creeping along a wall does not reallocate on every step.
        /// </summary>
        public const int InternalGrowthPaddingCells = 64;

        /// <summary>
        /// Runaway guard for the Internal Model, in metres from its origin. NOT a floor-size limit: it only
        /// stops a bad coordinate (infinity, NaN, a position bug) from allocating gigabytes. Writes beyond
        /// it are ignored with a warning.
        /// </summary>
        public const float InternalMaxHalfWidthMeters = 200f;
    }
}
