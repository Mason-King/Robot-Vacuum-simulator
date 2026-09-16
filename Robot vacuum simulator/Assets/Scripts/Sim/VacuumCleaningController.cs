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
    // PLACEHOLDER, clearly flagged below: the actual cleaning-effectiveness
    // formula (SDD 3.14 -- dwell time x velocity x surface type) doesn't exist
    // yet. This uses a decrease-per-second while the footprint overlaps a
    // cell, with only the surface part in place: each floor type's
    // cleaningEffort divides the rate, so carpet cleans slower than tile.
    // Swap CleanCellsUnderFootprint()'s math once 3.14 is real.
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
        [Tooltip("Grid cell edge length, in metres. SDD 4.6 targets roughly 1 inch (~0.0254m). Minimum/maximum bounds are still open -- pending the Level Editor team conversation -- so nothing here enforces a range yet.")]
        [SerializeField] float cellSizeMeters = 0.0254f;

        [Header("PLACEHOLDER cleaning rate -- not the full 3.14 formula")]
        [Tooltip("How fast dirtiness falls per second, on a floor with a cleaning effort of 1, while the robot's footprint overlaps a cell. Divided by each floor type's cleaningEffort. Still speed/dwell-independent -- swap this out once the real cleaning-effectiveness formula (SDD 3.14) exists.")]
        [SerializeField] float dirtinessDecreasePerSecond = 0.5f;

        // Per-cell rate multiplier (1 / cleaningEffort), looked up the first
        // time the footprint touches a cell; 0 means not looked up yet. Floor
        // type is static scene data, so it stays out of CellState (see the
        // SCOPE NOTE in ExternalModelGrid).
        float[] cellRateMultiplier;

        Rigidbody2D robotBody;
        CircleCollider2D robotCollider;
        VacuumRobot robot;

        // The grid itself. Public so the heatmap renderer (or anything else
        // that wants to read coverage state) can get at it without this
        // controller needing to know anything about rendering.
        public ExternalModelGrid Grid { get; private set; }

        // A simple accumulated clock, separate from Time.time. Time.time
        // includes time before this object started and behaves oddly around
        // pausing; accumulating our own FixedUpdate-driven clock here is a
        // closer (if still simplified) stand-in for the "simulated time"
        // concept the SDD's Action Log/telemetry design actually wants --
        // and it's the value passed into ExternalModelGrid.SetDirtiness's
        // currentSimTime parameter, which drives the event-log throttle.
        float simTimeElapsed;

        // Convenience read-out, e.g. for a debug UI later.
        public float CoveragePercent => Grid != null ? Grid.CalculateCoveragePercent() : 0f;

        void Awake()
        {
            robotBody = GetComponent<Rigidbody2D>();
            robotCollider = GetComponent<CircleCollider2D>();
            robot = GetComponent<VacuumRobot>();

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
            Grid = new ExternalModelGrid(level, levelRenderer, cellSizeMeters);

            // Marks cells under blocking furniture as non-cleanable (see ExternalModelGrid.cs).
            Grid.PopulateFromLevelObject();
            cellRateMultiplier = new float[Grid.Rows * Grid.Cols];
        }

        void FixedUpdate()
        {
            if (Grid == null || robotBody == null) return;

            simTimeElapsed += Time.fixedDeltaTime;
            CleanCellsUnderFootprint(Time.fixedDeltaTime);

            if (robot != null && robot.Print.HasValue) PrintUnderRobot(robot.Print.Value);
        }

        // ------------------------------------------------------------------
        // Finds every cell whose center falls within the robot's circular
        // footprint (robotCollider.radius, the SAME collider VacuumRobot.cs
        // already sizes and uses for collision) and decreases its dirtiness.
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
            // The robot can cap how clean it leaves the floor (the Picture
            // pattern shades with this); cells never go below that dirtiness.
            float dirtinessFloor = robot != null ? 1f - robot.CleaningLimit : 0f;
            if (dirtinessFloor >= 1f) return; // suction off

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
                    if (cell.isNonCleanable) continue; // under blocking furniture -- see ExternalModelGrid.PopulateFromLevelObject

                    if (cell.Dirtiness <= dirtinessFloor) continue;

                    float cleaned = cell.Dirtiness - dirtinessDecreasePerSecond * RateMultiplierAt(row, col, cellCenter) * dt;
                    float updated = Mathf.Max(dirtinessFloor, cleaned);
                    Grid.SetDirtiness(row, col, updated, simTimeElapsed);
                }
            }
        }

        // ------------------------------------------------------------------
        // Cleaning rate on a given floor: the base rate divided by that
        // floor type's cleaningEffort. Off the floor (null) or with no
        // effort set, the base rate applies unchanged.
        // ------------------------------------------------------------------
        public static float CleaningRate(float baseRatePerSecond, FloorType floor) =>
            floor != null && floor.cleaningEffort > 0f ? baseRatePerSecond / floor.cleaningEffort : baseRatePerSecond;

        float RateMultiplierAt(int row, int col, Vector2 worldCenter)
        {
            int index = row * Grid.Cols + col;
            if (cellRateMultiplier[index] > 0f) return cellRateMultiplier[index];

            Vector2 levelPoint = levelRenderer != null ? levelRenderer.WorldToLevel(worldCenter) : worldCenter;
            float multiplier = CleaningRate(1f, level.FloorTypeAt(levelPoint));

            cellRateMultiplier[index] = multiplier;
            return multiplier;
        }

        // ------------------------------------------------------------------
        // NOT CLEANING -- a toy for the picture-printing movement patterns.
        // Writes every cell in the patch straight to the picture's shade
        // (shade 1 = fully clean), so the heatmap shows the picture at full
        // grid resolution. Deliberately bypasses the dirt model above.
        // ------------------------------------------------------------------
        void PrintUnderRobot(PrintPatch patch)
        {
            if (patch.picture == null) return;

            Vector2 cornerA = ToWorld(patch.area.min);
            Vector2 cornerB = ToWorld(patch.area.max);
            Vector2 min = Vector2.Min(cornerA, cornerB);
            Vector2 max = Vector2.Max(cornerA, cornerB);

            Vector2 origin = Grid.OriginWorld;
            float cellSize = Grid.CellSizeMeters;
            int minCol = Mathf.Clamp(Mathf.FloorToInt((min.x - origin.x) / cellSize), 0, Grid.Cols - 1);
            int maxCol = Mathf.Clamp(Mathf.FloorToInt((max.x - origin.x) / cellSize), 0, Grid.Cols - 1);
            int minRow = Mathf.Clamp(Mathf.FloorToInt((min.y - origin.y) / cellSize), 0, Grid.Rows - 1);
            int maxRow = Mathf.Clamp(Mathf.FloorToInt((max.y - origin.y) / cellSize), 0, Grid.Rows - 1);

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int col = minCol; col <= maxCol; col++)
                {
                    CellState cell = Grid.GetCell(row, col);
                    if (cell.isNonCleanable) continue;

                    Vector2 world = Grid.GridIndexToWorldCenter(row, col);
                    Vector2 point = levelRenderer != null ? levelRenderer.WorldToLevel(world) : world;
                    if (!patch.area.Contains(point)) continue;
                    if (patch.radius > 0f && Vector2.Distance(point, patch.area.center) > patch.radius) continue;

                    float target = 1f - patch.picture.Sample(patch.canvas, point);
                    if (Mathf.Abs(cell.Dirtiness - target) > 1e-3f)
                        Grid.SetDirtiness(row, col, target, simTimeElapsed);
                }
            }
        }

        Vector2 ToWorld(Vector2 levelPoint) =>
            levelRenderer != null ? (Vector2)levelRenderer.LevelToWorld(levelPoint) : levelPoint;
    }
}
