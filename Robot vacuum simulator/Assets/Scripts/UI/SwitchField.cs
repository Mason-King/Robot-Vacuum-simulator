using System;
using UnityEngine;
using UnityEngine.UI;

namespace RobotVacuum.UI
{
    /// <summary>
    /// A pill switch with a label, for on/off settings: a Toggle whose track turns accent and whose knob
    /// slides right when it is on.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SwitchField : MonoBehaviour
    {
        [SerializeField] Toggle toggle;
        [SerializeField] Image track;
        [SerializeField] RectTransform knob;
        [SerializeField] float travel = 16f;

        bool hooked;

        public event Action<bool> ValueChanged;

        public bool Value => toggle != null && toggle.isOn;

        void Awake() => Hook();

        void Hook()
        {
            if (hooked || toggle == null) return;
            hooked = true;
            toggle.onValueChanged.AddListener(on =>
            {
                Refresh();
                ValueChanged?.Invoke(on);
            });
            Refresh();
        }

        public void SetValueWithoutNotify(bool on)
        {
            Hook();
            if (toggle != null) toggle.SetIsOnWithoutNotify(on);
            Refresh();
        }

        void Refresh()
        {
            bool on = Value;
            if (track != null) track.color = on ? UiTheme.Accent : UiTheme.Surface3;
            if (knob != null) knob.anchoredPosition = new Vector2(on ? travel * 0.5f : -travel * 0.5f, 0f);
        }
    }
}
