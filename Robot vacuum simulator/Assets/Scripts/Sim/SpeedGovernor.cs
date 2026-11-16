using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// The frame budget behind <see cref="SimulationSpeed"/>. Active whenever the time scale is above 1 (not when
    /// paused or at normal speed).
    ///
    /// Each frame it times the physics steps the frame actually ran (every FixedUpdate and the physics after them,
    /// from this component's first FixedUpdate of the frame to its Update). It keeps two real costs per step: a fast
    /// one (smoothed per frame) and a slow one (smoothed over about <see cref="SlowHorizonSteps"/> steps), and uses
    /// the larger. The slow one stops a stretch of cheap steps (paused, or at low speed, a frame runs one or two
    /// steps between lidar scans) from dragging the estimate down and opening the cap. It lets the next frame run
    /// only as many steps as fit in <see cref="SimulationSpeed.FrameBudgetSeconds"/> at that cost, through
    /// Time.maximumDeltaTime (simulated time beyond that is dropped, not owed, so there is no catch-up spiral). Sizing
    /// from the measured cost corrects in one frame; a frame can never be handed more than about the budget's worth
    /// of steps plus one slow step.
    ///
    /// When fewer steps fit the budget than the requested speed (Unity's smallest cap is one fixed step of real time,
    /// i.e. the time scale in steps), it also lowers Time.timeScale to that many steps, and raises it back towards the
    /// requested speed as the cost falls. <see cref="SimulationSpeed.Requested"/> stays what was asked for.
    ///
    /// The physics step itself never changes, so a run's results are the same at any speed.
    /// </summary>
    [DefaultExecutionOrder(-2000)]
    sealed class SpeedGovernor : MonoBehaviour
    {
        /// <summary>Steps a frame may run before any cost has been measured.</summary>
        const float InitialSteps = 8f;

        /// <summary>Smoothing of the per-step cost: rises fast (a slow stretch is felt at once), falls slower.</summary>
        const double RiseWeight = 0.5, FallWeight = 0.2;

        /// <summary>About how many steps the slow per-step cost averages over.</summary>
        const double SlowHorizonSteps = 2000;

        static SpeedGovernor instance;
        static float actual = 1f;
        static float allowedSteps = InitialSteps;

        float lastSetScale = -1f;    // the time scale this governor (or Apply for it) last set (-1: none yet)
        float lastSetMaxDelta = -1f; // and the cap set with it: a run's teardown restores both to other values
        static float sustainableSteps = InitialSteps; // steps that fit the budget at the SLOW cost (sets the time scale)
        double stepCostMs;        // fast-smoothed real ms per physics step (0 = not measured yet)
        double slowStepCostMs;    // slow-smoothed real ms per physics step (0 = not measured yet)
        double stepsMeasured;     // steps behind slowStepCostMs: a plain mean until SlowHorizonSteps, then a moving average
        int stepsThisFrame;
        long firstStepTimestamp;  // when this frame's first physics step began

        public static float Actual => actual;

        /// <summary>For the lag log: whether the cap was set last frame, the per-step costs (ms) and the steps allowed.</summary>
        public static bool LastActive { get; private set; }
        public static double FastCostMs => instance != null ? instance.stepCostMs : 0;
        public static double SlowCostMs => instance != null ? instance.slowStepCostMs : 0;
        public static float AllowedSteps => allowedSteps;

        /// <summary>True once the governor exists (play mode, after the first <see cref="SimulationSpeed.Apply"/>).</summary>
        public static bool Running => instance != null;

        /// <summary>The simulated time the governor lets one frame run now (Time.maximumDeltaTime × the time scale when active).</summary>
        public static float AllowedSeconds => Mathf.Max(Time.fixedDeltaTime, Mathf.Floor(allowedSteps) * Time.fixedDeltaTime);

        /// <summary>The time scale the governor would run <paramref name="requested"/> at now: at most the steps that fit the budget.</summary>
        /// <remarks>
        /// From the slow per-step cost only: one slow step (an algorithm decision) must not knock the time scale down,
        /// it only shortens the cap for a frame or two. The cap still follows the faster estimate.
        /// </remarks>
        public static float SustainableScale(float requested) => Mathf.Clamp(Mathf.Floor(sustainableSteps), 1f, requested);

        /// <summary>Tells the governor <see cref="SimulationSpeed.Apply"/> just set this time scale for it.</summary>
        public static void NoteScale(float scale, float maximumDeltaTime)
        {
            if (instance == null) return;
            instance.lastSetScale = scale;
            instance.lastSetMaxDelta = maximumDeltaTime;
        }

        public static void Ensure()
        {
            if (instance != null || !Application.isPlaying) return;
            var go = new GameObject("Simulation Speed Governor") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<SpeedGovernor>();
        }

        void FixedUpdate()
        {
            if (stepsThisFrame == 0) firstStepTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            stepsThisFrame++;
        }

        void Update()
        {
            float real = Time.unscaledDeltaTime;
            float step = Time.fixedDeltaTime;

            if (real > 0f)
            {
                float measured = Time.timeScale > 0f ? stepsThisFrame * step / real : 0f;
                actual = Mathf.Lerp(actual, measured, 0.1f);
            }

            if (stepsThisFrame > 0)
            {
                double stepsMs = (System.Diagnostics.Stopwatch.GetTimestamp() - firstStepTimestamp) * 1000.0
                                 / System.Diagnostics.Stopwatch.Frequency;
                double perStep = stepsMs / stepsThisFrame;
                if (stepCostMs <= 0) stepCostMs = perStep;
                else stepCostMs += (perStep - stepCostMs) * (perStep > stepCostMs ? RiseWeight : FallWeight);
                // Warm-up: the mean of every step so far (the run's first, costlier steps are diluted within a few
                // hundred steps); after SlowHorizonSteps, a moving average over about that many steps.
                stepsMeasured = System.Math.Min(stepsMeasured + stepsThisFrame, SlowHorizonSteps);
                slowStepCostMs += (perStep - slowStepCostMs) * (stepsThisFrame / stepsMeasured);
            }

            // What the cost allows, kept current even when idle, so the first speed change starts from it (the
            // requested speed bounds it in SustainableScale).
            sustainableSteps = slowStepCostMs > 0
                ? Mathf.Max(1f, (float)(SimulationSpeed.FrameBudgetSeconds * 1000.0 / slowStepCostMs))
                : InitialSteps;

            // Engaged while the time settings are the ones SimulationSpeed.Apply or this governor last set (or the
            // time scale is the requested speed). Anything else set them (paused at 0, or the time settings restored
            // when a run closes): stand down until the next Apply.
            float requested = SimulationSpeed.Requested;
            bool engaged = Time.timeScale > 0f &&
                           (Mathf.Approximately(Time.timeScale, requested) ||
                            (Mathf.Approximately(Time.timeScale, lastSetScale) &&
                             Mathf.Approximately(Time.maximumDeltaTime, lastSetMaxDelta)));
            bool active = engaged && requested > 1f;
            LastActive = active;
            if (active)
            {
                float ceilingSteps = Mathf.Max(1f, SimulationSpeed.Ceiling / step);
                double cost = System.Math.Max(stepCostMs, slowStepCostMs);
                float budgetSteps = cost > 0
                    ? (float)(SimulationSpeed.FrameBudgetSeconds * 1000.0 / cost)
                    : InitialSteps;
                allowedSteps = Mathf.Clamp(budgetSteps, 1f, ceilingSteps);

                // Unity will not cap a frame below one fixed step of REAL time, which is the time scale in steps. So
                // when fewer steps fit the budget than the requested speed, run at that many times real time instead:
                // the machine could not have kept up with the requested speed anyway, and frames stay near the budget.
                // As the cost per step falls, the time scale rises back to the requested speed.
                float scale = SustainableScale(requested);
                Time.timeScale = scale;

                // Time.maximumDeltaTime caps the REAL (unscaled) time a frame may catch up; Unity then multiplies it by
                // the time scale. So the cap for N steps is N steps of simulated time divided by the time scale.
                Time.maximumDeltaTime = AllowedSeconds / scale;
                lastSetScale = Time.timeScale;           // read back: what Unity actually holds
                lastSetMaxDelta = Time.maximumDeltaTime;
            }
            stepsThisFrame = 0;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
