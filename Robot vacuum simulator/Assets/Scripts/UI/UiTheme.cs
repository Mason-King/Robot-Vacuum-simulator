using UnityEngine;

namespace RobotVacuum.UI
{
    /// <summary>
    /// The app's colours for uGUI, the same tokens as LevelEditor.uss: dark, flat surfaces with one blue
    /// accent. Widgets read their state colours from here; the prefabs bake in the rest.
    /// </summary>
    public static class UiTheme
    {
        public static readonly Color Bg = Hex(0x0B0D11);
        public static readonly Color Canvas = Hex(0x101318);
        public static readonly Color Surface = Hex(0x161920);
        public static readonly Color Surface2 = Hex(0x1D2129);
        public static readonly Color Surface3 = Hex(0x262B35);
        public static readonly Color Border = Hex(0x262A33);
        public static readonly Color BorderStrong = Hex(0x353B47);
        public static readonly Color Text = Hex(0xE8EAEE);
        public static readonly Color Text2 = Hex(0xA3AAB6);
        public static readonly Color Text3 = Hex(0x6C7482);
        public static readonly Color Accent = Hex(0x5B8DEF);
        public static readonly Color AccentHover = Hex(0x72A0F4);
        public static readonly Color AccentSoft = new Color(91f / 255f, 141f / 255f, 239f / 255f, 0.16f);
        public static readonly Color Danger = Hex(0xEF5A67);
        public static readonly Color Success = Hex(0x3CCB8A);
        public static readonly Color Warning = Hex(0xF2B34C);
        public static readonly Color White = Color.white;
        public static readonly Color Clear = new Color(0f, 0f, 0f, 0f);

        public static Color Hex(int rgb, float alpha = 1f) =>
            new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
    }
}
