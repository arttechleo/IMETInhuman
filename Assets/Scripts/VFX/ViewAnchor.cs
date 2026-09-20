using UnityEngine;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Keeps something in view without gluing it to the head.
    ///
    /// It is placed where the viewer is looking and then stays still in the room
    /// -- small head movements look around it, not drag it. Only when it has
    /// drifted too far out of view does it glide back to the middle of the view,
    /// then settle and stay still again. The usual "lazy follow" for MR content
    /// that must never be lost but must not ride on the head either.
    /// </summary>
    public struct ViewAnchor
    {
        public Pose Pose;
        bool gliding;

        /// <summary>
        /// Upright, <paramref name="distance"/> metres along the gaze -- up/down
        /// included, so it sits in the middle of the view -- and facing the viewer.
        /// </summary>
        public static Pose InView(Transform eye, float distance)
        {
            var heading = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            if (heading.sqrMagnitude < 1e-6f)
                heading = Vector3.ProjectOnPlane(eye.up, Vector3.up);
            return new Pose(eye.position + eye.forward * distance,
                            Quaternion.LookRotation(heading.normalized, Vector3.up));
        }

        public void PlaceInView(Transform eye, float distance)
        {
            Pose = InView(eye, distance);
            gliding = false;
        }

        /// <summary>
        /// Once more than <paramref name="maxAngle"/> degrees out of the middle of
        /// the view, glides back to it over about <paramref name="glideSeconds"/>;
        /// otherwise stays exactly where it is.
        /// </summary>
        public void Follow(Transform eye, float distance, float maxAngle, float glideSeconds)
        {
            var toThing = Pose.position - eye.position;
            if (toThing.sqrMagnitude < 1e-6f)
                return;

            var off = Vector3.Angle(eye.forward, toThing);
            if (!gliding && off > maxAngle)
                gliding = true;
            if (!gliding)
                return;

            var target = InView(eye, distance);
            var t = 1f - Mathf.Exp(-4f * Time.deltaTime / Mathf.Max(glideSeconds, 0.01f));
            Pose = new Pose(Vector3.Lerp(Pose.position, target.position, t),
                            Quaternion.Slerp(Pose.rotation, target.rotation, t));
            if (off < 4f)
                gliding = false;
        }
    }
}
