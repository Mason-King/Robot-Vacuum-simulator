using UnityEditor;

namespace RobotVacuum.EditorTools
{
    /// <summary>
    /// The intro video plays when the built program opens. In the editor it is off by default, so pressing Play goes
    /// straight to the start screen; tick this to see it there too.
    /// </summary>
    static class IntroVideoMenu
    {
        const string MenuPath = "Robot Vacuum/Play Intro Video in Play Mode";

        [MenuItem(MenuPath, priority = 200)]
        static void Toggle() => EditorPrefs.SetBool(IntroVideo.EditorPrefsKey, !EditorPrefs.GetBool(IntroVideo.EditorPrefsKey, false));

        [MenuItem(MenuPath, true)]
        static bool Validate()
        {
            Menu.SetChecked(MenuPath, EditorPrefs.GetBool(IntroVideo.EditorPrefsKey, false));
            return true;
        }
    }
}
