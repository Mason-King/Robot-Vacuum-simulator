using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace RobotVacuum.LevelEditor
{
    public sealed class MenuEntry
    {
        public string label;
        public string shortcut;
        public IconKind? icon;
        public Action action;
        public bool enabled = true;
        public bool danger;
        public bool isChecked;
        public bool isSeparator;
        public bool isHeading;

        /// <summary>Builds a custom row, e.g. floor swatches. Receives a callback that closes the menu.</summary>
        public Func<Action, VisualElement> custom;

        public static MenuEntry Item(string label, Action action, IconKind? icon = null, string shortcut = null,
            bool enabled = true, bool danger = false) =>
            new MenuEntry { label = label, action = action, icon = icon, shortcut = shortcut, enabled = enabled, danger = danger };

        public static MenuEntry Separator() => new MenuEntry { isSeparator = true };
        public static MenuEntry Heading(string label) => new MenuEntry { label = label, isHeading = true };
        public static MenuEntry Custom(Func<Action, VisualElement> build) => new MenuEntry { custom = build };
    }

    public enum ButtonStyle { Ghost, Primary, Danger }

    public struct DialogButton
    {
        public string label;
        public ButtonStyle style;
        public Action action;

        public DialogButton(string label, ButtonStyle style, Action action)
        {
            this.label = label;
            this.style = style;
            this.action = action;
        }
    }

    /// <summary>
    /// Top-most layer for transient UI: tooltips, popup menus, modal dialogs and toasts.
    /// Runtime UI Toolkit has none of these built in, so they live here.
    /// </summary>
    public sealed class OverlayLayer : VisualElement
    {
        const long TooltipDelayMs = 450;
        const long ToastDurationMs = 2600;

        public static OverlayLayer Current { get; private set; }

        readonly Label tooltipLabel;
        readonly VisualElement toast;
        readonly Label toastText;
        readonly VisualElement toastAction;
        readonly Label toastActionLabel;

        IVisualElementScheduledItem tooltipTimer;
        IVisualElementScheduledItem toastTimer;
        Action toastCallback;

        VisualElement menuBlocker;
        VisualElement menu;
        VisualElement dialogBackdrop;
        Action dialogCancel;

        public OverlayLayer()
        {
            Current = this;
            AddToClassList("le-overlay");
            pickingMode = PickingMode.Ignore;

            toast = Ui.Div(this, "le-toast");
            toast.pickingMode = PickingMode.Ignore;
            toastText = Ui.Text(toast, string.Empty, "le-toast__text");
            toastAction = Ui.Div(toast, "le-toast__action");
            toastActionLabel = Ui.Text(toastAction, string.Empty);
            toastAction.AddManipulator(new Clickable(() =>
            {
                var callback = toastCallback;
                HideToast();
                callback?.Invoke();
            }));

            tooltipLabel = Ui.Text(this, string.Empty, "le-tooltip");
            tooltipLabel.pickingMode = PickingMode.Ignore;
        }

        public bool HasPopup => menu != null || dialogBackdrop != null;

        /// <summary>Closes the top-most popup. Returns false when there was nothing to close.</summary>
        public bool Dismiss()
        {
            if (menu != null) { CloseMenu(); return true; }
            if (dialogBackdrop != null) { CancelDialog(); return true; }
            return false;
        }

        // ---------------------------------------------------------------- tooltip

        public void ScheduleTooltip(VisualElement target, string text, TooltipSide side)
        {
            tooltipTimer?.Pause();
            if (dialogBackdrop != null) return;

            tooltipTimer = schedule.Execute(() => ShowTooltip(target, text, side)).StartingIn(TooltipDelayMs);
        }

        public void HideTooltip()
        {
            tooltipTimer?.Pause();
            tooltipLabel.RemoveFromClassList("le-tooltip--visible");
        }

        void ShowTooltip(VisualElement target, string text, TooltipSide side)
        {
            if (target.panel == null) return;

            tooltipLabel.text = text;
            var bounds = this.WorldToLocal(target.worldBound);

            switch (side)
            {
                case TooltipSide.Right:
                    tooltipLabel.style.left = bounds.xMax + 8f;
                    tooltipLabel.style.top = bounds.center.y;
                    tooltipLabel.style.translate = new Translate(0, Length.Percent(-50));
                    break;
                case TooltipSide.Above:
                    tooltipLabel.style.left = bounds.center.x;
                    tooltipLabel.style.top = bounds.yMin - 8f;
                    tooltipLabel.style.translate = new Translate(Length.Percent(-50), Length.Percent(-100));
                    break;
                default:
                    tooltipLabel.style.left = bounds.center.x;
                    tooltipLabel.style.top = bounds.yMax + 8f;
                    tooltipLabel.style.translate = new Translate(Length.Percent(-50), 0);
                    break;
            }

            tooltipLabel.BringToFront();
            tooltipLabel.AddToClassList("le-tooltip--visible");
        }

        // ---------------------------------------------------------------- menu

        public void ShowMenu(Vector2 panelPosition, IList<MenuEntry> entries)
        {
            CloseMenu();
            HideTooltip();

            menuBlocker = Ui.Div(this, "le-blocker");
            menuBlocker.RegisterCallback<PointerDownEvent>(evt =>
            {
                CloseMenu();
                evt.StopPropagation();
            });

            menu = Ui.Div(menuBlocker, "le-menu");
            menu.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());

            foreach (var entry in entries)
                menu.Add(BuildMenuRow(entry));

            var local = this.WorldToLocal(panelPosition);
            menu.style.left = local.x;
            menu.style.top = local.y;
            menu.RegisterCallback<GeometryChangedEvent>(_ => KeepInside(menu));
        }

        public void ShowMenuBelow(VisualElement anchor, IList<MenuEntry> entries)
        {
            var bounds = anchor.worldBound;
            ShowMenu(new Vector2(bounds.xMin, bounds.yMax + 6f), entries);
        }

        public void CloseMenu()
        {
            menuBlocker?.RemoveFromHierarchy();
            menuBlocker = null;
            menu = null;
        }

        VisualElement BuildMenuRow(MenuEntry entry)
        {
            if (entry.isSeparator) return Ui.Div(null, "le-menu__separator");
            if (entry.isHeading) return Ui.Text(null, entry.label, "le-menu__heading");
            if (entry.custom != null) return entry.custom(CloseMenu);

            var row = Ui.Div(null, "le-menu__item");
            if (entry.danger) row.AddToClassList("le-menu__item--danger");

            var iconSlot = Ui.Div(row, "le-menu__icon");
            if (entry.isChecked) iconSlot.Add(new IconElement(IconKind.Check));
            else if (entry.icon.HasValue) iconSlot.Add(new IconElement(entry.icon.Value));

            Ui.Text(row, entry.label, "le-menu__label");
            if (!string.IsNullOrEmpty(entry.shortcut)) Ui.Text(row, entry.shortcut, "le-menu__shortcut");

            row.SetEnabled(entry.enabled);
            row.AddManipulator(new Clickable(() =>
            {
                CloseMenu();
                entry.action?.Invoke();
            }));
            Ui.RepaintIconsOnInteraction(row);
            return row;
        }

        void KeepInside(VisualElement element)
        {
            float maxLeft = layout.width - element.layout.width - 8f;
            float maxTop = layout.height - element.layout.height - 8f;

            float left = Mathf.Max(8f, Mathf.Min(element.layout.x, maxLeft));
            float top = Mathf.Max(8f, Mathf.Min(element.layout.y, maxTop));

            if (!Mathf.Approximately(left, element.layout.x)) element.style.left = left;
            if (!Mathf.Approximately(top, element.layout.y)) element.style.top = top;
        }

        // ---------------------------------------------------------------- dialog

        public void ShowDialog(string title, string message, Action onCancel, params DialogButton[] buttons)
        {
            CloseDialog();
            CloseMenu();
            HideTooltip();

            dialogCancel = onCancel;
            dialogBackdrop = Ui.Div(this, "le-blocker le-blocker--dim");
            dialogBackdrop.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.target == dialogBackdrop) CancelDialog();
                evt.StopPropagation();
            });

            var card = Ui.Div(dialogBackdrop, "le-dialog");
            Ui.Text(card, title, "le-dialog__title");
            if (!string.IsNullOrEmpty(message)) Ui.Text(card, message, "le-dialog__message");

            var row = Ui.Div(card, "le-dialog__buttons");
            foreach (var button in buttons)
            {
                var captured = button;
                string style = captured.style switch
                {
                    ButtonStyle.Primary => "le-btn--primary",
                    ButtonStyle.Danger => "le-btn--danger",
                    _ => "le-btn--ghost",
                };

                Ui.Button(row, captured.label, null, () =>
                {
                    CloseDialog();
                    captured.action?.Invoke();
                }, style);
            }
        }

        public void CancelDialog()
        {
            var cancel = dialogCancel;
            CloseDialog();
            cancel?.Invoke();
        }

        void CloseDialog()
        {
            dialogBackdrop?.RemoveFromHierarchy();
            dialogBackdrop = null;
            dialogCancel = null;
        }

        // ---------------------------------------------------------------- toast

        public void Toast(string message, string actionLabel = null, Action action = null)
        {
            toastText.text = message;
            toastCallback = action;
            toastActionLabel.text = actionLabel ?? string.Empty;
            Ui.SetVisible(toastAction, action != null);
            toast.pickingMode = action != null ? PickingMode.Position : PickingMode.Ignore;

            toast.BringToFront();
            toast.AddToClassList("le-toast--visible");

            toastTimer?.Pause();
            toastTimer = schedule.Execute(HideToast).StartingIn(ToastDurationMs);
        }

        void HideToast()
        {
            toastTimer?.Pause();
            toastCallback = null;
            toast.pickingMode = PickingMode.Ignore;
            toast.RemoveFromClassList("le-toast--visible");
        }
    }
}
