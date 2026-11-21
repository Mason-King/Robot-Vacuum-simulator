using System;

namespace RobotVacuum.AlgorithmApi
{
    /// <summary>How the controller drives to the goals an algorithm returns.</summary>
    public enum DriveMode
    {
        /// <summary>Full speed: getting somewhere. The default.</summary>
        Travel,

        /// <summary>
        /// Cleaning speed: slow enough that every point the brush passes reaches the cleanliness target in one pass.
        /// The controller works the speed out continuously from the floor under the brush (see <see cref="CoverageMath"/>),
        /// but never goes below its floor speed (0.2 m/s): where that is too fast for one pass, closer lanes or more
        /// passes are needed.
        /// </summary>
        Sweep,
    }

    /// <summary>What the robot does once it reaches a goal (see <see cref="DriveRequests.Hold"/>).</summary>
    public enum HoldMode
    {
        /// <summary>Nothing: the goal is Reached on arrival. The default.</summary>
        None,

        /// <summary>Stay still over the goal for <see cref="DriveRequests.HoldSeconds"/> of simulated time.</summary>
        ForSeconds,

        /// <summary>
        /// Stay still over the goal until every passable cell within <see cref="DriveRequests.HoldRadiusMeters"/> of the
        /// goal, and under the brush, believes it is at <see cref="DriveRequests.HoldCleanliness"/>. Capped: the hold ends
        /// after 1.5 times the time the dirtiest of those cells needed when the hold began, clean or not.
        /// </summary>
        UntilClean,
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
        /// For Sweep: the share of the target one pass aims for (default 1). The controller divides the sweep speed by
        /// it, so 0.8 drives 1.25 times faster and each spot gets about 80% of the target. Values below 0.05 count as 0.05.
        /// </summary>
        public float SweepTargetFraction { get; set; } = 1f;

        /// <summary>
        /// The goal is a point ON THE WAY, not a destination: the controller doesn't slow down or stop for it, and
        /// reports it Reached as soon as the robot is within <see cref="PassThroughMeters"/> of it, so the next goal
        /// (asked for at once) carries the motion on. Use for lane ends, U-turn arcs, edge runs.
        /// </summary>
        public bool PassThrough { get; set; }

        /// <summary>
        /// What to do on arrival: nothing, stay still for a time, or stay still until the floor under the brush is clean.
        /// While the robot holds, the goal is InProgress; when the hold ends it is Reached. A second contact at least
        /// 3 s after the first ends a hold as Blocked (the first is ignored). Ignored for a pass-through goal.
        /// </summary>
        public HoldMode Hold { get; set; } = HoldMode.None;

        /// <summary>For <see cref="HoldMode.ForSeconds"/>: how long, simulated seconds.</summary>
        public float HoldSeconds { get; set; }

        /// <summary>For <see cref="HoldMode.UntilClean"/>: the cleanliness every checked cell must reach.</summary>
        public float HoldCleanliness { get; set; }

        /// <summary>
        /// For <see cref="HoldMode.UntilClean"/>: the cells checked are those whose centers lie within this distance of
        /// the goal, meters, and also under the brush from where the robot actually stopped. No larger than the robot's
        /// radius (larger is cut to it).
        /// </summary>
        public float HoldRadiusMeters { get; set; }

        /// <summary>How close counts as "passed" for a pass-through goal.</summary>
        public const float PassThroughMeters = 0.05f;

        /// <summary>The controller never sweeps slower than this, m/s, whatever the floor and exposure ask for.</summary>
        public const float SweepSpeedFloor = 0.2f;

        /// <summary>The controller sweeps this many times slower than <see cref="CoverageMath.SweepSpeed"/> (for drift between lanes).</summary>
        public const float SweepSpeedSafety = 1.1f;

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
