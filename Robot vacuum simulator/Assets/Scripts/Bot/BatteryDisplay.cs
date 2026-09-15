using System;
using System.Globalization;
using UnityEngine;

namespace RobotVacuum
{
    public class BatteryDisplay : MonoBehaviour
    {
        [SerializeField] Battery battery;
        [SerializeField] Vector2 screenPadding = new Vector2(12f, 12f);
        [SerializeField] Vector2 labelSize = new Vector2(360f, 165f);

        string batteryLifeInput;
        GUIStyle labelStyle;
        GUIStyle inputStyle;
        GUIStyle buttonStyle;

        void Awake()
        {
            if (battery == null)
                battery = GetComponent<Battery>();

            if (battery == null)
                battery = FindAnyObjectByType<Battery>();

            if (battery != null)
                batteryLifeInput = battery.BatteryLifeSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        }

        void OnGUI()
        {
            if (battery == null) return;

            float percentage = battery.BatteryLifeSeconds > 0f
                ? battery.CurrentLifeSeconds / battery.BatteryLifeSeconds * 100f
                : 0f;

            EnsureStyles();

            float left = screenPadding.x;
            float top = screenPadding.y;
            GUI.Label(new Rect(left, top, labelSize.x, 36f),
                $"Battery: {percentage:0}% ({battery.CurrentLifeSeconds:0.0}s)", labelStyle);

            TimeSpan elapsed = TimeSpan.FromSeconds(Mathf.Max(0f, Time.timeSinceLevelLoad));
            GUI.Label(new Rect(left, top + 42f, labelSize.x, 36f),
                $"Runtime: {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}", labelStyle);

            GUI.Label(new Rect(left, top + 84f, 150f, 36f), "Battery life (seconds)", labelStyle);
            batteryLifeInput = GUI.TextField(
                new Rect(left + 155f, top + 80f, 90f, 42f), batteryLifeInput, inputStyle);

            if (GUI.Button(new Rect(left + 255f, top + 80f, 90f, 42f), "Apply", buttonStyle)
                && float.TryParse(batteryLifeInput, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float seconds)
                && seconds >= 0f)
            {
                battery.SetBatteryLifeSeconds(seconds);
                batteryLifeInput = battery.BatteryLifeSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            }
        }

        void EnsureStyles()
        {
            if (labelStyle != null) return;

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };

            inputStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 24,
                normal = { textColor = Color.white },
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 24,
                normal = { textColor = Color.white },
            };
        }
    }
}
