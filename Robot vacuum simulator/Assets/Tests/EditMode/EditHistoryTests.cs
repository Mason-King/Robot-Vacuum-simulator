using NUnit.Framework;
using RobotVacuum.LevelEditor;

namespace RobotVacuum.Tests
{
    public class EditHistoryTests
    {
        [Test]
        public void NewHistory_HasNothingToUndoOrRedo()
        {
            var history = new EditHistory();

            Assert.IsFalse(history.CanUndo);
            Assert.IsFalse(history.CanRedo);
            Assert.IsNull(history.UndoLabel);
            Assert.IsNull(history.RedoLabel);
            Assert.IsFalse(history.TryUndo("now", out _, out _));
            Assert.IsFalse(history.TryRedo("now", out _, out _));
        }

        [Test]
        public void Undo_RestoresStateBeforeEdit_AndRedoReturnsToCurrent()
        {
            var history = new EditHistory();
            history.Push("Add Room", "before");

            Assert.AreEqual("Add Room", history.UndoLabel);
            Assert.IsTrue(history.TryUndo("after", out string restored, out string label));
            Assert.AreEqual("before", restored);
            Assert.AreEqual("Add Room", label);
            Assert.IsFalse(history.CanUndo);
            Assert.AreEqual("Add Room", history.RedoLabel);

            Assert.IsTrue(history.TryRedo("before", out restored, out label));
            Assert.AreEqual("after", restored);
            Assert.AreEqual("Add Room", label);
            Assert.IsTrue(history.CanUndo);
            Assert.IsFalse(history.CanRedo);
        }

        [Test]
        public void Undo_WalksBackInOrder()
        {
            var history = new EditHistory();
            history.Push("First", "s0");
            history.Push("Second", "s1");

            history.TryUndo("s2", out string restored, out string label);
            Assert.AreEqual(("s1", "Second"), (restored, label));

            history.TryUndo("s1", out restored, out label);
            Assert.AreEqual(("s0", "First"), (restored, label));
        }

        [Test]
        public void Push_DiscardsRedoBranch()
        {
            var history = new EditHistory();
            history.Push("First", "s0");
            history.TryUndo("s1", out _, out _);
            Assert.IsTrue(history.CanRedo);

            history.Push("Other", "s0");

            Assert.IsFalse(history.CanRedo);
        }

        [Test]
        public void DiscardIfUnchanged_DropsOnlyAMatchingTopEntry()
        {
            var history = new EditHistory();
            history.Push("Real", "s0");
            history.Push("Click", "s1");

            history.DiscardIfUnchanged("something else");
            Assert.AreEqual("Click", history.UndoLabel);

            history.DiscardIfUnchanged("s1");
            Assert.AreEqual("Real", history.UndoLabel);

            // Only the newest entry is ever compared.
            history.DiscardIfUnchanged("s1");
            Assert.AreEqual("Real", history.UndoLabel);
        }

        [Test]
        public void Push_BeyondCapacity_DropsOldestEntries()
        {
            var history = new EditHistory();
            for (int i = 0; i <= EditHistory.Capacity; i++) history.Push($"Edit {i}", $"s{i}");

            string restored = null;
            int steps = 0;
            while (history.TryUndo("current", out string state, out _))
            {
                restored = state;
                steps++;
            }

            Assert.AreEqual(EditHistory.Capacity, steps);
            Assert.AreEqual("s1", restored, "the very first state should have been evicted");
        }

        [Test]
        public void Clear_EmptiesBothStacks()
        {
            var history = new EditHistory();
            history.Push("A", "s0");
            history.Push("B", "s1");
            history.TryUndo("s2", out _, out _);

            history.Clear();

            Assert.IsFalse(history.CanUndo);
            Assert.IsFalse(history.CanRedo);
        }
    }
}
