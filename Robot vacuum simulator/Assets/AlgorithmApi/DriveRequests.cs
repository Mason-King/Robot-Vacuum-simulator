using System;

namespace RobotVacuum.AlgorithmApi
{
    /// <summary>What a run asks the cleaning to put first (a run setting).</summary>
    public enum CleaningPriority
    {
        /// <summary>
        /// Cover all the floor as fast as possible: full speed. One whole pass over every room first; then further
        /// passes, each complete before the next, until the floor reaches the target (the user can stop it sooner).
        /// </summary>
        Coverage,

        /// <summary>
        /// Clean each room to the target before moving on: sweeps no slower than the controller's floor speed, with
        /// further passes over the room (crossing the last) until it is clean.
        /// </summary>
        Cleanliness,
    }

    /// <summary>How the controller drives to the goals an algorithm returns.</summary>
    public enum DriveMode
    {
        /// <summary>Full speed: getting somewhere. The default.</summary>
        Travel,

        /// <summary>
        /// Cleaning speed: slow enough that every point the brush passes reaches the cleanliness target in one pass.
        /// The controller works the speed out continuously from the floor under the brush (see <see cref="CoverageMath"/>),
    /// but never goes below its floor speed (about 0.3 m/s): where that is too fast for one pass, more passes are needed.
        /// </summary>
        Sweep,
    }

    /// <summary>
    /// What an algorithm asks of the controller, beyond the goal itself. Set these BEFORE returning a goal from
    /// NextGoal; they apply to that goal and stay as they are until changed. One instance per run, in
    /// <see cref="AlgorithmContext.drive"/>.
    /// </summary>
    public sealed class DriveRequests
    {
        /// <summary>Travel (full speed) or Sweep (cleaning speed).</summary>
        public DriveMode Mode { get; set; } = DriveMode.Travel;

        /// <summary>
        /// For Sweep: the brush length (metres) every point under the sweep should get. Use
        /// <see cref="CoverageMath.LaneExposure"/> for lanes at a given spacing, <see cref="CoverageMath.EdgeExposure"/>
        /// for a single pass along a wall. Smaller = slower.
        /// </summary>
        public float SweepExposureMeters { get; set; }

        /// <summary>
        /// The goal is a point ON THE WAY, not a destination: the controller doesn't slow down or stop for it, and
        /// reports it Reached as soon as the robot is within <see cref="PassThroughMeters"/> of it, so the next goal
        /// (asked for at once) carries the motion on. Use for lane ends, U-turn arcs, edge runs.
        /// </summary>
        public bool PassThrough { get; set; }

        /// <summary>How close counts as "passed" for a pass-through goal.</summary>
        public const float PassThroughMeters = 0.05f;

        /// <summary>True once the algorithm has said its job is done.</summary>
        public bool Finished { get; private set; }

        /// <summary>
        /// The job is done: the controller drives back to the dock, and the run ends as completed. NextGoal is not
        /// asked again after this.
        /// </summary>
        public void Finish() => Finished = true;
    }

    /// <summary>
    /// The arithmetic of cleaning to a target in one pass. A point gains cleanliness at vacuumPower / resistance per
    /// second while it is under the brush (a disc of the brush radius), so a pass at speed v leaves
    /// (brush length over the point / v) * power / resistance. "Brush length over the point" is its EXPOSURE.
    /// </summary>
    public static class CoverageMath
    {
        /// <summary>
        /// The least exposure any point gets from straight parallel lanes <paramref name="spacing"/> apart, counting
        /// every lane whose brush reaches it. The worst point is not between two lanes but just beyond where the next
        /// lane's brush stops reaching. 0 when the lanes are too far apart to cover everything.
        /// </summary>
        public static float LaneExposure(float spacing, float brushRadius)
        {
            if (spacing <= 0f || brushRadius <= 0f || spacing >= 2f * brushRadius) return 0f;
            float worst = float.PositiveInfinity;
            const int Steps = 400;
            for (int i = 0; i <= Steps; i++)
            {
                float y = spacing * i / Steps;
                float total = 0f;
                for (int k = -4; k <= 4; k++)
                {
                    float d = Math.Abs(y - k * spacing);
                    if (d < brushRadius) total += 2f * (float)Math.Sqrt(brushRadius * brushRadius - d * d);
                }
                worst = Math.Min(worst, total);
            }
            return worst;
        }

        /// <summary>
        /// The exposure for a single pass along a wall that should clean up to <paramref name="inset"/> short of the
        /// brush's edge: the brush's chord at lateral offset (radius - inset).
        /// </summary>
        public static float EdgeExposure(float brushRadius, float inset)
        {
            float y = Math.Max(0f, brushRadius - inset);
            return 2f * (float)Math.Sqrt(Math.Max(0f, brushRadius * brushRadius - y * y));
        }

        /// <summary>The speed (m/s) at which every point getting <paramref name="exposure"/> metres of brush reaches <paramref name="target"/>.</summary>
        public static float SweepSpeed(float exposure, float vacuumPower, float resistance, float target) =>
            target <= 0f ? float.PositiveInfinity : exposure * vacuumPower / (Math.Max(0.01f, resistance) * target);
    }
}
