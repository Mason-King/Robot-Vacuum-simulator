using System.Collections.Generic;
using RobotVacuum.Level;
using UnityEngine;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// What to warn about before a floor plan is run, and what each problem will do to the results. Every
    /// way of starting a run goes through this, so the player always hears about it and can still go ahead.
    /// </summary>
    public static class RunChecks
    {
        /// <summary>One paragraph per problem, or an empty list when the floor plan is fine to run.</summary>
        public static List<string> Problems(LevelData level)
        {
            var problems = new List<string>();
            if (level == null || level.Rooms.Count == 0) return problems;

            if (level.RoomIndexAt(level.RobotSpawn) < 0)
            {
                problems.Add("The vacuum starts outside every room, so it can't reach any floor. " +
                             "Every room will register 0% coverage.");
                return problems;
            }

            var unreachable = level.UnreachableRooms();
            if (unreachable.Count > 0)
            {
                var names = new List<string>();
                foreach (int index in unreachable)
                {
                    var room = level.GetRoom(index);
                    names.Add(room != null && !string.IsNullOrEmpty(room.name) ? room.name : $"Room {index + 1}");
                }

                problems.Add($"No doorway path reaches {string.Join(", ", names)} from the vacuum's start. " +
                             $"{(names.Count == 1 ? "That room" : "Those rooms")} will register 0% coverage, " +
                             "which also lowers the whole floor plan's coverage.");
            }

            string openings = EditorSession.DescribeOpenings(level);
            if (openings != null) problems.Add(openings);

            return problems;
        }

        /// <summary>As <see cref="Problems(LevelData)"/>, for a saved floor plan.</summary>
        public static List<string> Problems(LevelSnapshot snapshot)
        {
            if (snapshot == null) return new List<string>();

            var scratch = ScriptableObject.CreateInstance<LevelData>();
            try
            {
                LevelSnapshot.Parse(snapshot.ToJson(false))?.ApplyTo(scratch);
                return Problems(scratch);
            }
            finally
            {
                Object.DestroyImmediate(scratch);
            }
        }

        /// <summary>
        /// Runs <paramref name="proceed"/> straight away when there is nothing to warn about; otherwise shows
        /// the problems and runs it only if the player chooses to go ahead anyway.
        /// </summary>
        public static void ConfirmThen(OverlayLayer overlay, List<string> problems, System.Action proceed,
            string fixLabel = "Fix first", System.Action fix = null)
        {
            if (problems == null || problems.Count == 0)
            {
                proceed();
                return;
            }

            string message = string.Join("\n\n", problems);
            if (fix != null)
                overlay.ShowDialog("Check the floor plan", message, null,
                    new DialogButton("Cancel", ButtonStyle.Ghost, null),
                    new DialogButton(fixLabel, ButtonStyle.Primary, fix),
                    new DialogButton("Run anyway", ButtonStyle.Danger, proceed));
            else
                overlay.ShowDialog("Check the floor plan", message, null,
                    new DialogButton(fixLabel, ButtonStyle.Primary, null),
                    new DialogButton("Run anyway", ButtonStyle.Danger, proceed));
        }
    }
}
