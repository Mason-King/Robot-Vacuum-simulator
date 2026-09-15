using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>
    /// Carries a level across a scene load. The sender calls <see cref="Send"/> just before loading the
    /// simulation scene; the first <see cref="LevelRenderer"/> to wake there takes it, before anything
    /// else in that scene reads the renderer's level.
    /// </summary>
    public static class LevelHandoff
    {
        static LevelData pending;

        /// <summary>The level most recently taken by a renderer, or null.</summary>
        public static LevelData Delivered { get; private set; }

        public static bool HasPending => pending != null;

        public static void Send(LevelData level) => pending = level;

        /// <summary>Takes the pending level, once. A destroyed level counts as nothing pending.</summary>
        public static bool TryTake(out LevelData level)
        {
            level = pending;
            pending = null;
            if (level == null) return false;

            Delivered = level;
            return true;
        }

        /// <summary>Drops a level nobody took and hands it back so the caller can destroy it.</summary>
        public static LevelData CancelPending()
        {
            var level = pending;
            pending = null;
            return level;
        }

        // Statics survive entering Play Mode when domain reload is off.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Clear()
        {
            pending = null;
            Delivered = null;
        }
    }
}
