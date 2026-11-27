using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Shows a string field holding an algorithm id as a dropdown of the catalog's algorithms in the Inspector, with
    /// a Rescan button. An empty id shows as the default algorithm. (The drawer is editor-only: AlgorithmIdDrawer.)
    /// </summary>
    public sealed class AlgorithmIdAttribute : PropertyAttribute
    {
    }
}
