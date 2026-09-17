using System.Globalization;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum
{
    [DisallowMultipleComponent]
    public class SpeedDisplay : MonoBehaviour
    {
        enum SpeedUnit { MetersPerSecond, FeetPerSecond }

        [SerializeField] VacuumRobot vacuum;
        [SerializeField] Vector2 screenPadding = new Vector2(12f, 250f);

        SpeedUnit unit;
        string driveSpeedInput;
        string reverseSpeedInput;
        string turnSpeedInput;
        GUIStyle panelStyle;
        GUIStyle titleStyle;
        GUIStyle valueStyle;
        GUIStyle settingLabelStyle;
        GUIStyle inputStyle;
        GUIStyle buttonStyle;
        static SpeedDisplay activeDisplay;

        void Awake()
        {
            if (activeDisplay != null && activeDisplay != this)
            {
                Destroy(this);
                return;
            }

            activeDisplay = this;

            if (vacuum == null)
                vacuum = GetComponent<VacuumRobot>();

            if (vacuum == null)
                vacuum = FindAnyObjectByType<VacuumRobot>();

            RefreshInputs();
        }

        void OnDestroy()
        {
            if (activeDisplay == this) activeDisplay = null;
        }

        void OnGUI()
        {
            if (vacuum == null) return;

            EnsureStyles();

            float metersPerSecond = vacuum.SpeedMetersPerSecond;
            bool showingFeetPerSecond = unit == SpeedUnit.FeetPerSecond;
            float displayedSpeed = showingFeetPerSecond
                ? metersPerSecond * 3.28084f
                : metersPerSecond;
            string displayedUnit = showingFeetPerSecond ? "ft/s" : "m/s";

            float left = screenPadding.x;
            float top = screenPadding.y;
            GUI.Box(new Rect(left, top, 400f, 242f), GUIContent.none, panelStyle);
            GUI.Label(new Rect(left + 18f, top + 14f, 150f, 24f), "SPEED", titleStyle);
            GUI.Label(new Rect(left + 18f, top + 35f, 235f, 42f),
                $"{displayedSpeed:0.00} {displayedUnit}", valueStyle);

            if (GUI.Button(new Rect(left + 274f, top + 33f, 108f, 34f),
                    showingFeetPerSecond ? "Use m/s" : "Use ft/s", buttonStyle))
            {
                unit = unit == SpeedUnit.MetersPerSecond
                    ? SpeedUnit.FeetPerSecond
                    : SpeedUnit.MetersPerSecond;
                driveSpeedInput = string.Empty;
                reverseSpeedInput = string.Empty;
                RefreshInputs();
            }

            string inputUnit = unit == SpeedUnit.MetersPerSecond ? "m/s" : "ft/s";
            DrawLinearSetting("Drive", inputUnit, top + 94f, ref driveSpeedInput,
                vacuum.DriveSpeedMetersPerSecond, vacuum.SetDriveSpeedMetersPerSecond);
            DrawLinearSetting("Reverse", inputUnit, top + 138f, ref reverseSpeedInput,
                vacuum.ReverseSpeedMetersPerSecond, vacuum.SetReverseSpeedMetersPerSecond);
            DrawTurnSetting(top + 182f);
        }

        void DrawLinearSetting(string label, string unitLabel, float top, ref string input,
            float metersPerSecond, System.Action<float> apply)
        {
            GUI.Label(new Rect(screenPadding.x + 18f, top, 150f, 30f), $"{label} ({unitLabel})", settingLabelStyle);
            input = GUI.TextField(new Rect(screenPadding.x + 174f, top - 3f, 90f, 34f), input, inputStyle);

            if (GUI.Button(new Rect(screenPadding.x + 274f, top - 3f, 108f, 34f), "Apply", buttonStyle)
                && float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float requested)
                && requested >= 0f)
            {
                apply(unit == SpeedUnit.MetersPerSecond ? requested : requested / 3.28084f);
                input = FormatSpeed(metersPerSecond);
            }
        }

        void DrawTurnSetting(float top)
        {
            GUI.Label(new Rect(screenPadding.x + 18f, top, 150f, 30f), "Turn (deg/s)", settingLabelStyle);
            turnSpeedInput = GUI.TextField(
                new Rect(screenPadding.x + 174f, top - 3f, 90f, 34f), turnSpeedInput, inputStyle);

            if (GUI.Button(new Rect(screenPadding.x + 274f, top - 3f, 108f, 34f), "Apply", buttonStyle)
                && float.TryParse(turnSpeedInput, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out float requested)
                && requested >= 0f)
            {
                vacuum.SetTurnSpeedDegreesPerSecond(requested);
                turnSpeedInput = vacuum.TurnSpeedDegreesPerSecond.ToString("0.00", CultureInfo.InvariantCulture);
            }
        }

        void RefreshInputs()
        {
            if (vacuum == null) return;

            driveSpeedInput = FormatSpeed(vacuum.DriveSpeedMetersPerSecond);
            reverseSpeedInput = FormatSpeed(vacuum.ReverseSpeedMetersPerSecond);
            turnSpeedInput = vacuum.TurnSpeedDegreesPerSecond.ToString("0.00", CultureInfo.InvariantCulture);
        }

        string FormatSpeed(float metersPerSecond)
        {
            float displayedSpeed = unit == SpeedUnit.MetersPerSecond
                ? metersPerSecond
                : metersPerSecond * 3.28084f;
            return displayedSpeed.ToString("0.00", CultureInfo.InvariantCulture);
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
                normal = { textColor = new Color(1f, 0.72f, 0.35f) },
            };

            valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 30,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };

            settingLabelStyle = new GUIStyle(GUI.skin.label)
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
                    background = MakeTexture(new Color(0.55f, 0.32f, 0.12f, 1f)),
                },
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
