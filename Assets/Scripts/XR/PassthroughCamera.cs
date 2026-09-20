using UnityEngine;

namespace ImetInHuman.XR
{
    /// <summary>
    /// Makes the camera clear to transparent black on the headset, so the
    /// passthrough composited behind the app shows wherever nothing is drawn.
    /// HDR goes off because the HDR colour buffer has no alpha channel, and
    /// without alpha the runtime cannot tell app pixels from see-through ones.
    /// Leaves the camera alone in the Editor and on PC.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class PassthroughCamera : MonoBehaviour
    {
        void Awake()
        {
            if (!MixedReality.PassthroughActive)
                return;

            var cam = GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear;
            cam.allowHDR = false;
        }
    }
}
