using NUnit.Framework;
using RobotVacuum.Level;
using RobotVacuum.Sim;
using UnityEngine;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class MovingCatTests
    {
        [Test]
        public void CatInvestigatesMovingVacuumAndStartlesWhenItGetsClose()
        {
            Assert.AreEqual(MovingCat.Behavior.Startled, MovingCat.ChooseBehavior(0.3f, 0.5f));
            Assert.AreEqual(MovingCat.Behavior.Investigating, MovingCat.ChooseBehavior(1.2f, 0.5f));
            Assert.AreEqual(MovingCat.Behavior.Roaming, MovingCat.ChooseBehavior(1.2f, 0f));
            Assert.AreEqual(MovingCat.Behavior.Roaming, MovingCat.ChooseBehavior(2f, 0.5f));
        }

        [Test]
        public void ReflectDirectionTurnsAwayFromContactSurface()
        {
            Vector2 reflected = MovingCat.ReflectDirection(Vector2.right, Vector2.left);

            Assert.Less(reflected.x, 0f);
            Assert.AreEqual(1f, reflected.magnitude, 1e-5f);
        }

        [Test]
        public void CatUsesASolidColliderMatchingItsDetailedVisual()
        {
            var catObject = new GameObject("Cat test actor");
            try
            {
                var cat = catObject.AddComponent<MovingCat>();
                var body = catObject.GetComponent<Rigidbody2D>();
                cat.Initialize(new Rect(-2f, -2f, 4f, 4f), Vector2.zero, new RandomProvider(1u));

                Assert.AreEqual(RigidbodyType2D.Dynamic, body.bodyType);
                Assert.IsFalse(cat.CollisionShape.isTrigger);
                Assert.AreEqual(1, cat.CollisionShape.pathCount);

                Vector2[] outline = cat.CollisionShape.GetPath(0);
                MeshFilter[] visuals = catObject.GetComponentsInChildren<MeshFilter>();
                Assert.AreEqual(CatShape.Layers.Count, visuals.Length);

                Mesh silhouette = null;
                foreach (var visual in visuals)
                    if (visual.sharedMesh.name == CatShape.SilhouetteLayerName) silhouette = visual.sharedMesh;
                Assert.IsNotNull(silhouette);
                Assert.AreEqual(outline.Length, silhouette.vertexCount);

                Vector3[] silhouetteVertices = silhouette.vertices;
                for (int i = 0; i < outline.Length; i++)
                    Assert.AreEqual(outline[i], new Vector2(silhouetteVertices[i].x, silhouetteVertices[i].y));

                foreach (var visual in visuals)
                {
                    if (visual.sharedMesh == silhouette) continue;
                    foreach (Vector3 vertex in visual.sharedMesh.vertices)
                        Assert.IsTrue(Poly2D.ContainsPoint(outline, new Vector2(vertex.x, vertex.y)),
                            $"Visual detail at {vertex} must remain inside the collider silhouette.");
                }
            }
            finally
            {
                Object.DestroyImmediate(catObject);
            }
        }

        [Test]
        public void SpawnPlacedCats_CreatesOnlyAuthoredCatsAtTheirSavedPositions()
        {
            LevelData level = TestLevels.NewLevel(SampleLevels.PopulateBlank);
            var levelObject = new GameObject("Cat spawn level");
            try
            {
                level.Obstacles.Add(new Obstacle
                {
                    name = "Sofa",
                    kind = ObstacleKind.Sofa,
                    center = new Vector2(-0.8f, 0.4f),
                    size = Obstacle.DefaultSize(ObstacleKind.Sofa),
                    blocksVacuum = true,
                });

                var renderer = levelObject.AddComponent<LevelRenderer>();
                renderer.Level = level;

                Assert.IsEmpty(MovingCat.SpawnPlacedCats(renderer, levelObject.transform, new RandomProvider(1u)));
                level.Obstacles.Add(new Obstacle
                {
                    name = "Miso",
                    kind = ObstacleKind.Cat,
                    center = new Vector2(0.8f, 0.4f),
                    size = Obstacle.DefaultSize(ObstacleKind.Cat),
                    blocksVacuum = false,
                });
                var cats = MovingCat.SpawnPlacedCats(renderer, levelObject.transform, new RandomProvider(1u));

                Assert.AreEqual(1, cats.Count);
                Assert.AreEqual("Miso", cats[0].name);
                Assert.AreEqual((Vector2)renderer.LevelToWorld(level.Obstacles[1].center), (Vector2)cats[0].transform.position);
            }
            finally
            {
                Object.DestroyImmediate(levelObject);
                Object.DestroyImmediate(level);
            }
        }

        static LevelData LevelWithTwoCats()
        {
            LevelData level = TestLevels.NewLevel(SampleLevels.PopulateBlank);
            foreach (var (name, center) in new[] { ("Miso", new Vector2(0.8f, 0.4f)), ("Tofu", new Vector2(-0.6f, -0.3f)) })
            {
                level.Obstacles.Add(new Obstacle
                {
                    name = name,
                    kind = ObstacleKind.Cat,
                    center = center,
                    size = Obstacle.DefaultSize(ObstacleKind.Cat),
                    blocksVacuum = false,
                });
            }
            return level;
        }

        static Vector2[] CatDirections(LevelRenderer renderer, Transform parent, RandomProvider randomSource)
        {
            var cats = MovingCat.SpawnPlacedCats(renderer, parent, randomSource);
            var directions = new Vector2[cats.Count];
            for (int i = 0; i < cats.Count; i++)
            {
                directions[i] = cats[i].Direction;
                Object.DestroyImmediate(cats[i].gameObject);
            }
            return directions;
        }

        [Test]
        public void SpawnPlacedCats_SameMasterSeedGivesSameCats_DifferentSeedDoesNot()
        {
            LevelData level = LevelWithTwoCats();
            var levelObject = new GameObject("Cat seed level");
            try
            {
                var renderer = levelObject.AddComponent<LevelRenderer>();
                renderer.Level = level;

                Vector2[] first = CatDirections(renderer, levelObject.transform, new RandomProvider(42u));
                Vector2[] again = CatDirections(renderer, levelObject.transform, new RandomProvider(42u));
                Vector2[] other = CatDirections(renderer, levelObject.transform, new RandomProvider(43u));

                Assert.AreEqual(2, first.Length);
                Assert.AreNotEqual(first[0], first[1], "each cat takes its own draws");
                CollectionAssert.AreEqual(first, again);
                CollectionAssert.AreNotEqual(first, other);
            }
            finally
            {
                Object.DestroyImmediate(levelObject);
                Object.DestroyImmediate(level);
            }
        }

        [Test]
        public void Cats_DrawFromTheSharedProvider_SoItsSnapshotCoversThem()
        {
            LevelData level = LevelWithTwoCats();
            var levelObject = new GameObject("Cat snapshot level");
            try
            {
                var renderer = levelObject.AddComponent<LevelRenderer>();
                renderer.Level = level;
                var source = new RandomProvider(42u);
                var before = source.SnapshotState();

                var cats = MovingCat.SpawnPlacedCats(renderer, levelObject.transform, source);
                Vector2 direction = cats[0].Direction;
                Assert.AreNotEqual(before[RandomProvider.Subsystem.DynamicObstacles], source.SnapshotState()[RandomProvider.Subsystem.DynamicObstacles],
                    "spawning the cats advances the shared stream");

                source.RestoreState(before);
                cats[0].ResetToSpawn(source);
                Assert.AreEqual(direction, cats[0].Direction, "restoring the provider replays the cat");

                Assert.AreEqual(before[RandomProvider.Subsystem.Odometry], source.SnapshotState()[RandomProvider.Subsystem.Odometry],
                    "the cats leave the other streams alone");
            }
            finally
            {
                Object.DestroyImmediate(levelObject);
                Object.DestroyImmediate(level);
            }
        }

        [Test]
        public void SpawnPlacedCats_PutsThemInOneHerd_InLevelOrder()
        {
            LevelData level = LevelWithTwoCats();
            var levelObject = new GameObject("Cat herd level");
            try
            {
                var renderer = levelObject.AddComponent<LevelRenderer>();
                renderer.Level = level;
                var cats = MovingCat.SpawnPlacedCats(renderer, levelObject.transform, new RandomProvider(1u));

                var herd = levelObject.GetComponentInChildren<CatHerd>();
                Assert.IsNotNull(herd);
                CollectionAssert.AreEqual(cats, herd.Cats);
                Assert.AreEqual("Miso", herd.Cats[0].name);
                Assert.AreEqual("Tofu", herd.Cats[1].name);
            }
            finally
            {
                Object.DestroyImmediate(levelObject);
                Object.DestroyImmediate(level);
            }
        }

        [Test]
        public void ResetToSpawn_RestoresPlacedPosition_AndReplaysFromARebuiltProvider()
        {
            LevelData level = LevelWithTwoCats();
            var levelObject = new GameObject("Cat reset level");
            try
            {
                var renderer = levelObject.AddComponent<LevelRenderer>();
                renderer.Level = level;
                var cat = MovingCat.SpawnPlacedCats(renderer, levelObject.transform, new RandomProvider(42u))[0];
                Vector2 spawned = cat.transform.position;
                Vector2 direction = cat.Direction;

                cat.transform.position = spawned + new Vector2(0.5f, -0.25f);
                cat.ResetToSpawn(new RandomProvider(42u)); // as a restart does: the robot rebuilds the provider from the seed

                Assert.AreEqual(spawned, (Vector2)cat.transform.position);
                Assert.AreEqual(direction, cat.Direction, "the random sequence starts over");
            }
            finally
            {
                Object.DestroyImmediate(levelObject);
                Object.DestroyImmediate(level);
            }
        }
    }
}
