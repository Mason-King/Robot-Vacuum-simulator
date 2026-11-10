namespace RobotVacuum.AlgorithmApi
{
    /// <summary>
    /// The run's random stream for the algorithm. It is seeded from the run's seed, so the same level, settings and
    /// seed give the same run. Draw all randomness from here, never from System.Random or the clock.
    /// </summary>
    public interface IRandom
    {
        /// <summary>A uniform number in [0, 1).</summary>
        double NextDouble();

        /// <summary>A uniform number in [<paramref name="min"/>, <paramref name="max"/>).</summary>
        float NextFloat(float min, float max);

        /// <summary>A uniform whole number in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
        int NextInt(int minInclusive, int maxExclusive);

        /// <summary>A normally distributed number.</summary>
        double NextGaussian(double mean, double standardDeviation);
    }
}
