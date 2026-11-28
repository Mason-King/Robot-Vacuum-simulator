using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>One saved version in a <see cref="VersionHistory"/>: what and when, not the content itself.</summary>
    [Serializable]
    public sealed class VersionInfo
    {
        /// <summary>1, 2, 3 … in the order saved; never reused.</summary>
        public int number;

        /// <summary>When it was saved, local time, round-trip format ("o").</summary>
        public string savedAt;

        /// <summary>What made it ("Saved", "Created", "Before a run" …), or a name the user gave it.</summary>
        public string label;

        /// <summary>The edits since the version before, in order, with repeats folded ("Move Room ×4").</summary>
        public List<string> edits = new List<string>();

        /// <summary>SHA-256 of the content (hex): a save that changed nothing makes no new version.</summary>
        public string hash;

        public DateTime SavedAt =>
            DateTime.TryParse(savedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.MinValue;

        /// <summary>"v3 · Saved", for lists and compare headings.</summary>
        public string Title => $"v{number} · {label}";
    }

    /// <summary>
    /// The full version history of one document (a floor plan, or the configuration): every saved version's
    /// content as its own JSON file in <see cref="Folder"/>, plus an index (versions.json). Nothing is ever pruned,
    /// and a version is never changed after it is written, except for its label.
    ///
    /// Undo/redo is separate (<see cref="EditHistory"/>, in memory, per editing session); this is what survives it.
    /// </summary>
    public sealed class VersionHistory
    {
        const string IndexFile = "versions.json";

        [Serializable]
        sealed class Index
        {
            public List<VersionInfo> versions = new List<VersionInfo>();
        }

        Index index;

        /// <summary>Where every document's history lives: History/ under the app's data folder. Tests point it elsewhere.</summary>
        public static string Root
        {
            get => root ??= Path.Combine(Application.persistentDataPath, "History");
            set => root = value;
        }

        static string root;

        /// <summary>A floor plan's history, by its id (<see cref="LevelSnapshot.id"/>).</summary>
        public static VersionHistory ForFloorPlan(string id) => new VersionHistory(Path.Combine(Root, "floor-plans", SafeName(id)));

        /// <summary>The simulation configuration's history.</summary>
        public static VersionHistory ForConfiguration() => new VersionHistory(Path.Combine(Root, "configuration"));

        public VersionHistory(string folder)
        {
            Folder = folder;
        }

        public string Folder { get; }

        /// <summary>Every version, oldest first.</summary>
        public IReadOnlyList<VersionInfo> Versions => Load().versions;

        public VersionInfo Latest => Versions.Count > 0 ? Versions[Versions.Count - 1] : null;

        public VersionInfo Find(int number) => Load().versions.Find(v => v.number == number);

        /// <summary>
        /// Saves <paramref name="content"/> as a new version and returns it, or returns null when it is the same as
        /// the latest version (a save that changed nothing). Throws on IO failure.
        /// </summary>
        public VersionInfo Record(string content, string label, IList<string> edits = null)
        {
            content ??= string.Empty;
            string hash = Hash(content);
            var latest = Latest;
            if (latest != null && latest.hash == hash) return null;

            var version = new VersionInfo
            {
                number = latest != null ? latest.number + 1 : 1,
                savedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                label = string.IsNullOrWhiteSpace(label) ? "Saved" : label.Trim(),
                edits = edits != null ? new List<string>(edits) : new List<string>(),
                hash = hash,
            };

            Directory.CreateDirectory(Folder);
            WriteAtomically(ContentPath(version.number), content);
            index.versions.Add(version);
            SaveIndex();
            return version;
        }

        /// <summary>The content saved as version <paramref name="number"/>, or null if there is none.</summary>
        public string Read(int number)
        {
            string path = ContentPath(number);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        /// <summary>Gives a version a name of the user's ("Before moving the kitchen"). Content is untouched.</summary>
        public bool Relabel(int number, string label)
        {
            var version = Find(number);
            if (version == null || string.IsNullOrWhiteSpace(label)) return false;
            version.label = label.Trim();
            SaveIndex();
            return true;
        }

        /// <summary>Removes the whole history (its document was deleted).</summary>
        public void DeleteAll()
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
            index = new Index();
        }

        /// <summary>
        /// Folds repeats in an edit log: "Move Room", "Move Room", "Add Doorway" → "Move Room ×2", "Add Doorway".
        /// </summary>
        public static List<string> Summarise(IEnumerable<string> edits)
        {
            var folded = new List<string>();
            string last = null;
            int count = 0;
            foreach (string edit in edits)
            {
                if (string.IsNullOrEmpty(edit)) continue;
                if (edit == last)
                {
                    count++;
                    continue;
                }
                if (last != null) folded.Add(count > 1 ? $"{last} ×{count}" : last);
                last = edit;
                count = 1;
            }
            if (last != null) folded.Add(count > 1 ? $"{last} ×{count}" : last);
            return folded;
        }

        // ---- files -------------------------------------------------------------------------------------------

        Index Load()
        {
            if (index != null) return index;
            index = new Index();
            string path = Path.Combine(Folder, IndexFile);
            if (!File.Exists(path)) return index;
            try
            {
                index = JsonUtility.FromJson<Index>(File.ReadAllText(path)) ?? new Index();
                index.versions ??= new List<VersionInfo>();
                index.versions.RemoveAll(v => v == null);
                index.versions.Sort((a, b) => a.number.CompareTo(b.number));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Version history: could not read '{path}': {exception.Message}");
                index = new Index();
            }
            return index;
        }

        void SaveIndex()
        {
            Directory.CreateDirectory(Folder);
            WriteAtomically(Path.Combine(Folder, IndexFile), JsonUtility.ToJson(index, true));
        }

        string ContentPath(int number) => Path.Combine(Folder, $"v{number:D4}.json");

        static void WriteAtomically(string path, string text)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, text, Encoding.UTF8);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        static string Hash(string content)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        static string SafeName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "unknown";
            var builder = new StringBuilder();
            foreach (char c in id) builder.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
            return builder.ToString();
        }
    }
}
