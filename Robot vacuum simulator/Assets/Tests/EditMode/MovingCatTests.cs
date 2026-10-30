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
                cat.Initialize(new Rect(-2f, -2f, 4f, 4f), Vector2.zero, 1u);

                Assert.AreEqual(RigidbodyType2D.Dynamic, body.bodyType);
                Assert.IsFalse(cat.CollisionShape.isTrigger);
                Assert.AreEqual(1, cat.CollisionShape.pathCount);

                Vector2[] outline = cat.CollisionShape.GetPath(0);
                MeshFilter[] visuals = catObject.GetComponentsInChildren<MeshFilter>();
                Assert.AreEqual(3, visuals.Length);

                Mesh silhouette = null;
                foreach (var visual in visuals)
                    if (visual.sharedMesh.name == "Cat silhouette") silhouette = visual.sharedMesh;
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

        static uint[] CatSeeds(LevelRenderer renderer, Transform parent, uint masterSeed)
        {
            var cats = MovingCat.SpawnPlacedCats(renderer, parent, new RandomProvider(masterSeed));
            var seeds = new uint[cats.Count];
            for (int i = 0; i < cats.Count; i++)
            {
                seeds[i] = cats[i].Seed;
                Object.DestroyImmediate(cats[i].gameObject);
            }
            return seeds;
        }

        [Test]
        public void SpawnPlacedCats_SameMasterSeedGivesSameCatSeeds_DifferentSeedDoesNot()
        {
            LevelData level = LevelWithTwoCats();
            var levelObject = new GameObject("Cat seed level");
            try
            {
                var renderer = levelObject.AddComponent<LevelRenderer>();
                renderer.Level = level;

                uint[] first = CatSeeds(renderer, levelObject.transform, 42u);
                uint[] again = CatSeeds(renderer, levelObject.transform, 42u);
                uint[] other = CatSeeds(renderer, levelObject.transform, 43u);

                Assert.AreEqual(2, first.Length);
                Assert.AreNotEqual(first[0], first[1], "each cat gets its own seed");
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
        public void ResetToSpawn_RestoresSeedAndPlacedPosition()
        {
            LevelData level = LevelWithTwoCats();
            var levelObject = new GameObject("Cat reset level");
            try
            {
                var renderer = levelObject.AddComponent<LevelRenderer>();
                renderer.Level = level;
                var cat = MovingCat.SpawnPlacedCats(renderer, levelObject.transform, new RandomProvider(42u))[0];
                uint seed = cat.Seed;
                Vector2 spawned = cat.transform.position;
                Vector2 direction = cat.Direction;

                cat.transform.position = spawned + new Vector2(0.5f, -0.25f);
                cat.ResetToSpawn();

                Assert.AreEqual(seed, cat.Seed);
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
