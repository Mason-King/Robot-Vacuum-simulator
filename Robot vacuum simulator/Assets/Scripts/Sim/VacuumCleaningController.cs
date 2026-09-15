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
    // yet. This uses a simple flat decrease-per-second while the footprint
    // overlaps a cell, as a stand-in, so there's something real to look at
    // tonight. Swap CleanCellsUnderFootprint()'s math once 3.14 is real.
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

        [Header("PLACEHOLDER cleaning rate -- not the real 3.14 formula")]
        [Tooltip("How fast dirtiness falls per second while the robot's footprint overlaps a cell. Flat and speed/surface-independent for now -- swap this out once the real cleaning-effectiveness formula (SDD 3.14) exists.")]
        [SerializeField] float dirtinessDecreasePerSecond = 0.5f;

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
        // and it's the value passed into ExternalModelGrid.SetDirtiness's
        // currentSimTime parameter, which drives the event-log throttle.
        float simTimeElapsed;

        // Convenience read-out, e.g. for a debug UI later.
        public float CoveragePercent => Grid != null ? Grid.CalculateCoveragePercent() : 0f;

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

            Grid = new ExternalModelGrid(level, levelRenderer, cellSizeMeters);

            // Currently a no-op (see ExternalModelGrid.cs) -- called anyway
            // so this call site doesn't need to change once obstacle data
            // exists on the Level Editor's side.
            Grid.PopulateFromLevelObject();
        }

        void FixedUpdate()
        {
            if (Grid == null || robotBody == null) return;

            simTimeElapsed += Time.fixedDeltaTime;
            CleanCellsUnderFootprint(Time.fixedDeltaTime);
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
                    if (cell.isNonCleanable) continue; // currently always false -- see ExternalModelGrid's PENDING note

                    float updated = cell.Dirtiness - dirtinessDecreasePerSecond * dt;
                    Grid.SetDirtiness(row, col, updated, simTimeElapsed);
                }
            }
        }
    }
}
