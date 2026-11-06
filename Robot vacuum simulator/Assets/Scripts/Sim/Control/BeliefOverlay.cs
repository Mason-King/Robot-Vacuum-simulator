using UnityEngine;

namespace RobotVacuum.Sim.Control
{
    /// <summary>Which belief the debug overlay draws.</summary>
    public enum BeliefOverlayView { Traversability, VacuumResistance, Dwell }

    /// <summary>
    /// DEBUG OVERLAY: draws what the robot BELIEVES (design doc, section 10). One self-contained component with one
    /// on/off switch, so it can be turned off or cut in one place.
    ///
    ///   * the Internal Model as a world-space layer over the floor:
    ///       Traversability    believed impassable = black, normal floor = clear, in between = shaded
    ///       VacuumResistance  harder to clean than normal = orange, easier = teal
    ///       Dwell             the robot's own estimate of time spent over each cell = blue
    ///     Cells never written are clear, so in a SLAM run you watch the map appear as the robot senses.
    ///   * a ring at the BELIEVED pose (in SLAM, its offset from the real robot is the drift)
    ///   * a dot at the current waypoint
    ///
    /// <see cref="Show"/> and <see cref="View"/> are public properties, so a UI (Emmi's GUI) can drive them later.
    /// Graphics runs only: SimulationRunner never builds it for a headless run.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class BeliefOverlay : MonoBehaviour
    {
        [Tooltip("The one on/off switch for the whole overlay (map layer and markers).")]
        [SerializeField] bool show;

        [Tooltip("Which belief the map layer draws. Can be changed while running.")]
        [SerializeField] BeliefOverlayView view = BeliefOverlayView.Traversability;

        [Tooltip("Left empty, this finds the first MovementBrain (controller) in the scene.")]
        [SerializeField] MovementBrain brain;

        [Tooltip("How often the map layer is redrawn, real seconds.")]
        [SerializeField] float updateIntervalSeconds = 0.25f;

        [Tooltip("Z of the map layer: just above the coverage heatmap (-0.02).")]
        [SerializeField] float depth = -0.03f;

        const float MaxAlpha = 0.8f;
        const float DwellForFullColourSeconds = 5f;

        static readonly Color32 BlockedColour = new Color32(10, 10, 14, 255);
        static readonly Color PoseColour = new Color(1f, 0.55f, 0.1f);
        static readonly Color WaypointColour = new Color(0.95f, 0.2f, 0.85f);

        Texture2D texture;
        Color32[] pixels;
        Mesh quad;
        int builtMinRow, builtMinCol, builtRows = -1, builtCols = -1;
        float sinceRepaint;
        bool repaintRequested = true;

        Transform poseMarker;
        Transform waypointMarker;
        LineRenderer pathLine;
        Material markerMaterial;

        /// <summary>The overlay's one on/off switch.</summary>
        public bool Show
        {
            get => show;
            set
            {
                show = value;
                repaintRequested = true;
                ApplyVisibility();
            }
        }

        /// <summary>Which belief the map layer draws.</summary>
        public BeliefOverlayView View
        {
            get => view;
            set
            {
                if (view == value) return;
                view = value;
                repaintRequested = true;
            }
        }

        /// <summary>The controller to draw. Set by SimulationRunner; otherwise found in the scene.</summary>
        public MovementBrain Brain
        {
            get => brain;
            set => brain = value;
        }

        void Awake()
        {
            GetComponent<MeshRenderer>().sharedMaterial = BuildMaterial("BeliefOverlay (generated)", transparent: true);
            markerMaterial = BuildMaterial("BeliefOverlay markers (generated)", transparent: false);

            poseMarker = BuildMarker("Believed Pose", BuildRing(0.17f, 0.025f, 40), PoseColour);
            waypointMarker = BuildMarker("Waypoint", BuildDisc(0.05f, 20), WaypointColour);
            pathLine = BuildPathLine();

            transform.position = new Vector3(0f, 0f, depth);
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;
        }

        void OnValidate()
        {
            repaintRequested = true;
            if (Application.isPlaying && poseMarker != null) ApplyVisibility();
        }

        void Update()
        {
            if (brain == null) brain = FindAnyObjectByType<MovementBrain>();
            ApplyVisibility();
            if (!show || brain == null || !brain.Ready || brain.Model == null) return;

            var model = brain.Model;
            if (model.IsEmpty) return;

            if (texture == null || builtMinRow != model.MinRow || builtMinCol != model.MinCol ||
                builtRows != model.Rows || builtCols != model.Cols)
            {
                Rebuild(model);
                repaintRequested = true;
            }

            sinceRepaint += Time.unscaledDeltaTime;
            if (!repaintRequested && sinceRepaint < updateIntervalSeconds) return;
            sinceRepaint = 0f;
            repaintRequested = false;
            Repaint(model);
        }

        void LateUpdate()
        {
            if (!show || brain == null || !brain.Ready) return;

            float markerZ = MarkerDepth();
            var pose = brain.BelievedPose;
            poseMarker.position = new Vector3(pose.position.x, pose.position.y, markerZ);
            poseMarker.rotation = Quaternion.identity;

            var waypoint = brain.CurrentWaypoint;
            waypointMarker.gameObject.SetActive(waypoint.HasValue);
            if (waypoint.HasValue) waypointMarker.position = new Vector3(waypoint.Value.x, waypoint.Value.y, markerZ);

            // The planned path, from where the robot believes it is through the corners still ahead.
            var path = brain.CurrentPath;
            int corner = brain.CurrentPathCorner;
            int points = waypoint.HasValue && path.Count >= 2 && corner < path.Count ? path.Count - corner + 1 : 0;
            pathLine.enabled = points >= 2;
            pathLine.positionCount = points;
            if (points >= 2)
            {
                pathLine.SetPosition(0, new Vector3(pose.position.x, pose.position.y, markerZ));
                for (int i = corner; i < path.Count; i++)
                    pathLine.SetPosition(i - corner + 1, new Vector3(path[i].x, path[i].y, markerZ));
            }

            if (texture == null) return;
            var block = new MaterialPropertyBlock();
            block.SetTexture("_BaseMap", texture);
            block.SetTexture("_MainTex", texture);
            GetComponent<MeshRenderer>().SetPropertyBlock(block);
        }

        // The markers sit just above the robot, which LevelRenderer places at its wall depth - 0.05.
        float MarkerDepth()
        {
            var robot = brain != null ? brain.GetComponent<VacuumRobot>() : null;
            var level = robot != null ? robot.LevelRenderer : null;
            return level != null ? level.WallDepth - 0.07f : -0.2f;
        }

        void ApplyVisibility()
        {
            var renderer = GetComponent<MeshRenderer>();
            bool ready = brain != null && brain.Ready;
            if (renderer != null) renderer.enabled = show;
            if (poseMarker != null) poseMarker.gameObject.SetActive(show && ready);
            if (waypointMarker != null && !(show && ready)) waypointMarker.gameObject.SetActive(false);
            if (pathLine != null && !(show && ready)) pathLine.enabled = false;
        }

        // ---- Map layer -----------------------------------------------------------------------------------

        void Rebuild(InternalModelView model)
        {
            builtMinRow = model.MinRow;
            builtMinCol = model.MinCol;
            builtRows = model.Rows;
            builtCols = model.Cols;

            if (texture != null) Destroy(texture);
            texture = new Texture2D(builtCols, builtRows, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point, // crisp cells: this is a debugging view
                wrapMode = TextureWrapMode.Clamp,
            };
            pixels = new Color32[builtCols * builtRows];

            float cell = model.CellSizeMeters;
            Vector2 origin = model.AnchorWorld + new Vector2(builtMinCol * cell, builtMinRow * cell);
            Vector2 size = new Vector2(builtCols * cell, builtRows * cell);

            if (quad == null) quad = new Mesh { name = "BeliefOverlayQuad" };
            quad.Clear();
            quad.vertices = new[]
            {
                new Vector3(origin.x, origin.y, 0f),
                new Vector3(origin.x + size.x, origin.y, 0f),
                new Vector3(origin.x + size.x, origin.y + size.y, 0f),
                new Vector3(origin.x, origin.y + size.y, 0f),
            };
            quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            quad.RecalculateBounds();
            GetComponent<MeshFilter>().sharedMesh = quad;
        }

        void Repaint(InternalModelView model)
        {
            for (int r = 0; r < builtRows; r++)
            {
                for (int c = 0; c < builtCols; c++)
                {
                    var cell = model.GetCell(builtMinRow + r, builtMinCol + c);
                    pixels[r * builtCols + c] = Colour(cell);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
        }

        Color32 Colour(RobotVacuumSim.EnvironmentModel.BeliefCell cell)
        {
            switch (view)
            {
                case BeliefOverlayView.Traversability:
                {
                    float t = cell.traversabilityBelief;
                    if (t <= 0f) return new Color32(BlockedColour.r, BlockedColour.g, BlockedColour.b, (byte)(MaxAlpha * 255f));
                    if (t >= 0.999f) return default;
                    float a = (1f - Mathf.Clamp01(t)) * MaxAlpha;
                    return new Color32(40, 40, 48, (byte)(a * 255f));
                }

                case BeliefOverlayView.VacuumResistance:
                {
                    float v = cell.vacuumResistanceBelief;
                    if (Mathf.Abs(v - 1f) < 0.01f) return default;
                    float a = Mathf.Clamp01(Mathf.Abs(v - 1f)) * MaxAlpha;
                    return v > 1f ? new Color32(240, 140, 30, (byte)(a * 255f)) : new Color32(30, 190, 180, (byte)(a * 255f));
                }

                default:
                {
                    float d = cell.dwellBelief;
                    if (d <= 0f) return default;
                    float a = Mathf.Clamp01(d / DwellForFullColourSeconds) * MaxAlpha;
                    return new Color32(40, 110, 240, (byte)(Mathf.Max(0.15f, a) * 255f));
                }
            }
        }

        // ---- Markers -------------------------------------------------------------------------------------

        LineRenderer BuildPathLine()
        {
            var go = new GameObject("Planned Path");
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = markerMaterial;
            line.useWorldSpace = true;
            line.widthMultiplier = 0.02f;
            line.numCapVertices = 2;
            line.numCornerVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.startColor = line.endColor = WaypointColour;
            line.positionCount = 0;
            line.enabled = false;

            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", WaypointColour);
            block.SetColor("_Color", WaypointColour);
            line.SetPropertyBlock(block);
            return line;
        }

        Transform BuildMarker(string name, Mesh mesh, Color colour)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = markerMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", colour);
            block.SetColor("_Color", colour);
            renderer.SetPropertyBlock(block);

            go.SetActive(false);
            return go.transform;
        }

        static Mesh BuildRing(float radius, float width, int segments)
        {
            var vertices = new Vector3[segments * 2];
            var triangles = new int[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                var direction = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                vertices[i * 2] = direction * (radius - width * 0.5f);
                vertices[i * 2 + 1] = direction * (radius + width * 0.5f);

                int next = (i + 1) % segments;
                int t = i * 6;
                triangles[t] = i * 2;
                triangles[t + 1] = next * 2;
                triangles[t + 2] = i * 2 + 1;
                triangles[t + 3] = i * 2 + 1;
                triangles[t + 4] = next * 2;
                triangles[t + 5] = next * 2 + 1;
            }

            var mesh = new Mesh { name = "BeliefOverlayRing", vertices = vertices, triangles = triangles };
            mesh.RecalculateBounds();
            return mesh;
        }

        static Mesh BuildDisc(float radius, int segments)
        {
            var vertices = new Vector3[segments + 1];
            var triangles = new int[segments * 3];
            vertices[0] = Vector3.zero;
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = (i + 1) % segments + 1;
            }

            var mesh = new Mesh { name = "BeliefOverlayDisc", vertices = vertices, triangles = triangles };
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- Materials (same approach as CoverageHeatmapRenderer) ------------------------------------------

        static Material BuildMaterial(string name, bool transparent)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find(transparent ? "Unlit/Transparent" : "Unlit/Color")
                         ?? Shader.Find("Sprites/Default");

            var material = new Material(shader) { name = name, hideFlags = HideFlags.HideAndDontSave };

            if (transparent)
            {
                // A material built in script never runs the Editor's surface-type translation, so the real blend
                // state is set directly (see CoverageHeatmapRenderer.BuildMaterial for the full story).
                if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
                if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
                if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
                material.SetOverrideTag("RenderType", "Transparent");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 1; // over the heatmap
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHATEST_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            else
            {
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 2; // markers on top
            }

            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            return material;
        }

        void OnDestroy()
        {
            if (texture != null) Destroy(texture);
            if (quad != null) Destroy(quad);
            if (markerMaterial != null) Destroy(markerMaterial);

            var renderer = GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterial != null) Destroy(renderer.sharedMaterial);

            foreach (var filter in GetComponentsInChildren<MeshFilter>(true))
                if (filter != null && filter.gameObject != gameObject && filter.sharedMesh != null) Destroy(filter.sharedMesh);
        }
    }
}
