using UnityEngine;

namespace RobotVacuum.Sim
{
    // ============================================================================
    // CoverageHeatmapRenderer.cs
    //
    // Draws VacuumCleaningController's grid as a colored overlay: untouched
    // (fully dirty) cells are fully transparent, so the real floor shows through
    // from the start, and cells change colour as the robot cleans them -- red
    // when barely touched, through orange and yellow, to green when fully clean
    // (an editable Gradient). One quad, one generated texture -- not one
    // GameObject per cell, which would be far too many objects at ~1-inch
    // resolution.
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

        [Tooltip("Colour by how clean a cell is: the left end is barely touched, the right end fully clean. A cell nobody has cleaned yet is always fully transparent, whatever the gradient says, so the real floor is visible from the start.")]
        [SerializeField] Gradient colors = DefaultColors();

        // The gradient sampled once into a lookup table, so repainting a
        // large grid doesn't call Gradient.Evaluate for every cell.
        const int PaletteSize = 256;
        Color32[] palette;

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
            BuildPalette();
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
            if (palette == null) BuildPalette();

            for (int row = 0; row < builtRows; row++)
            {
                for (int col = 0; col < builtCols; col++)
                {
                    var cell = grid.GetCell(row, col);

                    // Non-cleanable cells stay fully transparent -- nothing
                    // to show, since they're excluded from coverage entirely
                    // (matches CalculateCoveragePercent's own treatment).
                    // Set for cells under furniture that blocks the vacuum.
                    Color32 pixelColor;
                    if (cell.isNonCleanable)
                    {
                        pixelColor = default;
                    }
                    else
                    {
                        // dirtiness runs 1 (dirty, the starting state) -> 0
                        // (clean). The palette is indexed by how CLEAN a cell
                        // is, so untouched cells stay transparent and colour
                        // builds up from red to green as the robot works.
                        float cleanedFraction = 1f - cell.Dirtiness;
                        pixelColor = palette[Mathf.Clamp(Mathf.RoundToInt(cleanedFraction * (PaletteSize - 1)), 0, PaletteSize - 1)];
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

        void OnValidate() => palette = null; // rebuilt from the edited gradient on the next repaint

        void BuildPalette()
        {
            palette = new Color32[PaletteSize];
            for (int i = 0; i < PaletteSize; i++)
                palette[i] = colors.Evaluate(i / (float)(PaletteSize - 1));

            // Untouched floor stays see-through whatever the gradient's left end looks like.
            palette[0] = default;
        }

        static Gradient DefaultColors()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.90f, 0.26f, 0.29f), 0f),    // barely touched: red
                    new GradientColorKey(new Color(0.96f, 0.58f, 0.22f), 0.35f), // orange
                    new GradientColorKey(new Color(0.96f, 0.84f, 0.29f), 0.6f),  // yellow
                    new GradientColorKey(new Color(0.24f, 0.80f, 0.54f), 1f),    // fully clean: green
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.8f, 0.06f),
                    new GradientAlphaKey(0.85f, 1f),
                });
            return gradient;
        }

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
