using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// The "run finished" panel for Graphics runs: why the run ended, its headline numbers, and buttons
    /// that the owner of the run wires to <see cref="SimulationSession.Restart"/>, the run's analysis (when the
    /// run was recorded) and <see cref="SimulationSession.Close"/>. Built entirely in code with UGUI, so there is no prefab to wire
    /// up; the GUI owner can restyle or replace it later.
    ///
    /// This is presentation only. It knows nothing about sessions, and headless runs never create one.
    /// </summary>
    public sealed class RunEndPanel : MonoBehaviour
    {
        const int SortingOrder = 500; // above the run's HUD

        GameObject panel;
        Text titleText;
        Text detailText;
        Action onRunAgain;
        Action onViewAnalysis;
        Action onClose;
        RectTransform runAgainButton;
        RectTransform analysisButton;
        RectTransform closeButton;

        /// <summary>Builds the (hidden) panel under <paramref name="parent"/> and makes sure UI input works.</summary>
        public static RunEndPanel Create(Transform parent)
        {
            EnsureEventSystem();

            var root = new GameObject("Run End Panel", typeof(RectTransform));
            root.transform.SetParent(parent, false);

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;

            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            root.AddComponent<GraphicRaycaster>();

            var view = root.AddComponent<RunEndPanel>();
            view.Build(root.transform);
            view.Hide();
            return view;
        }

        /// <summary>
        /// Shows the panel. Each button hides it and then calls its action. "View analysis" appears only when
        /// <paramref name="viewAnalysis"/> is given (the run was recorded).
        /// </summary>
        public void Show(string title, string detail, Action runAgain, Action close, Action viewAnalysis = null)
        {
            titleText.text = title;
            detailText.text = detail;
            onRunAgain = runAgain;
            onViewAnalysis = viewAnalysis;
            onClose = close;

            // Two buttons or three, centred along the bottom.
            bool analysis = viewAnalysis != null;
            analysisButton.gameObject.SetActive(analysis);
            runAgainButton.anchoredPosition = new Vector2(analysis ? -195f : -130f, ButtonRow);
            analysisButton.anchoredPosition = new Vector2(0f, ButtonRow);
            closeButton.anchoredPosition = new Vector2(analysis ? 195f : 130f, ButtonRow);
            panel.SetActive(true);
        }

        const float ButtonRow = -95f;

        public void Hide() => panel.SetActive(false);

        public bool IsShowing => panel != null && panel.activeSelf;

        void Build(Transform root)
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(root, false);
            var panelRect = (RectTransform)panel.transform;
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(620f, 300f);
            panel.GetComponent<Image>().color = new Color(0.09f, 0.10f, 0.13f, 0.94f);

            titleText = AddText(panel.transform, font, 34, FontStyle.Bold, new Vector2(0f, 95f), new Vector2(560f, 50f));
            detailText = AddText(panel.transform, font, 22, FontStyle.Normal, new Vector2(0f, 15f), new Vector2(560f, 100f));

            runAgainButton = AddButton(panel.transform, font, "Run again", new Vector2(-130f, ButtonRow), () => Press(onRunAgain));
            analysisButton = AddButton(panel.transform, font, "View analysis", new Vector2(0f, ButtonRow), () => Press(onViewAnalysis));
            closeButton = AddButton(panel.transform, font, "Close", new Vector2(130f, ButtonRow), () => Press(onClose));
        }

        void Press(Action action)
        {
            Hide();
            action?.Invoke();
        }

        static Text AddText(Transform parent, Font font, int size, FontStyle style, Vector2 position, Vector2 boxSize)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchoredPosition = position;
            rect.sizeDelta = boxSize;

            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.93f, 0.94f, 0.96f);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        static RectTransform AddButton(Transform parent, Font font, string label, Vector2 position, Action onClick)
        {
            var go = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(180f, 56f);

            var image = go.GetComponent<Image>();
            image.color = new Color(0.20f, 0.45f, 0.85f);

            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick());

            var text = AddText(go.transform, font, 24, FontStyle.Bold, Vector2.zero, rect.sizeDelta);
            text.text = label;
            return rect;
        }

        // UGUI buttons need an EventSystem with an input module in the scene. This project uses the Input
        // System package only (Active Input Handling = Input System), so the module is the Input System's
        // own. It is looked up by name so this assembly needs no reference to the Input System package.
        static void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null) return;

            var go = new GameObject("EventSystem", typeof(EventSystem));

            var inputSystemModule = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSystemModule != null)
            {
                var module = go.AddComponent(inputSystemModule);
                // Gives the module the standard point/click/navigate actions when none are assigned.
                inputSystemModule.GetMethod("AssignDefaultActions")?.Invoke(module, null);
            }
            else
            {
                go.AddComponent<StandaloneInputModule>();
            }
        }
    }
}
