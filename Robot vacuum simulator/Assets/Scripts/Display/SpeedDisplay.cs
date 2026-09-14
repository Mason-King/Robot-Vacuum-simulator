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
        GUIStyle labelStyle;
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

            GUI.Label(
                new Rect(screenPadding.x, screenPadding.y, 360f, 36f),
                $"Speed: {displayedSpeed:0.00} {displayedUnit}",
                labelStyle);

            if (GUI.Button(new Rect(screenPadding.x, screenPadding.y + 40f, 150f, 38f),
                    "Switch units", buttonStyle))
            {
                unit = unit == SpeedUnit.MetersPerSecond
                    ? SpeedUnit.FeetPerSecond
                    : SpeedUnit.MetersPerSecond;
                driveSpeedInput = string.Empty;
                reverseSpeedInput = string.Empty;
                RefreshInputs();
            }

            string inputUnit = unit == SpeedUnit.MetersPerSecond ? "m/s" : "ft/s";
            DrawLinearSetting("Drive", inputUnit, screenPadding.y + 84f, ref driveSpeedInput,
                vacuum.DriveSpeedMetersPerSecond, vacuum.SetDriveSpeedMetersPerSecond);
            DrawLinearSetting("Reverse", inputUnit, screenPadding.y + 128f, ref reverseSpeedInput,
                vacuum.ReverseSpeedMetersPerSecond, vacuum.SetReverseSpeedMetersPerSecond);
            DrawTurnSetting(screenPadding.y + 172f);
        }

        void DrawLinearSetting(string label, string unitLabel, float top, ref string input,
            float metersPerSecond, System.Action<float> apply)
        {
            GUI.Label(new Rect(screenPadding.x, top, 170f, 36f), $"{label} ({unitLabel})", settingLabelStyle);
            input = GUI.TextField(new Rect(screenPadding.x + 175f, top - 4f, 90f, 42f), input, inputStyle);

            if (GUI.Button(new Rect(screenPadding.x + 275f, top - 4f, 100f, 42f), "Apply", buttonStyle)
                && float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float requested)
                && requested >= 0f)
            {
                apply(unit == SpeedUnit.MetersPerSecond ? requested : requested / 3.28084f);
                input = FormatSpeed(metersPerSecond);
            }
        }

        void DrawTurnSetting(float top)
        {
            GUI.Label(new Rect(screenPadding.x, top, 170f, 36f), "Turn (deg/s)", settingLabelStyle);
            turnSpeedInput = GUI.TextField(
                new Rect(screenPadding.x + 175f, top - 4f, 90f, 42f), turnSpeedInput, inputStyle);

            if (GUI.Button(new Rect(screenPadding.x + 275f, top - 4f, 100f, 42f), "Apply", buttonStyle)
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
            if (labelStyle != null) return;

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 28,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };

            settingLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
            };

            inputStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 22,
                normal = { textColor = Color.white },
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 18,
                normal = { textColor = Color.white },
            };
        
        }
    }
}
