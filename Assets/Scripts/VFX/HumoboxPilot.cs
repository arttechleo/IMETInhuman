using ImetInHuman.XR;
using UnityEngine;
using UnityEngine.Playables;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Intro: Pilot. The Humobox drops in from above and lands in front of the
    /// viewer, then keeps them company for its take: it stays where it is in
    /// the room while they look around, and only when they turn well away or
    /// walk off does it glide, unhurried, to a new spot in front of them. It is
    /// never locked to the head -- it lives in the room and catches up.
    ///
    /// The root carries the placement (landing, gliding, hovering); the pivot
    /// under it is the one <see cref="HumoboxHead"/> and the take's director
    /// sit on, exactly as for the rain cue.
    ///
    /// With a table-like surface in view (<see cref="TableSurfaceFinder"/>: flat,
    /// facing up, table height, bigger than a chair) it lands ON it instead and
    /// stays there, turning to face the viewer. It looks for one for a few
    /// seconds before landing; with none it keeps them company in the air.
    ///
    /// Built by IMETINHUMAN > Humobox > Add Humobox Pilot (Take 13).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HumoboxPilot : MonoBehaviour
    {
        [Tooltip("Hidden until the chapter starts. Carries HumoboxHead and the director.")]
        [SerializeField] GameObject pivot;
        [SerializeField] PlayableDirector director;
        [Tooltip("Defaults to Camera.main (the headset).")]
        [SerializeField] Transform viewer;

        [Header("Size and place")]
        [Tooltip("Widest the box is across, in metres. The model is exported around 3 m.")]
        [SerializeField] float boxSize = 0.45f;
        [Tooltip("Metres from the viewer's eye to the centre of the box.")]
        [SerializeField] float distance = 1.1f;
        [Tooltip("Metres relative to the viewer's eye height.")]
        [SerializeField] float heightOffset = -0.15f;
        [Tooltip("Extra turn on the box, in case the model's front is not its +Z.")]
        [SerializeField] float yawOffset;

        [Header("Landing")]
        [SerializeField] float landSeconds = 1.8f;
        [Tooltip("Metres above its spot that it drops from.")]
        [SerializeField] float dropHeight = 1.6f;
        [Tooltip("Squash on touching down: share of its height.")]
        [SerializeField, Range(0f, 0.4f)] float landingSquash = 0.14f;

        [Header("Following (lazy, in the room)")]
        [Tooltip("Degrees off the viewer's facing before it moves back in front.")]
        [SerializeField] float followAngle = 35f;
        [Tooltip("Metres: closer than this and it backs off, further than this and it catches up.")]
        [SerializeField] Vector2 distanceRange = new(0.7f, 1.8f);
        [Tooltip("Metres the viewer's eye may rise or sink before it follows the height.")]
        [SerializeField] float heightTolerance = 0.3f;
        [Tooltip("Seconds the viewer must stay turned away before it sets off; a glance does not move it.")]
        [SerializeField] float settleSeconds = 0.6f;
        [Tooltip("Seconds to cover most of a move; higher is lazier.")]
        [SerializeField] float glideTime = 0.9f;
        [SerializeField] float maxSpeed = 1.4f;
        [Tooltip("Degrees it leans into a move.")]
        [SerializeField] float lean = 8f;
        [Tooltip("Metres of slow hovering bob.")]
        [SerializeField] float bob = 0.012f;

        [Header("Table")]
        [Tooltip("Land on a table, desk or other table-height surface when one is found.")]
        [SerializeField] bool preferTable = true;
        [Tooltip("Seconds to look for one before landing in the air instead.")]
        [SerializeField] float tableSearchSeconds = 3f;
        [Tooltip("Share of the surface's shorter side the box may cover.")]
        [SerializeField, Range(0.2f, 1f)] float tableCoverage = 0.7f;

        [Header("Leaving")]
        [Tooltip("Seconds after the take ends before it lifts off.")]
        [SerializeField] float lingerSeconds = 1.5f;

        enum State { Off, Searching, Landing, Here, Leaving }
        State state = State.Off;
        float stateTime;
        float awaySince = -1f;
        bool moving;
        Vector3 spot, velocity, landFrom;
        Quaternion facing = Quaternion.identity;
        float scale = 1f;
        bool measured;
        Vector3 localCentre;
        float footRadius, halfHeight;
        bool onTable;
        TableSurfaceFinder tableFinder;

        /// <summary>True while any pilot's take is playing; the rain ducks under it.</summary>
        public static bool Speaking { get; private set; }

        /// <summary>Down and talking (not looking for a table, not landing).</summary>
        public bool Landed => state is State.Here or State.Leaving;

        /// <summary>The take's voice.</summary>
        public AudioSource Voice => pivot != null ? pivot.GetComponent<AudioSource>() : null;

        /// <summary>Seconds of the take still to play.</summary>
        public float TimeLeft => director != null ? Mathf.Max(0f, (float)(director.duration - director.time)) : 0f;

        bool TakeDone => director != null && director.time >= director.duration - 0.05;

        /// <summary>True once it stands on a real surface rather than in the air.</summary>
        public bool OnTable => onTable;

        /// <summary>True from the start of the chapter until the box has flown off.</summary>
        public bool Playing => state != State.Off;

        Transform Eye => viewer != null ? viewer : Camera.main != null ? Camera.main.transform : null;

        void Awake()
        {
            if (pivot != null)
                pivot.SetActive(false);
        }

        System.Collections.IEnumerator Start()
        {
            // Warm up, as the rain cue does: the first draw builds the model's
            // GPU pipelines, a visible stall on the headset. Draw it tiny and
            // out of sight for two frames now.
            var eye = Eye;
            if (pivot == null || eye == null || state != State.Off)
                yield break;
            transform.SetPositionAndRotation(eye.position + eye.forward, Quaternion.identity);
            transform.localScale = Vector3.one * 0.001f;
            pivot.SetActive(true);
            yield return null;
            yield return null;
            if (state == State.Off)
                pivot.SetActive(false);
            transform.localScale = Vector3.one;
        }

        /// <summary>Lands the box in front of the viewer and plays its take from the start.</summary>
        public void Begin()
        {
            var eye = Eye;
            if (pivot == null || eye == null)
            {
                Debug.LogWarning("Humobox pilot: no pivot or no viewer.", this);
                return;
            }

            transform.localScale = Vector3.one;
            pivot.SetActive(true);
            var head = pivot.GetComponent<HumoboxHead>();
            if (head != null)
                head.followPivotRotation = true;
            if (!measured)
                Measure();

            scale = footRadius > 0f ? boxSize / (2f * footRadius) : 1f;
            pivot.transform.localScale = Vector3.one * scale;
            pivot.transform.localRotation = Quaternion.Euler(0f, yawOffset, 0f);
            pivot.transform.localPosition = -(pivot.transform.localRotation * (localCentre * scale));

            onTable = false;
            velocity = Vector3.zero;
            moving = false;
            awaySince = -1f;

            if (preferTable)
            {
                // Hidden while it looks; the take starts when it lands.
                if (tableFinder == null)
                    tableFinder = gameObject.AddComponent<TableSurfaceFinder>();
                tableFinder.Begin();
                pivot.SetActive(false);
                Enter(State.Searching);
                return;
            }
            Land(eye, null);
        }

        void Land(Transform eye, TableSurfaceFinder.Surface? table)
        {
            onTable = table != null;
            var size = boxSize;
            if (onTable)
            {
                var t = table.Value;
                size = Mathf.Min(boxSize, t.shortSide * tableCoverage);
                scale = footRadius > 0f ? size / (2f * footRadius) : 1f;
                pivot.transform.localScale = Vector3.one * scale;
                pivot.transform.localPosition = -(pivot.transform.localRotation * (localCentre * scale));
                // Standing on it: its bottom on the top.
                spot = t.centre + Vector3.up * (halfHeight * scale + 0.005f);
            }
            else
            {
                spot = SpotInFront(eye);
            }

            pivot.SetActive(true);
            facing = FaceViewer(spot, eye);
            landFrom = spot + Vector3.up * dropHeight;
            transform.SetPositionAndRotation(landFrom, facing);
            Enter(State.Landing);

            if (director != null)
            {
                director.time = 0;
                director.Play();
            }
            // Take 13 is levelled to -20 LUFS; no distance fall-off within 2 m (on a table or in the air).
            var voice = Voice;
            if (voice != null)
            {
                voice.minDistance = 2f;
                voice.volume = 1f;
            }
            Debug.Log($"Humobox pilot: landing {(onTable ? $"on a {table.Value.source} surface {table.Value.longSide:0.00} x {table.Value.shortSide:0.00} m" : "in the air")} " +
                      $"at {spot:F2}, {size:0.##} m box, take {(director != null ? director.duration : 0):0.0} s.", this);
        }

        /// <summary>Takes the box away and stops its take now.</summary>
        public void EndNow()
        {
            if (state == State.Off)
                return;
            if (director != null)
                director.Stop();
            if (pivot != null)
                pivot.SetActive(false);
            if (tableFinder != null)
                tableFinder.End();
            Speaking = false;
            Enter(State.Off);
        }

        void Enter(State s)
        {
            state = s;
            stateTime = 0f;
        }

        void Update()
        {
            Speaking = state != State.Off && state != State.Searching && director != null &&
                       director.state == PlayState.Playing && !TakeDone;
            if (state == State.Off)
                return;
            var eye = Eye;
            if (eye == null)
                return;
            stateTime += Time.deltaTime;
            var squash = 0f;

            switch (state)
            {
                case State.Searching:
                {
                    var found = tableFinder != null ? tableFinder.Current : null;
                    if (found != null || stateTime >= tableSearchSeconds)
                    {
                        if (found == null)
                            Debug.Log($"Humobox pilot: no table ({(tableFinder != null ? tableFinder.Status : "no finder")}); in the air.", this);
                        if (tableFinder != null)
                            tableFinder.End();
                        Land(eye, found);
                    }
                    return;
                }

                case State.Landing:
                {
                    // Falls in with a soft ease, then a quick squash on touchdown.
                    var u = Mathf.Clamp01(stateTime / Mathf.Max(landSeconds, 0.01f));
                    var fall = 1f - Mathf.Pow(1f - u, 3f);
                    transform.position = Vector3.LerpUnclamped(landFrom, spot, fall);
                    facing = Quaternion.Slerp(facing, FaceViewer(transform.position, eye), 4f * Time.deltaTime);
                    if (u >= 1f)
                    {
                        Enter(State.Here);
                        velocity = Vector3.zero;
                    }
                    break;
                }

                case State.Here:
                {
                    if (stateTime < 0.45f)
                        squash = landingSquash * Mathf.Exp(-9f * stateTime) * Mathf.Cos(18f * stateTime);
                    if (onTable)
                        facing = Quaternion.Slerp(facing, FaceViewer(spot, eye), 2.5f * Time.deltaTime);
                    else
                        Follow(eye);
                    // The director holds its last frame (wrap mode Hold) and still reports
                    // Playing, so the end is judged by time alone.
                    if (TakeDone && stateTime > 1f)
                    {
                        director.Stop();
                        Enter(State.Leaving);
                        landFrom = spot;
                    }
                    break;
                }

                case State.Leaving:
                {
                    if (stateTime < lingerSeconds)
                    {
                        if (!onTable)
                            Follow(eye);
                        break;
                    }
                    var u = Mathf.Clamp01((stateTime - lingerSeconds) / Mathf.Max(landSeconds, 0.01f));
                    transform.position = spot + Vector3.up * (dropHeight * u * u);
                    if (u >= 1f)
                    {
                        EndNow();
                        return;
                    }
                    break;
                }
            }

            // Bob, lean into the move, squash on landing -- all on the root.
            var hover = state == State.Landing || onTable ? 0f : bob * Mathf.Sin(Time.time * 2.2f);
            var flat = Vector3.ProjectOnPlane(velocity, Vector3.up);
            var leanAxis = Vector3.Cross(Vector3.up, flat);
            var leanTurn = leanAxis.sqrMagnitude > 1e-6f
                ? Quaternion.AngleAxis(lean * Mathf.Clamp01(flat.magnitude / maxSpeed), leanAxis.normalized)
                : Quaternion.identity;
            if (state != State.Landing && !(state == State.Leaving && stateTime >= lingerSeconds))
                transform.position = spot + Vector3.up * hover;
            transform.rotation = leanTurn * facing;
            transform.localScale = new Vector3(1f + squash * 0.5f, 1f - squash, 1f + squash * 0.5f);
        }

        // Stays put in the room; moves only when the viewer has turned away or
        // walked off for a moment, then glides to a new spot in front of them.
        void Follow(Transform eye)
        {
            var toBox = spot - eye.position;
            var flatTo = Vector3.ProjectOnPlane(toBox, Vector3.up);
            var angle = Vector3.Angle(FlatForward(eye), flatTo);
            var dist = flatTo.magnitude;
            var dy = Mathf.Abs(spot.y - (eye.position.y + heightOffset));
            var away = angle > followAngle || dist < distanceRange.x || dist > distanceRange.y || dy > heightTolerance;

            if (away)
            {
                if (awaySince < 0f)
                    awaySince = Time.time;
                if (Time.time - awaySince >= settleSeconds)
                    moving = true;
            }
            else
            {
                awaySince = -1f;
            }

            if (moving)
            {
                var target = SpotInFront(eye);
                spot = Vector3.SmoothDamp(spot, target, ref velocity, glideTime, maxSpeed);
                if ((spot - target).sqrMagnitude < 0.03f * 0.03f && velocity.sqrMagnitude < 0.01f)
                {
                    moving = false;
                    awaySince = -1f;
                }
            }
            else
            {
                velocity = Vector3.Lerp(velocity, Vector3.zero, 6f * Time.deltaTime);
            }

            // Always turns, slowly, to face them -- it turns, it does not move.
            facing = Quaternion.Slerp(facing, FaceViewer(spot, eye), 2.5f * Time.deltaTime);
        }

        Vector3 SpotInFront(Transform eye) =>
            eye.position + FlatForward(eye) * distance + Vector3.up * heightOffset;

        static Quaternion FaceViewer(Vector3 at, Transform eye)
        {
            // +Z of the box towards the viewer.
            var toViewer = Vector3.ProjectOnPlane(eye.position - at, Vector3.up);
            return toViewer.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(toViewer.normalized, Vector3.up)
                : Quaternion.identity;
        }

        static Vector3 FlatForward(Transform eye)
        {
            var f = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
            if (f.sqrMagnitude < 1e-4f)
                f = Vector3.ProjectOnPlane(eye.up, Vector3.up);   // looking straight down
            return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
        }

        // Once, at scale 1, on the first landing (renderer bounds need it active).
        void Measure()
        {
            var was = pivot.transform.localScale;
            pivot.transform.localScale = Vector3.one;
            var renderers = pivot.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                foreach (var r in renderers)
                    bounds.Encapsulate(r.bounds);
                localCentre = pivot.transform.InverseTransformVector(bounds.center - pivot.transform.position);
                footRadius = new Vector2(bounds.extents.x, bounds.extents.z).magnitude;
                halfHeight = bounds.extents.y;
                measured = true;
            }
            pivot.transform.localScale = was;
        }
    }
}
