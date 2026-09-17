using System.Globalization;
using UnityEngine;

namespace RobotVacuum
{
    public class BatteryDisplay : MonoBehaviour
    {
        [SerializeField] Battery battery;
        [SerializeField] Vector2 screenPadding = new Vector2(12f, 12f);

        string batteryLifeInput;
        GUIStyle panelStyle;
        GUIStyle titleStyle;
        GUIStyle valueStyle;
        GUIStyle mutedStyle;
        GUIStyle fieldLabelStyle;
        GUIStyle inputStyle;
        GUIStyle buttonStyle;
        GUIStyle progressBackgroundStyle;
        GUIStyle progressFillStyle;

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
            float clampedPercentage = Mathf.Clamp01(percentage / 100f);

            EnsureStyles();

            float left = screenPadding.x;
            float top = screenPadding.y;
            Rect panel = new Rect(left, top, 360f, 178f);
            GUI.Box(panel, GUIContent.none, panelStyle);
            GUI.Label(new Rect(left + 18f, top + 14f, 150f, 24f), "BATTERY", titleStyle);
            GUI.Label(new Rect(left + 18f, top + 37f, 220f, 42f),
                $"{percentage:0}%", valueStyle);
            GUI.Label(new Rect(left + 242f, top + 49f, 100f, 24f),
                FormatDuration(battery.CurrentLifeSeconds) + " left", mutedStyle);

            Rect progress = new Rect(left + 18f, top + 86f, 324f, 12f);
            GUI.Box(progress, GUIContent.none, progressBackgroundStyle);
            GUI.Box(new Rect(progress.x, progress.y, progress.width * clampedPercentage, progress.height),
                GUIContent.none, progressFillStyle);

            GUI.Label(new Rect(left + 18f, top + 111f, 145f, 24f), "Battery life", fieldLabelStyle);
            batteryLifeInput = GUI.TextField(
                new Rect(left + 142f, top + 106f, 100f, 34f), batteryLifeInput, inputStyle);

            if (GUI.Button(new Rect(left + 252f, top + 106f, 90f, 34f), "Apply", buttonStyle)
                && float.TryParse(batteryLifeInput, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float seconds)
                && seconds >= 0f)
            {
                battery.SetBatteryLifeSeconds(seconds);
                batteryLifeInput = battery.BatteryLifeSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            }
        }

        static string FormatDuration(float seconds)
        {
            int totalSeconds = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
        }

        void EnsureStyles()
        {
            if (panelStyle != null) return;

            panelStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTexture(new Color(0.035f, 0.05f, 0.075f, 0.94f)) },
                border = new RectOffset(8, 8, 8, 8),
            };

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.45f, 0.8f, 1f) },
            };

            valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 32,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };

            mutedStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.62f, 0.7f, 0.78f) },
            };

            fieldLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.75f, 0.8f, 0.86f) },
            };

            inputStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 16,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = Color.white },
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal =
                {
                    textColor = Color.white,
                    background = MakeTexture(new Color(0.12f, 0.42f, 0.62f, 1f)),
                },
            };

            progressBackgroundStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTexture(new Color(0.12f, 0.16f, 0.21f, 1f)) },
            };

            progressFillStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTexture(new Color(0.25f, 0.76f, 0.62f, 1f)) },
            };
        }

        static Texture2D MakeTexture(Color color)
        {
            var texture = new Texture2D(1, 1);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }
    }
}
