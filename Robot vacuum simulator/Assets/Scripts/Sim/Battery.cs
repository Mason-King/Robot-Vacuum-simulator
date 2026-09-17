using UnityEngine;

namespace RobotVacuum
{
    public class Battery : MonoBehaviour
    {
        [Tooltip("How long the vacuum can operate before its battery is empty, in seconds.")]
        [Min(0f)]
        [SerializeField] float batteryLifeSeconds = 300f;

        public float BatteryLifeSeconds
        {
            get => batteryLifeSeconds;
            set => SetBatteryLifeSeconds(value);
        }

        public float CurrentLifeSeconds { get; private set; }
        public bool CanOperate => CurrentLifeSeconds > 0f;

        void Awake() => ResetBattery();

        // Drains on the physics step, like everything else in the simulation. Frame time would make a run
        // depend on how fast the machine drew it, so a headless run and a watched one would disagree.
        void FixedUpdate()
        {
            if (Application.isPlaying && CanOperate)
                Consume(Time.fixedDeltaTime);
        }

        public void Consume(float seconds)
        {
            CurrentLifeSeconds = Mathf.Max(0f, CurrentLifeSeconds - Mathf.Max(0f, seconds));
        }

        public void SetBatteryLifeSeconds(float seconds)
        {
            batteryLifeSeconds = Mathf.Max(0f, seconds);
            ResetBattery();
        }

        public void ResetBattery()
        {
            CurrentLifeSeconds = batteryLifeSeconds;
        }
    }
}
