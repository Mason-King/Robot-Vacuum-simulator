using System.Collections.Generic;
using UnityEngine;
using RobotVacuum.Level;

// ============================================================================
//
// STATUS: working script, wired to the real Level Editor types (LevelData,
// Room, LevelRenderer) from the bot-logic repo, Unity 6.3 LTS. No longer a
// standalone example -- this compiles against real project code.
//
// Remaining open items are still flagged inline:
//   // PENDING: ...   -> depends on a feature that doesn't exist yet elsewhere
//                        in the project (specifically: obstacle/furniture data)
//   // TODO: ...      -> known future work, deliberately left for later
//
// Purpose: the grid-based structure tracking floor cleanliness state for the
// External Model (per SDD 4.3). Write-only from the Sim Machine's point of
// view; never read by the robot.
// ============================================================================

namespace RobotVacuumSim.EnvironmentModel
{
    // ------------------------------------------------------------------
    // CellState: the two-field struct we agreed on.
    //
    // "struct" instead of "class" is deliberate here: structs are value
    // types (copied by value, stored inline in the array rather than as
    // separate heap objects with pointers). For a grid with potentially
    // hundreds of thousands of cells (fine resolution over a large
    // floor plan), storing cells inline in one contiguous array is
    // much more cache-friendly than an array of references to
    // scattered heap objects. This is a performance-motivated choice,
    // not just style.
    // ------------------------------------------------------------------
    public struct CellState
    {
        // The permanent historical record: how dirty this cell currently
        // is, on a continuous scale. Never overwritten by furniture moving.
        //
        // CONVENTION: 1.0 = fully dirty, 0.0 = fully clean. Chosen (per
        // Eric) so the value naturally starts at 1 (see DefaultDirtiness
        // below) and only ever decreases as cleaning happens -- there is
        // no "super clean" state past 0, and the value is HARD CLAMPED to
        // the [0, 1] range. The clamp lives in SetDirtiness() below, not
        // just as a comment/convention -- callers cannot bypass it because
        // this field is private and only reachable through that method.
        private float dirtiness;

        // Backing field is private; this property is the only way to read
        // the value from outside the struct. "get; " with no "set;" makes
        // it read-only from the outside -- mutation only happens through
        // SetDirtiness(), which is where the clamp is enforced.
        public float Dirtiness => dirtiness;

        // The value a freshly-created cell starts at: fully dirty.
        public const float DefaultDirtiness = 1f;

        // The temporary visibility mask: does this cell currently count
        // toward the coverage-percent denominator? True while a blocking
        // obstruction sits on top of it. Recomputed on furniture
        // placement/removal events -- see RecomputeNonCleanableForRegion()
        // below -- it is NOT a permanent property of the cell.
        //
        // Set by ExternalModelGrid.PopulateFromLevelObject() for cells under
        // furniture that blocks the vacuum (LevelData.Obstacles). Furniture
        // the vacuum passes under leaves its cells cleanable.
        public bool isNonCleanable;

        // The only way to change dirtiness. Mathf.Clamp01 forces the
        // result into [0, 1] no matter what value is passed in -- this is
        // the single enforcement point Eric asked for, so no calling code
        // anywhere else needs to remember to clamp.
        public void SetDirtiness(float newValue)
        {
            dirtiness = Mathf.Clamp01(newValue);
        }

        // ------------------------------------------------------------------
        // THROTTLE BOOKKEEPING -- lives here, not as a separate parallel
        // array on the grid. Rationale: a second array indexed the exact
        // same way as "cells" is two things that must always stay in sync
        // by convention -- resize one and forget the other, or index them
        // inconsistently somewhere, and you get a silent bug. Folding this
        // into CellState means there is only ONE array on the grid, so
        // there's nothing to get out of sync in the first place.
        //
        // Kept private, same reasoning as "dirtiness" above: nothing
        // outside this struct should read or write it directly except
        // through the method below, which is the only place the throttle
        // decision actually gets made.
        // ------------------------------------------------------------------
        private float lastLoggedTime;

        // True the first time this is called for a given cell (relies on
        // C#'s default float value of 0f combined with the check below).
        private bool hasLoggedBefore;

        // ------------------------------------------------------------------
        // Encapsulates the entire throttle decision. Given the current
        // simulated time and the minimum interval between logged events,
        // returns whether THIS call should result in a logged event -- and
        // if so, updates its own bookkeeping so the next call knows when
        // "last logged" was. Callers never touch lastLoggedTime directly;
        // they just ask "should I log right now?" and get a yes/no.
        //
        // CONFIRMED (per Eric): what gets timestamped/logged is always the
        // resulting ABSOLUTE dirtiness value, never a delta/decrement.
        // Reconstruction works by "find the latest logged entry at or
        // before T" -- with absolute values that's a direct read. With
        // decrements, reconstruction would have to sum every decrement
        // since the cell's start, and that sum could silently diverge from
        // truth because the live value is continuously clamped -- a
        // decrement-sum has no way to know when a clamp absorbed part of a
        // change between two logged samples. Absolute-value logging avoids
        // that failure mode entirely. See SetDirtiness(row, col, ...) below
        // for where the logged value is actually read off the cell.
        // ------------------------------------------------------------------
        public bool ShouldLogEvent(float currentSimTime, float minIntervalSeconds)
        {
            // First-ever change to this cell always logs, regardless of
            // interval -- otherwise a cell's very first dirtiness change
            // could be silently dropped if it happens at simulated time
            // 0.0 (which would look identical to "already logged at time
            // 0" without this explicit flag).
            if (!hasLoggedBefore)
            {
                hasLoggedBefore = true;
                lastLoggedTime = currentSimTime;
                return true;
            }

            float elapsed = currentSimTime - lastLoggedTime;
            if (elapsed >= minIntervalSeconds)
            {
                lastLoggedTime = currentSimTime;
                return true;
            }

            return false;
        }
    }

    // ------------------------------------------------------------------
    // The grid container itself.
    // ------------------------------------------------------------------
    public class ExternalModelGrid
    {
        // --- Configuration, set once at construction, fixed for the run ---

        // Size of one cell's edge, in meters. This is what "grid
        // resolution" means concretely. Pre-run configurable per SDD 4.6;
        // once this grid object is built, it does not change for the
        // life of the run.
        //
        // Still open, not yet enforced here: minimum/maximum resolution
        // bounds. Pending Eric's conversation with the Level Editor team.
        private readonly float cellSizeMeters;

        // Grid dimensions, computed from the Level Object's bounding box
        // divided by cell size -- not hardcoded, so different resolutions
        // or floor plan sizes just produce a different rows/cols without
        // any code change.
        private readonly int rows;
        private readonly int cols;

        // World-space position (meters, X-Y plane, Z==0) that grid cell
        // (0,0)'s corner corresponds to.
        //
        // CONFIRMED: anchored to the Level Object's bounding-box minimum
        // corner, converted from level-local space to world space ONCE at
        // construction (see the constructor below) -- not recomputed
        // per-tick. The robot's live position (what actually marks cells
        // clean, every tick) comes straight from Rigidbody2D/Physics2D,
        // which is always world space unconditionally -- so the grid
        // living in world space means no per-tick conversion is ever
        // needed on the hot path. Converting the much-smaller, one-time
        // bounding box instead is strictly cheaper.
        private readonly Vector2 originWorld;

        // The actual cell data. A 2D array indexed [row, col].
        // C#'s [,] syntax is a true 2D array (one contiguous memory
        // block) as opposed to [][] (a "jagged" array of arrays) --
        // contiguous is what we want here for the same cache-locality
        // reason CellState is a struct above.
        private CellState[,] cells;

        // How many cells are currently non-cleanable, kept in step by
        // SetNonCleanable() so area read-outs don't rescan the grid.
        private int nonCleanableCount;

        // Stored so future population/recompute work (see the stubs below)
        // has the same LevelData/LevelRenderer pair available without
        // re-threading them through every method call. LevelRenderer
        // specifically is what performs the level-local -> world-space
        // conversion (LevelToWorld), confirmed as a plain, unflipped
        // transform by reading the actual source.
        private readonly LevelData level;
        private readonly LevelRenderer levelRenderer;

        // ------------------------------------------------------------------
        // PROVISIONAL VALUE -- deliberately conservative, NOT tuned.
        //
        // Originally borrowed the 10Hz value used for entity-trajectory
        // logging (SDD 7.2), but that reasoning doesn't actually transfer:
        // entity position changes continuously through space and needs a
        // high sample rate to avoid visibly "teleporting" in replay. Cell
        // dirtiness just fades from 1 toward 0 as the robot dwells -- the
        // RIGHT rate for that depends on how fast a full clean pass
        // actually takes, which depends on the 3.14 cleaning-effectiveness
        // formula. That formula doesn't exist in concrete form yet.
        //
        // Until it does, this is set conservatively low (1 event/second)
        // to avoid over-logging in the meantime. REVISIT once 3.14's
        // formula is real -- the right value might end up higher OR lower
        // than this, this is a placeholder, not a calculated answer.
        // ------------------------------------------------------------------
        private const float EventLogIntervalSeconds = 1f;

        // ------------------------------------------------------------------
        // Constructor. Takes the real Level Object (LevelData) and the
        // LevelRenderer that positions it in the Simulation Scene, so the
        // grid can size and place itself directly from real project data
        // instead of a hand-built placeholder.
        //
        // levelRenderer may be null (e.g. quick testing without a full
        // scene) -- in that case the level's bounds are used as-is,
        // assuming identity transform, with a loud warning rather than a
        // silent wrong answer. In normal use within the real scene, always
        // pass the actual LevelRenderer so the conversion is correct even
        // if that transform is ever offset or rotated.
        // ------------------------------------------------------------------
        public ExternalModelGrid(LevelData level, LevelRenderer levelRenderer, float cellSizeMeters)
        {
            this.level = level;
            this.levelRenderer = levelRenderer;
            this.cellSizeMeters = cellSizeMeters;

            // LevelData.Bounds() is computed purely from Room.outline
            // points, which live in LEVEL-LOCAL space (confirmed by
            // reading LevelData.cs / Room's doc comment) -- NOT
            // automatically world space. If the LevelRenderer's transform
            // is ever anything other than identity (repositioned,
            // rotated), using this rect directly as world coordinates
            // would be wrong. Converting both corners through
            // LevelToWorld here, once, is the one-time cost we chose to
            // pay so nothing downstream (TryWorldToGridIndex, called every
            // tick) ever has to think about level-local space at all.
            Rect localBounds = level.Bounds();

            Vector2 worldCornerA;
            Vector2 worldCornerB;

            if (levelRenderer != null)
            {
                // LevelToWorld returns a Vector3; Unity provides an
                // implicit Vector3 -> Vector2 conversion that just drops Z,
                // which is exactly right here since Z is confirmed
                // cosmetic-only (draw-order depth, never meaningful
                // position) in LevelRenderer's own code.
                worldCornerA = levelRenderer.LevelToWorld(localBounds.min);
                worldCornerB = levelRenderer.LevelToWorld(localBounds.max);
            }
            else
            {
                Debug.LogWarning(
                    "ExternalModelGrid: no LevelRenderer provided -- assuming " +
                    "the Level Object sits at identity transform in world space. " +
                    "This is only safe for quick/offline testing; pass the real " +
                    "LevelRenderer in normal use.");
                worldCornerA = localBounds.min;
                worldCornerB = localBounds.max;
            }

            // Guard against the (currently unexpected, but cheap to guard)
            // case of a rotated transform swapping which corner is
            // numerically smaller -- Vector2.Min/Max sorts it out either way.
            Vector2 worldMin = Vector2.Min(worldCornerA, worldCornerB);
            Vector2 worldMax = Vector2.Max(worldCornerA, worldCornerB);
            Vector2 worldSize = worldMax - worldMin;

            this.originWorld = worldMin;

            // Mathf.CeilToInt rounds up: if the floor plan doesn't divide
            // evenly by cell size, we'd rather have one extra partial row/col
            // than silently clip off real floor area.
            this.cols = Mathf.CeilToInt(worldSize.x / cellSizeMeters);
            this.rows = Mathf.CeilToInt(worldSize.y / cellSizeMeters);

            cells = new CellState[rows, cols];

            // Explicitly initialize every cell to the default-dirty,
            // non-blocked state. C# would actually zero-initialize this
            // automatically (default struct values), but we set it
            // explicitly here so the "default to Dirty" decision is
            // visible in the code, not an invisible side effect of
            // array allocation.
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    CellState newCell = new CellState { isNonCleanable = false };
                    newCell.SetDirtiness(CellState.DefaultDirtiness); // starts fully dirty (1.0)
                    cells[r, c] = newCell;
                }
            }
        }

        // ------------------------------------------------------------------
        // Coordinate mapping: world space -> grid index.
        //
        // CONFIRMED: plain X-Y floor plane, no axis flip. Verified directly
        // against LevelRenderer.WorldToLevel/LevelToWorld in the real
        // source (Assets/Scripts/Level/LevelRenderer.cs) -- both are a
        // straight InverseTransformPoint/TransformPoint with no inversion.
        // This is the single place that logic lives; nothing else in this
        // file references world coordinates directly.
        // ------------------------------------------------------------------
        public bool TryWorldToGridIndex(Vector2 worldPos, out int row, out int col)
        {
            Vector2 relative = worldPos - originWorld;

            col = Mathf.FloorToInt(relative.x / cellSizeMeters);
            row = Mathf.FloorToInt(relative.y / cellSizeMeters);

            bool inBounds = row >= 0 && row < rows && col >= 0 && col < cols;
            if (!inBounds)
            {
                row = -1;
                col = -1;
            }
            return inBounds;
        }

        // The inverse mapping: grid index -> world-space position of that
        // cell's center. Useful for rendering the grid/heatmap overlay.
        public Vector2 GridIndexToWorldCenter(int row, int col)
        {
            float worldX = originWorld.x + (col + 0.5f) * cellSizeMeters;
            float worldY = originWorld.y + (row + 0.5f) * cellSizeMeters;
            return new Vector2(worldX, worldY);
        }

        // ------------------------------------------------------------------
        // Basic accessors. Kept deliberately narrow -- callers can't reach
        // in and mutate the array directly, only through these methods.
        // ------------------------------------------------------------------
        public CellState GetCell(int row, int col)
        {
            return cells[row, col];
        }

        // ------------------------------------------------------------------
        // Takes currentSimTime because the throttle decision (inside
        // CellState.ShouldLogEvent) needs it. Whatever calls this
        // (presumably the Sim Machine, once it exists) must pass the
        // current simulated time, not just a row/col/value.
        // ------------------------------------------------------------------
        public void SetDirtiness(int row, int col, float newValue, float currentSimTime)
        {
            // --- Part 1: update the LIVE value. Always happens, every
            // call, no throttling. This is what CalculateCoveragePercent()
            // reads, and per SDD 7.3 that must stay authoritative at full
            // tick resolution regardless of what the replay log does. ---
            CellState cell = cells[row, col];
            cell.SetDirtiness(newValue);

            // --- Part 2: ask the cell itself whether enough time has
            // passed to log this change. All the bookkeeping (when did
            // this cell last log, has it ever logged before) lives inside
            // CellState -- this method just acts on the yes/no answer. ---
            bool shouldLog = cell.ShouldLogEvent(currentSimTime, EventLogIntervalSeconds);

            // Write the cell back to the array AFTER both mutations above --
            // remember cells[row, col] would hand back a stale COPY if read
            // again mid-method, so both SetDirtiness and ShouldLogEvent had
            // to run on this same local "cell" copy before it goes back in.
            cells[row, col] = cell;

            if (shouldLog)
            {
                // Logging the ABSOLUTE post-clamp value (cell.Dirtiness),
                // never a delta -- see the CONFIRMED note on
                // CellState.ShouldLogEvent above for why.
                EmitCellStateChangeEvent(row, col, cell.Dirtiness, currentSimTime);
            }
        }

        // ------------------------------------------------------------------
        // STUB: actually writing a {cellID, time, value} event out to
        // wherever the run's Action Log / telemetry storage lives (SDD 7.1).
        //
        // PENDING: Run Results Storage mechanism isn't built yet. For now
        // this just defines the shape of what needs to happen -- swap the
        // body for a real call once that storage exists.
        // ------------------------------------------------------------------
        private void EmitCellStateChangeEvent(int row, int col, float dirtinessValue, float simTime)
        {
            // TODO: replace with a real call once telemetry storage exists.
            // Expected shape: append { cellID: (row, col), time: simTime,
            // value: dirtinessValue } to this run's cell-state event log,
            // same {ID, time, value} pattern as the furniture entity log
            // (SDD 7.2), reconstructed later via "find latest entry at or
            // before T."
        }

        public void SetNonCleanable(int row, int col, bool isNonCleanable)
        {
            if (cells[row, col].isNonCleanable == isNonCleanable) return;

            cells[row, col].isNonCleanable = isNonCleanable;
            nonCleanableCount += isNonCleanable ? 1 : -1;
        }

        // Cells currently excluded from coverage (under blocking furniture).
        public int NonCleanableCellCount => nonCleanableCount;

        // Floor the robot can't clean because furniture blocks it, in square
        // metres: the non-cleanable cell count times one cell's area.
        public float NonCleanableAreaSquareMeters => nonCleanableCount * cellSizeMeters * cellSizeMeters;

        public int Rows => rows;
        public int Cols => cols;
        public float CellSizeMeters => cellSizeMeters;

        // Exposed read-only so callers that need to compute a bounding box
        // of cells around a world point (e.g. a circular cleaning
        // footprint) can do that math themselves, without this class
        // needing a bespoke "cells within radius" method of its own. The
        // grid stays a plain data structure; callers own their own
        // footprint/query logic.
        public Vector2 OriginWorld => originWorld;

        // ------------------------------------------------------------------
        // Coverage percentage, respecting the non-cleanable mask.
        //
        // This directly implements "dirty by default, not marked against
        // the total until uncovered": isNonCleanable cells are excluded
        // entirely, not counted as dirty.
        //
        // SUM/AVERAGE approach (per Eric, matches 3.14/3.15's graded
        // cleaning effectiveness): each cleanable cell contributes its
        // OWN partial cleanliness, not just a binary clean/not-clean vote.
        // A cell sitting at dirtiness 0.3 contributes 0.7 "clean units" to
        // the total, same as if 70% of that one cell had been fully
        // cleaned. Since dirtiness is 1=dirty/0=clean, "how clean" a cell
        // is is (1 - dirtiness); summing that across all cleanable cells
        // and dividing by the count gives the average fractional
        // cleanliness, which we then express as a percentage.
        //
        // NOTE: cells under blocking furniture are marked non-cleanable by
        // PopulateFromLevelObject(), so they drop out of this denominator.
        // ------------------------------------------------------------------
        public float CalculateCoveragePercent()
        {
            int cleanableCellCount = 0;
            float summedCleanliness = 0f;

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    if (cells[row, col].isNonCleanable)
                    {
                        continue; // excluded entirely, per 1.7.6
                    }
                    cleanableCellCount++;
                    summedCleanliness += (1f - cells[row, col].Dirtiness);
                }
            }

            if (cleanableCellCount == 0)
            {
                return 0f; // avoid divide-by-zero on a degenerate/empty floor plan
            }

            return summedCleanliness / cleanableCellCount * 100f;
        }

        // ------------------------------------------------------------------
        // Marks cells under furniture that blocks the vacuum as
        // non-cleanable, so they drop out of coverage instead of counting as
        // floor the robot failed to clean. Furniture the vacuum passes under
        // (Obstacle.blocksVacuum == false) leaves its cells cleanable.
        //
        // SCOPE NOTE (per Eric): the External Model exists to capture TRUE
        // data that can't already be read off the scene itself -- i.e.
        // cleanliness, which only exists because the robot has (or hasn't)
        // cleaned it. Floor covering type is static scene data, already
        // readable directly from LevelData.FloorTypeAt(...) whenever
        // something needs it -- it does NOT belong on CellState.
        //
        // Follows the plan that was noted here: each footprint is converted
        // to world space once through LevelToWorld, then only cells inside
        // its bounding box are tested with Poly2D.ContainsPoint, so the cost
        // scales with the furniture rather than the whole grid.
        // ------------------------------------------------------------------
        public void PopulateFromLevelObject()
        {
            if (level == null || rows == 0 || cols == 0) return;

            var footprint = new List<Vector2>(4);

            foreach (var obstacle in level.Obstacles)
            {
                if (obstacle == null || !obstacle.blocksVacuum) continue;

                footprint.Clear();
                foreach (var corner in obstacle.Corners())
                    footprint.Add(levelRenderer != null ? (Vector2)levelRenderer.LevelToWorld(corner) : corner);

                Rect bounds = Poly2D.Bounds(footprint);
                int minCol = Mathf.Clamp(Mathf.FloorToInt((bounds.xMin - originWorld.x) / cellSizeMeters), 0, cols - 1);
                int maxCol = Mathf.Clamp(Mathf.FloorToInt((bounds.xMax - originWorld.x) / cellSizeMeters), 0, cols - 1);
                int minRow = Mathf.Clamp(Mathf.FloorToInt((bounds.yMin - originWorld.y) / cellSizeMeters), 0, rows - 1);
                int maxRow = Mathf.Clamp(Mathf.FloorToInt((bounds.yMax - originWorld.y) / cellSizeMeters), 0, rows - 1);

                for (int row = minRow; row <= maxRow; row++)
                {
                    for (int col = minCol; col <= maxCol; col++)
                    {
                        Vector2 center = GridIndexToWorldCenter(row, col);
                        if (!Poly2D.ContainsPoint(footprint, center)) continue;

                        // Only floor counts: cells outside every room were never
                        // floor the robot could clean in the first place.
                        Vector2 levelPoint = levelRenderer != null ? levelRenderer.WorldToLevel(center) : center;
                        if (level.RoomIndexAt(levelPoint) < 0) continue;

                        SetNonCleanable(row, col, true);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // STUB, deliberately empty -- not throwing.
        //
        // Recompute the non-cleanable mask for a region after a furniture
        // placement/removal event (SDD 6.4). Same blocker as
        // PopulateFromLevelObject above: no furniture/obstacle data model
        // exists yet to trigger this from.
        //
        // TODO: called from wherever the furniture Place/Remove event
        // handler lives, once it exists. Should only touch cells within
        // the affected furniture footprint, not rescan the whole grid.
        // ------------------------------------------------------------------
        public void RecomputeNonCleanableForRegion(Vector2 regionWorldMin, Vector2 regionWorldSize)
        {
            // Intentionally empty -- see comment above.
        }
    }
}
