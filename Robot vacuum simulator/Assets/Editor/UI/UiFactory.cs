using System.Collections.Generic;
using RobotVacuum.LevelEditor;
using RobotVacuum.UI;
using UnityEditor;
using IconKind = RobotVacuum.LevelEditor.IconKind;
using UnityEngine;
using UnityEngine.UI;

namespace RobotVacuum.EditorTools.UI
{
    public enum ButtonKind { Primary, Secondary, Ghost }

    /// <summary>
    /// Builds the app's uGUI parts as GameObjects in the design's style: dark rounded surfaces, one accent,
    /// layout groups for sizing. Used only by the prefab builders; once a prefab exists it is edited as
    /// an ordinary Unity object.
    /// </summary>
    public static class UiFactory
    {
        public static Font Font => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // ---------------------------------------------------------------- basics

        public static GameObject Go(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        public static RectTransform Stretch(GameObject go, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        /// <summary>A screen-space canvas scaled from a 1440 × 900 design, the root of every screen prefab.</summary>
        public static GameObject Canvas(string name, int sortingOrder = 0)
        {
            var go = Go(name, null);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1440f, 900f);
            scaler.matchWidthOrHeight = 0.5f;
            scaler.referencePixelsPerUnit = 100f;

            go.AddComponent<GraphicRaycaster>();
            return go;
        }

        public static Image Box(GameObject go, Color color, int radius = 0, bool raycast = false)
        {
            var image = go.AddComponent<Image>();
            if (radius > 0)
            {
                image.sprite = UiSprites.Rounded(radius);
                image.type = Image.Type.Sliced;
            }
            image.color = color;
            image.raycastTarget = raycast;
            return image;
        }

        /// <summary>A 1 px rounded outline over <paramref name="go"/>, outside its layout.</summary>
        public static Image Outline(GameObject go, Color color, int radius)
        {
            var border = Go("Border", go.transform);
            Stretch(border);
            border.AddComponent<LayoutElement>().ignoreLayout = true;
            var image = border.AddComponent<Image>();
            image.sprite = UiSprites.Ring(radius);
            image.type = Image.Type.Sliced;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        public static Text Label(Transform parent, string text, int size, Color color, FontStyle style = FontStyle.Normal,
            TextAnchor alignment = TextAnchor.MiddleLeft, bool wrap = false, string name = "Label")
        {
            var go = Go(name, parent);
            var label = go.AddComponent<Text>();
            label.font = Font;
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color;
            label.alignment = alignment;
            label.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.supportRichText = false;
            label.raycastTarget = false;
            return label;
        }

        public static IconGraphic Icon(Transform parent, IconKind kind, float size, Color color)
        {
            var go = Go($"Icon ({kind})", parent);
            var icon = go.AddComponent<IconGraphic>();
            icon.Kind = kind;
            icon.color = color;
            icon.raycastTarget = false;
            Size(go, size, size);
            return icon;
        }

        public static LayoutElement Size(GameObject go, float width = -1f, float height = -1f, float flexWidth = -1f, float flexHeight = -1f)
        {
            // TryGetComponent, not ??: in the editor GetComponent hands back a fake null that ?? sees as real.
            if (!go.TryGetComponent(out LayoutElement element)) element = go.AddComponent<LayoutElement>();
            if (width >= 0f) { element.minWidth = width; element.preferredWidth = width; }
            if (height >= 0f) { element.minHeight = height; element.preferredHeight = height; }
            if (flexWidth >= 0f) element.flexibleWidth = flexWidth;
            if (flexHeight >= 0f) element.flexibleHeight = flexHeight;
            return element;
        }

        public static HorizontalLayoutGroup Row(GameObject go, float spacing = 8f, RectOffset padding = null,
            TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            var row = go.AddComponent<HorizontalLayoutGroup>();
            row.spacing = spacing;
            row.padding = padding ?? new RectOffset();
            row.childAlignment = alignment;
            row.childControlWidth = true;
            row.childControlHeight = true;
            row.childForceExpandWidth = false;
            row.childForceExpandHeight = false;
            return row;
        }

        public static VerticalLayoutGroup Column(GameObject go, float spacing = 8f, RectOffset padding = null,
            TextAnchor alignment = TextAnchor.UpperLeft, bool expandWidth = true)
        {
            var column = go.AddComponent<VerticalLayoutGroup>();
            column.spacing = spacing;
            column.padding = padding ?? new RectOffset();
            column.childAlignment = alignment;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = expandWidth;
            column.childForceExpandHeight = false;
            return column;
        }

        public static RectOffset Pad(int left, int right, int top, int bottom) => new RectOffset(left, right, top, bottom);

        public static GameObject Spacer(Transform parent)
        {
            var go = Go("Spacer", parent);
            Size(go, flexWidth: 1f);
            return go;
        }

        // ---------------------------------------------------------------- controls

        public static Button Button(Transform parent, string text, IconKind? icon, ButtonKind kind, float height = 32f, string name = null)
        {
            var go = Go(name ?? $"Button ({text ?? icon?.ToString()})", parent);
            bool iconOnly = string.IsNullOrEmpty(text);

            Color fill = kind switch
            {
                ButtonKind.Primary => UiTheme.Accent,
                ButtonKind.Secondary => UiTheme.Surface2,
                _ => new Color(1f, 1f, 1f, 0.03f),
            };
            var image = Box(go, fill, 8, raycast: true);
            if (kind == ButtonKind.Ghost && !iconOnly) Outline(go, UiTheme.Border, 8);

            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            var colors = ColorBlock.defaultColorBlock;
            colors.normalColor = Color.white;
            colors.selectedColor = Color.white;
            // Ghost fills are nearly clear, so hover multiplies their alpha up rather than their brightness.
            colors.highlightedColor = kind == ButtonKind.Ghost ? new Color(1f, 1f, 1f, 3f) : new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = kind == ButtonKind.Ghost ? new Color(1f, 1f, 1f, 5f) : new Color(0.85f, 0.85f, 0.85f, 1f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.38f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };

            Color ink = kind == ButtonKind.Primary ? UiTheme.White : UiTheme.Text;
            Row(go, 6f, iconOnly ? Pad(0, 0, 0, 0) : Pad(height > 34f ? 18 : 12, height > 34f ? 18 : 12, 0, 0), TextAnchor.MiddleCenter);
            Size(go, iconOnly ? height : -1f, height);

            if (icon.HasValue) Icon(go.transform, icon.Value, iconOnly ? 18f : 16f, iconOnly ? UiTheme.Text2 : ink);
            if (!iconOnly) Label(go.transform, text, 13, ink, FontStyle.Bold, TextAnchor.MiddleCenter);
            return button;
        }

        public static Button IconButton(Transform parent, IconKind icon, string name = null) =>
            Button(parent, null, icon, ButtonKind.Ghost, 32f, name ?? $"Icon Button ({icon})");

        public static SegmentedControl Segmented(Transform parent, IReadOnlyList<string> options, string name = "Segmented")
        {
            var go = Go(name, parent);
            Box(go, UiTheme.Surface2, 8);
            Row(go, 2f, Pad(3, 3, 3, 3));
            Size(go, height: 34f);

            var buttons = new List<Object>();
            var labels = new List<Object>();
            foreach (string option in options)
            {
                var optionGo = Go($"Option ({option})", go.transform);
                var image = Box(optionGo, UiTheme.Clear, 6, raycast: true);
                var button = optionGo.AddComponent<Button>();
                button.targetGraphic = image;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                var colors = ColorBlock.defaultColorBlock;
                colors.highlightedColor = new Color(1.1f, 1.1f, 1.1f, 1f);
                colors.fadeDuration = 0.08f;
                button.colors = colors;
                Row(optionGo, 0f, Pad(10, 10, 0, 0), TextAnchor.MiddleCenter);
                // Zero preferred width so every option gets an equal share, not one sized by its label.
                Size(optionGo, width: 0f, height: 28f, flexWidth: 1f);
                labels.Add(Label(optionGo.transform, option, 12, UiTheme.Text2, FontStyle.Bold, TextAnchor.MiddleCenter));
                buttons.Add(button);
            }

            var control = go.AddComponent<SegmentedControl>();
            Wire.List(control, "options", buttons);
            Wire.List(control, "labels", labels);
            return control;
        }

        public static SliderField Slider(Transform parent, string text, string name = null)
        {
            var go = Go(name ?? $"Slider ({text})", parent);
            Column(go, 6f);

            var head = Go("Head", go.transform);
            Row(head, 8f);
            var label = Label(head.transform, text, 12, UiTheme.Text2);
            Size(label.gameObject, flexWidth: 1f);
            var value = Label(head.transform, "—", 12, UiTheme.Text, FontStyle.Bold, TextAnchor.MiddleRight, name: "Value");

            var sliderGo = Go("Slider", go.transform);
            Size(sliderGo, height: 18f);
            var slider = sliderGo.AddComponent<Slider>();
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.navigation = new Navigation { mode = Navigation.Mode.None };

            var track = Go("Track", sliderGo.transform);
            var trackRt = Stretch(track);
            trackRt.anchorMin = new Vector2(0f, 0.5f);
            trackRt.anchorMax = new Vector2(1f, 0.5f);
            trackRt.sizeDelta = new Vector2(0f, 4f);
            Box(track, UiTheme.Surface3, 2, raycast: true);

            var fillArea = Go("Fill Area", sliderGo.transform);
            var fillAreaRt = Stretch(fillArea);
            fillAreaRt.anchorMin = new Vector2(0f, 0.5f);
            fillAreaRt.anchorMax = new Vector2(1f, 0.5f);
            fillAreaRt.sizeDelta = new Vector2(0f, 4f);
            var fill = Go("Fill", fillArea.transform);
            Stretch(fill);
            Box(fill, UiTheme.Accent, 2);

            var handleArea = Go("Handle Slide Area", sliderGo.transform);
            Stretch(handleArea, 7f, 0f, 7f, 0f);
            var handle = Go("Handle", handleArea.transform);
            var handleRt = (RectTransform)handle.transform;
            handleRt.sizeDelta = new Vector2(14f, 14f);
            var handleImage = handle.AddComponent<Image>();
            handleImage.sprite = UiSprites.Circle;
            handleImage.color = UiTheme.White;

            slider.fillRect = (RectTransform)fill.transform;
            slider.handleRect = handleRt;
            slider.targetGraphic = handleImage;

            var field = go.AddComponent<SliderField>();
            Wire.Set(field, "label", label);
            Wire.Set(field, "slider", slider);
            Wire.Set(field, "valueText", value);
            return field;
        }

        public static SwitchField Switch(Transform parent, string text, string name = null)
        {
            var go = Go(name ?? $"Switch ({text})", parent);
            Row(go, 10f);
            Size(go, height: 24f);
            var hit = Box(go, UiTheme.Clear, 0, raycast: true);

            var label = Label(go.transform, text, 13, UiTheme.Text);
            Size(label.gameObject, flexWidth: 1f);

            var track = Go("Track", go.transform);
            Size(track, 36f, 20f);
            var trackImage = Box(track, UiTheme.Surface3, 10);
            var knob = Go("Knob", track.transform);
            var knobRt = (RectTransform)knob.transform;
            knobRt.anchorMin = knobRt.anchorMax = new Vector2(0.5f, 0.5f);
            knobRt.sizeDelta = new Vector2(14f, 14f);
            var knobImage = knob.AddComponent<Image>();
            knobImage.sprite = UiSprites.Circle;
            knobImage.color = UiTheme.White;
            knobImage.raycastTarget = false;

            var toggle = go.AddComponent<Toggle>();
            toggle.targetGraphic = hit;
            toggle.transition = Selectable.Transition.None;
            toggle.navigation = new Navigation { mode = Navigation.Mode.None };

            var field = go.AddComponent<SwitchField>();
            Wire.Set(field, "toggle", toggle);
            Wire.Set(field, "track", trackImage);
            Wire.Set(field, "knob", knobRt);
            return field;
        }

        public static InputField Input(Transform parent, float width, string placeholder = "", string name = "Input")
        {
            var go = Go(name, parent);
            Size(go, width, 32f);
            var image = Box(go, UiTheme.Surface2, 8, raycast: true);

            var text = Label(go.transform, string.Empty, 13, UiTheme.Text, name: "Text");
            Stretch(text.gameObject, 10f, 0f, 10f, 0f);
            var hint = Label(go.transform, placeholder, 13, UiTheme.Text3, FontStyle.Italic, name: "Placeholder");
            Stretch(hint.gameObject, 10f, 0f, 10f, 0f);

            var input = go.AddComponent<InputField>();
            input.targetGraphic = image;
            input.textComponent = text;
            input.placeholder = hint;
            input.customCaretColor = true;
            input.caretColor = UiTheme.Text;
            input.selectionColor = new Color(91f / 255f, 141f / 255f, 239f / 255f, 0.45f);
            input.navigation = new Navigation { mode = Navigation.Mode.None };
            return input;
        }

        /// <summary>A vertical scroll view filling its parent's layout; children go in <paramref name="content"/>.</summary>
        public static ScrollRect Scroll(Transform parent, out RectTransform content, string name = "Scroll View")
        {
            var go = Go(name, parent);
            Size(go, flexWidth: 1f, flexHeight: 1f);
            Box(go, UiTheme.Canvas, 0, raycast: true);

            var viewport = Go("Viewport", go.transform);
            Stretch(viewport);
            viewport.AddComponent<RectMask2D>();

            var contentGo = Go("Content", viewport.transform);
            content = (RectTransform)contentGo.transform;
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = Vector2.zero;
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = go.AddComponent<ScrollRect>();
            scroll.viewport = (RectTransform)viewport.transform;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
            return scroll;
        }

        // ---------------------------------------------------------------- composites

        /// <summary>A page's top bar: a back button, a title and subtitle, then room for actions on the right.</summary>
        public static GameObject TopBar(Transform parent, string title, string subtitle, out Button back, out Transform actions)
        {
            var bar = Go("Top Bar", parent);
            Box(bar, UiTheme.Surface);
            Size(bar, height: 56f);
            Row(bar, 10f, Pad(12, 16, 0, 0));

            var edge = Go("Bottom Edge", bar.transform);
            edge.AddComponent<LayoutElement>().ignoreLayout = true;
            var edgeRt = (RectTransform)edge.transform;
            edgeRt.anchorMin = new Vector2(0f, 0f);
            edgeRt.anchorMax = new Vector2(1f, 0f);
            edgeRt.pivot = new Vector2(0.5f, 0f);
            edgeRt.sizeDelta = new Vector2(0f, 1f);
            Box(edge, UiTheme.Border);

            back = IconButton(bar.transform, IconKind.Back, "Back");

            var titles = Go("Titles", bar.transform);
            Column(titles, 2f, alignment: TextAnchor.MiddleLeft);
            Label(titles.transform, title, 15, UiTheme.Text, FontStyle.Bold, name: "Title");
            Label(titles.transform, subtitle, 12, UiTheme.Text3, name: "Subtitle");

            Spacer(bar.transform);
            actions = bar.transform;
            return bar;
        }

        /// <summary>A titled card: a surface with a heading and a line of explanation over its body.</summary>
        public static Transform Card(Transform parent, string title, string text, string name = null)
        {
            var card = Go(name ?? $"Card ({title})", parent);
            Box(card, UiTheme.Surface, 12);
            Outline(card, UiTheme.Border, 12);
            Column(card, 10f, Pad(18, 18, 16, 18));

            var head = Go("Head", card.transform);
            Column(head, 2f);
            Label(head.transform, title, 15, UiTheme.Text, FontStyle.Bold, name: "Title");
            Label(head.transform, text, 12, UiTheme.Text3, wrap: true, name: "Text");
            return card.transform;
        }

        public static Text Hint(Transform parent, string text) => Label(parent, text, 12, UiTheme.Text3, wrap: true, name: "Hint");

        public static Text FieldLabel(Transform parent, string text) => Label(parent, text, 12, UiTheme.Text2, name: $"Label ({text})");
    }

    /// <summary>Sets a component's serialized (Inspector) fields, so prefabs come out wired up.</summary>
    public static class Wire
    {
        public static void Set(Object target, string field, Object value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(field);
            if (property == null)
            {
                Debug.LogError($"UI builder: {target.GetType().Name} has no field '{field}'.");
                return;
            }
            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        public static void List(Object target, string field, IReadOnlyList<Object> values)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(field);
            if (property == null || !property.isArray)
            {
                Debug.LogError($"UI builder: {target.GetType().Name} has no list '{field}'.");
                return;
            }
            property.arraySize = values.Count;
            for (int i = 0; i < values.Count; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
