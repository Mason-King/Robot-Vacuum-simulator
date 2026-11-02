using RobotVacuum.Sim.Sensors;
using UnityEngine;
using K = RobotVacuum.Sim.Control.PoseCorrectionConstants;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// SLAM ONLY: keeps the robot's believed pose honest between steps (design doc, section 17). It owns the evidence
    /// map and the pose matcher, decides WHEN to correct (the preset's rate; every scan while turning; not while
    /// stationary), and sizes the soft limit from the odometry travelled since the last correction.
    ///
    /// Plain C#, no Unity objects, so the controller and the EditMode harness run exactly the same code.
    /// Order each step: <see cref="AddOdometry"/> → <see cref="Correct"/> → map the scan (ObservationMapper) from the
    /// pose <see cref="Correct"/> returned. The scan must be matched BEFORE it is mapped.
    /// </summary>
    public sealed class SlamFrontEnd
    {
        readonly PoseMatcher matcher = new PoseMatcher();
        readonly float noiseScale;

        float sinceDistance;
        float sinceTurn;
        float nextTime;
        float lastAttemptTime;
        RobotPose lastAttemptPose;
        float lastCorrectionHeading;

        public SlamFrontEnd(Vector2 anchorWorld, float cellSizeMeters, PoseCorrectionQuality quality, float noiseScale)
        {
            Map = new EvidenceMap(anchorWorld, cellSizeMeters);
            Profile = PoseCorrectionProfile.For(quality);
            this.noiseScale = noiseScale;
        }

        /// <summary>The evidence map: give it to the ObservationMapper so mapping and matching share it.</summary>
        public EvidenceMap Map { get; }

        public PoseCorrectionProfile Profile { get; }

        public PoseMatcher Matcher => matcher;

        // ---- Counts (for result.json) ----
        public int Attempts { get; private set; }
        public int Applied { get; private set; }
        public int Partial { get; private set; }
        public int TooFewHits { get; private set; }
        public int WeakMatch { get; private set; }
        public int WideSearches { get; private set; }
        public MatchOutcome LastOutcome { get; private set; } = MatchOutcome.WeakMatch;

        /// <summary>Starts a run at the dock: the one pose known exactly.</summary>
        public void Begin(RobotPose start)
        {
            sinceDistance = 0f;
            sinceTurn = 0f;
            nextTime = 0f;
            lastAttemptTime = 0f;
            lastAttemptPose = start;
            lastCorrectionHeading = start.heading;
            Attempts = Applied = Partial = TooFewHits = WeakMatch = WideSearches = 0;
        }

        /// <summary>This step's odometry (what the wheels reported), for sizing the soft limit.</summary>
        public void AddOdometry(float forwardMeters, float turnDegrees)
        {
            sinceDistance += Mathf.Abs(forwardMeters);
            sinceTurn += Mathf.Abs(turnDegrees);
        }

        /// <summary>
        /// If a correction is due and this step has a lidar scan, matches it against the map and returns the corrected
        /// pose; otherwise returns <paramref name="believed"/> unchanged.
        /// </summary>
        public RobotPose Correct(SensorFrame frame, RobotPose believed, float simTime, float stepSeconds)
        {
            if (!frame.hasLidarScan) return believed;

            bool due = simTime >= nextTime - 0.5f * stepSeconds ||
                       Mathf.Abs(Mathf.DeltaAngle(lastCorrectionHeading, believed.heading)) >= K.TurnTriggerDegrees;
            if (!due) return believed;

            bool stationary =
                Vector2.Distance(believed.position, lastAttemptPose.position) < K.StationaryMeters &&
                Mathf.Abs(Mathf.DeltaAngle(believed.heading, lastAttemptPose.heading)) < K.StationaryDegrees &&
                simTime - lastAttemptTime < K.StationaryRecheckSeconds;
            if (stationary) return believed;

            nextTime = simTime + 1f / Mathf.Max(0.1f, Profile.rateHz);
            lastAttemptTime = simTime;

            // How far odometry could have drifted since the last correction (the soft limit's σ).
            float sigmaPos = Sensors.SensorNoiseProfile.OdometryDistanceSigmaFraction * noiseScale *
                             Mathf.Sqrt(sinceDistance * Sensors.SensorNoiseProfile.OdometryDistanceReferenceMeters) + K.PriorFloorMeters;
            float sigmaHead = Sensors.SensorNoiseProfile.OdometryHeadingSigmaFraction * noiseScale *
                              Mathf.Sqrt(sinceTurn * Sensors.SensorNoiseProfile.OdometryHeadingReferenceDegrees) + K.PriorFloorDegrees;

            Attempts++;
            var profile = Profile;
            LastOutcome = matcher.Match(Map, frame, believed, profile, sigmaPos, sigmaHead, out RobotPose corrected);
            if (matcher.UsedWideSearch) WideSearches++;

            switch (LastOutcome)
            {
                case MatchOutcome.Applied: Applied++; believed = corrected; break;
                case MatchOutcome.Partial: Partial++; believed = corrected; break;
                case MatchOutcome.TooFewHits: TooFewHits++; break;
                case MatchOutcome.WeakMatch: WeakMatch++; break;
            }

            sinceDistance = 0f;
            sinceTurn = 0f;
            lastAttemptPose = believed;
            lastCorrectionHeading = believed.heading;
            return believed;
        }
    }
}
