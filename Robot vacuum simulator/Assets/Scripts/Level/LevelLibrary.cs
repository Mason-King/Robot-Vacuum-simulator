using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// The player's saved floor plans: one JSON file per level under the app's persistent data
    /// folder, so levels made in a shipped build live outside the Unity project entirely.
    /// </summary>
    public static class LevelLibrary
    {
        public const string FileExtension = ".json";

        public sealed class Entry
        {
            public string path;
            public LevelSnapshot level;
            public DateTime modified;
        }

        public static string Folder => Path.Combine(Application.persistentDataPath, "Levels");

        /// <summary>Every readable level, most recently edited first. Unreadable files are skipped with a warning.</summary>
        public static List<Entry> LoadAll()
        {
            var entries = new List<Entry>();
            if (!Directory.Exists(Folder)) return entries;

            foreach (string path in Directory.GetFiles(Folder, "*" + FileExtension))
            {
                try
                {
                    var level = Read(path);
                    if (level == null)
                    {
                        Debug.LogWarning($"Level library: '{path}' is not a level file, skipping.");
                        continue;
                    }

                    entries.Add(new Entry { path = path, level = level, modified = File.GetLastWriteTime(path) });
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Level library: could not read '{path}': {exception.Message}");
                }
            }

            entries.Sort((a, b) => b.modified.CompareTo(a.modified));
            return entries;
        }

        public static LevelSnapshot Read(string path) => LevelSnapshot.Parse(File.ReadAllText(path));

        /// <summary>Writes through a temp file so a crash mid-save cannot leave a half-written level.</summary>
        public static void Write(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Folder);

            string temp = path + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>Saves a new level under a file name derived from its display name. Returns the path.</summary>
        public static string Create(LevelSnapshot level)
        {
            string path = UniquePath(level.name);
            Write(path, level.ToJson());
            return path;
        }

        public static void Delete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>"Kitchen", then "Kitchen 2", "Kitchen 3" … skipping names already taken.</summary>
        public static string UniqueName(string wanted, IEnumerable<string> taken)
        {
            var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
            if (!used.Contains(wanted)) return wanted;

            for (int n = 2; ; n++)
            {
                string candidate = $"{wanted} {n}";
                if (!used.Contains(candidate)) return candidate;
            }
        }

        static string UniquePath(string name)
        {
            string slug = Slug(name);
            string path = Path.Combine(Folder, slug + FileExtension);

            for (int n = 2; File.Exists(path); n++)
                path = Path.Combine(Folder, $"{slug}-{n}{FileExtension}");

            return path;
        }

        static string Slug(string name)
        {
            var builder = new StringBuilder();
            bool pendingDash = false;

            foreach (char c in (name ?? string.Empty).ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (pendingDash && builder.Length > 0) builder.Append('-');
                    builder.Append(c);
                    pendingDash = false;
                }
                else
                {
                    pendingDash = true;
                }

                if (builder.Length >= 48) break;
            }

            return builder.Length > 0 ? builder.ToString() : "level";
        }
    }
}
