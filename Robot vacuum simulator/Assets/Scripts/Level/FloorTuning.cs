using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>
    /// Player adjustments to floor coverings, layered over the palette's own values without touching the
    /// assets. Keyed by covering name, so the same adjustment applies whichever palette a level uses.
    /// </summary>
    public static class FloorTuning
    {
        /// <summary>The bounds cleaning effort is held to, matching <see cref="FloorType.cleaningEffort"/>'s range.</summary>
        public const float MinCleaningEffort = 0.1f;
        public const float MaxCleaningEffort = 4f;

        static readonly Dictionary<string, float> cleaningEffort = new Dictionary<string, float>();

        public static void SetCleaningEffort(string floorLabel, float effort) =>
            cleaningEffort[floorLabel] = Mathf.Clamp(effort, MinCleaningEffort, MaxCleaningEffort);

        public static void ClearCleaningEffort(string floorLabel) => cleaningEffort.Remove(floorLabel);

        public static void ClearAll() => cleaningEffort.Clear();

        /// <summary>The cleaning effort in force for <paramref name="floor"/>: the adjustment if there is one.</summary>
        public static float CleaningEffort(FloorType floor)
        {
            if (floor == null) return 1f;
            return cleaningEffort.TryGetValue(floor.Label, out float effort) ? effort : floor.cleaningEffort;
        }

        // Statics survive entering Play Mode when domain reload is off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => cleaningEffort.Clear();
    }
}
