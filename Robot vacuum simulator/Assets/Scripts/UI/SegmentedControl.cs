using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace RobotVacuum.UI
{
    /// <summary>
    /// A row of mutually exclusive options, one highlighted. Each option is a child Button with a Text; the
    /// selected one gets the accent tint. Built as a prefab part by the UI builder.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SegmentedControl : MonoBehaviour
    {
        [SerializeField] List<Button> options = new List<Button>();
        [SerializeField] List<Text> labels = new List<Text>();
        [SerializeField] int selected;

        bool hooked;

        public event Action<int> SelectionChanged;

        public int Selected => selected;

        public int Count => options.Count;

        void Awake() => Hook();

        void Hook()
        {
            if (hooked) return;
            hooked = true;

            for (int i = 0; i < options.Count; i++)
            {
                int index = i;
                if (options[i] == null) continue;
                options[i].onClick.AddListener(() =>
                {
                    SetSelectedWithoutNotify(index);
                    SelectionChanged?.Invoke(index);
                });
            }
            Refresh();
        }

        public void SetSelectedWithoutNotify(int index)
        {
            Hook();
            selected = Mathf.Clamp(index, 0, Mathf.Max(0, options.Count - 1));
            Refresh();
        }

        /// <summary>Relabels the options, e.g. from a list that lives in code. Extra labels are ignored.</summary>
        public void SetLabels(IReadOnlyList<string> texts)
        {
            for (int i = 0; i < labels.Count && i < texts.Count; i++)
                if (labels[i] != null) labels[i].text = texts[i];
        }

        void Refresh()
        {
            for (int i = 0; i < options.Count; i++)
            {
                bool on = i == selected;
                if (options[i] != null && options[i].targetGraphic != null)
                    options[i].targetGraphic.color = on ? UiTheme.Surface3 : UiTheme.Clear;
                if (i < labels.Count && labels[i] != null)
                    labels[i].color = on ? UiTheme.Text : UiTheme.Text2;
            }
        }
    }
}
