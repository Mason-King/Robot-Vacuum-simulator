using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// The frame budget behind <see cref="SimulationSpeed"/>. Active only while the time scale is the requested speed
    /// and above 1 (not when paused, and not after a caller restored the time settings). Each frame it compares the
    /// last frame's real duration with the budget and adjusts how many physics steps the next frame may run, through
    /// Time.maximumDeltaTime (simulated time beyond that is dropped, not owed, so there is no catch-up spiral).
    /// </summary>
    [DefaultExecutionOrder(-2000)]
    sealed class SpeedGovernor : MonoBehaviour
    {
        static SpeedGovernor instance;
        static float actual = 1f;

        float allowedSteps = 8f;
        int stepsThisFrame;

        public static float Actual => actual;

        public static void Ensure()
        {
            if (instance != null || !Application.isPlaying) return;
            var go = new GameObject("Simulation Speed Governor") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<SpeedGovernor>();
        }

        void FixedUpdate() => stepsThisFrame++;

        void Update()
        {
            float real = Time.unscaledDeltaTime;
            float step = Time.fixedDeltaTime;

            if (real > 0f)
            {
                float measured = Time.timeScale > 0f ? stepsThisFrame * step / real : 0f;
                actual = Mathf.Lerp(actual, measured, 0.1f);
            }

            bool active = Time.timeScale > 1f && Mathf.Approximately(Time.timeScale, SimulationSpeed.Requested);
            if (active)
            {
                float ceilingSteps = Mathf.Max(1f, SimulationSpeed.Ceiling / step);
                if (real > SimulationSpeed.FrameBudgetSeconds) allowedSteps = Mathf.Max(1f, allowedSteps * 0.8f);
                else if (stepsThisFrame >= Mathf.FloorToInt(allowedSteps)) allowedSteps = Mathf.Min(ceilingSteps, allowedSteps * 1.1f + 1f);
                Time.maximumDeltaTime = Mathf.Max(step, Mathf.Floor(allowedSteps) * step);
            }
            stepsThisFrame = 0;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }
    }
}
