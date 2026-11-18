namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// Waiting out a temporary obstacle on the way to the dock. When the way home is blocked or has no path, and the
    /// map holds temporary obstacles (<see cref="InternalModelWriter.HasTransientBlocks"/>, e.g. a cat in a doorway),
    /// the robot stops and waits instead of giving up: every <see cref="ControllerConstants.HomeReplanSeconds"/> it
    /// forgets temporary obstacles it hasn't seen for a while and plans again. It gives up only when there are no
    /// temporary obstacles to wait for, or after <see cref="ControllerConstants.HomeWaitMaxSeconds"/> of waiting in all.
    /// </summary>
    public sealed class WayHomeWait
    {
        readonly float replanSeconds;
        readonly float maxSeconds;
        float started = -1f;
        float replanAt;

        public WayHomeWait(float replanSeconds = ControllerConstants.HomeReplanSeconds, float maxSeconds = ControllerConstants.HomeWaitMaxSeconds)
        {
            this.replanSeconds = replanSeconds;
            this.maxSeconds = maxSeconds;
        }

        /// <summary>True while the robot is stopped, waiting for the way to clear.</summary>
        public bool Waiting { get; private set; }

        /// <summary>True once the robot has had to wait at all on this trip home.</summary>
        public bool EverWaited => started >= 0f;

        public float WaitedSeconds(float now) => started >= 0f ? now - started : 0f;

        /// <summary>A new trip home: no waiting yet.</summary>
        public void Reset()
        {
            started = -1f;
            Waiting = false;
        }

        /// <summary>
        /// The way home just failed. True: wait (a temporary obstacle may clear); false: give up, because nothing on
        /// the map is temporary or the robot has waited long enough.
        /// </summary>
        public bool TryWait(float now, bool temporaryObstacles)
        {
            if (!temporaryObstacles) return false;
            if (started < 0f) started = now;
            if (now - started >= maxSeconds) return false;

            Waiting = true;
            replanAt = now + replanSeconds;
            return true;
        }

        /// <summary>True (once) when it is time to forget stale temporary obstacles and plan the way home again.</summary>
        public bool ReplanDue(float now)
        {
            if (!Waiting || now < replanAt) return false;
            Waiting = false;
            return true;
        }
    }
}
