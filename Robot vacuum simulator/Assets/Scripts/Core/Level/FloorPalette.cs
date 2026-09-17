using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>
    /// Ordered list of floor coverings. Rooms store an index into this list, so reordering
    /// a palette that levels already reference will change those levels' floors.
    /// </summary>
    [CreateAssetMenu(menuName = "Robot Vacuum/Floor Palette", fileName = "FloorPalette")]
    public class FloorPalette : ScriptableObject
    {
        [SerializeField] List<FloorType> entries = new List<FloorType>();

        public IReadOnlyList<FloorType> Entries => entries;
        public int Count => entries.Count;

        public FloorType Get(int index) =>
            index >= 0 && index < entries.Count ? entries[index] : null;

        public Color ColorOf(int index)
        {
            var floor = Get(index);
            return floor != null ? floor.color : Color.magenta;
        }

        public string LabelOf(int index)
        {
            var floor = Get(index);
            return floor != null ? floor.Label : "(missing)";
        }

        public string[] Labels()
        {
            var labels = new string[entries.Count];
            for (int i = 0; i < entries.Count; i++) labels[i] = LabelOf(i);
            return labels;
        }

        public int IndexOf(FloorType floor) => entries.IndexOf(floor);

        public void Add(FloorType floor) => entries.Add(floor);

        // ---------------------------------------------------------------- defaults

        struct Preset
        {
            public string name;
            public string hex;
            public float speed;
            public float effort;
            public float soiling;
            public float step;
            public FloorPattern pattern;
        }

        static readonly Preset[] DefaultPresets =
        {
            new Preset { name = "Hardwood",    hex = "C79A6B", speed = 1.00f, effort = 0.8f, soiling = 1.0f, step = 0.000f, pattern = FloorPattern.Planks },
            new Preset { name = "Tile",        hex = "D9DDE2", speed = 1.00f, effort = 0.7f, soiling = 1.2f, step = 0.000f, pattern = FloorPattern.Tiles },
            new Preset { name = "Laminate",    hex = "B98E5E", speed = 0.95f, effort = 0.8f, soiling = 1.0f, step = 0.000f, pattern = FloorPattern.Planks },
            new Preset { name = "Low Carpet",  hex = "97A183", speed = 0.85f, effort = 1.6f, soiling = 1.4f, step = 0.008f, pattern = FloorPattern.Carpet },
            new Preset { name = "High Carpet", hex = "6E7C5C", speed = 0.60f, effort = 2.4f, soiling = 1.6f, step = 0.020f, pattern = FloorPattern.Shag },
            new Preset { name = "Area Rug",    hex = "A8646B", speed = 0.75f, effort = 2.0f, soiling = 1.3f, step = 0.015f, pattern = FloorPattern.Rug },
        };

        /// <summary>
        /// Builds the stock six-covering palette in memory. The asset factory saves this to disk;
        /// the in-app editor uses it directly when no palette asset is assigned.
        /// </summary>
        public static FloorPalette CreateDefault()
        {
            var palette = CreateInstance<FloorPalette>();
            palette.name = "Default Floor Palette";

            foreach (var preset in DefaultPresets)
            {
                var floor = CreateInstance<FloorType>();
                floor.name = preset.name;
                floor.displayName = preset.name;
                floor.color = ColorUtility.TryParseHtmlString("#" + preset.hex, out var color) ? color : Color.magenta;
                floor.speedMultiplier = preset.speed;
                floor.cleaningEffort = preset.effort;
                floor.soilingRate = preset.soiling;
                floor.stepHeight = preset.step;
                floor.pattern = preset.pattern;

                palette.Add(floor);
            }

            return palette;
        }
    }
}
