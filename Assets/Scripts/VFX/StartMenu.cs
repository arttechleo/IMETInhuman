using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Hands;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// The choice the app opens on: the whole piece, or any one chapter of it.
    ///
    /// A panel stands within arm's reach, a button per chapter. Put a hand on
    /// one and it lights; pinch there and it is taken. Nothing runs until then --
    /// the effects are held back rather than started and stopped, so nothing
    /// flashes up behind it.
    ///
    /// Hands are what point at it: no gaze, so looking around chooses nothing.
    ///
    /// While a chapter plays, a thumbs up stops it and brings the panel back.
    /// Pinching cannot do that job: pinching is how the capture is held and
    /// turned, so it would stop the chapter every time it was handled.
    ///
    /// The chapters are there for working on the piece: each one starts at the
    /// moment worth looking at rather than where it falls in the running order,
    /// so the Humobox chapter brings it in a second in rather than at the end
    /// of the rain. The panel comes back on its own once a chapter has played
    /// out, so one can be tried after another without taking the headset off.
    ///
    /// The panel is laid out in pixels and then shrunk to its size in metres.
    /// Laying a canvas out directly in metres leaves text with a box a fraction
    /// of a unit across, which is why the labels came out empty.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StartMenu : MonoBehaviour
    {
        [SerializeField] KaleidoscopeIntroBootstrap bootstrap;

        [Header("Panel")]
        [SerializeField] float distance = 0.62f;
        [Tooltip("Metres across.")]
        [SerializeField] float width = 0.44f;
        [Tooltip("Metres from a button that a pinching hand still counts as on it.")]
        [SerializeField] float reach = 0.09f;
        [Tooltip("Seconds of thumbs up that stops a chapter and calls the panel back.")]
        [SerializeField] float stopGestureSeconds = 0.6f;

        const int PanelPixels = 700;      // the panel's width in layout pixels
        const int ButtonPixels = 104;
        const int GapPixels = 14;
        const int TitlePixels = 96;

        // In the order they play, with the whole piece first.
        static readonly (string label, KaleidoscopeIntroBootstrap.Chapter chapter)[] Chapters =
        {
            ("Full experience", KaleidoscopeIntroBootstrap.Chapter.Everything),
            ("Scrolling shorts", KaleidoscopeIntroBootstrap.Chapter.Shorts),
            ("Raindrops", KaleidoscopeIntroBootstrap.Chapter.Rain),
            ("Humobox", KaleidoscopeIntroBootstrap.Chapter.Humobox),
            ("PLY sequence", KaleidoscopeIntroBootstrap.Chapter.Splats),
            ("Stereo video", KaleidoscopeIntroBootstrap.Chapter.Stereo),
            ("Dark room", KaleidoscopeIntroBootstrap.Chapter.DarkRoom),
        };

        static readonly List<XRHandSubsystem> subsystems = new();
        XRHandSubsystem hands;
        Transform originTransform;

        Transform panel;
        readonly List<Transform> buttons = new();
        readonly List<RectTransform> fills = new();
        bool wasPinching;
        bool chosen;
        float chosenAt;
        float thumbUpSince = -1f;

        void Start()
        {
            if (bootstrap == null)
                bootstrap = GetComponent<KaleidoscopeIntroBootstrap>();
            if (bootstrap == null)
            {
                enabled = false;
                return;
            }
            var origin = FindFirstObjectByType<XROrigin>();
            originTransform = origin != null ? origin.Origin.transform : null;
            Build();
        }

        void Build()
        {
            var eye = Camera.main != null ? Camera.main.transform : null;
            var go = new GameObject("Start Menu", typeof(RectTransform), typeof(Canvas));
            panel = go.transform;
            if (eye != null)
                panel.SetPositionAndRotation(eye.position + eye.forward * distance,
                                             Quaternion.LookRotation(Flat(eye.forward), Vector3.up));

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rect = (RectTransform)go.transform;
            var height = TitlePixels + Chapters.Length * (ButtonPixels + GapPixels) + GapPixels;
            rect.sizeDelta = new Vector2(PanelPixels, height);
            // Pixels to metres, once, on the whole panel.
            rect.localScale = Vector3.one * (width / PanelPixels);

            // The built-in font is not always in a player; the headset's own is.
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                       ?? Font.CreateDynamicFontFromOSFont("Roboto", 54)
                       ?? Font.CreateDynamicFontFromOSFont(Font.GetOSInstalledFontNames(), 54);
            Debug.Log(font != null ? $"Start menu: font {font.name}." : "Start menu: no font at all; labels will be blank.", this);

            var top = height * 0.5f;
            Label(rect, font, "IMETINHUMAN", 46, new Color(1f, 1f, 1f, 0.8f),
                  new Vector2(PanelPixels, 70), new Vector2(0f, top - TitlePixels * 0.5f));

            for (var i = 0; i < Chapters.Length; i++)
            {
                var y = top - TitlePixels - GapPixels - (ButtonPixels + GapPixels) * i - ButtonPixels * 0.5f;
                Button(rect, font, Chapters[i].label, new Vector2(0f, y), i == 0);
            }
            Debug.Log($"Start menu: {Chapters.Length} chapters; reach out and pinch one.", this);
        }

        static Text Label(RectTransform parent, Font font, string text, int size, Color color,
                          Vector2 sizeDelta, Vector2 position)
        {
            var go = new GameObject(text, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = sizeDelta;
            rect.anchoredPosition = position;
            var label = go.AddComponent<Text>();
            label.font = font;
            label.text = text;
            label.color = color;
            label.fontSize = size;
            label.alignment = TextAnchor.MiddleCenter;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            return label;
        }

        void Button(RectTransform parent, Font font, string text, Vector2 position, bool headline)
        {
            var go = new GameObject(text, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.sizeDelta = new Vector2(PanelPixels - 60, ButtonPixels);
            rect.anchoredPosition = position;
            // The whole piece stands out from the chapters under it.
            go.AddComponent<Image>().color = headline
                ? new Color(0.07f, 0.12f, 0.11f, 0.85f)
                : new Color(0.04f, 0.05f, 0.06f, 0.8f);

            // Fills across the button while a hand is on it.
            var fillObject = new GameObject("Fill", typeof(RectTransform));
            var fillRect = (RectTransform)fillObject.transform;
            fillRect.SetParent(rect, false);
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(1f, 1f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillObject.AddComponent<Image>().color = new Color(0.3f, 0.72f, 0.52f, 0.85f);
            fillRect.localScale = new Vector3(0f, 1f, 1f);
            fills.Add(fillRect);

            Label(rect, font, text, headline ? 52 : 44, Color.white, rect.sizeDelta, Vector2.zero);
            buttons.Add(rect);
        }

        void Update()
        {
            if (panel == null)
                return;

            // Back again once the chapter has played itself out, or on a
            // thumbs up to cut it short.
            if (chosen)
            {
                if (Time.time - chosenAt > 1f && (!bootstrap.Playing || StopGesture()))
                    Reopen();
                return;
            }

            // A hand near a button lights it; a pinch there takes it.
            var hovered = -1;
            var pinchedButton = -1;
            foreach (var hand in Hands())
            {
                var button = Nearest(hand.point);
                if (button < 0)
                    continue;
                hovered = button;
                if (hand.pinching)
                    pinchedButton = button;
            }

            // A pinch must open again before it can take anything else, so one
            // held closed while moving does not run through the whole menu.
            if (pinchedButton >= 0 && !wasPinching)
                Choose(pinchedButton);
            wasPinching = pinchedButton >= 0;

            for (var i = 0; i < fills.Count; i++)
            {
                var wanted = i == hovered ? 1f : 0f;
                var amount = Mathf.MoveTowards(fills[i].localScale.x, wanted, Time.deltaTime * 6f);
                fills[i].localScale = new Vector3(amount, 1f, 1f);
            }
        }

        // A thumbs up, held: stop what is playing and come back.
        bool StopGesture()
        {
            var up = false;
            Hands();   // finds the subsystem if it is not yet running
            if (hands != null && hands.running)
                up = ThumbsUp(hands.leftHand) || ThumbsUp(hands.rightHand);

            if (!up)
            {
                thumbUpSince = -1f;
                return false;
            }
            if (thumbUpSince < 0f)
                thumbUpSince = Time.time;
            if (Time.time - thumbUpSince < stopGestureSeconds)
                return false;

            thumbUpSince = -1f;
            Debug.Log("Start menu: stopped by a thumbs up.", this);
            bootstrap.StopEverything();
            return true;
        }

        // Thumb out and pointing up, the other four folded into the palm.
        // Measured against the hand's own size, so it reads the same on any
        // hand, and in the room's up, so it means up however the wrist is held.
        bool ThumbsUp(XRHand hand)
        {
            if (!hand.isTracked
                || !hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out var wrist)
                || !hand.GetJoint(XRHandJointID.MiddleProximal).TryGetPose(out var knuckle)
                || !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumbTip)
                || !hand.GetJoint(XRHandJointID.ThumbProximal).TryGetPose(out var thumbBase))
                return false;

            var palmSize = Vector3.Distance(wrist.position, knuckle.position);
            if (palmSize < 0.02f)
                return false;

            // The thumb stands away from the fist...
            if (Vector3.Distance(thumbTip.position, wrist.position) < palmSize * 1.35f)
                return false;

            // ...and points up in the room.
            var along = thumbTip.position - thumbBase.position;
            if (originTransform != null)
                along = originTransform.rotation * along;
            if (Vector3.Dot(along.normalized, Vector3.up) < 0.6f)
                return false;

            // ...while the fingers are folded: their tips sit near the wrist.
            return Folded(hand, XRHandJointID.IndexTip, wrist.position, palmSize)
                   && Folded(hand, XRHandJointID.MiddleTip, wrist.position, palmSize)
                   && Folded(hand, XRHandJointID.RingTip, wrist.position, palmSize)
                   && Folded(hand, XRHandJointID.LittleTip, wrist.position, palmSize);
        }

        static bool Folded(XRHand hand, XRHandJointID tip, Vector3 wrist, float palmSize)
        {
            return hand.GetJoint(tip).TryGetPose(out var pose)
                   && Vector3.Distance(pose.position, wrist) < palmSize * 1.5f;
        }

        // The button a hand is on, or -1: inside its face and close to the panel.
        int Nearest(Vector3 point)
        {
            var best = reach;
            var found = -1;
            for (var i = 0; i < buttons.Count; i++)
            {
                var rect = (RectTransform)buttons[i];
                var local = rect.InverseTransformPoint(point);
                var half = rect.sizeDelta * 0.5f;
                var inside = Mathf.Abs(local.x) < half.x * 1.1f && Mathf.Abs(local.y) < half.y * 1.3f;
                var away = Mathf.Abs(local.z) * rect.lossyScale.z;
                if (inside && away < best)
                {
                    best = away;
                    found = i;
                }
            }
            return found;
        }

        struct HandPoint
        {
            public Vector3 point;
            public bool pinching;
        }

        readonly List<HandPoint> handPoints = new();

        List<HandPoint> Hands()
        {
            handPoints.Clear();
            if (hands == null || !hands.running)
            {
                SubsystemManager.GetSubsystems(subsystems);
                hands = subsystems.Count > 0 ? subsystems[0] : null;
            }
            if (hands == null || !hands.running)
                return handPoints;

            Add(hands.leftHand);
            Add(hands.rightHand);
            return handPoints;

            void Add(XRHand hand)
            {
                if (!hand.isTracked
                    || !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var index)
                    || !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb))
                    return;
                var gap = Vector3.Distance(thumb.position, index.position);
                var point = (thumb.position + index.position) * 0.5f;
                if (originTransform != null)
                    point = originTransform.TransformPoint(point);
                handPoints.Add(new HandPoint { point = point, pinching = gap > 0.002f && gap < 0.03f });
            }
        }

        void Choose(int index)
        {
            chosen = true;
            chosenAt = Time.time;
            wasPinching = true;          // this pinch must open before it counts again
            panel.gameObject.SetActive(false);
            Debug.Log($"Start menu: {Chapters[index].label}.", this);
            bootstrap.Play(Chapters[index].chapter);
        }

        // In front of the viewer again, wherever they are now looking.
        void Reopen()
        {
            chosen = false;
            wasPinching = true;   // a pinch made while stopping must open first
            thumbUpSince = -1f;
            var eye = Camera.main != null ? Camera.main.transform : null;
            if (eye != null)
                panel.SetPositionAndRotation(eye.position + eye.forward * distance,
                                             Quaternion.LookRotation(Flat(eye.forward), Vector3.up));
            foreach (var fill in fills)
                fill.localScale = new Vector3(0f, 1f, 1f);
            panel.gameObject.SetActive(true);
        }

        static Vector3 Flat(Vector3 v)
        {
            v = Vector3.ProjectOnPlane(v, Vector3.up);
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }
    }
}
