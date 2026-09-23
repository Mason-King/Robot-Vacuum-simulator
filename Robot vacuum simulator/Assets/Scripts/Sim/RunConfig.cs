using System;
using System.IO;
using RobotVacuum.LevelEditor;
using UnityEngine;

namespace RobotVacuum.Sim
{
    // The file that actually crosses the process boundary going IN. Everything
    // Secondary Boot needs to build and run a simulation, with nothing left to
    // ask the Primary process for afterwards -- once this file is written, the
    // two processes have no further connection until RunResult comes back.
    [Serializable]
    public class RunConfig
    {
        // LevelSnapshot is already the project's proven JSON-serializable level
        // format (used for save/load) -- nested directly here rather than
        // double-encoded as a JSON-string-inside-a-JSON-string, so the file is
        // readable/debuggable by just opening it.
        public LevelSnapshot level;

        public VacuumSettings vacuumSettings;
        public MovementPattern pattern;
        public PictureKind picture;
        public int seed;
        public float simulatedSeconds;
        public bool headless;

        // How fast Graphics-mode plays back (Time.timeScale while watching).
        // Irrelevant in headless -- that always runs at a fixed fast multiplier
        // regardless, since nobody's watching it.
        public float playbackSpeed = 1f;

        // Secondary Boot writes its RunResult here. Decided by Primary Boot
        // (not a fixed convention) so multiple concurrent/batched runs never
        // collide on the same output file.
        public string resultPath;

        public void WriteTo(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(this, prettyPrint: true));
        }

        public static RunConfig ReadFrom(string path) =>
            JsonUtility.FromJson<RunConfig>(File.ReadAllText(path));
    }
}
