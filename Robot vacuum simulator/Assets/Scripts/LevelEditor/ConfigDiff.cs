using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// What changed between two configurations (<see cref="SimConfig.Data"/>), setting by setting: "Vacuum › Drive
    /// speed: 0.3 → 0.35". Works on any serialisable settings object by its public fields, so a setting added later
    /// is compared without changes here. List items with a "floor", "name" or "id" field are matched by it.
    /// </summary>
    public static class ConfigDiff
    {
        public const string Separator = " › ";

        /// <summary>One line per setting that differs, in field order; settings only one side has show "(none)".</summary>
        public static List<string> Compare(object before, object after)
        {
            var a = Flatten(before);
            var b = Flatten(after);
            var lines = new List<string>();
            foreach (var pair in b)
            {
                a.TryGetValue(pair.Key, out string old);
                if (old != pair.Value) lines.Add($"{pair.Key}: {old ?? "(none)"} → {pair.Value}");
            }
            foreach (var pair in a)
                if (!b.ContainsKey(pair.Key)) lines.Add($"{pair.Key}: {pair.Value} → (none)");
            return lines;
        }

        /// <summary>
        /// A short name for a change, for undo and the version history: the setting's own name ("Drive speed") when
        /// one setting changed, else "Drive speed and 2 more", else "Change settings".
        /// </summary>
        public static string Label(object before, object after)
        {
            var a = Flatten(before);
            var b = Flatten(after);
            string first = null;
            int changed = 0;
            foreach (var pair in b)
            {
                a.TryGetValue(pair.Key, out string old);
                if (old == pair.Value) continue;
                changed++;
                first ??= pair.Key;
            }
            foreach (var pair in a)
                if (!b.ContainsKey(pair.Key)) { changed++; first ??= pair.Key; }
            if (first == null) return "Change settings";
            int cut = first.LastIndexOf(Separator, StringComparison.Ordinal);
            string setting = cut >= 0 ? first.Substring(cut + Separator.Length) : first;
            return changed == 1 ? setting : $"{setting} and {changed - 1} more";
        }

        /// <summary>Every setting as "Group › Setting" → its value as text, in field order.</summary>
        public static Dictionary<string, string> Flatten(object settings)
        {
            var values = new Dictionary<string, string>();
            if (settings != null) Walk(settings, null, values, 0);
            return values;
        }

        static void Walk(object value, string path, Dictionary<string, string> into, int depth)
        {
            if (depth > 8) return;
            if (value == null)
            {
                if (path != null) into[path] = "(none)";
                return;
            }

            var type = value.GetType();
            if (IsLeaf(type))
            {
                into[path ?? "Value"] = Format(value);
                return;
            }

            if (value is IList list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    object item = list[i];
                    string key = KeyOf(item) ?? (i + 1).ToString(CultureInfo.InvariantCulture);
                    string itemPath = Join(path, key);
                    if (item != null && !IsLeaf(item.GetType()) && KeyOf(item) != null && SingleOtherField(item, out var field))
                        into[itemPath] = Format(field.GetValue(item)); // "Cleaning effort › Carpet: 1.2"
                    else Walk(item, itemPath, into, depth + 1);
                }
                if (list.Count == 0 && path != null) into[path] = "(none)";
                return;
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsNotSerialized) continue;
                Walk(field.GetValue(value), Join(path, Humanise(field.Name)), into, depth + 1);
            }
        }

        static bool IsLeaf(Type type) =>
            type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
            type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Color);

        static readonly string[] KeyFields = { "floor", "name", "id" };

        static string KeyOf(object item)
        {
            if (item == null) return null;
            foreach (string name in KeyFields)
            {
                var field = item.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (field != null && field.FieldType == typeof(string) && field.GetValue(item) is string key && key.Length > 0)
                    return key;
            }
            return null;
        }

        /// <summary>The item has its key field and exactly one other: that one is its value.</summary>
        static bool SingleOtherField(object item, out FieldInfo other)
        {
            other = null;
            var fields = item.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length != 2) return false;
            foreach (var field in fields)
                if (Array.IndexOf(KeyFields, field.Name) < 0) other = field;
            return other != null && IsLeaf(other.FieldType);
        }

        static string Join(string path, string name) => path == null ? name : path + Separator + name;

        static string Format(object value) => value switch
        {
            null => "(none)",
            bool b => b ? "on" : "off",
            float f => f.ToString("0.####", CultureInfo.InvariantCulture),
            double d => d.ToString("0.####", CultureInfo.InvariantCulture),
            string s => s.Length == 0 ? "(empty)" : s,
            Color c => "#" + ColorUtility.ToHtmlStringRGBA(c),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        /// <summary>"driveSpeed" → "Drive speed", "runSettings" → "Run settings", "slamEnabled" → "Slam enabled".</summary>
        public static string Humanise(string field)
        {
            if (string.IsNullOrEmpty(field)) return field;
            var builder = new StringBuilder();
            for (int i = 0; i < field.Length; i++)
            {
                char c = field[i];
                if (i == 0) builder.Append(char.ToUpperInvariant(c));
                else if (char.IsUpper(c) && !char.IsUpper(field[i - 1])) builder.Append(' ').Append(char.ToLowerInvariant(c));
                else builder.Append(c);
            }
            return builder.ToString();
        }
    }
}
