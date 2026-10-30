using RobotVacuum.UI;
using UnityEngine;
using UnityEngine.UI;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The app's first screen: start a simulation (which goes to the floor plan list), open Analysis, or
    /// change the global settings every run starts from. The pages are uGUI objects in the StartScreen
    /// prefab (built by Robot Vacuum ▸ UI ▸ Build Start Screen); this only wires them up.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StartScreenApp : MonoBehaviour
    {
        [Header("Pages")]
        [SerializeField] GameObject homePage;
        [SerializeField] GameObject settingsPage;
        [SerializeField] GameObject analysisPage;

        [Header("Home")]
        [SerializeField] Button startButton;
        [SerializeField] Button analysisButton;
        [SerializeField] Button settingsButton;
        [SerializeField] Button quitButton;

        [Header("Settings")]
        [SerializeField] Button settingsBackButton;
        [SerializeField] Button resetButton;
        [SerializeField] SegmentedControl units;
        [SerializeField] RunSettingsFormView form;

        [Header("Analysis")]
        [SerializeField] Button analysisBackButton;

        void Start()
        {
            if (homePage == null)
            {
                Debug.LogError("StartScreenApp has no pages. Run Robot Vacuum ▸ UI ▸ Build Start Screen to create the start screen prefab.", this);
                return;
            }

            UiEventSystem.Ensure();

            Click(startButton, () => SimLauncher.OpenFloorPlans());
            Click(analysisButton, ShowAnalysis);
            Click(settingsButton, ShowSettings);
            Click(settingsBackButton, ShowHome);
            Click(analysisBackButton, ShowHome);
            Click(resetButton, () =>
            {
                RunPreferences.ResetGlobalToDefaults();
                form?.Refresh();
            });

            // Quitting does nothing inside the Unity Editor, so only offer it in builds.
            if (quitButton != null)
            {
                quitButton.gameObject.SetActive(!Application.isEditor);
                quitButton.onClick.AddListener(Application.Quit);
            }

            if (units != null)
            {
                units.SetSelectedWithoutNotify(RunPreferences.UseFeet ? 0 : 1);
                units.SelectionChanged += index =>
                {
                    RunPreferences.UseFeet = index == 0;
                    form?.Refresh(); // the read-outs switch unit too
                };
            }

            if (form != null)
            {
                form.Bind(RunPreferences.Global);
                form.Changed += RunPreferences.SaveGlobal;
            }

            ShowHome();
        }

        static void Click(Button button, System.Action action)
        {
            if (button != null) button.onClick.AddListener(() => action());
        }

        void ShowHome() => ShowPage(homePage);

        void ShowSettings()
        {
            form?.Refresh();
            ShowPage(settingsPage);
        }

        /// <summary>
        /// The Analysis page. Placeholder until the analysis work from its own branch is merged in: put that
        /// page's content in the prefab's Analysis page and fill it from here.
        /// </summary>
        void ShowAnalysis() => ShowPage(analysisPage);

        void ShowPage(GameObject page)
        {
            if (homePage != null) homePage.SetActive(page == homePage);
            if (settingsPage != null) settingsPage.SetActive(page == settingsPage);
            if (analysisPage != null) analysisPage.SetActive(page == analysisPage);
        }
    }
}
