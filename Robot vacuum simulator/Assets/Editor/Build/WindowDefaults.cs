using UnityEditor;
using UnityEngine;

namespace RobotVacuum.EditorTools
{
    /// <summary>
    /// Keeps the Windows client's player settings at "open in a resizable window, with no Unity splash screen",
    /// whoever builds it and however (the
    /// Robot Vacuum > Build > Windows menu, Build Profiles, or CI). Applied when the editor loads and before every
    /// Windows build; see also Scripts/Display/AppWindow.cs, which handles a PC that remembers full screen.
    /// </summary>
    [InitializeOnLoad]
    public static class WindowDefaults
    {
        public const int Width = 1600;
        public const int Height = 900;

        static WindowDefaults() => EditorApplication.delayCall += Apply;

        public static void Apply()
        {
            bool changed = false;
            if (PlayerSettings.fullScreenMode != FullScreenMode.Windowed) { PlayerSettings.fullScreenMode = FullScreenMode.Windowed; changed = true; }
            if (!PlayerSettings.resizableWindow) { PlayerSettings.resizableWindow = true; changed = true; }
            if (PlayerSettings.defaultIsNativeResolution) { PlayerSettings.defaultIsNativeResolution = false; changed = true; }
            if (PlayerSettings.defaultScreenWidth != Width) { PlayerSettings.defaultScreenWidth = Width; changed = true; }
            if (PlayerSettings.defaultScreenHeight != Height) { PlayerSettings.defaultScreenHeight = Height; changed = true; }
            if (!PlayerSettings.allowFullscreenSwitch) { PlayerSettings.allowFullscreenSwitch = true; changed = true; }
            // No "Made with Unity" screen at start (allowed on every Unity 6 licence).
            if (PlayerSettings.SplashScreen.show) { PlayerSettings.SplashScreen.show = false; changed = true; }
            if (PlayerSettings.SplashScreen.showUnityLogo) { PlayerSettings.SplashScreen.showUnityLogo = false; changed = true; }
            if (changed)
            {
                AssetDatabase.SaveAssets();
                Debug.Log($"[WindowDefaults] Player settings set to a resizable {Width}x{Height} window with no splash screen.");
            }
        }
    }
}
