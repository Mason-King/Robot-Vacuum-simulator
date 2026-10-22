using UnityEngine;

namespace RobotVacuum.Sim
{
    public static class SimulationSpeed
    {
        public const float MaxPlaybackSpeed = 100f;

        public static float ClampPlaybackSpeed(float multiplier) =>
            Mathf.Clamp(multiplier, 0.01f, MaxPlaybackSpeed);

        public static void Apply(float multiplier, float baseMaximumDeltaTime)
        {
            float speed = ClampPlaybackSpeed(multiplier);
            Time.timeScale = speed;
            Time.maximumDeltaTime = Mathf.Max(
                Time.fixedDeltaTime,
                baseMaximumDeltaTime * Mathf.Max(1f, speed));
        }
    }
}
