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

        readonly HashSet<long> permanent = new HashSet<long>();
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

        public BeliefCell Get(int row, int col) => Grid.GetCell(row, col);

        public float Traversability(int row, int col) => Grid.GetCell(row, col).traversabilityBelief;

        // ---- Writes ----------------------------------------------------------

        public void SetTraversability(int row, int col, float value)
        {
            var cell = Grid.GetCell(row, col);
            float old = cell.traversabilityBelief;
            if (old == value) return; // a cell already equal to the observation is not rewritten

            bool wasWall = IsWall(row, col, old);
            cell.traversabilityBelief = value;
            if (!Grid.SetCell(row, col, cell)) return; // beyond the runaway guard

            if (wasWall != IsWall(row, col, value)) LogWallChange(row, col);
            if ((old <= 0f) != (value <= 0f))
            {
                TopologyVersion++;
                NoteFlip(row, col);
            }
            if (Mathf.Abs(value - old) >= ReportThreshold || (old <= 0f) != (value <= 0f)) Record(row, col);
            if (value <= 0f) Unsettle(row, col); // anything that blocks a cell ends its "settled floor" status
        }

        // ---- Flicker ---------------------------------------------------------
        //
        // A thin obstacle the lidar only half sees (a chair leg between beams) flips its cells between blocked and
        // clear many times while in view. To the boxed-in check those cells look like "floor reached earlier that is
        // blocked now", which is how it recognizes something that moved in, such as the cat (a static chair once cost
        // a run 220 s of waiting). So each flip is noted here: only on the flip itself (already the rare branch of a
        // write), sparsely, and nothing is ever changed on the map.

        struct FlipRecord
        {
            public float last;        // sim time of the last flip
            public int streak;        // flips in a row, each within FlickerGapSeconds of the one before
            public bool flickered;    // the streak has reached FlickerFlips at some point...
            public float lastFlicker; // ...most recently at this sim time
        }

        readonly Dictionary<long, FlipRecord> flipRecords = new Dictionary<long, FlipRecord>();
        int flickerCells;

        /// <summary>Sim time of the writes, set by the controller each step (for telling flicker apart).</summary>
        public float Clock { get; set; }

        /// <summary>Cells that have flickered at some point this run.</summary>
        public int FlickerCellCount => flickerCells;

        void NoteFlip(int row, int col)
        {
            long key = CellIndex.Pack(row, col);
            bool known = flipRecords.TryGetValue(key, out var record);
            if (known && Clock == record.last) return; // several writes in one step count once
            record.streak = known && Clock - record.last <= ControllerConstants.FlickerGapSeconds ? record.streak + 1 : 1;
            record.last = Clock;
            if (record.streak >= ControllerConstants.FlickerFlips)
            {
                if (!record.flickered) flickerCells++;
                record.flickered = true;
                record.lastFlicker = Clock;
            }
            flipRecords[key] = record;
        }

        /// <summary>
        /// True when the cell has kept flipping between blocked and clear (<see cref="ControllerConstants.FlickerFlips"/>
        /// flips, each within <see cref="ControllerConstants.FlickerGapSeconds"/> of the last) within the last
        /// <see cref="ControllerConstants.FlickerMemorySeconds"/>: a thin static obstacle, not something that moved in.
        /// </summary>
        public bool Flickers(int row, int col) =>
            flickerCells > 0 && flipRecords.TryGetValue(CellIndex.Pack(row, col), out var record) && record.flickered &&
            Clock - record.lastFlicker <= ControllerConstants.FlickerMemorySeconds;

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

        // ---- Settled floor (Normal mode only) ------------------------------------

        /// <summary>A cell's settled state (Normal preload only; every cell is <see cref="NotSettled"/> in SLAM).</summary>
        public const byte NotSettled = 0, SettledFloor = 1, SettledFace = 2, PreloadBlocked = 3;

        // Dense over the preload's window, so the lidar can test a cell without hashing.
        byte[] settled = System.Array.Empty<byte>();
        int settledMinRow, settledMinCol, settledRows, settledCols;

        /// <summary>
        /// Starts the settled-floor map over the preload's window (Normal only). A SETTLED cell is floor the preload
        /// wrote and nothing has blocked since. Its value can only be what the preload wrote, so the lidar's clear
        /// views skip it (no lookups, no write): the same result, far cheaper. A SETTLED FACE is settled floor beside
        /// a cell the preload blocked (wall, closed doorway, furniture): the cell holding that obstacle's surface.
        /// A lidar hit or bump there is the known obstacle, and is not written; otherwise rays ending on the surface
        /// and rays passing through it toggle the cell between blocked and passable every scan (each toggle forces
        /// the planner to rebuild its clearance field).
        ///
        /// Any write that blocks a settled cell (a cat, furniture added mid-run) unsettles it for good: from then on
        /// it is an ordinary cell, so later rays can clear it again. Cells the preload blocked are never settled, so
        /// rays still clear them if what blocked them is gone (furniture removed mid-run).
        /// </summary>
        public void BeginSettledFloor(int minRow, int minCol, int maxRow, int maxCol)
        {
            settledMinRow = minRow;
            settledMinCol = minCol;
            settledRows = Mathf.Max(0, maxRow - minRow + 1);
            settledCols = Mathf.Max(0, maxCol - minCol + 1);
            settled = new byte[settledRows * settledCols];
        }

        /// <summary>Preload only: records what the preload wrote into a cell (<see cref="SettledFloor"/> or <see cref="PreloadBlocked"/>).</summary>
        public void MarkPreloaded(int row, int col, byte state)
        {
            int index = SettledIndex(row, col);
            if (index >= 0) settled[index] = state;
        }

        /// <summary>Preload only, after every cell is marked: settled floor beside a preload-blocked cell becomes a settled face.</summary>
        public void FinishSettledFloor()
        {
            for (int r = 0; r < settledRows; r++)
            {
                for (int c = 0; c < settledCols; c++)
                {
                    int index = r * settledCols + c;
                    if (settled[index] != SettledFloor) continue;
                    if (BesidePreloadBlocked(r, c)) settled[index] = SettledFace;
                }
            }
        }

        bool BesidePreloadBlocked(int r, int c)
        {
            for (int dr = -1; dr <= 1; dr++)
            {
                int nr = r + dr;
                if (nr < 0 || nr >= settledRows) continue;
                for (int dc = -1; dc <= 1; dc++)
                {
                    int nc = c + dc;
                    if ((dr == 0 && dc == 0) || nc < 0 || nc >= settledCols) continue;
                    if (settled[nr * settledCols + nc] == PreloadBlocked) return true;
                }
            }
            return false;
        }

        /// <summary>The cell's settled state; <see cref="NotSettled"/> outside the preload's window and in SLAM runs.</summary>
        public byte SettledState(int row, int col)
        {
            int index = SettledIndex(row, col);
            return index >= 0 ? settled[index] : NotSettled;
        }

        int SettledIndex(int row, int col)
        {
            int r = row - settledMinRow, c = col - settledMinCol;
            if (r < 0 || c < 0 || r >= settledRows || c >= settledCols) return -1;
            return r * settledCols + c;
        }

        void Unsettle(int row, int col)
        {
            int index = SettledIndex(row, col);
            if (index >= 0 && (settled[index] == SettledFloor || settled[index] == SettledFace)) settled[index] = NotSettled;
        }

        // ---- Floor-ease memory -----------------------------------------------

        /// <summary>How easily the robot believes this floor cell drives: 1 unless something told it otherwise.</summary>
        public float EaseAt(int row, int col)
        {
            int r = row - easeMinRow, c = col - easeMinCol;
            return r >= 0 && c >= 0 && r < easeRows && c < easeCols ? easeMap[r * easeCols + c] : 1f;
        }

        // Dense, 1 everywhere not told otherwise (it was a hash map of the cells whose ease isn't 1; EaseAt is asked on
        // nearly every map write, and the hash lookup was a large share of mapping's cost). Grows on a write that isn't 1.
        float[] easeMap = System.Array.Empty<float>();
        int easeMinRow, easeMinCol, easeRows, easeCols;
        const int EasePad = 32;

        void GrowEase(int row, int col)
        {
            int minRow = easeRows == 0 ? row - EasePad : Mathf.Min(easeMinRow, row - EasePad);
            int minCol = easeCols == 0 ? col - EasePad : Mathf.Min(easeMinCol, col - EasePad);
            int maxRow = easeRows == 0 ? row + EasePad : Mathf.Max(easeMinRow + easeRows - 1, row + EasePad);
            int maxCol = easeCols == 0 ? col + EasePad : Mathf.Max(easeMinCol + easeCols - 1, col + EasePad);
            int rows = maxRow - minRow + 1, cols = maxCol - minCol + 1;
            var grown = new float[rows * cols];
            for (int i = 0; i < grown.Length; i++) grown[i] = 1f;
            for (int r = 0; r < easeRows; r++)
                System.Array.Copy(easeMap, r * easeCols, grown, (easeMinRow + r - minRow) * cols + (easeMinCol - minCol), easeCols);
            easeMap = grown;
            easeMinRow = minRow;
            easeMinCol = minCol;
            easeRows = rows;
            easeCols = cols;
        }

        public void SetEase(int row, int col, float value)
        {
            // Ease decides whether a shaded cell counts as wall (IsWall), so a change can flip it.
            float traversability = Grid.GetCell(row, col).traversabilityBelief;
            bool shaded = traversability > 0f && traversability < 1f;
            bool wasWall = shaded && IsWall(row, col, traversability);

            int r = row - easeMinRow, c = col - easeMinCol;
            if (r < 0 || c < 0 || r >= easeRows || c >= easeCols)
            {
                if (value == 1f) return; // already 1 there (and nothing can flip)
                GrowEase(row, col);
                r = row - easeMinRow;
                c = col - easeMinCol;
            }
            easeMap[r * easeCols + c] = value;

            if (shaded && wasWall != IsWall(row, col, traversability)) LogWallChange(row, col);
        }

        // ---- Wall-change log ----------------------------------------------------

        /// <summary>
        /// Exactly <see cref="InternalModelView.IsBlocked"/>'s rule: traversability 0, or shaded below
        /// <see cref="ControllerConstants.WallEaseFraction"/> of the cell's floor ease. (Kept identical on purpose: the
        /// planner's clearance field is updated from this log and must match a full rebuild from the view.)
        /// </summary>
        bool IsWall(int row, int col, float traversability) =>
            traversability <= 0f ||
            (traversability < 1f && traversability < ControllerConstants.WallEaseFraction * EaseAt(row, col));

        /// <summary>Most entries the wall-change log keeps; past this the older half is dropped (a reader that far behind rebuilds in full).</summary>
        public const int MaxWallLog = 1 << 16;

        readonly List<long> wallLog = new List<long>();
        int wallLogBase; // the WallVersion of wallLog[0]

        /// <summary>Goes up once for every cell that starts or stops counting as wall (by the view's IsBlocked rule).</summary>
        public int WallVersion { get; private set; }

        void LogWallChange(int row, int col)
        {
            WallVersion++;
            wallLog.Add(CellIndex.Pack(row, col));
            if (wallLog.Count < MaxWallLog) return;
            int drop = MaxWallLog / 2;
            wallLog.RemoveRange(0, drop);
            wallLogBase += drop;
        }

        /// <summary>
        /// Fills <paramref name="into"/> (cleared first) with every cell that started or stopped counting as wall since
        /// <paramref name="version"/> (a <see cref="WallVersion"/>), packed with <see cref="CellIndex.Pack"/>; a cell
        /// can appear more than once. False if the log no longer reaches back that far.
        /// </summary>
        public bool WallChangesSince(int version, List<long> into)
        {
            into.Clear();
            if (version < wallLogBase || version > WallVersion) return false;
            for (int i = version - wallLogBase; i < wallLog.Count; i++) into.Add(wallLog[i]);
            return true;
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
