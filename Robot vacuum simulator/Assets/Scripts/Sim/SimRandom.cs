namespace RobotVacuum.Sim
{
    /// <summary>
    /// The one source of randomness for the simulation. Every random choice the vacuum, its algorithms and
    /// the cats make comes from here, so a run is fully determined by its seed: reseed with the same value
    /// and the run plays out identically, rendered or headless.
    /// </summary>
    public static class SimRandom
    {
        static System.Random random = new System.Random(0);

        /// <summary>The seed the current run started from.</summary>
        public static int Seed { get; private set; }

        public static void Reseed(int seed)
        {
            Seed = seed;
            random = new System.Random(seed);
        }

        /// <summary>A uniform value in [0, 1).</summary>
        public static float Value => (float)random.NextDouble();

        /// <summary>A uniform value in [min, max).</summary>
        public static float Range(float min, float max) => min + (max - min) * Value;

        /// <summary>A uniform integer in [0, max).</summary>
        public static int Next(int max) => random.Next(max);

        /// <summary>A seed for a sub-system that keeps its own generator, drawn from this one.</summary>
        public static int NextSeed() => random.Next();

        /// <summary>A fresh seed for a run that wasn't given one, from the clock rather than the sequence.</summary>
        public static int NewRunSeed() => System.Environment.TickCount & int.MaxValue;

        // Statics survive entering Play Mode when domain reload is off.
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Reseed(0);
    }
}
