using System;
using UnityEngine;
using UnityEngine.UI;

namespace RobotVacuum.UI
{
    /// <summary>
    /// A labelled slider with a live read-out of its value, e.g. "0.65 m/s". The range and the read-out's
    /// format are set from code with <see cref="Configure"/>, so one prefab part serves every setting.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SliderField : MonoBehaviour
    {
        [SerializeField] Text label;
        [SerializeField] Slider slider;
        [SerializeField] Text valueText;

        Func<float, string> format = v => v.ToString("0.##");
        bool hooked;

        public event Action<float> ValueChanged;

        public float Value => slider != null ? slider.value : 0f;

        void Awake() => Hook();

        void Hook()
        {
            if (hooked || slider == null) return;
            hooked = true;
            slider.onValueChanged.AddListener(value =>
            {
                ShowValue(value);
                ValueChanged?.Invoke(value);
            });
        }

        public void Configure(string text, float min, float max, float value, Func<float, string> formatter)
        {
            Hook();
            if (label != null && !string.IsNullOrEmpty(text)) label.text = text;
            if (formatter != null) format = formatter;
            if (slider == null) return;

            slider.minValue = min;
            slider.maxValue = Mathf.Max(min + 1e-4f, max);
            slider.SetValueWithoutNotify(Mathf.Clamp(value, min, max));
            ShowValue(slider.value);
        }

        void ShowValue(float value)
        {
            if (valueText != null) valueText.text = format(value);
        }
    }
}
