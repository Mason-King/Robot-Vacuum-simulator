using System.Collections.Generic;
using UnityEngine;

namespace RobotVacuum.Sim
{
    /// <summary>
    /// Steps a level's placed cats in a fixed order, once per physics step. The cats share the run's random
    /// stream, so stepping them in the same order every run is what keeps a seed's cats the same.
    /// </summary>
    public sealed class CatHerd : MonoBehaviour
    {
        readonly List<MovingCat> cats = new List<MovingCat>();

        public IReadOnlyList<MovingCat> Cats => cats;

        public void Add(MovingCat cat) => cats.Add(cat);

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            foreach (var cat in cats)
                if (cat != null) cat.Step(dt);
        }
    }
}
