using System.Collections.Generic;

namespace RobotVacuum.LevelEditor
{
    /// <summary>
    /// Snapshot-based undo/redo. Each entry is the whole level as JSON, which for floor plans of a
    /// few dozen rooms is a handful of kilobytes and sidesteps per-operation inverse logic entirely.
    /// </summary>
    public sealed class EditHistory
    {
        public const int Capacity = 200;

        struct Entry
        {
            public string label;
            public string state;
        }

        readonly List<Entry> undo = new List<Entry>();
        readonly List<Entry> redo = new List<Entry>();

        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public string UndoLabel => CanUndo ? undo[undo.Count - 1].label : null;
        public string RedoLabel => CanRedo ? redo[redo.Count - 1].label : null;

        /// <summary>Records the state from before an edit. Any redo branch is discarded.</summary>
        public void Push(string label, string stateBefore)
        {
            undo.Add(new Entry { label = label, state = stateBefore });
            if (undo.Count > Capacity) undo.RemoveAt(0);
            redo.Clear();
        }

        /// <summary>
        /// Drops the newest undo step when it matches <paramref name="currentState"/>, so a click that
        /// grabbed something without moving it does not leave an empty undo behind.
        /// </summary>
        public void DiscardIfUnchanged(string currentState)
        {
            if (undo.Count > 0 && undo[undo.Count - 1].state == currentState) undo.RemoveAt(undo.Count - 1);
        }

        public bool TryUndo(string currentState, out string restore, out string label) =>
            Step(undo, redo, currentState, out restore, out label);

        public bool TryRedo(string currentState, out string restore, out string label) =>
            Step(redo, undo, currentState, out restore, out label);

        public void Clear()
        {
            undo.Clear();
            redo.Clear();
        }

        static bool Step(List<Entry> from, List<Entry> to, string currentState, out string restore, out string label)
        {
            restore = null;
            label = null;
            if (from.Count == 0) return false;

            var entry = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            to.Add(new Entry { label = entry.label, state = currentState });

            restore = entry.state;
            label = entry.label;
            return true;
        }
    }
}
