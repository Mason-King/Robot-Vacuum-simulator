using UnityEngine;
using UnityEngine.Serialization;

namespace RobotVacuum.Level
{
    /// <summary>Surface detail drawn over a floor's colour, so coverings read apart at a glance.</summary>
    public enum FloorPattern { Plain, Planks, Tiles, Carpet, Shag, Rug, Hazard }

    /// <summary>
    /// A floor covering. Rooms reference one of these; it drives how the floor is drawn
    /// and how the vacuum behaves while it is over that surface.
    /// </summary>
    [CreateAssetMenu(menuName = "Robot Vacuum/Floor Type", fileName = "FloorType")]
    public class FloorType : ScriptableObject
    {
        public string displayName = "Floor";

        [Tooltip("Colour the room is filled with, and the swatch shown in the level editor. " +
                 "With a texture or pattern this tints it.")]
        [FormerlySerializedAs("editorColor")]
        public Color color = new Color(0.78f, 0.78f, 0.78f);

        [Tooltip("Surface detail drawn over the colour in the editor and the simulation. " +
                 "The simulation ignores it when a texture is assigned.")]
        public FloorPattern pattern = FloorPattern.Plain;

        [Tooltip("Optional seamless tile drawn across the floor. Leave empty to use the pattern.")]
        public Texture2D texture;

        [Tooltip("How many metres one repeat of the texture covers. 1 = one tile per metre. " +
                 "For a pattern this scales its natural size.")]
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
