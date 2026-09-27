using UnityEngine;
using UnityEngine.Serialization;

namespace RobotVacuum
{
    public class Battery : MonoBehaviour
    {
        [Tooltip("Available work units before the battery is empty.")]
        [Min(0f)]
        [SerializeField, FormerlySerializedAs("batteryLifeSeconds")] float capacity = 300f;

        [Tooltip("Battery work units used per metre travelled on a neutral surface.")]
        [Min(0f)]
        [SerializeField] float distanceEnergyPerMeter = 1f;

        [Tooltip("Battery work units used per degree of rotation on a neutral surface.")]
        [Min(0f)]
        [SerializeField] float turnEnergyPerDegree = 0.01f;

        public float Capacity
        {
            get => capacity;
            set => SetCapacity(value);
        }

        public float CurrentCharge { get; private set; }
        public bool CanOperate => CurrentCharge > 0f;

        void Awake() => ResetCharge();

        public void ConsumeWork(float distanceMeters, float turnDegrees, float surfaceMultiplier)
        {
            float distanceCost = Mathf.Max(0f, distanceMeters) * distanceEnergyPerMeter;
            float turnCost = Mathf.Abs(turnDegrees) * turnEnergyPerDegree;
            float surfaceCost = Mathf.Max(0f, surfaceMultiplier);
            CurrentCharge = Mathf.Max(0f, CurrentCharge - (distanceCost + turnCost) * surfaceCost);
        }

        public void SetCapacity(float workUnits)
        {
            capacity = Mathf.Max(0f, workUnits);
            ResetCharge();
        }

        public void ResetCharge()
        {
            CurrentCharge = capacity;
        }
    }
}
