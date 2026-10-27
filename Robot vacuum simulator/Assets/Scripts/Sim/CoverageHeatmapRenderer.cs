using UnityEngine;

namespace RobotVacuum.Sim
{
    // ============================================================================
    // CoverageHeatmapRenderer.cs
    //
    // Draws VacuumCleaningController's grid as a colored overlay in one of three
    // modes (HeatmapMode): Floor Cleanliness, Floor Coverage, Floor OverCoverage.
    // Cells with nothing in them yet are fully transparent, so the real floor
    // shows through from the start. All colours and opacities come from
    // HeatmapColors / HeatmapDefaults -- nothing is configured here. One quad,
    // one generated texture -- not one GameObject per cell, which would be far
    // too many objects at ~1-inch resolution.
    //
    // The mode is a public property (Mode) AND an Inspector field, and may be
    // changed at any time, including mid-run: the overlay repaints on the next
    // frame. A GUI binds to Mode; Mode and the target (the controller's
    // TargetMaxClean) are all this component reads.
    // ============================================================================
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class CoverageHeatmapRenderer : MonoBehaviour
    {
        [Tooltip("Must be the same controller/grid you want visualized. Left empty, this finds the first one in the scene.")]
        [SerializeField] VacuumCleaningController controller;

        [Tooltip("How often the texture is rebuilt, in seconds. Rebuilding every physics tick would be wasteful -- this is a display, not the authoritative data (that's Grid.CalculateCoveragePercent(), always live).")]
        [SerializeField] float updateIntervalSeconds = 0.25f;

        [Tooltip("Z offset from the floor. Small negative values sit the heatmap above the floor and below the walls, matching LevelRenderer's floorDepth/wallDepth convention (floor at 0, walls slightly negative).")]
        [SerializeField] float depthOffset = -0.02f;

        [Tooltip("Which view of the External Model to show. Can be changed while running; the overlay repaints right away.")]
        [SerializeField] HeatmapMode mode = HeatmapMode.FloorCleanliness;

        /// <summary>The view being shown. Setting it repaints on the next frame.</summary>
        public HeatmapMode Mode
        {
            get => mode;
            set
            {
                if (mode == value) return;
                mode = value;
                repaintRequested = true;
            }
        }

        bool repaintRequested;

        Texture2D texture;
        Mesh quadMesh;
        float timeSinceLastUpdate;
        int builtRows = -1;
        int builtCols = -1;

        void Awake()
        {
            if (controller == null)
                controller = FindAnyObjectByType<VacuumCleaningController>(FindObjectsInactive.Include);

            GetComponent<MeshRenderer>().sharedMaterial = BuildMaterial();
        }

        void Update()
        {
            if (controller == null || controller.Grid == null) return;

            // Grid dimensions are only known once VacuumCleaningController's
            // Awake() has actually run and built the grid -- this could be a
            // frame or two after THIS object's own Awake(), depending on
            // script execution order, so the mesh/texture are built lazily
            // here on first Update() rather than in this component's Awake().
            if (texture == null || builtRows != controller.Grid.Rows || builtCols != controller.Grid.Cols)
                BuildTextureAndMesh();

            timeSinceLastUpdate += Time.deltaTime;
            if (!repaintRequested && timeSinceLastUpdate < updateIntervalSeconds) return;
            timeSinceLastUpdate = 0f;
            repaintRequested = false;

            RepaintTexture();
        }

        // ------------------------------------------------------------------
        // Builds the texture (one pixel per grid cell) and a single quad
        // mesh sized and positioned to exactly cover the grid's real-world
        // footprint, using OriginWorld/CellSizeMeters/Rows/Cols -- the same
        // values TryWorldToGridIndex uses, so the overlay lines up with the
        // actual floor geometry underneath it, not an approximation of it.
        // ------------------------------------------------------------------
        void BuildTextureAndMesh()
        {
            var grid = controller.Grid;
            builtRows = grid.Rows;
            builtCols = grid.Cols;

            texture = new Texture2D(builtCols, builtRows, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear, // smooth blending between cells rather than hard blocky pixels
                wrapMode = TextureWrapMode.Clamp,
            };

            Vector2 origin = grid.OriginWorld;
            float cellSize = grid.CellSizeMeters;
            Vector2 worldSize = new Vector2(builtCols * cellSize, builtRows * cellSize);

            quadMesh = new Mesh { name = "CoverageHeatmapQuad" };
            quadMesh.vertices = new[]
            {
                new Vector3(origin.x, origin.y, 0f),
                new Vector3(origin.x + worldSize.x, origin.y, 0f),
                new Vector3(origin.x + worldSize.x, origin.y + worldSize.y, 0f),
                new Vector3(origin.x, origin.y + worldSize.y, 0f),
            };
            quadMesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            quadMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            quadMesh.RecalculateNormals();
            quadMesh.RecalculateBounds();

            GetComponent<MeshFilter>().sharedMesh = quadMesh;

            // The mesh's own vertices above are already absolute world X/Y
            // (built from origin.x/origin.y directly) -- so this object's
            // transform must stay at identity rotation/scale, only offset
            // in Z, or the overlay will visibly drift out of alignment with
            // the real floor. Setting all three explicitly here rather than
            // just position, so an accidental rotate/scale in the Inspector
            // can't silently break the alignment.
            transform.position = new Vector3(0f, 0f, depthOffset);
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;

            RepaintTexture();
        }

        // ------------------------------------------------------------------
        // Rebuilds every pixel from the grid's current values, coloured for
        // the current mode and target. Reads Grid.GetCell for every cell --
        // this is the "display copy," never the authoritative source.
        // ------------------------------------------------------------------
        void RepaintTexture()
        {
            var grid = controller.Grid;
            var pixels = new Color32[builtCols * builtRows];
            float target = grid.TargetMaxClean;

            for (int row = 0; row < builtRows; row++)
            {
                for (int col = 0; col < builtCols; col++)
                {
                    var cell = grid.GetCell(row, col);

                    // Cells that don't count toward coverage (under furniture
                    // that blocks the vacuum, or outside every room) stay
                    // fully transparent.
                    // Texture2D rows run bottom-to-top and so does our grid
                    // (row 0 = lowest Y), so this is a direct index.
                    pixels[row * builtCols + col] = !cell.CountsTowardCoverage
                        ? default
                        : HeatmapColors.Evaluate(mode, cell.DwellSeconds, cell.Cleanliness, target);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
        }

        // An Inspector edit to the mode while running takes effect straight away.
        void OnValidate() => repaintRequested = true;

        // Runs may be started and stopped many times in one session, so the
        // generated texture, mesh and material go with the component.
        void OnDestroy()
        {
            if (texture != null) Destroy(texture);
            if (quadMesh != null) Destroy(quadMesh);

            var meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.sharedMaterial != null) Destroy(meshRenderer.sharedMaterial);
        }

        Material BuildMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Transparent")
                         ?? Shader.Find("Sprites/Default");

            var material = new Material(shader)
            {
                name = "CoverageHeatmap (generated)",
                hideFlags = HideFlags.HideAndDontSave,
            };

            // IMPORTANT: _Surface/_Blend are Inspector-facing dropdown
            // properties only. In the Editor, changing them by hand runs a
            // custom shader-editor script that translates the choice into
            // the REAL blend state (_SrcBlend/_DstBlend/_ZWrite + keywords).
            // That translation never runs for a material built through
            // script -- so setting _Surface alone silently leaves the
            // material fully OPAQUE, alpha gets ignored entirely, and every
            // pixel renders its RGB at full solid opacity. That's exactly
            // the "whole map is solid green from frame one" bug -- the
            // dirtiness data was very likely fine; the material just never
            // actually blended. Setting the real properties below directly
            // is what actually fixes it.
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);

            if (material.HasProperty("_SrcBlend"))
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend"))
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 0f);

            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            // Some URP shader variants gate blending behind this keyword
            // rather than (or in addition to) the properties above.
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            if (material.HasProperty("_Cull"))
                material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);

            return material;
        }

        void LateUpdate()
        {
            // Texture is assigned here (not in BuildTextureAndMesh) so a
            // material property block, rather than a shared material
            // instance per heatmap, is what carries it -- same pattern
            // LevelRenderer.cs and VacuumRobot.cs already use for their own
            // generated visuals, kept consistent rather than inventing a
            // second approach.
            if (texture == null) return;

            var renderer = GetComponent<MeshRenderer>();
            var block = new MaterialPropertyBlock();
            block.SetTexture("_BaseMap", texture);
            block.SetTexture("_MainTex", texture);
            renderer.SetPropertyBlock(block);
        }
    }
}
