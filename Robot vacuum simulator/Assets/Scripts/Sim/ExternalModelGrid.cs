using System.Collections.Generic;
using UnityEngine;
using RobotVacuum.Level;
using RobotVacuum.Sim;

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
// Purpose: the grid-based structure tracking, per cell, the TRUE dwell time
// and cleanliness for the External Model (per SDD 4.3). Write-only from the
// Sim Machine's point of view; never read by the robot.
// ============================================================================

namespace RobotVacuumSim.EnvironmentModel
{
    // ------------------------------------------------------------------
    // CellState: dwell time + cleanliness (both unbounded, increase-only),
    // plus the non-cleanable mask and throttle bookkeeping.
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
        // The two permanent historical records for this cell. Both are
        // ABSOLUTE and UNBOUNDED, and in practice only ever INCREASE:
        //
        //   dwellSeconds -- the pure time the robot has spent over this cell.
        //                   No vacuum power, no floor resistance: just seconds.
        //   cleanliness  -- cleaning actually accomplished, accumulated as
        //                   (vacuumPower / floorResistance) * seconds. Not
        //                   capped at the target: going past it is exactly
        //                   what the OverCoverage view measures.
        //
        // Both start at 0 (nothing has happened here yet). There is no
        // "fully clean" state any more; the goalpost (target_max_clean) is
        // just a number other code compares against. Private fields with
        // read-only properties, so Accumulate() below is the only way in.
        private float dwellSeconds;
        private float cleanliness;

        public float DwellSeconds => dwellSeconds;
        public float Cleanliness => cleanliness;

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

        // The only way to change the cell. No guard against negative amounts:
        // the only writer (VacuumCleaningController) adds positive amounts, and
        // a check on every cell on every tick is cost for nothing. Readers
        // (coverage, heatmaps) tolerate a value <= 0 by treating it as empty.
        public void Accumulate(float dwellDeltaSeconds, float cleanlinessDelta)
        {
            dwellSeconds += dwellDeltaSeconds;
            cleanliness += cleanlinessDelta;
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
        // Kept private, same reasoning as the dwell/cleanliness fields above: nothing
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
        // Telemetry note (the log itself is deferred until after the
        // prototype): what gets logged should be the cell's ABSOLUTE values
        // (dwell and cleanliness), never a delta -- reconstruction is then
        // "find the latest logged entry at or before T", a direct read.
        // Both values are now unclamped and increase-only, so the old
        // clamp-divergence worry no longer applies, but absolute values
        // stay the simplest thing to reconstruct from.
        // ------------------------------------------------------------------
        public bool ShouldLogEvent(float currentSimTime, float minIntervalSeconds)
        {
            // First-ever change to this cell always logs, regardless of
            // interval -- otherwise a cell's very first change
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

        // target_max_clean: the cleanliness goalpost. Not a property of any
        // cell -- it is the yardstick coverage and the heatmaps measure
        // cells against. Set once at construction from the run's config and
        // fixed for the whole run (it is NOT changeable mid-run, per Eric).
        private readonly float targetMaxClean;

        // The shared LATTICE both models sit on. Its anchor is a world point
        // (the robot's spawn) and cell edges fall at anchor + k * cellSize.
        // This grid's cell (0,0) sits at lattice index (rowOffset, colOffset),
        // so lattice index = local index + offset, and a cell here and a cell
        // in InternalModelGrid with the same lattice index are the same
        // physical square.
        private readonly Vector2 latticeAnchor;
        private readonly int rowOffset;
        private readonly int colOffset;

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
        // values just grow as the robot dwells -- the RIGHT rate for that depends on how fast a full clean pass
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
        //
        // latticeAnchorWorld: the world point the shared lattice passes
        // through (the robot's spawn in normal use). When given, this grid's
        // corner is snapped DOWN onto that lattice, which moves it by less
        // than one cell. When null (quick tests), the lattice is anchored at
        // the grid's own corner, as before.
        // ------------------------------------------------------------------
        public ExternalModelGrid(LevelData level, LevelRenderer levelRenderer, float cellSizeMeters,
            float targetMaxClean = HeatmapDefaults.TargetMaxClean, Vector2? latticeAnchorWorld = null)
        {
            this.level = level;
            this.levelRenderer = levelRenderer;
            this.cellSizeMeters = cellSizeMeters;
            this.targetMaxClean = Mathf.Max(0f, targetMaxClean);

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

            if (latticeAnchorWorld.HasValue)
            {
                // Snap the corner down onto the lattice (FloorToInt, not an
                // int cast, so negative offsets round the right way).
                latticeAnchor = latticeAnchorWorld.Value;
                colOffset = Mathf.FloorToInt((worldMin.x - latticeAnchor.x) / cellSizeMeters);
                rowOffset = Mathf.FloorToInt((worldMin.y - latticeAnchor.y) / cellSizeMeters);
                this.originWorld = latticeAnchor + new Vector2(colOffset * cellSizeMeters, rowOffset * cellSizeMeters);
            }
            else
            {
                latticeAnchor = worldMin;
                this.originWorld = worldMin;
            }

            // Size from the (possibly snapped) corner to the far corner.
            // Mathf.CeilToInt rounds up: if the floor plan doesn't divide
            // evenly by cell size, we'd rather have one extra partial row/col
            // than silently clip off real floor area.
            this.cols = Mathf.CeilToInt((worldMax.x - originWorld.x) / cellSizeMeters);
            this.rows = Mathf.CeilToInt((worldMax.y - originWorld.y) / cellSizeMeters);

            cells = new CellState[rows, cols];

            // C# zero-initializes the array, which is exactly the starting
            // state we want: no dwell, no cleanliness, not excluded. Nothing
            // to set per cell.
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
        // The write path. Adds time and cleanliness to one cell: the robot
        // calls this for every cell under its footprint each tick. Takes
        // currentSimTime because the throttle decision (inside
        // CellState.ShouldLogEvent) needs it.
        // ------------------------------------------------------------------
        public void Accumulate(int row, int col, float dwellDeltaSeconds, float cleanlinessDelta, float currentSimTime)
        {
            // Part 1: update the LIVE values. Always happens, every call,
            // no throttling -- coverage must stay authoritative at full tick
            // resolution regardless of what the replay log does.
            CellState cell = cells[row, col];
            cell.Accumulate(dwellDeltaSeconds, cleanlinessDelta);

            // Part 2: ask the cell whether enough time has passed to log.
            bool shouldLog = cell.ShouldLogEvent(currentSimTime, EventLogIntervalSeconds);

            // Write the copy back AFTER both mutations (cells[row, col]
            // returns a COPY of a struct, so mutating it does nothing until
            // it is stored back).
            cells[row, col] = cell;

            if (shouldLog)
                EmitCellStateChangeEvent(row, col, cell.DwellSeconds, cell.Cleanliness, currentSimTime);
        }

        // ------------------------------------------------------------------
        // STUB: writing a {cellID, time, dwell, cleanliness} event out to
        // the run's telemetry. Deferred until after the first prototype.
        // ------------------------------------------------------------------
        private void EmitCellStateChangeEvent(int row, int col, float dwellSeconds, float cleanliness, float simTime)
        {
            // TODO (post-prototype): append the absolute values to the run's cell-state event log.
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

        // target_max_clean, in cleanliness units. Fixed for the run.
        public float TargetMaxClean => targetMaxClean;

        // The shared lattice (see the field comments above).
        public Vector2 LatticeAnchor => latticeAnchor;
        public int RowOffset => rowOffset;
        public int ColOffset => colOffset;

        // ------------------------------------------------------------------
        // Coverage percentage, respecting the non-cleanable mask.
        //
        // Graded average (matches 3.14/3.15): each cleanable cell
        // contributes min(cleanliness / target, 1) -- half the target counts
        // as half clean, anything at or past the target counts as fully
        // clean and no more, so overshooting one cell cannot make up for
        // missing another. Non-cleanable cells (under blocking furniture)
        // are excluded from numerator and denominator.
        //
        // INFERENCE, not confirmed: this exact formula is Claude's reading
        // of "coverage against the target". CalculateFullyCleanedPercent()
        // below is the strict alternative (share of cells at the target).
        // ------------------------------------------------------------------
        public float CalculateCoveragePercent()
        {
            int cleanableCellCount = 0;
            float summed = 0f;

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    if (cells[row, col].isNonCleanable) continue;
                    cleanableCellCount++;
                    summed += ExternalModelData.CleanFraction(cells[row, col].Cleanliness, targetMaxClean);
                }
            }

            return cleanableCellCount == 0 ? 0f : summed / cleanableCellCount * 100f;
        }

        // The share of cleanable cells that have reached the target, 0 to 100.
        public float CalculateFullyCleanedPercent()
        {
            int cleanableCellCount = 0;
            int reached = 0;

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    if (cells[row, col].isNonCleanable) continue;
                    cleanableCellCount++;
                    if (cells[row, col].Cleanliness >= targetMaxClean) reached++;
                }
            }

            return cleanableCellCount == 0 ? 0f : reached / (float)cleanableCellCount * 100f;
        }

        // ------------------------------------------------------------------
        // Copies the grid into a plain saveable object (see
        // ExternalModelData). Also records which cells lie outside every
        // room, so a picture drawn from the data alone can leave them clear.
        // The room test runs once per cell, so call this at save time, not
        // every frame.
        // ------------------------------------------------------------------
        public ExternalModelData ToData()
        {
            var data = new ExternalModelData
            {
                rows = rows,
                cols = cols,
                cellSizeMeters = cellSizeMeters,
                originX = originWorld.x,
                originY = originWorld.y,
                targetMaxClean = targetMaxClean,
                dwellSeconds = new float[rows * cols],
                cleanliness = new float[rows * cols],
                nonCleanable = new bool[rows * cols],
                outsideRooms = new bool[rows * cols],
            };

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    int index = data.Index(row, col);
                    CellState cell = cells[row, col];
                    data.dwellSeconds[index] = cell.DwellSeconds;
                    data.cleanliness[index] = cell.Cleanliness;
                    data.nonCleanable[index] = cell.isNonCleanable;

                    if (level != null)
                    {
                        Vector2 world = GridIndexToWorldCenter(row, col);
                        Vector2 point = levelRenderer != null ? levelRenderer.WorldToLevel(world) : world;
                        data.outsideRooms[index] = level.RoomIndexAt(point) < 0;
                    }
                }
            }

            return data;
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
