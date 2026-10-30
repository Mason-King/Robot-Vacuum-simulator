using NUnit.Framework;
using RobotVacuum.LevelEditor;
using RobotVacuum.Sim;

namespace RobotVacuum.Tests
{
    public class RunPreferencesTests
    {
        [TearDown]
        public void TearDown() => RunPreferences.ResetRunToGlobal();

        [Test]
        public void Run_MirrorsGlobal_UntilCustomized()
        {
            RunPreferences.ResetRunToGlobal();

            Assert.IsFalse(RunPreferences.IsRunCustomized);
            Assert.AreEqual(RunPreferences.Global.driveSpeed, RunPreferences.Run.driveSpeed);
            Assert.AreNotSame(RunPreferences.Global, RunPreferences.Run, "the run edits a copy, never the globals");
        }

        [Test]
        public void CustomizingTheRun_KeepsTheEdit_AndLeavesGlobalAlone()
        {
            RunPreferences.ResetRunToGlobal();
            float global = RunPreferences.Global.driveSpeed;

            var run = RunPreferences.Run;
            run.driveSpeed = global + 0.5f;
            RunPreferences.MarkRunCustomized();

            Assert.AreSame(run, RunPreferences.Run, "a bound form keeps editing the same object");
            Assert.AreEqual(global + 0.5f, RunPreferences.Run.driveSpeed, 1e-5f);
            Assert.AreEqual(global, RunPreferences.Global.driveSpeed, 1e-5f);
        }

        [Test]
        public void ResetRunToGlobal_DropsTheEdit_InPlace()
        {
            var run = RunPreferences.Run;
            run.pattern = RunPreferences.Global.pattern == MovementPattern.Spiral ? MovementPattern.Lawnmower : MovementPattern.Spiral;
            RunPreferences.MarkRunCustomized();

            RunPreferences.ResetRunToGlobal();

            Assert.IsFalse(RunPreferences.IsRunCustomized);
            Assert.AreSame(run, RunPreferences.Run);
            Assert.AreEqual(RunPreferences.Global.pattern, run.pattern);
        }

        [Test]
        public void Copy_IsIndependent()
        {
            var original = RunPreferences.Defaults();
            var copy = original.Copy();
            copy.batteryCapacity = 999f;

            Assert.AreEqual(300f, original.batteryCapacity, 1e-5f);
        }
    }
}
