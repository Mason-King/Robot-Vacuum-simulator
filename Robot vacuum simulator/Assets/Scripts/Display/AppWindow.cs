using RobotVacuum.Headless;
using UnityEngine;

namespace RobotVacuum
{
    /// <summary>
    /// The program always opens in a resizable window (the player settings make the window resizable; see
    /// Editor/Build/WindowDefaults.cs). Unity remembers the last window mode per computer, so a PC that once ran it
    /// full screen would otherwise keep opening full screen: on start, a full-screen GUI is put back in a window of
    /// about 80% of the display. Headless jobs and workers have no window and are left alone. Alt+Enter still switches
    /// to full screen for the session.
    /// </summary>
    public static class AppWindow
    {
        public const int MinWidth = 1280;
        public const int MinHeight = 760;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void OpenWindowed()
        {
            if (Application.isEditor || Application.isBatchMode || HeadlessBootstrap.IsHeadlessJob) return;
            if (Screen.fullScreenMode == FullScreenMode.Windowed) return;

            var display = Screen.currentResolution;
            int width = Mathf.Clamp(Mathf.RoundToInt(display.width * 0.8f), Mathf.Min(MinWidth, display.width), display.width);
            int height = Mathf.Clamp(Mathf.RoundToInt(display.height * 0.8f), Mathf.Min(MinHeight, display.height), display.height);
            Screen.SetResolution(width, height, FullScreenMode.Windowed);
        }
    }
}
