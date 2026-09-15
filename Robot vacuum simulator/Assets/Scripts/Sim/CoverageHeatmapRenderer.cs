using UnityEngine;

namespace RobotVacuum.Sim
{
    // ============================================================================
    // CoverageHeatmapRenderer.cs
    //
    // Draws VacuumCleaningController's grid as a colored overlay: untouched
    // (fully dirty) cells are fully transparent, so the real floor shows through
    // from the start, and cells solidify toward an opaque evergreen color as the
    // robot cleans them -- a coverage-progress look, not the usual red "dirt"
    // heatmap. One quad, one generated texture -- not one GameObject per cell,
    // which would be far too many objects at ~1-inch resolution.
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

        [Tooltip("Overlay color for a fully CLEAN cell (dirtiness = 0), shown at full alpha -- solid. A fully DIRTY cell (dirtiness = 1, the starting state for every cell) is always fully transparent, regardless of this color, so the real floor is visible from the start. Cells interpolate between the two as they're cleaned.")]
        [SerializeField] Color cleanColor = new Color(0.09f, 0.35f, 0.18f, 1f); // evergreen

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
            if (timeSinceLastUpdate < updateIntervalSeconds) return;
            timeSinceLastUpdate = 0f;

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
        // Rebuilds every pixel from the grid's current dirtiness values.
        // Reads Grid.GetCell for every cell -- this is the "display copy,"
        // never the authoritative source; CalculateCoveragePercent() always
        // computes fresh from the real cell array, independent of whatever
        // this texture currently shows.
        // ------------------------------------------------------------------
        void RepaintTexture()
        {
            var grid = controller.Grid;
            var pixels = new Color32[builtCols * builtRows];

            for (int row = 0; row < builtRows; row++)
            {
                for (int col = 0; col < builtCols; col++)
                {
                    var cell = grid.GetCell(row, col);

                    // Non-cleanable cells stay fully transparent -- nothing
                    // to show, since they're excluded from coverage entirely
                    // (matches CalculateCoveragePercent's own treatment).
                    // Currently always false (see ExternalModelGrid's
                    // PENDING note on obstacle data), but written correctly
                    // now so nothing needs to change here once that lands.
                    Color pixelColor;
                    if (cell.isNonCleanable)
                    {
                        pixelColor = new Color(0f, 0f, 0f, 0f);
                    }
                    else
                    {
                        // dirtiness runs 1 (dirty, the starting state) -> 0
                        // (clean). We want the OPPOSITE mapping to alpha:
                        // dirty -> transparent (floor visible from the
                        // start), clean -> solid evergreen. So alpha scales
                        // with (1 - dirtiness), not dirtiness directly.
                        float cleanedFraction = 1f - cell.Dirtiness;
                        float alpha = cleanedFraction * cleanColor.a;
                        pixelColor = new Color(cleanColor.r, cleanColor.g, cleanColor.b, alpha);
                    }

                    // Texture2D rows run bottom-to-top by convention; our
                    // grid's row 0 is also the bottom (lowest Y, per
                    // GridIndexToWorldCenter) since both follow the same
                    // confirmed Y-up convention -- so this is a direct
                    // index, no flip needed.
                    pixels[row * builtCols + col] = pixelColor;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
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
