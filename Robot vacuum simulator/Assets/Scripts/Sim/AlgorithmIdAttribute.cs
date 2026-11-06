using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Shows a string field holding an algorithm id as a dropdown of the catalog's algorithms in the Inspector, with
    /// a Rescan button. Picking a built-in also sets the <see cref="MovementPattern"/> field named
    /// <see cref="patternField"/> beside it, so the two stay in step. (The drawer is editor-only: AlgorithmIdDrawer.)
    /// </summary>
    public sealed class AlgorithmIdAttribute : PropertyAttribute
    {
        /// <summary>The name of the MovementPattern field in the same object.</summary>
        public readonly string patternField;

        public AlgorithmIdAttribute(string patternField)
        {
            this.patternField = patternField;
        }
    }
}
