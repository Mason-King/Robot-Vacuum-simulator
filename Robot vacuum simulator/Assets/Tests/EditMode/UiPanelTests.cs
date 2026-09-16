using NUnit.Framework;
using RobotVacuum.LevelEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace RobotVacuum.Tests
{
    public class UiPanelTests
    {
        GameObject owner;
        PanelSettings settings;

        [SetUp]
        public void SetUp() => owner = new GameObject("Panel owner");

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(owner);
            if (settings != null) Object.DestroyImmediate(settings);
        }

        [Test]
        public void Panels_ScaleWithTheWindow_NotWithTheScreensDpi()
        {
            UiPanel.Create(owner.transform, "Test UI", 1f, out settings);

            Assert.AreEqual(PanelScaleMode.ScaleWithScreenSize, settings.scaleMode,
                "a physical-size panel stays the same size on every screen, which is the bug this guards");
            Assert.AreEqual(UiPanel.ReferenceResolution, settings.referenceResolution);
            Assert.AreEqual(PanelScreenMatchMode.MatchWidthOrHeight, settings.screenMatchMode);
            Assert.AreEqual(0.5f, settings.match, 1e-4f);
        }

        [Test]
        public void Create_BuildsAStyledRootUnderTheOwner()
        {
            var root = UiPanel.Create(owner.transform, "Test UI", 1.25f, out settings);

            Assert.IsNotNull(root);
            Assert.AreEqual(1.25f, settings.scale, 1e-4f, "the uiScale setting still multiplies the result");
            Assert.AreEqual(1, owner.transform.childCount);
            Assert.IsNotNull(owner.GetComponentInChildren<UIDocument>());
        }
    }
}
