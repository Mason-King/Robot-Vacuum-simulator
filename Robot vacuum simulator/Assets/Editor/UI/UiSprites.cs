using System.IO;
using UnityEditor;
using UnityEngine;

namespace RobotVacuum.EditorTools.UI
{
    /// <summary>
    /// The 9-sliced sprites the uGUI prefabs are drawn with: anti-aliased rounded rectangles (filled, and a
    /// 1 px ring for borders) at the radii the design uses, and a circle. Generated once into
    /// Assets/UI/Sprites and reused; delete a file to have it made again.
    /// </summary>
    public static class UiSprites
    {
        public const string Folder = "Assets/UI/Sprites";

        public static Sprite Rounded(int radius) => Get($"Rounded{radius}", radius, false);

        /// <summary>A 1 px border on the inside edge of a rounded rectangle, laid over a fill to outline it.</summary>
        public static Sprite Ring(int radius) => Get($"Ring{radius}", radius, true);

        public static Sprite Circle => Get("Circle", 32, false, simple: true);

        static Sprite Get(string name, int radius, bool ring, bool simple = false)
        {
            string path = $"{Folder}/{name}.png";
            var existing = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (existing != null) return existing;

            Directory.CreateDirectory(Folder);
            int size = simple ? radius * 2 : radius * 2 + 4;
            File.WriteAllBytes(path, Draw(size, radius, ring).EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.mipmapEnabled = false;
            importer.alphaIsTransparency = true;
            importer.filterMode = FilterMode.Bilinear;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.spritePixelsPerUnit = 100f;
            int border = simple ? 0 : radius + 2;
            importer.spriteBorder = new Vector4(border, border, border, border);
            importer.SaveAndReimport();

            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        static Texture2D Draw(int size, int radius, bool ring)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            float half = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Signed distance to the rounded rectangle filling the texture; negative inside.
                    float px = Mathf.Abs(x + 0.5f - half) - (half - radius);
                    float py = Mathf.Abs(y + 0.5f - half) - (half - radius);
                    float outside = new Vector2(Mathf.Max(px, 0f), Mathf.Max(py, 0f)).magnitude;
                    float d = outside + Mathf.Min(Mathf.Max(px, py), 0f) - radius;

                    float alpha = Mathf.Clamp01(0.5f - d);
                    if (ring) alpha *= Mathf.Clamp01(d + 1.5f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }
    }
}
