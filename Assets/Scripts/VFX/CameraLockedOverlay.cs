using UnityEngine;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Holds an effect's quad in front of the viewer and fades it in and out on a
    /// schedule. Shared by every full-view effect; the effects themselves own
    /// nothing about placement or timing.
    ///
    /// Head-anchored, the quad is re-fitted to the view every frame and moves
    /// with the head. World-anchored, it is placed once when it becomes visible --
    /// upright, at eye height, facing the viewer -- and then stays in the room, so
    /// turning the head looks across it the way it would across a real object.
    /// Booth: the quad becomes a glass cylinder standing on the floor around the
    /// viewer, placed once when it appears. It never turns with the head, and
    /// stays where it was put unless told to follow.
    /// Handheld: a phone-sized screen held at reading distance below the eyes,
    /// always tilted to face them, the way anyone holds a phone to scroll. Its
    /// position stays in the room and follows only when the viewer turns away.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer))]
    public sealed class CameraLockedOverlay : MonoBehaviour
    {
        public enum Anchoring { Head, World, Booth, Handheld }

        [SerializeField] Camera targetCamera;
        [SerializeField] Material sourceMaterial;
        [SerializeField] float startDelay;
        [SerializeField] float duration = 30f;
        [SerializeField] float fadeIn = 1.25f;
        [SerializeField] float fadeOut = 3f;
        [SerializeField] float distance = 0.35f;
        [SerializeField, Range(1f, 1.6f)] float overscan = 1.12f;
        [SerializeField] bool deactivateWhenFinished = true;

        [Header("Placement")]
        [SerializeField] Anchoring anchoring = Anchoring.Head;
        [Tooltip("Size as a share of the view at the moment it is placed. 1 fills it.")]
        [SerializeField, Range(0.1f, 1.5f)] float size = 1f;
        [Tooltip("World-anchored only: once the viewer has turned this many degrees away, " +
                 "glide back in front of them. 0 never follows.")]
        [SerializeField, Range(0f, 180f)] float followAngle;
        [Tooltip("Seconds the glide back takes to mostly settle.")]
        [SerializeField, Range(0.1f, 3f)] float followTime = 0.8f;

        [Tooltip("Booth only: slide after the viewer as they walk. Off, the booth stays where " +
                 "it was placed and the viewer can walk about inside it.")]
        [SerializeField] bool boothFollows;
        [Tooltip("Booth only: seconds the booth takes to catch up as the viewer walks.")]
        [SerializeField, Range(0.05f, 5f)] float boothFollowTime = 1.2f;

        bool placed;
        bool following;

        // Frames left to draw at zero alpha while waiting to start. The headset
        // builds a shader's GPU pipeline on its first draw -- mid-effect, that is
        // a visible hitch; here it happens while nothing is on screen.
        int warmFrames = 2;
        float boothRadius = 1f;
        float boothHeight = 2.6f;
        float handDistance = 0.45f;
        float handDrop = 0.22f;
        Vector2 handSize = new Vector2(0.36f, 0.3f);

        /// <summary>
        /// Metres the effect's uv square spans on each axis: the quad's size, or
        /// for the booth, once around it by its height.
        /// </summary>
        public Vector2 PaneSize => anchoring == Anchoring.Booth
            ? new Vector2(2f * Mathf.PI * boothRadius, boothHeight)
            : new Vector2(transform.lossyScale.x, transform.lossyScale.y);

        MeshRenderer meshRenderer;
        Material runtimeMaterial;
        float elapsed;

        /// <summary>Material instance this overlay drives. Null before Configure.</summary>
        public Material RuntimeMaterial => runtimeMaterial;

        /// <summary>Normalised fade value currently applied to _Alpha.</summary>
        public float CurrentAlpha { get; private set; }

        /// <summary>Seconds since the overlay became visible; negative while it
        /// waits on its start delay.</summary>
        public float PlaybackTime => elapsed - startDelay;

        public bool IsPlaying => gameObject.activeInHierarchy && PlaybackTime >= 0f
                              && (duration <= 0f || PlaybackTime < duration);

        /// <summary>Runs the overlay again from the top: waits
        /// <paramref name="delay"/> seconds, fades in, holds, fades out.</summary>
        public void Restart(float delay)
        {
            elapsed = 0f;
            placed = false;
            startDelay = Mathf.Max(0f, delay);
            gameObject.SetActive(true);
            SetAlpha(0f);
        }

        /// <summary>Hides the overlay at once, skipping its fade out.</summary>
        public void Stop()
        {
            SetAlpha(0f);
            gameObject.SetActive(false);
        }

        public void Configure(Camera camera, Material material, float delay, float overlayDuration,
                              float planeDistance = -1f)
        {
            targetCamera = camera;
            sourceMaterial = material;
            startDelay = delay;
            duration = overlayDuration;
            if (planeDistance > 0f)
                distance = planeDistance;

            ApplyMaterial();
            SetAlpha(0f);
        }

        /// <summary>How the quad is held: see the class summary.</summary>
        public void ConfigurePlacement(Anchoring mode, float viewShare, float followAfterDegrees)
        {
            anchoring = mode;
            size = viewShare;
            followAngle = followAfterDegrees;
            placed = false;
        }

        /// <summary>
        /// Holds the overlay like a phone: <paramref name="distance"/> metres out,
        /// <paramref name="drop"/> below the eyes, <paramref name="size"/> metres
        /// wide and tall, and follows once the viewer turns past
        /// <paramref name="followAfterDegrees"/>.
        /// </summary>
        public void ConfigureHandheld(float distance, float drop, Vector2 size, float followAfterDegrees)
        {
            anchoring = Anchoring.Handheld;
            handDistance = Mathf.Max(distance, 0.1f);
            handDrop = drop;
            handSize = Vector2.Max(size, Vector2.one * 0.02f);
            followAngle = followAfterDegrees;
            placed = false;
        }

        readonly System.Collections.Generic.List<MeshRenderer> boothCaps = new();

        /// <summary>The booth's floor and ceiling, when it has them.</summary>
        public System.Collections.Generic.IReadOnlyList<MeshRenderer> BoothCaps => boothCaps;

        /// <summary>
        /// Turns the overlay into a glass booth of this size around the viewer,
        /// closed with a glass floor and ceiling when <paramref name="caps"/>.
        /// The caps draw with the same material and fade with the walls.
        /// </summary>
        public void ConfigureBooth(float radius, float height, bool caps = false)
        {
            anchoring = Anchoring.Booth;
            boothRadius = Mathf.Max(radius, 0.2f);
            boothHeight = Mathf.Max(height, 0.2f);
            placed = false;

            var filter = GetComponent<MeshFilter>();
            if (filter != null)
                filter.sharedMesh = BoothMesh.Build(boothRadius, boothHeight);
            transform.localScale = Vector3.one;

            foreach (var cap in boothCaps)
                if (cap != null)
                    Destroy(cap.gameObject);
            boothCaps.Clear();
            if (!caps)
                return;

            boothCaps.Add(AddCap("Rain Booth Floor", BoothMesh.BuildCap(boothRadius, 0f, false)));
            boothCaps.Add(AddCap("Rain Booth Ceiling", BoothMesh.BuildCap(boothRadius, boothHeight, true)));
        }

        MeshRenderer AddCap(string capName, Mesh mesh)
        {
            var cap = new GameObject(capName);
            cap.transform.SetParent(transform, false);
            cap.AddComponent<MeshFilter>().sharedMesh = mesh;
            var capRenderer = cap.AddComponent<MeshRenderer>();
            capRenderer.sharedMaterial = runtimeMaterial;
            capRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            capRenderer.receiveShadows = false;
            capRenderer.allowOcclusionWhenDynamic = false;
            capRenderer.enabled = meshRenderer != null && meshRenderer.enabled;
            return capRenderer;
        }

        void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            if (runtimeMaterial == null)
                ApplyMaterial();
            SetAlpha(0f);
        }

        void LateUpdate()
        {
            if (targetCamera == null)
                targetCamera = Camera.main;

            elapsed += Time.deltaTime;
            if (elapsed < startDelay)
            {
                SetAlpha(0f);
                if (warmFrames > 0 && meshRenderer != null)
                {
                    if (anchoring == Anchoring.Head)
                        FitToCamera();
                    meshRenderer.enabled = true;
                    foreach (var cap in boothCaps)
                        if (cap != null)
                            cap.enabled = true;
                    warmFrames--;
                }
                return;
            }

            // Placed on its first visible frame, not at spawn: the effect may
            // start long after the scene does, wherever the viewer has turned.
            if (anchoring == Anchoring.Booth)
            {
                FollowInBooth(!placed);
                placed = true;
            }
            else if (anchoring == Anchoring.Handheld)
            {
                HoldInHand(!placed);
                placed = true;
            }
            else if (anchoring == Anchoring.Head || !placed)
            {
                // Placed once only when the view is known: XR sets the eye
                // projections a frame or so after start, and a world-anchored
                // effect sized from an unset one would keep that size.
                if (FitToCamera())
                    placed = true;
            }
            else
            {
                FollowIfTurnedAway();
            }

            var playbackTime = elapsed - startDelay;
            if (duration > 0f && playbackTime >= duration)
            {
                SetAlpha(0f);
                if (deactivateWhenFinished)
                    gameObject.SetActive(false);
                return;
            }

            var inAlpha = fadeIn <= 0f ? 1f : Mathf.Clamp01(playbackTime / fadeIn);
            var outAlpha = 1f;
            if (duration > 0f && fadeOut > 0f)
            {
                var outStart = Mathf.Max(0f, duration - fadeOut);
                if (playbackTime >= outStart)
                    outAlpha = 1f - Mathf.Clamp01((playbackTime - outStart) / fadeOut);
            }

            SetAlpha(Mathf.Min(inAlpha, outAlpha));
        }

        void OnDestroy()
        {
            if (runtimeMaterial != null)
                Destroy(runtimeMaterial);
        }

        bool FitToCamera()
        {
            if (targetCamera == null)
                return false;

            if (!ViewRect(out var width, out var height, out var centerVS))
                return false;

            if (anchoring == Anchoring.Head)
            {
                transform.SetPositionAndRotation(
                    targetCamera.transform.TransformPoint(centerVS),
                    targetCamera.transform.rotation);
            }
            else
            {
                PlacementPose(centerVS, out var position, out var rotation);
                transform.SetPositionAndRotation(position, rotation);
            }
            transform.localScale = new Vector3(width, height, 1f);
            return true;
        }

        // Where the hand holds the phone for this head pose: ahead along the
        // heading, below the eyes.
        Vector3 HandPosition(Transform eye)
        {
            var forward = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.ProjectOnPlane(eye.up, Vector3.up);
            return eye.position + forward.normalized * handDistance + Vector3.down * handDrop;
        }

        void HoldInHand(bool snap)
        {
            if (targetCamera == null)
                return;

            var eye = targetCamera.transform;
            transform.localScale = new Vector3(handSize.x, handSize.y, 1f);

            if (snap)
            {
                transform.position = HandPosition(eye);
            }
            else if (followAngle > 0f)
            {
                // Carried along only once the viewer has turned away from it.
                var look = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
                var toPhone = Vector3.ProjectOnPlane(transform.position - eye.position, Vector3.up);
                if (look.sqrMagnitude > 1e-6f && toPhone.sqrMagnitude > 1e-6f)
                {
                    var off = Vector3.Angle(look, toPhone);
                    if (!following && off > followAngle)
                        following = true;
                    if (following)
                    {
                        var t = 1f - Mathf.Exp(-4f * Time.deltaTime / Mathf.Max(followTime, 0.01f));
                        transform.position = Vector3.Lerp(transform.position, HandPosition(eye), t);
                        if (off < 5f)
                            following = false;
                    }
                }
            }

            // Screen square to the eyes, top towards the head's up: tilted back
            // and turned the way a phone is angled to be read. The quad faces
            // along its +z away from the viewer, as the head-anchored one does.
            var toScreen = transform.position - eye.position;
            if (toScreen.sqrMagnitude > 1e-6f)
                transform.rotation = Quaternion.LookRotation(toScreen, eye.up);
        }

        // Stands on the floor, centred under the viewer, facing the way they did
        // when it appeared. After that it stays put, or with boothFollows slides
        // after them; it never turns.
        void FollowInBooth(bool snap)
        {
            if (targetCamera == null)
                return;

            var eye = targetCamera.transform.position;
            // The rig's root sits on the floor when tracking is floor-relative.
            var floor = targetCamera.transform.root.position.y;
            var target = new Vector3(eye.x, floor, eye.z);

            if (snap)
            {
                var forward = Vector3.ProjectOnPlane(targetCamera.transform.forward, Vector3.up);
                var heading = forward.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward, Vector3.up) : Quaternion.identity;
                transform.SetPositionAndRotation(target, heading);
                return;
            }

            if (!boothFollows)
                return;

            var t = 1f - Mathf.Exp(-3f * Time.deltaTime / Mathf.Max(boothFollowTime, 0.01f));
            transform.position = Vector3.Lerp(transform.position, target, t);
        }

        // Upright in front of the viewer at eye height: heading only, no pitch or
        // roll, as anything standing in a room is.
        void PlacementPose(Vector3 centerVS, out Vector3 position, out Quaternion rotation)
        {
            var eye = targetCamera.transform;
            var forward = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f)
                forward = Vector3.ProjectOnPlane(eye.up, Vector3.up);
            forward.Normalize();

            rotation = Quaternion.LookRotation(forward, Vector3.up);
            position = eye.position + forward * distance + rotation * new Vector3(centerVS.x, 0f, 0f);
        }

        void FollowIfTurnedAway()
        {
            if (followAngle <= 0f || targetCamera == null)
                return;

            var eye = targetCamera.transform;
            var look = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            var toQuad = Vector3.ProjectOnPlane(transform.position - eye.position, Vector3.up);
            if (look.sqrMagnitude < 1e-6f || toQuad.sqrMagnitude < 1e-6f)
                return;

            var off = Vector3.Angle(look, toQuad);
            if (!following && off > followAngle)
                following = true;
            if (!following)
                return;

            if (!ViewRect(out _, out _, out var centerVS))
                return;
            PlacementPose(centerVS, out var position, out var rotation);

            // Exponential ease: quick to start, soft to land.
            var t = 1f - Mathf.Exp(-4f * Time.deltaTime / Mathf.Max(followTime, 0.01f));
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, position, t),
                Quaternion.Slerp(transform.rotation, rotation, t));

            if (off < 2f)
                following = false;
        }

        // The view at the overlay's distance, scaled by size, in view space.
        // False while the camera's projection is not set yet.
        bool ViewRect(out float width, out float height, out Vector3 centerVS)
        {
            // fieldOfView/aspect describe the mono camera. HMD frusta are asymmetric
            // and differ per eye, so fitting from them leaves the overlay short of
            // the view edges. Take the union of both eye frusta instead.
            float left, right, bottom, top;
            if (targetCamera.stereoEnabled)
            {
                GetFrustumTangents(targetCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left),
                    out var l0, out var r0, out var b0, out var t0);
                GetFrustumTangents(targetCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right),
                    out var l1, out var r1, out var b1, out var t1);

                left = Mathf.Min(l0, l1);
                right = Mathf.Max(r0, r1);
                bottom = Mathf.Min(b0, b1);
                top = Mathf.Max(t0, t1);
            }
            else
            {
                GetFrustumTangents(targetCamera.projectionMatrix, out left, out right, out bottom, out top);
            }

            if (!(right - left > 1e-3f) || !(top - bottom > 1e-3f) || float.IsInfinity(right - left) || float.IsInfinity(top - bottom))
            {
                width = height = 0f;
                centerVS = Vector3.forward * distance;
                return false;
            }

            left *= distance;
            right *= distance;
            bottom *= distance;
            top *= distance;

            // Head-anchored, the margin covers head rotation between LateUpdate and
            // photon, so reprojection never drags an overlay edge into view.
            var margin = anchoring == Anchoring.Head ? overscan : 1f;
            width = (right - left) * margin * size;
            height = (top - bottom) * margin * size;
            centerVS = new Vector3((left + right) * 0.5f, (bottom + top) * 0.5f, distance);
            return true;
        }

        // Frustum edges as tangents (multiply by depth for metres). Unity view space
        // looks down -Z, so x_ndc = (m00 * x + m02 * z) / -z.
        static void GetFrustumTangents(Matrix4x4 p, out float left, out float right, out float bottom, out float top)
        {
            left = (p.m02 - 1f) / p.m00;
            right = (p.m02 + 1f) / p.m00;
            bottom = (p.m12 - 1f) / p.m11;
            top = (p.m12 + 1f) / p.m11;
        }

        void ApplyMaterial()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                return;

            if (runtimeMaterial != null)
                Destroy(runtimeMaterial);

            runtimeMaterial = sourceMaterial != null
                ? new Material(sourceMaterial)
                : new Material(meshRenderer.sharedMaterial);
            meshRenderer.sharedMaterial = runtimeMaterial;
        }

        void SetAlpha(float alpha)
        {
            CurrentAlpha = alpha;
            if (runtimeMaterial != null)
                runtimeMaterial.SetFloat(ShaderIds.Alpha, alpha);

            // Skip the draw entirely while faded out. These are full-view quads
            // running expensive fragment work, so a transparent one still costs a
            // full screen of shading in both eyes -- and any term the shader adds
            // outside the alpha channel would keep showing through.
            if (meshRenderer != null)
                meshRenderer.enabled = alpha > 0f;
            foreach (var cap in boothCaps)
                if (cap != null)
                    cap.enabled = alpha > 0f;
        }

        static class ShaderIds
        {
            public static readonly int Alpha = Shader.PropertyToID("_Alpha");
        }
    }
}
