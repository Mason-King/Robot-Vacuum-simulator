using UnityEngine;
using UnityEngine.Serialization;

namespace RobotVacuum
{
    public class Battery : MonoBehaviour
    {
        public const float MaxCapacity = 10000f;

        [Tooltip("Available work units before the battery is empty.")]
        [Range(0f, MaxCapacity)]
        [SerializeField, FormerlySerializedAs("batteryLifeSeconds")] float capacity = 300f;

        [Tooltip("Battery work units used per metre travelled on a neutral surface.")]
        [Min(0f)]
        [SerializeField] float distanceEnergyPerMeter = 1f;

        [Tooltip("Battery work units used per degree of rotation on a neutral surface.")]
        [Min(0f)]
        [SerializeField] float turnEnergyPerDegree = 0.01f;

        [Tooltip("Battery work units used per second when the vacuum is trying to move but is stuck.")]
        [Min(0f)]
        [SerializeField] float stalledEnergyPerSecond = 0.03f;

        public float Capacity
        {
            get => capacity;
            set => SetCapacity(value);
        }

        public float CurrentCharge { get; private set; }
        public float ChargePercentage => capacity > 0f ? CurrentCharge / capacity * 100f : 0f;
        public bool CanOperate => CurrentCharge > 0f;

        void Awake() => SetCapacity(capacity);

        public void ConsumeWork(float distanceMeters, float turnDegrees, float surfaceMultiplier)
        {
            float distanceCost = Mathf.Max(0f, distanceMeters) * distanceEnergyPerMeter;
            float turnCost = Mathf.Abs(turnDegrees) * turnEnergyPerDegree;
            float surfaceCost = Mathf.Max(0f, surfaceMultiplier);
            CurrentCharge = Mathf.Max(0f, CurrentCharge - (distanceCost + turnCost) * surfaceCost);
        }

        public void ConsumeStalledTime(float seconds)
        {
            float timeCost = Mathf.Max(0f, seconds) * stalledEnergyPerSecond;
            CurrentCharge = Mathf.Max(0f, CurrentCharge - timeCost);
        }

        public void SetCapacity(float workUnits)
        {
            capacity = Mathf.Clamp(workUnits, 0f, MaxCapacity);
            ResetCharge();
        }

        public void ResetCharge()
        {
            CurrentCharge = capacity;
        }
    }
}
