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
    public class LevelRenderer : MonoBehaviour
    {
        const string GeneratedRootName = "Generated";

        [SerializeField] LevelData level;
        [SerializeField] bool rebuildOnEnable = true;
        [SerializeField] bool buildWalls = true;

        [Tooltip("Z depth for room floors. Walls are drawn slightly in front.")]
        [SerializeField] float floorDepth = 0f;
        [SerializeField] float wallDepth = -0.05f;

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
                float tile = floorType != null ? Mathf.Max(0.01f, floorType.textureScale) : 1f;

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

                CreateMeshObject($"Floor_{room.name}", mesh, level.ColorOf(room),
                    floorType != null ? floorType.texture : null);
            }
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

            CreateMeshObject("Walls", mesh, level.WallColor);
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
