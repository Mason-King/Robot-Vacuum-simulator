namespace RobotVacuum.Sim
{
    /// <summary>
    /// A small deterministic random source. The brains draw from one of these rather than
    /// <see cref="UnityEngine.Random"/>, whose global state anything in the app can disturb, so a run
    /// depends only on its seed: the same level, pattern and seed drive the same route whether the run is
    /// watched in the simulator or stepped headless.
    /// </summary>
    public sealed class Rng
    {
        int seed;
        uint state;

        public Rng(int seed) => Seed = seed;

        /// <summary>The seed this source started from. Setting it rewinds the sequence.</summary>
        public int Seed
        {
            get => seed;
            set
            {
                seed = value;

                // Xorshift stalls at zero, so an unset seed starts from the golden-ratio constant instead.
                state = value == 0 ? 0x9E3779B9u : (uint)value;
            }
        }

        /// <summary>A fresh seed for a run nobody asked to reproduce.</summary>
        public static int NewSeed() => (int)(System.DateTime.UtcNow.Ticks & 0x7FFFFFFF);

        /// <summary>The next value, 0 inclusive to 1 exclusive.</summary>
        public float Value => Next() / 4294967296f;

        public float Range(float min, float max) => min + (max - min) * Value;

        /// <summary>Xorshift32: cheap, repeatable, and identical on every platform.</summary>
        uint Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }
}
