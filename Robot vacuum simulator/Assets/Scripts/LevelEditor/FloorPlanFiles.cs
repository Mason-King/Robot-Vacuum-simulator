using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// Moves floor plans and configurations in and out of the app as plain JSON files that can be written
    /// or edited by hand (the format is described in Docs/floor-plan-format.md). The Unity Editor gets
    /// native file dialogs; a player build has none, so it exports to <see cref="ExportFolder"/> and imports
    /// whatever has been dropped into <see cref="ImportFolder"/>.
    /// </summary>
    public static class FloorPlanFiles
    {
        public static string ExportFolder => Path.Combine(Application.persistentDataPath, "Exports");
        public static string ImportFolder => Path.Combine(Application.persistentDataPath, "Import");

        /// <summary>
        /// Writes <paramref name="level"/> to a file, naming each room's covering so the file reads without
        /// the palette. Returns the path, or null if the user cancelled.
        /// </summary>
        public static string Export(LevelSnapshot level, FloorPalette palette)
        {
            var copy = LevelSnapshot.Parse(level.ToJson(false));
            foreach (var room in copy.rooms)
                if (room != null && palette != null && palette.Get(room.floorIndex) != null)
                    room.floor = palette.LabelOf(room.floorIndex);

            string path = PickSavePath("Export floor plan", copy.name, "json");
            if (path == null) return null;

            File.WriteAllText(path, copy.ToJson(true));
            return path;
        }

        /// <summary>
        /// Reads a floor plan file. Rooms that name their covering get the matching palette entry; unknown
        /// names keep their index. Returns null with a reason when the file isn't a floor plan.
        /// </summary>
        public static LevelSnapshot Import(string path, FloorPalette palette, out string problem)
        {
            problem = null;

            LevelSnapshot level;
            try
            {
                level = LevelSnapshot.Parse(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                problem = $"Couldn't read {Path.GetFileName(path)}: {exception.Message}";
                return null;
            }

            if (level == null || level.ValidRoomCount == 0)
            {
                problem = $"{Path.GetFileName(path)} isn't a floor plan with at least one room.";
                return null;
            }

            ResolveFloorNames(level, palette);
            return level;
        }

        /// <summary>Turns room covering names into palette indices, then drops the names.</summary>
        public static void ResolveFloorNames(LevelSnapshot level, FloorPalette palette)
        {
            foreach (var room in level.rooms)
            {
                if (room == null || string.IsNullOrWhiteSpace(room.floor)) continue;

                if (palette != null)
                    for (int i = 0; i < palette.Count; i++)
                        if (string.Equals(palette.LabelOf(i), room.floor.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            room.floorIndex = i;
                            break;
                        }

                room.floor = null;
            }
        }

        /// <summary>Files to import: the one chosen in a dialog, or every JSON file in the import folder.</summary>
        public static List<string> PickImports(string title)
        {
#if UNITY_EDITOR
            string path = UnityEditor.EditorUtility.OpenFilePanel(title, string.Empty, "json");
            return string.IsNullOrEmpty(path) ? new List<string>() : new List<string> { path };
#else
            Directory.CreateDirectory(ImportFolder);
            return Directory.GetFiles(ImportFolder, "*.json").OrderBy(File.GetLastWriteTime).ToList();
#endif
        }

        /// <summary>Moves an imported file aside so it isn't imported twice. Only the import folder is touched.</summary>
        public static void MarkImported(string path)
        {
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(ImportFolder)) return;

                string done = Path.Combine(ImportFolder, "Imported");
                Directory.CreateDirectory(done);
                string target = Path.Combine(done, Path.GetFileName(path));
                if (File.Exists(target)) File.Delete(target);
                File.Move(path, target);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Import: could not move '{path}' aside: {exception.Message}");
            }
        }

        /// <summary>Asks where to save (Unity Editor) or picks a free name in the export folder (player build).</summary>
        public static string PickSavePath(string title, string name, string extension)
        {
            string fileName = SafeFileName(name) + "." + extension;
#if UNITY_EDITOR
            string path = UnityEditor.EditorUtility.SaveFilePanel(title, string.Empty, fileName, extension);
            return string.IsNullOrEmpty(path) ? null : path;
#else
            Directory.CreateDirectory(ExportFolder);
            string path = Path.Combine(ExportFolder, fileName);
            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(ExportFolder, $"{SafeFileName(name)} {n}.{extension}");
            return path;
#endif
        }

        /// <summary>Opens a folder in the system file browser (Explorer on Windows, Finder on macOS).</summary>
        public static void Reveal(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);
            Application.OpenURL(new Uri(Path.GetFullPath(folder)).AbsoluteUri);
        }

        static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((name ?? "floor plan").Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
            return clean.Length > 0 ? clean : "floor plan";
        }
    }
}
