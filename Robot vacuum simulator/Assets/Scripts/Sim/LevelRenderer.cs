using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Level
{
    /// <summary>
    /// Turns a <see cref="LevelData"/> floor plan into visible 2D geometry: one triangulated
    /// mesh per room tinted by its floor covering, plus a combined wall mesh with doorway
    /// gaps cut out and a rotated <c>BoxCollider2D</c> per wall segment.
    /// Everything it creates lives under one child object and is discarded on each rebuild.
    /// </summary>
    [ExecuteAlways]
    [SelectionBase]
    [DefaultExecutionOrder(-1000)] // wakes before the robot and cleaning grid, which read the level in Awake
    public class LevelRenderer : MonoBehaviour
    {
        const string GeneratedRootName = "Generated";
        const float PassableRimWidth = 0.06f;

        [SerializeField] LevelData level;
        [SerializeField] bool rebuildOnEnable = true;
        [SerializeField] bool buildWalls = true;

        [Tooltip("Off builds the colliders but no meshes, for a run with nothing to look at.")]
        [SerializeField] bool drawMeshes = true;

        [Tooltip("Z depth for room floors. Walls are drawn slightly in front.")]
        [SerializeField] float floorDepth = 0f;
        [SerializeField] float wallDepth = -0.05f;

        [Tooltip("Z depth for furniture: above the floor and coverage overlay, below walls and the vacuum.")]
        [SerializeField] float obstacleDepth = -0.03f;

        [Tooltip("Optional. Leave empty to generate an unlit, double-sided material at build time.")]
        [SerializeField] Material materialOverride;

        Transform generatedRoot;
        Material generatedMaterial;

        public LevelData Level
        {
            get => level;
            set { level = value; Rebuild(); }
        }

        public float WallDepth => wallDepth;

        /// <summary>
        /// Whether to build the floor, wall and furniture meshes. Colliders are built either way, so a
        /// headless run can turn this off and still have a level the vacuum bumps into.
        /// </summary>
        public bool DrawMeshes
        {
            get => drawMeshes;
            set { drawMeshes = value; Rebuild(); }
        }

        void Awake()
        {
            // A floor plan sent from the level editor replaces the one saved in the scene.
            if (Application.isPlaying && LevelHandoff.TryTake(out var handed)) level = handed;
        }

        void OnEnable()
        {
            if (rebuildOnEnable) Rebuild();
        }

        /// <summary>Finds the renderer in the loaded scenes that is showing <paramref name="data"/>.</summary>
        public static LevelRenderer FindFor(LevelData data)
        {
            if (data == null) return null;

            var all = FindObjectsByType<LevelRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var renderer in all)
                if (renderer.level == data) return renderer;
            return null;
        }

        public Vector2 WorldToLevel(Vector3 world)
        {
            var local = transform.InverseTransformPoint(world);
            return new Vector2(local.x, local.y);
        }

        public Vector3 LevelToWorld(Vector2 point, float depth = 0f) =>
            transform.TransformPoint(new Vector3(point.x, point.y, depth));

        public FloorType FloorTypeAtWorld(Vector3 world) =>
            level != null ? level.FloorTypeAt(WorldToLevel(world)) : null;

        // ---------------------------------------------------------------- build

        public void Rebuild()
        {
            ClearGenerated();
            if (level == null) return;

            var root = new GameObject(GeneratedRootName);
            root.transform.SetParent(transform, false);
            generatedRoot = root.transform;

            BuildFloors();
            BuildObstacles();
            if (buildWalls) BuildWalls();
        }

        void ClearGenerated()
        {
            var existing = transform.Find(GeneratedRootName);
            if (existing == null) return;

            SafeDestroy(existing.gameObject);
            generatedRoot = null;
        }

        static void SafeDestroy(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Destroy(target);
            else DestroyImmediate(target);
        }

        void BuildFloors()
        {
            for (int i = 0; i < level.Rooms.Count; i++)
            {
                var room = level.Rooms[i];
                if (room == null || !room.IsValid) continue;

                int[] triangles = Poly2D.Triangulate(room.outline);
                if (triangles.Length == 0) continue;

                var vertices = new Vector3[room.outline.Count];
                var uvs = new Vector2[room.outline.Count];

                var floorType = level.FloorTypeOf(room);
                var texture = FloorTextureFor(floorType, out float tile, out bool selfColoured);

                for (int v = 0; v < room.outline.Count; v++)
                {
                    var point = room.outline[v];
                    vertices[v] = new Vector3(point.x, point.y, floorDepth);

                    // World-scaled UVs, so a seamless tile repeats at a real-world size and
                    // stays continuous across rooms instead of stretching to each outline.
                    uvs[v] = point / tile;
                }

                var mesh = new Mesh { name = $"Floor_{room.name}" };
                mesh.vertices = vertices;
                mesh.uv = uvs;
                mesh.triangles = triangles;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                if (drawMeshes)
                    CreateMeshObject($"Floor_{room.name}", mesh, selfColoured ? Color.white : level.ColorOf(room), texture);
            }
        }

        /// <summary>A hand-assigned texture wins; otherwise the floor's pattern is generated at its natural size.</summary>
        static Texture2D FloorTextureFor(FloorType floor, out float tileMetres, out bool selfColoured)
        {
            tileMetres = 1f;
            selfColoured = false;
            if (floor == null) return null;

            float scale = Mathf.Max(0.01f, floor.textureScale);
            if (floor.texture != null)
            {
                tileMetres = scale;
                return floor.texture;
            }

            tileMetres = FloorTextures.TileMetres(floor.pattern) * scale;
            selfColoured = FloorTextures.IsSelfColoured(floor.pattern);
            return FloorTextures.Get(floor.pattern);
        }

        /// <summary>
        /// Furniture that blocks the vacuum is solid with a collider. Furniture the vacuum passes under is
        /// only a rim with no collider, so the floor and its coverage stay visible beneath it.
        /// </summary>
        void BuildObstacles()
        {
            Transform colliderRoot = null;

            foreach (var obstacle in level.Obstacles)
            {
                if (obstacle == null) continue;

                var corners = obstacle.Corners();
                var vertices = new List<Vector3>();
                var triangles = new List<int>();

                if (obstacle.blocksVacuum)
                {
                    AddQuad(vertices, triangles, corners[0], corners[1], corners[2], corners[3], obstacleDepth);
                }
                else
                {
                    float rim = Mathf.Min(PassableRimWidth, Mathf.Min(obstacle.size.x, obstacle.size.y) * 0.25f);
                    var inner = LevelData.RectangleOutline(obstacle.center, obstacle.size - Vector2.one * (rim * 2f), obstacle.rotation);
                    for (int c = 0; c < 4; c++)
                    {
                        int next = (c + 1) % 4;
                        AddQuad(vertices, triangles, corners[c], corners[next], inner[next], inner[c], obstacleDepth);
                    }
                }

                var mesh = new Mesh { name = $"Obstacle_{obstacle.name}" };
                mesh.SetVertices(vertices);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                if (drawMeshes) CreateMeshObject($"Obstacle_{obstacle.name}", mesh, Obstacle.ColorOf(obstacle.kind));

                if (!obstacle.blocksVacuum) continue;

                if (colliderRoot == null)
                {
                    colliderRoot = new GameObject("ObstacleColliders").transform;
                    colliderRoot.SetParent(generatedRoot, false);
                }

                var body = new GameObject($"Obstacle_{obstacle.name}");
                body.transform.SetParent(colliderRoot, false);
                body.transform.localPosition = obstacle.center;
                body.transform.localRotation = Quaternion.Euler(0f, 0f, obstacle.rotation);
                body.AddComponent<BoxCollider2D>().size = obstacle.size;
            }
        }

        static void AddQuad(List<Vector3> vertices, List<int> triangles, Vector2 a, Vector2 b, Vector2 c, Vector2 d, float depth)
        {
            int start = vertices.Count;
            vertices.Add(new Vector3(a.x, a.y, depth));
            vertices.Add(new Vector3(b.x, b.y, depth));
            vertices.Add(new Vector3(c.x, c.y, depth));
            vertices.Add(new Vector3(d.x, d.y, depth));

            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }

        void BuildWalls()
        {
            var segments = LevelGeometry.CollectWallSegments(level);
            if (segments.Count == 0) return;

            var vertices = new List<Vector3>(segments.Count * 4);
            var triangles = new List<int>(segments.Count * 6);
            var uvs = new List<Vector2>(segments.Count * 4);

            float half = level.WallThickness * 0.5f;
            var colliderRoot = new GameObject("WallColliders");
            colliderRoot.transform.SetParent(generatedRoot, false);

            foreach (var segment in segments)
            {
                Vector2 direction = segment.b - segment.a;
                float length = direction.magnitude;
                if (length < 1e-4f) continue;

                Vector2 normal = new Vector2(-direction.y, direction.x).normalized * half;
                int baseIndex = vertices.Count;

                vertices.Add(new Vector3(segment.a.x + normal.x, segment.a.y + normal.y, wallDepth));
                vertices.Add(new Vector3(segment.b.x + normal.x, segment.b.y + normal.y, wallDepth));
                vertices.Add(new Vector3(segment.b.x - normal.x, segment.b.y - normal.y, wallDepth));
                vertices.Add(new Vector3(segment.a.x - normal.x, segment.a.y - normal.y, wallDepth));

                uvs.Add(new Vector2(0f, 0f));
                uvs.Add(new Vector2(length, 0f));
                uvs.Add(new Vector2(length, 1f));
                uvs.Add(new Vector2(0f, 1f));

                triangles.Add(baseIndex);
                triangles.Add(baseIndex + 1);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 3);

                CreateWallCollider(colliderRoot.transform, segment.a, segment.b, length);
            }

            if (vertices.Count == 0) return;

            var mesh = new Mesh { name = "Walls" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            if (drawMeshes) CreateMeshObject("Walls", mesh, level.WallColor);
        }

        void CreateWallCollider(Transform parent, Vector2 a, Vector2 b, float length)
        {
            var go = new GameObject("WallSegment");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = (a + b) * 0.5f;
            go.transform.localRotation = Quaternion.Euler(
                0f, 0f, Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg);

            var collider = go.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(length, level.WallThickness);
        }

        GameObject CreateMeshObject(string name, Mesh mesh, Color tint, Texture2D texture = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(generatedRoot, false);

            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = ResolveMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", tint);
            block.SetColor("_Color", tint);

            if (texture != null)
            {
                block.SetTexture("_BaseMap", texture);
                block.SetTexture("_MainTex", texture);
            }

            renderer.SetPropertyBlock(block);

            return go;
        }

        Material ResolveMaterial()
        {
            if (materialOverride != null) return materialOverride;

            if (generatedMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit")
                             ?? Shader.Find("Unlit/Color")
                             ?? Shader.Find("Sprites/Default");

                generatedMaterial = new Material(shader)
                {
                    name = "Level2D (generated)",
                    hideFlags = HideFlags.HideAndDontSave,
                };

                // Winding varies with room orientation, so never cull.
                if (generatedMaterial.HasProperty("_Cull"))
                    generatedMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            }

            return generatedMaterial;
        }

        void OnDrawGizmosSelected()
        {
            if (level == null) return;

            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.2f, 1f, 0.45f, 0.9f);
            Gizmos.DrawWireSphere(new Vector3(level.RobotSpawn.x, level.RobotSpawn.y, 0f), 0.18f);
        }
    }
}
