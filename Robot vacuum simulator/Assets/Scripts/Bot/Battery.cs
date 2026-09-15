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

        void Update()
        {
            if (Application.isPlaying && CanOperate)
                Consume(Time.deltaTime);
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
