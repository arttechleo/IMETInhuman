using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace ImetInHuman.XR
{
    /// <summary>
    /// Turns on fixed foveated rendering: the headset shades the edge of each eye
    /// at a lower rate, where the lenses blur it anyway. The intro effects cover
    /// the whole view, so this cuts their cost roughly in proportion to the area
    /// it coarsens. Needs the OpenXR Foveated Rendering feature on for Android.
    /// </summary>
    public sealed class HeadsetFoveation : MonoBehaviour
    {
        [Tooltip("0 off, 1 strongest. Above about 0.75 the rain's edges start to look blocky.")]
        [SerializeField, Range(0f, 1f)] float level = 0.75f;

        readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        bool applied;

        // The display subsystem only exists once XR has started, which can be a
        // few frames after this component wakes.
        void Update()
        {
            if (applied)
                return;

            SubsystemManager.GetSubsystems(displays);
            foreach (var display in displays)
            {
                display.foveatedRenderingFlags = XRDisplaySubsystem.FoveatedRenderingFlags.None;
                display.foveatedRenderingLevel = level;
                applied = true;
            }
        }
    }
}
