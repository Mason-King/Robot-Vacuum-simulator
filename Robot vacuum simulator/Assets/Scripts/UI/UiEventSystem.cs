using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RobotVacuum.UI
{
    /// <summary>Makes sure a scene has the EventSystem uGUI needs for clicks, hover and typing.</summary>
    public static class UiEventSystem
    {
        /// <summary>
        /// Adds an EventSystem when the scene has none. This project uses the Input System package only, so
        /// the module is the Input System's own, looked up by name so no package reference is needed here.
        /// </summary>
        public static void Ensure()
        {
            if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null) return;

            var go = new GameObject("EventSystem", typeof(EventSystem));
            var module = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (module != null)
            {
                var added = go.AddComponent(module);
                module.GetMethod("AssignDefaultActions")?.Invoke(added, null);
            }
            else go.AddComponent<StandaloneInputModule>();
        }
    }
}
