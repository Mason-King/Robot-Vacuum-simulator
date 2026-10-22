using System;
using System.Collections.Generic;
using Unity.Mathematics;

/// <summary>
/// Central RNG provider for one simulation run. One master seed derives
/// deterministic per-subsystem sub-generators via Random.CreateFromIndex.
///
/// IMPORTANT: Unity.Mathematics.Random is a struct — calling any Next*()
/// method mutates its internal state. Storing it by value in a collection
/// and retrieving a copy will silently break determinism, since mutations
/// to the copy never write back. RandomHandle below wraps it in a class
/// specifically so every caller shares the same mutable instance.
/// </summary>
public class RandomProvider
{
    // Extend this as new noise/randomness sources are added to the sim.
    public enum Subsystem : uint
    {
        SensorNoise = 0,
        RandomBounceAlgorithm = 1,
        DebrisAccumulation = 2,
    }

    private class RandomHandle
    {
        public Unity.Mathematics.Random Rng;
        public RandomHandle(Unity.Mathematics.Random rng) { Rng = rng; }
    }

    private readonly uint _masterSeed;
    private readonly Dictionary<Subsystem, RandomHandle> _subGenerators = new();

    public RandomProvider(uint masterSeed)
    {
        _masterSeed = masterSeed;
        foreach (Subsystem sub in Enum.GetValues(typeof(Subsystem)))
        {
            // Deterministic per-subsystem seed, derived from the one master seed.
            var rng = Unity.Mathematics.Random.CreateFromIndex(masterSeed + (uint)sub);
            _subGenerators[sub] = new RandomHandle(rng);
        }
    }

    /// <summary>Draw a uniform [0,1) double from the given subsystem's stream.</summary>
    public double NextDouble(Subsystem subsystem)
    {
        var handle = _subGenerators[subsystem];
        return handle.Rng.NextDouble(); // mutates handle.Rng.state in place — safe, handle is a shared reference
    }

    /// <summary>Draw a uniform float in [min, max) from the given subsystem's stream.</summary>
    public float NextFloat(Subsystem subsystem, float min, float max)
    {
        var handle = _subGenerators[subsystem];
        return handle.Rng.NextFloat(min, max);
    }

    // --- Snapshot / restore for replay and telemetry sampling ---
    // Random's entire state is a single uint, so this is trivially cheap —
    // no serialization, just copying/restoring one integer per subsystem.

    public Dictionary<Subsystem, uint> SnapshotState()
    {
        var snapshot = new Dictionary<Subsystem, uint>();
        foreach (var kvp in _subGenerators)
            snapshot[kvp.Key] = kvp.Value.Rng.state;
        return snapshot;
    }

    public void RestoreState(Dictionary<Subsystem, uint> snapshot)
    {
        foreach (var kvp in snapshot)
            _subGenerators[kvp.Key].Rng.state = kvp.Value;
    }
}
