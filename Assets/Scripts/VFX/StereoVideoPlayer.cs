using System.IO;
using UnityEngine;
using UnityEngine.Video;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Plays a side-by-side stereo video with alpha, each eye seeing its own
    /// half, keyed over the room (StereoVideo.shader).
    ///
    /// The video is a baked stereo pair: its depth is fixed at render time, so
    /// leaning or turning shows no parallax -- it is a 3D picture standing in the
    /// room, where the splat sequence before it is a volume. Frames play as
    /// decoded, one after another, never blended.
    ///
    /// The file is the packed transcode (colour above alpha) from
    /// Tools/SplatSequence; on the headset it is read from the app's storage
    /// (pushed over USB), in the Editor from the media folder beside the project.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StereoVideoPlayer : MonoBehaviour, IIntroEffect
    {
        [Header("Media")]
        [SerializeField] string file = "Stereo/IMG_8750_stereo_sbs_coloralpha.mp4";
        [Tooltip("The same video as H.264, used on PC when present: Windows decodes HEVC only " +
                 "with an optional codec installed.")]
        [SerializeField] string pcFile = "Stereo/IMG_8750_stereo_sbs_coloralpha_h264.mp4";
#if UNITY_EDITOR
        [Tooltip("Editor only: the media root, relative to the project folder.")]
        [SerializeField] string editorMediaRoot = "../IMETINHUMAN_Media";
#endif
        [Tooltip("One eye's picture, width over height (each half of the side-by-side).")]
        [SerializeField] float eyeAspect = 1080f / 1920f;

        [Header("Timing")]
        [SerializeField] float startDelay;
        [Tooltip("Play only this range of the video, in frames -- 20..558 matches the .ply " +
                 "sequence, so both show the same moments. -1 plays to the end.")]
        [SerializeField] int firstFrame = 20;
        [SerializeField] int lastFrame = 558;
        [SerializeField] float videoFps = 24f;
        [Tooltip("Seconds; replaced by the video's own length once it is prepared.")]
        [SerializeField] float duration = 24.79f;
        [SerializeField] float fadeSeconds = 0.4f;

        [Header("Placement")]
        [SerializeField] float distance = 1.5f;
        [Tooltip("Height of the picture in metres. Ignored while matching the subject.")]
        [SerializeField] float height = 1.6f;

        [Header("Match The Splats")]
        [Tooltip("The person's height in metres; the splat sequence uses the same. With the " +
                 "per-frame track, the picture is scaled so they stay this tall.")]
        [SerializeField] float subjectHeight = 1.7f;
        [SerializeField] bool swapEyes;
        [Tooltip("Per-frame box of the person in the video, from its alpha (x centre, top, bottom " +
                 "as shares of one eye's picture). With it the picture is moved and scaled every " +
                 "frame so the person keeps the spot, as the splats do; the frames are untouched.")]
        [SerializeField] string trackFile = "Stereo/stereo_track.json";

        float[] boxes;   // flat: x centre, top, bottom per frame
        Pose stage;
        ViewAnchor view;

        [Tooltip("Degrees out of the middle of the view before the picture glides back into it. " +
                 "Within that, it stays still in the room while the head moves.")]
        [SerializeField, Range(5f, 90f)] float followAngle = 30f;
        [SerializeField, Range(0.1f, 3f)] float glideSeconds = 0.6f;

        VideoPlayer player;
        RenderTexture target;
        Material material;
        Transform quad;
        MeshRenderer quadRenderer;
        float elapsed;
        bool playing;
        bool placed;
        bool started;

        public string EffectName => "Stereo video";

        public float Duration
        {
            get => duration;
            set { }
        }

        public float StartDelay
        {
            get => startDelay;
            set => startDelay = value;
        }

        public float PlaybackTime => playing ? elapsed - startDelay : float.NegativeInfinity;

        public bool IsPlaying => playing && PlaybackTime >= 0f && PlaybackTime < duration;

        public void PlayFromStart(float delay = 0f)
        {
            startDelay = Mathf.Max(0f, delay);
            elapsed = 0f;
            playing = true;
            placed = false;
            started = false;
            UpdateDuration();
            if (player != null)
            {
                player.Stop();
                player.Prepare();
            }
        }

        public void StopNow()
        {
            playing = false;
            if (player != null)
                player.Pause();
            SetVisible(0f);
        }

        string MediaPath()
        {
#if UNITY_EDITOR
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", editorMediaRoot));
#else
            var root = Application.persistentDataPath;
#endif
            if (Application.platform != RuntimePlatform.Android && !string.IsNullOrEmpty(pcFile)
                && File.Exists(Path.Combine(root, pcFile)))
                return Path.Combine(root, pcFile);
            return Path.Combine(root, file);
        }

        void Start()
        {
            var path = MediaPath();
            var shader = Shader.Find("IMETINHUMAN/VFX/Stereo Video");
            if (shader == null || !File.Exists(path))
            {
                Debug.LogWarning(shader == null ? "Stereo Video shader was not found."
                                                : $"Stereo video: no file at {path}. Push it with IMETINHUMAN > Media > Push To Headset.", this);
                playing = false;
                enabled = false;
                return;
            }

            LoadTrack(path);

            material = new Material(shader) { name = "Stereo Video" };
            material.SetFloat(Ids.SwapEyes, swapEyes ? 1f : 0f);

            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "Stereo Video";
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            quad = go.transform;
            quadRenderer = go.GetComponent<MeshRenderer>();
            quadRenderer.sharedMaterial = material;
            quadRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            quadRenderer.receiveShadows = false;
            quadRenderer.enabled = false;

            player = go.AddComponent<VideoPlayer>();
            player.playOnAwake = false;
            player.isLooping = false;
            player.waitForFirstFrame = true;
            player.skipOnDrop = true;
            player.audioOutputMode = VideoAudioOutputMode.None;
            player.source = VideoSource.Url;
            player.url = path;
            player.renderMode = VideoRenderMode.RenderTexture;
            player.errorReceived += (_, message) => Debug.LogError($"Stereo video error: {message}", this);
            player.prepareCompleted += source =>
            {
                UpdateDuration(source);
                if (target == null)
                {
                    target = new RenderTexture((int)source.width, (int)source.height, 0,
                                               RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                    {
                        name = "Stereo Video RT",
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear
                    };
                    target.Create();
                    source.targetTexture = target;
                    material.SetTexture(Ids.MainTex, target);
                }
                Debug.Log($"Stereo video prepared: {source.width}x{source.height}, {duration:0.0}s.", this);
            };
            player.Prepare();
        }

        void Update()
        {
            if (!playing || player == null)
                return;

            elapsed += Time.deltaTime;
            var t = elapsed - startDelay;
            if (t < 0f)
            {
                SetVisible(0f);
                return;
            }

            if (!started && player.isPrepared)
            {
                player.Play();
                player.time = Mathf.Max(firstFrame, 0) / videoFps;   // seek after Play
                started = true;
            }
            if (!placed)
                Place();

            if (t > duration)
            {
                var over = t - duration;
                SetVisible(1f - Mathf.Clamp01(over / Mathf.Max(fadeSeconds, 0.01f)));
                if (over > fadeSeconds)
                    StopNow();
                return;
            }

            if (placed && Camera.main != null)
            {
                // In view, still in the room, gliding back only if lost from view.
                view.Follow(Camera.main.transform, distance, followAngle, glideSeconds);
                stage = view.Pose;
                if (boxes != null)
                    Follow(started ? (int)player.frame : firstFrame);
                else
                    quad.SetPositionAndRotation(stage.position, stage.rotation);
            }

            var fadeIn = Mathf.Clamp01(t / Mathf.Max(fadeSeconds, 0.01f));
            SetVisible(started && player.frame >= 0 ? fadeIn : 0f);
        }

        void Place()
        {
            var eye = Camera.main != null ? Camera.main.transform : null;
            if (eye == null)
                return;
            // Where the viewer is looking as it starts, the person's middle on it.
            view.PlaceInView(eye, distance);
            stage = view.Pose;
            quad.SetPositionAndRotation(stage.position, stage.rotation);
            quad.localScale = new Vector3(height * eyeAspect, height, 1f);
            placed = true;
            if (boxes != null)
                Follow(firstFrame);
        }

        // The person's middle on the spot, 1.7 m tall, whatever frame is up:
        // the video's own framing moves them about and changes their size as
        // they near the camera; the picture is moved and scaled to cancel it.
        void Follow(int frame)
        {
            var n = boxes.Length / 3;
            frame = Mathf.Clamp(frame, 0, n - 1);
            var cx = boxes[frame * 3];
            var top = boxes[frame * 3 + 1];
            var bottom = boxes[frame * 3 + 2];
            var share = Mathf.Max(bottom - top, 0.25f);

            var h = subjectHeight / share;
            var w = h * eyeAspect;
            var middle = (top + bottom) * 0.5f;
            var right = stage.rotation * Vector3.right;
            var centre = stage.position - right * ((cx - 0.5f) * w) + Vector3.up * ((middle - 0.5f) * h);
            quad.SetPositionAndRotation(centre, stage.rotation);
            quad.localScale = new Vector3(w, h, 1f);
        }

        void LoadTrack(string videoPath)
        {
#if UNITY_EDITOR
            var trackPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", editorMediaRoot, trackFile));
#else
            var trackPath = Path.Combine(Application.persistentDataPath, trackFile);
#endif
            if (!File.Exists(trackPath))
                return;
            // JsonUtility cannot read nested arrays; flatten "[[x,t,b],...]" by hand.
            var text = File.ReadAllText(trackPath);
            var start = text.IndexOf("[[", System.StringComparison.Ordinal);
            if (start < 0)
                return;
            var numbers = text.Substring(start).Replace("[", " ").Replace("]", " ").Replace("}", " ")
                .Split(new[] { ',', ' ', (char)10, (char)13 }, System.StringSplitOptions.RemoveEmptyEntries);
            var values = new System.Collections.Generic.List<float>(numbers.Length);
            foreach (var number in numbers)
                if (float.TryParse(number, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var v))
                    values.Add(v);
            if (values.Count >= 3)
                boxes = values.GetRange(0, values.Count - values.Count % 3).ToArray();
        }

        // The played range's length, from the frame range and the video's own length.
        void UpdateDuration(VideoPlayer source = null)
        {
            var total = source != null && source.length > 0.1 ? (float)source.length : duration;
            var start = Mathf.Max(firstFrame, 0) / videoFps;
            var end = lastFrame >= 0 ? Mathf.Min((lastFrame + 1) / videoFps, total) : total;
            if (source != null || lastFrame >= 0)
                duration = Mathf.Max(end - start, 0.1f);
        }

        void SetVisible(float value)
        {
            if (material != null)
                material.SetFloat(Ids.Alpha, value);
            if (quadRenderer != null)
                quadRenderer.enabled = value > 0.001f;
        }

        void OnDestroy()
        {
            if (player != null)
                player.Stop();
            if (target != null)
            {
                target.Release();
                Destroy(target);
            }
            if (material != null)
                Destroy(material);
        }

        static class Ids
        {
            public static readonly int MainTex = Shader.PropertyToID("_MainTex");
            public static readonly int Alpha = Shader.PropertyToID("_Alpha");
            public static readonly int SwapEyes = Shader.PropertyToID("_SwapEyes");
        }
    }
}
