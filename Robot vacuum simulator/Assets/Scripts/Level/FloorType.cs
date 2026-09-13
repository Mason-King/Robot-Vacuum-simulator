using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>
    /// A floor covering. Rooms reference one of these; it drives how the floor is drawn
    /// and how the vacuum behaves while it is over that surface.
    /// </summary>
    [CreateAssetMenu(menuName = "Robot Vacuum/Floor Type", fileName = "FloorType")]
    public class FloorType : ScriptableObject
    {
        public string displayName = "Floor";

        [Tooltip("Colour the room is filled with, and the swatch shown in the level editor. " +
                 "With a texture assigned this tints it.")]
        public Color color = new Color(0.78f, 0.78f, 0.78f);

        [Tooltip("Optional seamless tile drawn across the floor. Leave empty for a flat colour.")]
        public Texture2D texture;

        [Tooltip("How many metres one repeat of the texture covers. 1 = one tile per metre.")]
        [Min(0.01f)]
        public float textureScale = 1f;

        [Range(0.1f, 2f)]
        [Tooltip("Multiplies the vacuum's drive speed while it is on this covering.")]
        public float speedMultiplier = 1f;

        [Range(0.1f, 4f)]
        [Tooltip("Suction-seconds needed to clear one unit of dirt. Carpet is harder than hardwood.")]
        public float cleaningEffort = 1f;

        [Range(0f, 4f)]
        [Tooltip("How quickly dirt builds back up between runs.")]
        public float soilingRate = 1f;

        [Tooltip("Lip the vacuum has to climb to get onto this covering, in metres (rugs, thresholds).")]
        public float stepHeight = 0f;

        public string Label => string.IsNullOrEmpty(displayName) ? name : displayName;
    }
}
