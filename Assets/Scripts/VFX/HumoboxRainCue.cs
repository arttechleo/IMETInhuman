using ImetInHuman.XR;
using UnityEngine;
using UnityEngine.Playables;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Brings the Humobox in partway through the rain effect: once the rain has
    /// played for <see cref="cueSecond"/> seconds, the pivot is placed in front of
    /// the viewer, shown, and its take is played from the start.
    ///
    /// With a table found by <see cref="HumoboxTableAnchor"/> the box stands on it,
    /// anchored, sized to the tabletop. Without one -- no Space Setup, or in the
    /// Editor -- it floats in front of the viewer, pushed back until all of it is
    /// in view.
    ///
    /// Follows the rain rather than a clock of its own, so the rain test hotkey
    /// and the full sequence both bring it in at the same moment. Restarting or
    /// stopping the rain early hides it again. When the rain simply finishes, the
    /// take keeps playing to its end -- it runs longer than the rain does.
    ///
    /// Built by IMETINHUMAN > Humobox > Add Humobox To Rain (Take 4).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HumoboxRainCue : MonoBehaviour
    {
        /// <summary>True while the Humobox is up and talking; other sound makes room.</summary>
        public static bool Speaking { get; private set; }

        [Tooltip("Found in the scene at Start when empty -- the bootstrap adds it at runtime.")]
        [SerializeField] RainWindowOverlayController rain;

        [Tooltip("Second of the rain effect at which the Humobox appears.")]
        [SerializeField] float cueSecond = 15f;

        [Tooltip("Hidden until the cue. Carries HumoboxHead and the director.")]
        [SerializeField] GameObject pivot;
        [SerializeField] PlayableDirector director;

        [Header("Size")]
        [Tooltip("Widest the box may be across, in metres, spin included. The model " +
                 "is exported around 3 m, so it is always scaled down to this.")]
        [SerializeField] float boxSize = 0.45f;
        [Tooltip("Share of the table's shorter side the box may cover.")]
        [SerializeField, Range(0.2f, 1f)] float tableCoverage = 0.7f;

        [Header("Table")]
        [Tooltip("Stands the box on a real table when one is found. Optional.")]
        [SerializeField] HumoboxTableAnchor table;

        [Header("Fallback: in the air")]
        [Tooltip("Defaults to Camera.main (the headset).")]
        [SerializeField] Transform viewer;
        [Tooltip("Minimum metres from the viewer to the centre of the box, beyond the rain pane.")]
        [SerializeField] float distance = 1.2f;
        [Tooltip("Pushes the box back until all of it is in view, spun or stretched included.")]
        [SerializeField] bool fitInView = true;
        [Tooltip("Widest angle, in degrees, the box may take up. Kept under the headset's " +
                 "field of view so it sits in the middle of the view, not at its edges.")]
        [SerializeField, Range(10f, 110f)] float maxViewAngle = 60f;
        [Tooltip("Head room around the box. Covers the stretch and slide HumoboxHead adds.")]
        [SerializeField, Range(1f, 2f)] float fitMargin = 1.15f;
        [Tooltip("Metres relative to the viewer's eye height.")]
        [SerializeField] float heightOffset = -0.1f;
        [Tooltip("Extra turn on the box, in case the model's front is not its +Z.")]
        [SerializeField] float yawOffset;

        /// <summary>Brings the Humobox in at this second of the rain.</summary>
        public void SetCue(float second) => cueSecond = Mathf.Max(0f, second);

        /// <summary>How long the take runs, in seconds.</summary>
        public float TakeLength => director != null ? (float)director.duration : 0f;

        /// <summary>Second of the rain at which the take has played to its end.</summary>
        public float TakeEndsAtRainSecond => cueSecond + (director != null ? (float)director.duration : 0f);

        bool shown;
        bool measured;
        Vector3 localCentre;  // centre of the model in pivot space, at scale 1
        float radius;         // bounding sphere, which holds the model at any spin
        float footRadius;     // bounding circle seen from above, its footprint at any spin
        float halfHeight;
        float lastRainTime = float.NegativeInfinity;

        void Awake()
        {
            if (pivot != null)
                pivot.SetActive(false);
        }

        System.Collections.IEnumerator Start()
        {
            if (rain == null)
                rain = FindFirstObjectByType<RainWindowOverlayController>();
            if (rain == null)
                Debug.LogWarning("Humobox cue found no rain effect to follow.", this);

            // Warm up: its first draw builds the model's GPU pipelines, which
            // on the headset is a visible stall mid-rain. Draw it for a couple of
            // frames now, a millimetre across in front of the viewer, unseen.
            var eye = viewer != null ? viewer : Camera.main != null ? Camera.main.transform : null;
            if (pivot == null || eye == null || shown)
                yield break;

            pivot.transform.SetPositionAndRotation(eye.position + eye.forward, Quaternion.identity);
            pivot.transform.localScale = Vector3.one * 0.001f;
            pivot.SetActive(true);
            yield return null;
            yield return null;
            if (!shown)
                pivot.SetActive(false);
            pivot.transform.localScale = Vector3.one;
        }

        void OnDestroy() => Speaking = false;

        void Update()
        {
            if (shown && director != null && director.state != PlayState.Playing && director.time >= director.duration - 0.05)
                Speaking = false;

            if (rain == null)
                return;

            var t = rain.PlaybackTime;

            if (float.IsNegativeInfinity(t))
            {
                // Gone before its end means stopped, not finished.
                if (shown && !float.IsNegativeInfinity(lastRainTime) && lastRainTime < rain.Duration - 0.5f)
                    Hide();
            }
            else if (t < cueSecond)
            {
                // Rain time only runs forward, so being back before the cue means
                // the rain was restarted.
                if (shown)
                    Hide();
            }
            else if (!shown)
            {
                Show();
            }

            lastRainTime = t;
        }

        void Show()
        {
            shown = true;
            Speaking = true;
            if (pivot == null)
                return;

            // Active first: HumoboxHead builds its pivot chain in Awake, and
            // renderer bounds are only valid on active objects.
            pivot.SetActive(true);

            if (!measured)
                Measure();

            var eye = viewer != null ? viewer : Camera.main != null ? Camera.main.transform : null;
            if (table != null && table.TryGetTable(out var tableAnchor, out var shortSide))
                PlaceOnTable(tableAnchor, shortSide, eye);
            else if (eye != null)
                PlaceInAir(eye);

            if (director != null)
            {
                director.time = 0;
                director.Play();
            }

            // Into the rain's drops and fog, which otherwise see only the real room.
            Mirror.Show(pivot.transform);
        }

        HumoboxMirror mirror;
        HumoboxMirror Mirror
        {
            get
            {
                if (mirror == null)
                    mirror = GetComponent<HumoboxMirror>();
                if (mirror == null)
                    mirror = gameObject.AddComponent<HumoboxMirror>();
                return mirror;
            }
        }

        void PlaceOnTable(Transform tableAnchor, float shortSide, Transform eye)
        {
            var size = Mathf.Min(boxSize, shortSide * tableCoverage);
            var scale = footRadius > 0f ? size / (2f * footRadius) : 1f;
            pivot.transform.localScale = Vector3.one * scale;

            // Anchored, so the box stays on the table as tracking refines.
            transform.SetParent(tableAnchor, true);

            var facing = eye != null ? FlatDirection(tableAnchor.position - eye.position) : tableAnchor.forward;
            pivot.transform.rotation = Quaternion.LookRotation(-facing, Vector3.up) * Quaternion.Euler(0f, yawOffset, 0f);

            var centre = tableAnchor.position + Vector3.up * (halfHeight * scale);
            pivot.transform.position = centre - pivot.transform.TransformVector(localCentre);
        }

        void PlaceInAir(Transform eye)
        {
            var scale = footRadius > 0f ? boxSize / (2f * footRadius) : 1f;
            pivot.transform.localScale = Vector3.one * scale;

            var forward = FlatDirection(eye.forward);

            var d = distance;
            if (fitInView && radius > 0f)
            {
                var cam = eye.GetComponent<Camera>();
                var vertical = cam != null ? cam.fieldOfView : maxViewAngle;
                var horizontal = cam != null ? Camera.VerticalToHorizontalFieldOfView(vertical, cam.aspect) : maxViewAngle;
                var angle = Mathf.Min(maxViewAngle, vertical, horizontal);
                // A sphere of radius r fills a cone of half-angle asin(r / d).
                d = Mathf.Max(d, radius * scale * fitMargin / Mathf.Sin(angle * 0.5f * Mathf.Deg2Rad));
            }

            var centre = eye.position + forward * d + Vector3.up * heightOffset;
            pivot.transform.rotation = Quaternion.LookRotation(-forward, Vector3.up) * Quaternion.Euler(0f, yawOffset, 0f);
            pivot.transform.position = centre - pivot.transform.TransformVector(localCentre);
        }

        static Vector3 FlatDirection(Vector3 v)
        {
            v = Vector3.ProjectOnPlane(v, Vector3.up);
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        // Once, at scale 1, on the first show: bounds are only valid on active
        // renderers, and HumoboxHead has built its pivot chain by then.
        void Measure()
        {
            pivot.transform.localScale = Vector3.one;

            var renderers = pivot.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return;

            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);

            localCentre = pivot.transform.InverseTransformVector(bounds.center - pivot.transform.position);
            radius = bounds.extents.magnitude;
            footRadius = new Vector2(bounds.extents.x, bounds.extents.z).magnitude;
            halfHeight = bounds.extents.y;
            measured = true;
        }

        /// <summary>Hides the Humobox and stops its take now.</summary>
        public void EndNow()
        {
            if (shown)
                Hide();
        }

        void Hide()
        {
            shown = false;
            Speaking = false;
            if (mirror != null)
                mirror.Show(null);
            if (director != null)
                director.Stop();
            if (pivot != null)
                pivot.SetActive(false);
        }
    }
}
