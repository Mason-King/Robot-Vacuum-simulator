using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>A component the engine advances once per fixed step, in a fixed order.</summary>
    public interface ISimStepper
    {
        void Step(float dt);
    }

    /// <summary>
    /// The simulation loop. Advances the world in fixed <see cref="StepSeconds"/> steps that are decoupled
    /// from rendering: the vacuum, then the cats, then cleaning, then 2D physics, every step and in that
    /// order. A rendered run takes as many steps per frame as the chosen speed asks for; a headless run calls
    /// <see cref="StepMany"/> as fast as it can. Both go through <see cref="Step"/>, so the same seed gives
    /// the same result either way, and speed never changes the step size, so nothing tunnels at 100×.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimEngine : MonoBehaviour
    {
        /// <summary>Simulated seconds per step.</summary>
        public const float StepSeconds = 0.02f;

        public const float MinSpeed = 0.25f;
        public const float MaxSpeed = 100f;

        /// <summary>Real time a rendered frame may spend stepping, so the window stays responsive at 100×.</summary>
        const double FrameBudgetMilliseconds = 30.0;

        /// <summary>The engine driving the scene, or null when components step themselves.</summary>
        public static SimEngine Active { get; private set; }

        readonly List<ISimStepper> steppers = new List<ISimStepper>();
        readonly Stopwatch budget = new Stopwatch();
        SimulationMode2D savedSimulationMode;
        double backlog;
        float speed = 1f;
        float measuredSince;
        float measuredSimSeconds;

        /// <summary>Simulated seconds since the run began.</summary>
        public float SimTime { get; private set; }

        public long StepCount { get; private set; }

        public bool Paused { get; set; }

        /// <summary>When set, the engine stops stepping on its own; the owner drives it with <see cref="StepMany"/>.</summary>
        public bool ManualStepping { get; set; }

        /// <summary>Requested multiple of real time, from <see cref="MinSpeed"/> to <see cref="MaxSpeed"/>.</summary>
        public float Speed
        {
            get => speed;
            set => speed = Mathf.Clamp(value, MinSpeed, MaxSpeed);
        }

        /// <summary>The multiple of real time actually achieved over the last second or so.</summary>
        public float AchievedSpeed { get; private set; }

        /// <summary>Raised after every step, once physics has moved: the hook for metrics and telemetry.</summary>
        public event Action<SimEngine> Stepped;

        void OnEnable()
        {
            Active = this;
            savedSimulationMode = Physics2D.simulationMode;
            Physics2D.simulationMode = SimulationMode2D.Script;
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
            Physics2D.simulationMode = savedSimulationMode;
        }

        /// <summary>Steps these components each step, in the order given. Replaces any earlier list.</summary>
        public void SetSteppers(IEnumerable<ISimStepper> ordered)
        {
            steppers.Clear();
            steppers.AddRange(ordered);
        }

        /// <summary>Starts the clock again at zero, for a new run.</summary>
        public void ResetClock()
        {
            SimTime = 0f;
            StepCount = 0;
            backlog = 0;
        }

        /// <summary>One fixed step of the whole world.</summary>
        public void Step()
        {
            foreach (var stepper in steppers)
                if (stepper is UnityEngine.Object unityObject ? unityObject != null : stepper != null)
                    stepper.Step(StepSeconds);

            Physics2D.Simulate(StepSeconds);
            SimTime += StepSeconds;
            StepCount++;
            Stepped?.Invoke(this);
        }

        /// <summary>Takes up to <paramref name="count"/> steps, stopping early if <paramref name="until"/> says so.</summary>
        public int StepMany(int count, Func<bool> until = null)
        {
            int taken = 0;
            while (taken < count)
            {
                if (until != null && until()) break;
                Step();
                taken++;
            }
            return taken;
        }

        void Update()
        {
            MeasureSpeed();
            if (ManualStepping || Paused)
            {
                backlog = 0;
                return;
            }

            // Real time owed to the simulation, at the chosen speed. Unscaled, so Time.timeScale plays no part.
            backlog += Time.unscaledDeltaTime * speed;
            budget.Restart();
            while (backlog >= StepSeconds && budget.Elapsed.TotalMilliseconds < FrameBudgetMilliseconds)
            {
                Step();
                backlog -= StepSeconds;
            }

            // Out of time this frame: drop the debt rather than spiral. The run just goes slower than asked.
            if (backlog > StepSeconds) backlog = 0;
        }

        void MeasureSpeed()
        {
            float now = Time.unscaledTime;
            if (measuredSince <= 0f)
            {
                measuredSince = now;
                measuredSimSeconds = SimTime;
                return;
            }

            float window = now - measuredSince;
            if (window < 1f) return;

            AchievedSpeed = Mathf.Max(0f, SimTime - measuredSimSeconds) / window;
            measuredSince = now;
            measuredSimSeconds = SimTime;
        }
    }
}
