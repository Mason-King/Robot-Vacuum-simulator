using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    public enum TooltipSide { Below, Right, Above }

    /// <summary>
    /// Small builders for the editor's UI. Buttons are plain elements with a Clickable rather than
    /// UIElements Buttons, so none of the default theme's button styling has to be undone in USS.
    /// </summary>
    public static class Ui
    {
        public static VisualElement Div(VisualElement parent, string classes = null)
        {
            var element = new VisualElement();
            AddClasses(element, classes);
            parent?.Add(element);
            return element;
        }

        public static Label Text(VisualElement parent, string text, string classes = null)
        {
            var label = new Label(text);
            AddClasses(label, classes);
            parent?.Add(label);
            return label;
        }

        public static void AddClasses(VisualElement element, string classes)
        {
            if (string.IsNullOrEmpty(classes)) return;
            foreach (string name in classes.Split(' '))
                if (name.Length > 0) element.AddToClassList(name);
        }

        public static VisualElement Button(VisualElement parent, string text, IconKind? icon, Action onClick, string classes = null)
        {
            var button = Div(parent, "le-btn");
            AddClasses(button, classes);

            if (icon.HasValue) button.Add(new IconElement(icon.Value));
            if (!string.IsNullOrEmpty(text)) Text(button, text, "le-btn__label");
            else button.AddToClassList("le-btn--icon");

            button.AddManipulator(new Clickable(() => onClick?.Invoke()));
            RepaintIconsOnInteraction(button);
            return button;
        }

        public static VisualElement IconButton(VisualElement parent, IconKind icon, string tooltip, Action onClick,
            string classes = null, TooltipSide side = TooltipSide.Below)
        {
            var button = Button(parent, null, icon, onClick, classes);
            if (!string.IsNullOrEmpty(tooltip)) Tooltip(button, tooltip, side);
            return button;
        }

        /// <summary>Toggles a class and refreshes any icons whose colour depends on it.</summary>
        public static void SetClass(VisualElement element, string className, bool enabled)
        {
            if (element.ClassListContains(className) == enabled) return;
            element.EnableInClassList(className, enabled);
            IconElement.RepaintAll(element);
        }

        public static void SetVisible(VisualElement element, bool visible) =>
            element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        public static void RepaintIconsOnInteraction(VisualElement element)
        {
            element.RegisterCallback<PointerEnterEvent>(_ => IconElement.RepaintAll(element));
            element.RegisterCallback<PointerLeaveEvent>(_ => IconElement.RepaintAll(element));
            element.RegisterCallback<PointerDownEvent>(_ => IconElement.RepaintAll(element), TrickleDown.TrickleDown);
            element.RegisterCallback<PointerUpEvent>(_ => IconElement.RepaintAll(element), TrickleDown.TrickleDown);
        }

        public static void Tooltip(VisualElement target, string text, TooltipSide side = TooltipSide.Below)
        {
            target.RegisterCallback<PointerEnterEvent>(_ => OverlayLayer.Current?.ScheduleTooltip(target, text, side));
            target.RegisterCallback<PointerLeaveEvent>(_ => OverlayLayer.Current?.HideTooltip());
            target.RegisterCallback<PointerDownEvent>(_ => OverlayLayer.Current?.HideTooltip(), TrickleDown.TrickleDown);
        }

        public static VisualElement Swatch(VisualElement parent, Color color, string classes = "le-swatch")
        {
            var swatch = Div(parent, classes);
            swatch.style.backgroundColor = color;
            return swatch;
        }

        public static VisualElement Section(VisualElement parent, string title, out VisualElement body)
        {
            var section = Div(parent, "le-section");
            if (!string.IsNullOrEmpty(title)) Text(section, title, "le-section__title");
            body = Div(section, "le-section__body");
            return section;
        }

        /// <summary>A label-over-value read-out for the run bars. Returns the value label.</summary>
        public static Label RunStat(VisualElement parent, string label)
        {
            var stat = Div(parent, "le-run-stat");
            Text(stat, label, "le-run-stat__label");
            return Text(stat, "—", "le-run-stat__value");
        }

        public static Label Stat(VisualElement parent, string label, string value)
        {
            var tile = Div(parent, "le-stat");
            var valueLabel = Text(tile, value, "le-stat__value");
            Text(tile, label, "le-stat__label");
            return valueLabel;
        }

        public static TextField TextInput(VisualElement parent, string value, Action<string> onCommit, string classes = null)
        {
            var field = new TextField { value = value, isDelayed = true, maxLength = 64 };
            field.AddToClassList("le-input");
            AddClasses(field, classes);
            field.RegisterValueChangedCallback(evt => onCommit?.Invoke(evt.newValue));
            parent?.Add(field);
            return field;
        }

        public static string FormatMetres(float value) => value >= 10f ? $"{value:0.0} m" : $"{value:0.00} m";

        public static string FormatArea(float value) => $"{value:0.0} m²";

        /// <summary>True when keyboard focus is inside a text field, so single-key shortcuts should stand down.</summary>
        public static bool IsTyping(IPanel panel)
        {
            var focused = panel?.focusController?.focusedElement as VisualElement;
            for (var element = focused; element != null; element = element.parent)
                if (element is TextField) return true;
            return false;
        }
    }

    /// <summary>A pill switch with an optional label, used for boolean settings.</summary>
    public sealed class SwitchToggle : VisualElement
    {
        readonly VisualElement track;
        bool value;

        public event Action<bool> ValueChanged;

        public SwitchToggle(string label, bool initial)
        {
            AddToClassList("le-switch-row");
            if (!string.IsNullOrEmpty(label)) Ui.Text(this, label, "le-switch-row__label");

            track = Ui.Div(this, "le-switch");
            Ui.Div(track, "le-switch__knob");

            SetValueWithoutNotify(initial);
            this.AddManipulator(new Clickable(() =>
            {
                SetValueWithoutNotify(!value);
                ValueChanged?.Invoke(value);
            }));
        }

        public bool Value => value;

        public void SetValueWithoutNotify(bool next)
        {
            value = next;
            track.EnableInClassList("le-switch--on", next);
        }
    }

    /// <summary>
    /// A flat slider with a live value read-out. Begin/End bracket a drag so the caller can record
    /// one undo step for the whole gesture instead of one per frame.
    /// </summary>
    public sealed class ValueSlider : VisualElement
    {
        readonly VisualElement track;
        readonly VisualElement fill;
        readonly VisualElement knob;
        readonly Label readout;
        readonly TextField preciseInput;
        readonly float min;
        readonly float max;
        readonly Func<float, string> format;
        float value;
        int pointerId = -1;

        public event Action DragStarted;
        public event Action<float> ValueChanged;
        public event Action DragEnded;

        public ValueSlider(float min, float max, float initial, Func<float, string> format, bool showInput = false)
        {
            this.min = min;
            this.max = Mathf.Max(min + 1e-4f, max);
            this.format = format ?? (v => v.ToString("0.00"));

            AddToClassList("le-slider");
            track = Ui.Div(this, "le-slider__track");
            Ui.Div(track, "le-slider__rail").pickingMode = PickingMode.Ignore;
            fill = Ui.Div(track, "le-slider__fill");
            fill.pickingMode = PickingMode.Ignore;
            knob = Ui.Div(track, "le-slider__knob");
            readout = Ui.Text(this, string.Empty, "le-slider__value");

            if (showInput)
            {
                preciseInput = new TextField { isDelayed = true, maxLength = 16 };
                preciseInput.AddToClassList("le-slider__input");
                preciseInput.RegisterValueChangedCallback(evt => ApplyTypedValue(evt.newValue));
                Add(preciseInput);
            }

            track.RegisterCallback<PointerDownEvent>(OnPointerDown);
            track.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            track.RegisterCallback<PointerUpEvent>(OnPointerUp);
            track.RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag());

            SetValueWithoutNotify(initial);
        }

        public bool IsDragging => pointerId >= 0;

        public void SetValueWithoutNotify(float next)
        {
            value = Mathf.Clamp(next, min, max);
            float t = (value - min) / (max - min);
            fill.style.width = Length.Percent(t * 100f);
            knob.style.left = Length.Percent(t * 100f);
            readout.text = format(value);
            preciseInput?.SetValueWithoutNotify(value.ToString("0.##", CultureInfo.InvariantCulture));
        }

        void ApplyTypedValue(string text)
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float next))
            {
                preciseInput.SetValueWithoutNotify(value.ToString("0.##", CultureInfo.InvariantCulture));
                return;
            }

            float previous = value;
            float clamped = Mathf.Clamp(next, min, max);
            SetValueWithoutNotify(clamped);
            if (!Mathf.Approximately(clamped, previous)) ValueChanged?.Invoke(clamped);
        }

        void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;

            pointerId = evt.pointerId;
            track.CapturePointer(pointerId);
            DragStarted?.Invoke();
            SetFromPosition(evt.position);
            evt.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (pointerId != evt.pointerId || !track.HasPointerCapture(pointerId)) return;
            SetFromPosition(evt.position);
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            if (pointerId != evt.pointerId) return;
            SetFromPosition(evt.position);
            EndDrag();
        }

        void EndDrag()
        {
            if (pointerId < 0) return;

            int id = pointerId;
            pointerId = -1;
            if (track.HasPointerCapture(id)) track.ReleasePointer(id);
            DragEnded?.Invoke();
        }

        void SetFromPosition(Vector2 panelPosition)
        {
            float width = track.layout.width;
            if (width <= 1f) return;

            float t = Mathf.Clamp01(track.WorldToLocal(panelPosition).x / width);
            float next = Mathf.Lerp(min, max, t);
            if (Mathf.Approximately(next, value)) return;

            SetValueWithoutNotify(next);
            ValueChanged?.Invoke(value);
        }
    }

    /// <summary>A row of mutually exclusive text options.</summary>
    public sealed class Segmented : VisualElement
    {
        readonly List<VisualElement> options = new List<VisualElement>();

        public event Action<int> SelectionChanged;

        public Segmented(IReadOnlyList<string> labels, int selected)
        {
            AddToClassList("le-segmented");

            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                var option = Ui.Div(this, "le-segmented__option");
                Ui.Text(option, labels[i], "le-segmented__label");
                option.AddManipulator(new Clickable(() =>
                {
                    SetSelectedWithoutNotify(index);
                    SelectionChanged?.Invoke(index);
                }));
                options.Add(option);
            }

            SetSelectedWithoutNotify(selected);
        }

        public void SetSelectedWithoutNotify(int selected)
        {
            for (int i = 0; i < options.Count; i++)
                options[i].EnableInClassList("le-segmented__option--selected", i == selected);
        }
    }
}
