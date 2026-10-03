using UnityEngine;
using UnityEngine.InputSystem;

namespace ImetInHuman.XR
{
    public sealed class XRHandInputPose : MonoBehaviour
    {
        public enum Handedness
        {
            Left,
            Right
        }

        [SerializeField] Handedness hand = Handedness.Left;
        [SerializeField] bool useMouseKeyboardFallback = true;
        [SerializeField] Vector3 fallbackOffset = new(0.25f, -0.25f, 0.65f);

        InputAction positionAction;
        InputAction rotationAction;
        InputAction trackedAction;

        void OnEnable()
        {
            var handTag = hand == Handedness.Left ? "{LeftHand}" : "{RightHand}";
            positionAction = new InputAction($"{hand} Hand Position", binding: $"<XRHandDevice>{handTag}/devicePosition");
            rotationAction = new InputAction($"{hand} Hand Rotation", binding: $"<XRHandDevice>{handTag}/deviceRotation");
            trackedAction = new InputAction($"{hand} Hand Tracked", binding: $"<XRHandDevice>{handTag}/isTracked");
            positionAction.Enable();
            rotationAction.Enable();
            trackedAction.Enable();
        }

        void OnDisable()
        {
            positionAction?.Dispose();
            rotationAction?.Dispose();
            trackedAction?.Dispose();
            positionAction = null;
            rotationAction = null;
            trackedAction = null;
        }

        void Update()
        {
            // A hand device stays bound after the hand is lost; its pose then
            // reads stale or zero. Hold the last pose until it is tracked again.
            // (No isTracked control bound: trust the pose, as before.)
            if (positionAction != null && positionAction.controls.Count > 0
                && trackedAction != null && trackedAction.controls.Count > 0
                && trackedAction.ReadValue<float>() < 0.5f)
                return;

            var trackedPosition = positionAction != null && positionAction.controls.Count > 0;
            var trackedRotation = rotationAction != null && rotationAction.controls.Count > 0;

            if (trackedPosition)
                transform.localPosition = positionAction.ReadValue<Vector3>();
            else if (useMouseKeyboardFallback)
                transform.localPosition = GetFallbackPosition();

            if (trackedRotation)
                transform.localRotation = rotationAction.ReadValue<Quaternion>();
            else if (useMouseKeyboardFallback)
                transform.localRotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        }

        Vector3 GetFallbackPosition()
        {
            var side = hand == Handedness.Left ? -1f : 1f;
            var offset = fallbackOffset;
            offset.x = Mathf.Abs(offset.x) * side;

            if (Mouse.current == null)
                return offset;

            var mouse = Mouse.current.position.ReadValue();
            var normalized = new Vector2(
                Mathf.InverseLerp(0f, Screen.width, mouse.x) - 0.5f,
                Mathf.InverseLerp(0f, Screen.height, mouse.y) - 0.5f);

            offset.x += normalized.x * 0.4f;
            offset.y += normalized.y * 0.3f;
            return offset;
        }
    }
}
