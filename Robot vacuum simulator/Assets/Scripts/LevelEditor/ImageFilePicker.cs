using System;
using System.IO;
using System.Linq;
using RobotVacuum.Sim;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// Chooses an image for the Picture movement pattern. The Unity Editor has a native file dialog; a
    /// player build has none, so there the newest image dropped into <see cref="ImagesFolder"/> is used.
    /// </summary>
    public static class ImageFilePicker
    {
        static readonly string[] Extensions = { ".png", ".jpg", ".jpeg" };

        public static string ImagesFolder => Path.Combine(Application.persistentDataPath, "Images");

        /// <summary>A path to an image file, or null when the user cancelled or there is none.</summary>
        public static string Pick()
        {
#if UNITY_EDITOR
            string path = UnityEditor.EditorUtility.OpenFilePanelWithFilters(
                "Choose an image for the heatmap", string.Empty, new[] { "Images", "png,jpg,jpeg" });
            return string.IsNullOrEmpty(path) ? null : path;
#else
            Directory.CreateDirectory(ImagesFolder);
            return Directory.GetFiles(ImagesFolder)
                .Where(file => Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
#endif
        }

        /// <summary>
        /// Asks for an image and makes it the <see cref="PictureKind.Image"/> picture. Returns false when
        /// nothing was loaded, with a message worth showing, or a null message if the user simply cancelled.
        /// </summary>
        public static bool TryLoad(out string problem)
        {
            problem = null;

            string path;
            try
            {
                path = Pick();
            }
            catch (Exception exception)
            {
                problem = $"Couldn't look for images: {exception.Message}";
                return false;
            }

            if (string.IsNullOrEmpty(path))
            {
                if (!Application.isEditor) problem = $"Put a PNG or JPG in {ImagesFolder}, then pick Image again.";
                return false;
            }

            if (Pictures.LoadImage(path) == null)
            {
                problem = $"Couldn't read {Path.GetFileName(path)} as an image.";
                return false;
            }

            return true;
        }
    }
}
