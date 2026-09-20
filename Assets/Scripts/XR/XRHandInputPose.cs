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

        void OnEnable()
        {
            var handTag = hand == Handedness.Left ? "{LeftHand}" : "{RightHand}";
            positionAction = new InputAction($"{hand} Hand Position", binding: $"<XRHandDevice>{handTag}/devicePosition");
            rotationAction = new InputAction($"{hand} Hand Rotation", binding: $"<XRHandDevice>{handTag}/deviceRotation");
            positionAction.Enable();
            rotationAction.Enable();
        }

        void OnDisable()
        {
            positionAction?.Dispose();
            rotationAction?.Dispose();
            positionAction = null;
            rotationAction = null;
        }

        void Update()
        {
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
