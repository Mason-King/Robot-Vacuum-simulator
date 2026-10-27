using System;
using System.IO;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// A run's inputs, saved as config.json in its run folder when the run starts. Everything needed to
    /// rebuild the same run: the floor plan as it was, and the resolved settings (defaults filled in, the
    /// concrete seed). It is the SDD's Config Snapshot: an immutable copy, never a live pointer to a
    /// Profile or level file that may change later.
    /// </summary>
    [Serializable]
    public class RunConfig
    {
        /// <summary>The floor plan, nested as plain JSON so the file is readable by just opening it.</summary>
        public LevelSnapshot level;

        /// <summary>The run's settings with every default resolved (see <see cref="RunSettings.Resolved"/>).</summary>
        public RunSettings settings;

        /// <summary>Whether the run was headless. Recorded so Graphics and headless runs can be compared (2.17).</summary>
        public bool headless;

        /// <summary>
        /// The program's safety cap on simulated seconds when this run started. Not a setting: it is
        /// recorded only so a run that hit it can be explained later.
        /// </summary>
        public float maxSimulatedSeconds = SimDefaults.MaxSimulatedSeconds;

        /// <summary>When the run started, UTC, ISO 8601.</summary>
        public string startedUtc;

        public void WriteTo(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(this, prettyPrint: true));
        }

        public static RunConfig ReadFrom(string path) =>
            JsonUtility.FromJson<RunConfig>(File.ReadAllText(path));
    }
}
