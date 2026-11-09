using RobotVacuum.Level;
using UnityEngine;
using RobotVacuumSim.EnvironmentModel;

namespace RobotVacuum.Sim
{
    // ============================================================================
    // VacuumCleaningController.cs
    //
    // Owns the ExternalModelGrid instance and marks cells clean as the robot's
    // footprint passes over them, every FixedUpdate. VacuumRobot.cs itself has
    // no cleaning logic at all right now (confirmed by reading its source --
    // it only drives and bumps off walls), so this stands in for that missing
    // piece tonight. Conceptually this is a rough stand-in for what the SDD
    // calls the "Sim Machine" -- the thing that actually WRITES to the
    // External Model each tick -- which doesn't exist as its own component yet.
    //
    // What it writes, per cell under the footprint, every FixedUpdate:
    //   dwellSeconds += dt                                   (pure time over the cell)
    //   cleanliness  += (vacuumPower / floorResistance) * dt (cleaning done)
    // where the floor's resistance is its cleaningEffort, so carpet cleans
    // slower than tile. Neither value is capped: dwell keeps accruing on cells
    // that are already clean, which is what the Coverage and OverCoverage
    // heatmaps measure. Cells under blocking furniture are skipped entirely.
    //
    // PLACEHOLDER, clearly flagged: the full cleaning-effectiveness formula
    // (SDD 3.14 -- dwell time x velocity x surface type) doesn't exist yet;
    // velocity does not enter this yet.
    // ============================================================================
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(CircleCollider2D))]
    public class VacuumCleaningController : MonoBehaviour
    {
        [Header("Level Object references")]
        [Tooltip("The floor plan this grid is built from.")]
        [SerializeField] LevelData level;

        [Tooltip("Positions the Level Object in the Simulation Scene. Left empty, this finds the first LevelRenderer in the scene, same pattern VacuumRobot.cs already uses.")]
        [SerializeField] LevelRenderer levelRenderer;

        [Header("Grid resolution")]
        [Tooltip("Grid cell edge length, in metres. Default (2 inches) lives in SimDefaults.cs; an object that already saved another value keeps it until reset here. Minimum/maximum bounds are still open -- pending the Level Editor team conversation -- so nothing here enforces a range yet.")]
        [SerializeField] float cellSizeMeters = SimDefaults.CellSizeMeters;

        [Header("Cleaning")]
        [Tooltip("Cleanliness gained per second of dwell on a floor with a cleaning effort of 1. Divided by each floor type's cleaningEffort.")]
        [SerializeField] float vacuumPower = HeatmapDefaults.BaseVacuumPower;

        [Tooltip("target_max_clean: the cleanliness a cell is aiming for. Coverage and the heatmaps measure against it. Set it before the run: it is read once when the grid is built and fixed for the run. The default lives in HeatmapDefaults.cs.")]
        [SerializeField] float targetMaxClean = HeatmapDefaults.TargetMaxClean;

        // Per-cell rate multiplier (1 / cleaningEffort), looked up the first
        // time the footprint touches a cell; 0 means not looked up yet. Floor
        // type is static scene data, so it stays out of CellState (see the
        // SCOPE NOTE in ExternalModelGrid).
        float[] cellRateMultiplier;

        Rigidbody2D robotBody;
        CircleCollider2D robotCollider;

        // The grid itself. Public so the heatmap renderer (or anything else
        // that wants to read coverage state) can get at it without this
        // controller needing to know anything about rendering.
        public ExternalModelGrid Grid { get; private set; }

        // A simple accumulated clock, separate from Time.time. Time.time
        // includes time before this object started and behaves oddly around
        // pausing; accumulating our own FixedUpdate-driven clock here is a
        // closer (if still simplified) stand-in for the "simulated time"
        // concept the SDD's Action Log/telemetry design actually wants --
        // and it's the value passed into ExternalModelGrid.Accumulate's
        // currentSimTime parameter, which drives the event-log throttle.
        float simTimeElapsed;

        // Live coverage, kept up to date by the grid itself so it is cheap every tick.
        public float CoveragePercent => Grid != null ? Grid.CoveragePercent : 0f;

        // Per cell: the step it was last under the footprint, 0 for never.
        // Lets the controller count revisits: the footprint coming back to a
        // cell it had already covered and left.
        int[] lastVisitStep;
        int stepIndex;

        /// <summary>Times the vacuum came back over floor it had already covered and left (cell entries).</summary>
        public long RevisitCount { get; private set; }

        /// <summary>Distinct cells the footprint has covered at least once.</summary>
        public int VisitedCellCount { get; private set; }

        public float CellSizeMeters => cellSizeMeters;

        /// <summary>Furniture moved, appeared or changed mid-run: re-mark the floor it blocks, keeping progress.</summary>
        public void RefreshObstacles()
        {
            if (Grid != null) Grid.RefreshNonCleanable();
        }

        /// <summary>
        /// Changes the grid resolution and starts again on a dirty floor. Coverage is a fraction of area, so
        /// runs at different resolutions stay comparable; finer cells only trace the edges more exactly.
        /// </summary>
        public void SetCellSize(float metres)
        {
            cellSizeMeters = Mathf.Clamp(metres, 0.005f, 0.5f);
            ResetCoverage();
        }

        // target_max_clean for this run. Read-only here: it is set before the
        // run (Inspector field, or Initialize from the run config) and fixed
        // once the grid is built.
        public float TargetMaxClean => Grid != null ? Grid.TargetMaxClean : targetMaxClean;

        // Applies the run config's target and rebuilds the (still empty) grid
        // so it is measured against that value from the start. Call ONCE,
        // right after the component is added and before the run begins; a
        // value of zero or less keeps the Inspector's value.
        public void Initialize(float targetMaxClean) => Initialize(targetMaxClean, 0f, 0f);

        // Same, plus the grid's cell size and the vacuum power, for runs built
        // from code (SimulationRunner). Each value of zero or less keeps the
        // current one. Setting them here, before the run starts, is what keeps
        // a scene-saved Inspector value from silently overriding the run's
        // settings.
        public void Initialize(float targetMaxClean, float cellSizeMeters, float vacuumPower)
        {
            if (targetMaxClean > 0f) this.targetMaxClean = targetMaxClean;
            if (cellSizeMeters > 0f) this.cellSizeMeters = cellSizeMeters;
            if (vacuumPower > 0f) this.vacuumPower = vacuumPower;
            if (level != null) BuildGrid();
        }

        // Cleanliness per second on a floor with an effort of 1.
        public float VacuumPower
        {
            get => vacuumPower;
            set => vacuumPower = Mathf.Max(0f, value);
        }

        void OnValidate()
        {
            vacuumPower = Mathf.Max(0f, vacuumPower);
            targetMaxClean = Mathf.Max(0f, targetMaxClean);
        }

        void Awake()
        {
            robotBody = GetComponent<Rigidbody2D>();
            robotCollider = GetComponent<CircleCollider2D>();

            if (levelRenderer == null)
                levelRenderer = FindAnyObjectByType<LevelRenderer>(FindObjectsInactive.Include);

            if (level == null && levelRenderer != null)
                level = levelRenderer.Level;

            if (level == null)
            {
                Debug.LogError("VacuumCleaningController: no LevelData assigned or found -- cannot build the grid.");
                return;
            }

            BuildGrid();
        }

        // ------------------------------------------------------------------
        // Throws away all coverage and starts from a fully dirty floor, e.g.
        // when a run is restarted.
        // ------------------------------------------------------------------
        public void ResetCoverage()
        {
            if (level == null) return;

            simTimeElapsed = 0f;
            BuildGrid();
        }

        void BuildGrid()
        {
            // The shared lattice is anchored at the robot's spawn, in world space.
            Vector2 spawnLevel = level.RobotSpawn;
            Vector2 spawnWorld = levelRenderer != null ? (Vector2)levelRenderer.LevelToWorld(spawnLevel) : spawnLevel;

            Grid = new ExternalModelGrid(level, levelRenderer, cellSizeMeters, targetMaxClean, spawnWorld);

            // Marks cells under blocking furniture as non-cleanable (see ExternalModelGrid.cs).
            Grid.PopulateFromLevelObject();
            cellRateMultiplier = new float[Grid.Rows * Grid.Cols];
            lastVisitStep = new int[Grid.Rows * Grid.Cols];
            stepIndex = 0;
            RevisitCount = 0;
            VisitedCellCount = 0;
        }

        void FixedUpdate() => Step(Time.fixedDeltaTime);

        public void Step(float dt)
        {
            if (Grid == null || robotBody == null) return;

            simTimeElapsed += dt;
            stepIndex++;
            CleanCellsUnderFootprint(dt);
        }

        // ------------------------------------------------------------------
        // Finds every cell whose center falls within the robot's circular
        // footprint (robotCollider.radius, the SAME collider VacuumRobot.cs
        // already sizes and uses for collision) and adds this tick's dwell time and cleanliness to it.
        //
        // Bounding-box-then-distance-check, not a scan of the whole grid:
        // first narrow to the small rectangle of cells that could possibly
        // be within radius (using OriginWorld + CellSizeMeters), then only
        // distance-check cells inside that rectangle. Cost scales with the
        // robot's footprint size, not total grid size -- matches the
        // "per-tick cost scales with footprint-cells" note already in the
        // SDD (4.6) about grid resolution.
        // ------------------------------------------------------------------
        void CleanCellsUnderFootprint(float dt)
        {
            Vector2 position = robotBody.position;
            float radius = robotCollider.radius;

            Vector2 origin = Grid.OriginWorld;
            float cellSize = Grid.CellSizeMeters;

            // Same floor-division math TryWorldToGridIndex uses internally,
            // but clamped into range here instead of rejected -- this is a
            // bounding box for a search, not a single point lookup, so an
            // out-of-grid corner should just clamp to the grid's edge
            // rather than invalidate the whole query.
            int minCol = Mathf.Clamp(Mathf.FloorToInt((position.x - radius - origin.x) / cellSize), 0, Grid.Cols - 1);
            int maxCol = Mathf.Clamp(Mathf.FloorToInt((position.x + radius - origin.x) / cellSize), 0, Grid.Cols - 1);
            int minRow = Mathf.Clamp(Mathf.FloorToInt((position.y - radius - origin.y) / cellSize), 0, Grid.Rows - 1);
            int maxRow = Mathf.Clamp(Mathf.FloorToInt((position.y + radius - origin.y) / cellSize), 0, Grid.Rows - 1);

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int col = minCol; col <= maxCol; col++)
                {
                    Vector2 cellCenter = Grid.GridIndexToWorldCenter(row, col);
                    if (Vector2.Distance(cellCenter, position) > radius) continue; // square box, circular footprint -- corners get skipped here

                    CellState cell = Grid.GetCell(row, col);
                    if (!cell.CountsTowardCoverage) continue; // under blocking furniture or off the floor -- see ExternalModelGrid.PopulateFromLevelObject

                    int index = row * Grid.Cols + col;
                    int last = lastVisitStep[index];
                    if (last == 0) VisitedCellCount++;
                    else if (last < stepIndex - 1) RevisitCount++;
                    lastVisitStep[index] = stepIndex;

                    // Dwell accrues on every cleanable cell under the footprint,
                    // already-clean ones included; cleanliness scales with the floor.
                    float cleanlinessGained = vacuumPower * RateMultiplierAt(row, col, cellCenter) * dt;
                    Grid.Accumulate(row, col, dt, cleanlinessGained, simTimeElapsed);
                }
            }
        }

        // ------------------------------------------------------------------
        // Cleaning rate on a given floor: the base rate divided by that
        // floor type's cleaningEffort. Off the floor (null) or with no
        // effort set, the base rate applies unchanged.
        // ------------------------------------------------------------------
        public static float CleaningRate(float baseRatePerSecond, FloorType floor)
        {
            float effort = FloorTuning.CleaningEffort(floor);
            return floor != null && effort > 0f ? baseRatePerSecond / effort : baseRatePerSecond;
        }

        // ------------------------------------------------------------------
        // Coverage of each room on its own, in percent, indexed like
        // level.Rooms. A room the vacuum never reached reads 0.
        // ------------------------------------------------------------------
        public float[] RoomCoveragePercent()
        {
            var result = new float[level != null ? level.Rooms.Count : 0];
            if (Grid == null || result.Length == 0) return result;

            var sums = new double[result.Length];
            var counts = new int[result.Length];
            for (int row = 0; row < Grid.Rows; row++)
            {
                for (int col = 0; col < Grid.Cols; col++)
                {
                    if (!Grid.GetCell(row, col).CountsTowardCoverage) continue;

                    Vector2 world = Grid.GridIndexToWorldCenter(row, col);
                    int room = level.RoomIndexAt(levelRenderer != null ? levelRenderer.WorldToLevel(world) : world);
                    if (room < 0) continue;
                    sums[room] += Grid.CleanFractionOf(row, col);
                    counts[room]++;
                }
            }

            for (int i = 0; i < result.Length; i++) result[i] = counts[i] > 0 ? (float)(sums[i] / counts[i] * 100.0) : 0f;
            return result;
        }

        float RateMultiplierAt(int row, int col, Vector2 worldCenter)
        {
            int index = row * Grid.Cols + col;
            if (cellRateMultiplier[index] > 0f) return cellRateMultiplier[index];

            Vector2 levelPoint = levelRenderer != null ? levelRenderer.WorldToLevel(worldCenter) : worldCenter;
            float multiplier = CleaningRate(1f, level.FloorTypeAt(levelPoint));

            cellRateMultiplier[index] = multiplier;
            return multiplier;
        }
    }
}
