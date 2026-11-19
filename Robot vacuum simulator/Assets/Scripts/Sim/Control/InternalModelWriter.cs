using System.Collections.Generic;
using RobotVacuumSim.EnvironmentModel;
using RobotVacuum.AlgorithmApi;
using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>
    /// The ONLY writer to the Internal Model (owned by the controller, never given to an algorithm). Wraps the
    /// live <see cref="InternalModelGrid"/> and keeps three things the grid itself doesn't:
    ///
    ///   * a CHANGE FEED: which cells changed meaningfully since the algorithm last asked, so an algorithm can
    ///     update its plan incrementally instead of rescanning or copying the map (design doc 4.1);
    ///   * a TOPOLOGY VERSION, bumped only when a cell flips between impassable (exactly 0) and passable, so the
    ///     cached reachability flood fill is redone only when the answer could have changed;
    ///   * the robot's FLOOR-EASE MEMORY: how easily it believes each floor cell drives, kept apart from
    ///     traversability so a cell that was briefly believed blocked (the cat sat there) goes back to its
    ///     floor's ease, not to 1, when it is seen clear again. Sparse: only cells whose ease isn't 1 are stored.
    ///
    /// Dwell and cleanliness beliefs change on every step under the robot, so they are NOT reported in the feed;
    /// an algorithm reads them directly.
    /// </summary>
    public sealed class InternalModelWriter
    {
        /// <summary>A traversability or resistance change smaller than this is not reported in the change feed (but is still stored).</summary>
        public const float ReportThreshold = 0.02f;

        /// <summary>
        /// Most unread changes the feed holds. Past this it stops collecting and tells the reader to rescan
        /// everything instead -- so an algorithm that never reads the feed (RandomBounce) costs no memory.
        /// </summary>
        public const int MaxPendingChanges = 100_000;

        readonly Dictionary<long, float> ease = new Dictionary<long, float>();
        readonly HashSet<long> permanent = new HashSet<long>();
        readonly Dictionary<long, float> transient = new Dictionary<long, float>();
        readonly List<long> forgetting = new List<long>();
        readonly List<CellIndex> pending = new List<CellIndex>();
        readonly HashSet<long> pendingKeys = new HashSet<long>();
        bool overflowed;

        public InternalModelWriter(InternalModelGrid grid)
        {
            Grid = grid;
        }

        /// <summary>The live grid. Read it through <see cref="InternalModelView"/>; only this class writes it.</summary>
        public InternalModelGrid Grid { get; }

        /// <summary>Bumped whenever any cell flips between impassable (0) and passable.</summary>
        public int TopologyVersion { get; private set; }

        /// <summary>Bumped on every reported change, so a reader can tell "something changed" without taking the feed.</summary>
        public int ChangeCounter { get; private set; }

        /// <summary>Simulated time, set by the controller each step; stamps when a temporary obstacle was last seen.</summary>
        public float Clock { get; set; }

        /// <summary>A write at least this passable counts as the robot knowing the cell is floor.</summary>
        public const float KnownFloorThreshold = 0.5f;

        public BeliefCell Get(int row, int col) => Grid.GetCell(row, col);

        public float Traversability(int row, int col) => Grid.GetCell(row, col).traversabilityBelief;

        // ---- Writes ----------------------------------------------------------

        public void SetTraversability(int row, int col, float value)
        {
            var cell = Grid.GetCell(row, col);
            float old = cell.traversabilityBelief;
            bool learnsFloor = value >= KnownFloorThreshold && !cell.knownFloor;
            if (value <= 0f || transient.Count > 0) TrackTransient(CellIndex.Pack(row, col), old, value, cell.knownFloor);
            if (old == value && !learnsFloor) return; // a cell already equal to the observation is not rewritten

            cell.traversabilityBelief = value;
            if (learnsFloor) cell.knownFloor = true;
            if (!Grid.SetCell(row, col, cell)) return; // beyond the runaway guard
            if (old == value) return; // only learned that it is floor: nothing to report

            if ((old <= 0f) != (value <= 0f)) TopologyVersion++;
            if (Mathf.Abs(value - old) >= ReportThreshold || (old <= 0f) != (value <= 0f)) Record(row, col);
        }

        public void SetVacuumResistance(int row, int col, float value)
        {
            var cell = Grid.GetCell(row, col);
            float old = cell.vacuumResistanceBelief;
            if (old == value) return;

            cell.vacuumResistanceBelief = value;
            if (!Grid.SetCell(row, col, cell)) return;

            if (Mathf.Abs(value - old) >= ReportThreshold) Record(row, col);
        }

        /// <summary>The robot's own estimate of what it did to a cell this step (not reported in the change feed).</summary>
        public void AddDwellAndCleanliness(int row, int col, float dwellSeconds, float cleanliness)
        {
            var cell = Grid.GetCell(row, col);
            cell.dwellBelief += dwellSeconds;
            cell.cleanlinessBelief += cleanliness;
            Grid.SetCell(row, col, cell);
        }

        /// <summary>Writes a whole cell during the Normal preload (reported like any other change).</summary>
        public void Preload(int row, int col, float traversability, float vacuumResistance)
        {
            SetTraversability(row, col, traversability);
            SetVacuumResistance(row, col, vacuumResistance);
        }

        /// <summary>Makes sure the grid covers this rectangle in one growth, rather than growing again and again as a preload sweeps across it.</summary>
        public void Reserve(int minRow, int minCol, int maxRow, int maxCol)
        {
            // Writing the default into two opposite corners grows the grid to cover both without changing any belief.
            Grid.SetCell(minRow, minCol, Grid.GetCell(minRow, minCol));
            Grid.SetCell(maxRow, maxCol, Grid.GetCell(maxRow, maxCol));
        }

        // ---- Permanent cells (Normal mode only) ---------------------------------

        /// <summary>
        /// Marks a cell as a wall the Level Object guarantees (Normal preload only). In a Normal run, observations
        /// never clear a permanent cell: a lidar ray that grazes a wall's surface would otherwise punch speckles
        /// into it. Empty in SLAM runs, where nothing is known in advance. A door that opens mid-run would need
        /// its gap left out of this set; closed doors are not marked permanent for that reason.
        /// </summary>
        public void MarkPermanent(int row, int col) => permanent.Add(CellIndex.Pack(row, col));

        public bool IsPermanent(int row, int col) => permanent.Count > 0 && permanent.Contains(CellIndex.Pack(row, col));

        // ---- Temporary (dynamic) obstacles ----------------------------------------

        /// <summary>
        /// A TEMPORARY obstacle is a cell the robot knew as floor (preloaded floor, or seen clear) that is now
        /// believed blocked: something moved onto it, like a cat. Walls, furniture and closed doors are not
        /// temporary: they are preloaded as blocked, or blocked the first time they are seen. Each temporary
        /// cell remembers when it was last observed blocked, so a stale one can be forgotten
        /// (<see cref="ForgetTransients"/>) when the robot is waiting for a way through.
        /// </summary>
        void TrackTransient(long key, float old, float value, bool knownFloor)
        {
            if (value > 0f)
            {
                transient.Remove(key); // seen clear again
                return;
            }

            if (transient.ContainsKey(key)) transient[key] = Clock; // still there
            else if (old > 0f && knownFloor && !permanent.Contains(key)) transient[key] = Clock; // something moved in
        }

        /// <summary>True while any cell is blocked by a temporary obstacle.</summary>
        public bool HasTransientBlocks => transient.Count > 0;

        public int TransientCount => transient.Count;

        public bool IsTransient(int row, int col) => transient.ContainsKey(CellIndex.Pack(row, col));

        /// <summary>
        /// Clears every temporary obstacle last seen before <paramref name="seenBefore"/> back to its floor's ease:
        /// whatever was there has had time to move on. One still in view is re-marked by the next observation.
        /// Returns how many cells were cleared.
        /// </summary>
        public int ForgetTransients(float seenBefore)
        {
            forgetting.Clear();
            foreach (var pair in transient)
                if (pair.Value < seenBefore) forgetting.Add(pair.Key);

            foreach (long key in forgetting)
            {
                int row = (int)(key >> 32), col = (int)(uint)key; // CellIndex.Pack reversed
                SetTraversability(row, col, EaseAt(row, col)); // removes it from the set
            }
            return forgetting.Count;
        }

        // ---- Floor-ease memory -----------------------------------------------

        /// <summary>How easily the robot believes this floor cell drives: 1 unless something told it otherwise.</summary>
        public float EaseAt(int row, int col) => ease.TryGetValue(CellIndex.Pack(row, col), out float value) ? value : 1f;

        public void SetEase(int row, int col, float value)
        {
            long key = CellIndex.Pack(row, col);
            if (value == 1f) ease.Remove(key);
            else ease[key] = value;
        }

        // ---- Change feed ------------------------------------------------------

        void Record(int row, int col)
        {
            ChangeCounter++;
            if (overflowed) return;

            if (!pendingKeys.Add(CellIndex.Pack(row, col))) return; // already waiting to be read

            pending.Add(new CellIndex(row, col));
            if (pending.Count < MaxPendingChanges) return;

            overflowed = true;
            pending.Clear();
            pendingKeys.Clear();
        }

        /// <summary>
        /// Moves every cell changed since the last call into <paramref name="into"/> (which is cleared first).
        /// Returns false if too much changed to list: the reader should then rescan the whole map.
        /// </summary>
        public bool TakeChanges(List<CellIndex> into)
        {
            into.Clear();
            bool complete = !overflowed;

            into.AddRange(pending);
            pending.Clear();
            pendingKeys.Clear();
            overflowed = false;
            return complete;
        }

        /// <summary>Forgets every unread change (used right after the Normal preload, so the first read isn't the whole map).</summary>
        public void DiscardPendingChanges()
        {
            pending.Clear();
            pendingKeys.Clear();
            overflowed = false;
        }
    }
}
