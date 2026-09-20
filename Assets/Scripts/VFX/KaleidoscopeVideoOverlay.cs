using ImetInHuman.XR;
using UnityEngine;
using UnityEngine.Video;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// The opening effect: the six art clips, one after another, in one of two
    /// styles.
    ///
    /// Shorts (the default): a phone feed. Each clip plays full screen in
    /// portrait and the next arrives with a swipe -- a small drag, a fling, an
    /// overshoot that snaps into place. See ShortsFeed.shader.
    ///
    /// Projector: a rounded-square gate under a cream frame, the picture's light
    /// spilling past it; changing slide is a cartridge swap -- lift out, bare
    /// lamp, drop in. See ProjectorSlideshow.shader.
    ///
    /// It replaced the kaleidoscope and kept its class name, so the intro scene
    /// and <see cref="KaleidoscopeIntroBootstrap"/> still find it.
    ///
    /// The clips come from one video atlas -- a grid of separate looping shots
    /// packed into one file -- decoded by a single VideoPlayer; the shader picks
    /// the tile for the current slide. Build the atlas with the tool at
    /// IMET IN HUMAN/Rebuild Kaleidoscope Atlas.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class KaleidoscopeVideoOverlay : MonoBehaviour, IIntroEffect
    {
        public enum Style { Shorts, Projector }

        [Tooltip("Shorts: a phone feed, swiped. Projector: slides in a projector.")]
        [SerializeField] Style style = Style.Shorts;

        [Header("Placement")]
        [SerializeField] Camera targetCamera;
        [SerializeField] float startDelay;
        [SerializeField] float duration = 30f;
        [SerializeField] float distance = 2.5f;

        [Tooltip("Head: stays in the middle of the view. World: placed in front of the " +
                 "viewer when it starts, then stays in the room.")]
        [SerializeField] CameraLockedOverlay.Anchoring anchoring = CameraLockedOverlay.Anchoring.World;
        [Tooltip("Share of the view the effect's area covers. The gate and its light sit inside it.")]
        [SerializeField, Range(0.1f, 1.5f)] float viewSize = 0.5f;
        [Tooltip("World only: glide back in front of the viewer once they have turned " +
                 "this many degrees away. 0 stays put.")]
        [SerializeField, Range(0f, 180f)] float followAngle;

        [Header("Video Atlas")]
        [Tooltip("Grid of separate clips packed into one video. Each slide plays one tile.")]
        [SerializeField] VideoClip atlasClip;

        [Tooltip("Used only when no clip asset is assigned.")]
        [SerializeField] string atlasUrl;

        [Tooltip("Tile layout of the atlas. Must match how the file was built.")]
        [SerializeField] Vector2Int atlasGrid = new Vector2Int(3, 2);

        [Tooltip("Render target size. Falls back to this when the clip does not " +
                 "report its own resolution.")]
        [SerializeField] Vector2Int videoResolution = new Vector2Int(1536, 1024);

        [Tooltip("Seconds the picture takes to fade in once the clip is prepared.")]
        [SerializeField] float videoFadeIn = 0.8f;

        [SerializeField] bool playAudio;

        [Header("Slides")]
        [Tooltip("Seconds each slide is up, its swap included. Six slides at 5 s fill a 30 s intro.")]
        [SerializeField, Range(1f, 30f)] float secondsPerSlide = 5f;
        [Tooltip("Length of a cartridge swap: lift out, empty gate, drop in and settle.")]
        [SerializeField, Range(0.2f, 3f)] float swapSeconds = 0.9f;
        [Tooltip("Half the gate's size, in half-heights of the effect's area.")]
        [SerializeField, Range(0.1f, 1f)] float gateSize = 0.68f;
        [Tooltip("Corner rounding, as a share of the gate.")]
        [SerializeField, Range(0f, 0.5f)] float cornerRadius = 0.1f;
        [Tooltip("1 plays the footage as filmed.")]
        [SerializeField, Range(0f, 2f)] float videoBrightness = 1f;

        [Header("Shorts")]
        [Tooltip("Hold the phone like a phone: at reading distance below the eyes, tilted to " +
                 "face them. Off, the Shorts style uses the placement settings above.")]
        [SerializeField] bool handheld = true;
        [Tooltip("Handheld: metres in front of the eyes.")]
        [SerializeField, Range(0.2f, 1.2f)] float handDistance = 0.45f;
        [Tooltip("Handheld: metres below the eyes.")]
        [SerializeField, Range(-0.3f, 0.6f)] float handDrop = 0.22f;
        [Tooltip("Handheld: height of the phone and its light, in metres. A real phone's screen " +
                 "is about 15 cm; a little larger reads better in a headset.")]
        [SerializeField, Range(0.1f, 1f)] float handHeight = 0.36f;
        [Tooltip("Handheld: once the viewer turns this many degrees away, the phone is carried " +
                 "back in front of them. 0 leaves it where it was.")]
        [SerializeField, Range(0f, 180f)] float handFollowAngle = 50f;
        [Tooltip("Length of a swipe to the next clip: drag, fling, snap.")]
        [SerializeField, Range(0.2f, 2f)] float swipeSeconds = 0.6f;
        [Tooltip("Half the phone screen's height, in half-heights of the effect's area.")]
        [SerializeField, Range(0.2f, 1f)] float screenHalfHeight = 0.8f;
        [Tooltip("Screen corner radius, as a share of the screen's width.")]
        [SerializeField, Range(0f, 0.3f)] float screenCorner = 0.12f;
        [SerializeField] Color phoneColor = new Color(0.035f, 0.035f, 0.04f, 1f);
        [Tooltip("Bezel around the screen, as a share of the screen's width.")]
        [SerializeField, Range(0f, 0.15f)] float bezel = 0.045f;
        [Tooltip("The dark wash shorts put behind captions, at the bottom of each clip.")]
        [SerializeField, Range(0f, 1f)] float captionShade = 0.45f;

        [Header("Sound")]
        [Tooltip("One clip per atlas tile, in tile order: each shot's own sound, cut to exactly " +
                 "the loop its tile plays. Built from the source montage (Assets/Audio/Shorts).")]
        [SerializeField] AudioClip[] shotAudio;
        [SerializeField, Range(0f, 1f)] float volume = 0.9f;
        [Tooltip("Seconds the sound crossfades as one clip swipes to the next.")]
        [SerializeField, Range(0f, 1f)] float audioCrossfade = 0.25f;
        [Tooltip("1 plays the sound from the phone's place in the room; 0 plays it flat.")]
        [SerializeField, Range(0f, 1f)] float spatialBlend = 1f;

        [Header("Frame")]
        [SerializeField] Color frameColor = new Color(0.9f, 0.87f, 0.8f, 1f);
        [Tooltip("Frame width, as a share of the gate.")]
        [SerializeField, Range(0f, 0.3f)] float frameWidth = 0.07f;
        [Tooltip("How far the frame reaches over the picture's edge.")]
        [SerializeField, Range(0f, 0.1f)] float frameOverlap = 0.02f;
        [Tooltip("How dark the slide's mount edge is as it crosses the gate during a swap.")]
        [SerializeField, Range(0f, 1f)] float mountShadow = 0.85f;

        [Header("Light")]
        [SerializeField] Color lampColor = new Color(1f, 0.93f, 0.8f, 1f);
        [Tooltip("Brightness of the empty gate between slides.")]
        [SerializeField, Range(0f, 2f)] float lampBrightness = 0.9f;
        [Tooltip("How far the picture's light spills past the frame, as a share of the gate.")]
        [SerializeField, Range(0.02f, 1f)] float spillSize = 0.3f;
        [SerializeField, Range(0f, 2f)] float spillStrength = 0.9f;
        [Tooltip("How much the spill tints what is behind it, rather than only adding light.")]
        [SerializeField, Range(0f, 1f)] float spillOpacity = 0.45f;
        [Tooltip("0: spill is the picture's average colour. 1: each side takes the colour of the picture's nearest edge.")]
        [SerializeField, Range(0f, 1f)] float spillFromEdge = 0.6f;
        [SerializeField, Range(0f, 0.2f)] float lampFlicker = 0.025f;
        [Tooltip("Extra flicker while the tray moves.")]
        [SerializeField, Range(0f, 0.6f)] float swapFlicker = 0.2f;
        [Tooltip("A projector is a little brighter at the centre of its throw.")]
        [SerializeField, Range(0f, 0.5f)] float hotspot = 0.08f;

        [Header("Background")]
        [Tooltip("Around the gate and its light, on screen.")]
        [SerializeField] Color backgroundColor = new Color(0.05f, 0.05f, 0.055f, 1f);
        [SerializeField, Range(0f, 1f)] float backgroundOpacity = 0.9f;
        [Tooltip("Background opacity on the headset. 0 lets the room show around the gate.")]
        [SerializeField, Range(0f, 1f)] float passthroughBackgroundOpacity;

        CameraLockedOverlay overlay;
        Material material;
        AudioSource[] voices;
        int voice;
        int audioSlide = -1;
        VideoPlayer player;
        RenderTexture target;
        float ready;

        const float PrepareTimeout = 4f;
        const float StallTimeout = 1.5f;
        float prepareStarted;
        float lastFrameChange;
        long lastFrame = -1;

        bool HasSource => atlasClip != null || !string.IsNullOrEmpty(atlasUrl);

        /// <summary>
        /// Seconds from scene start before the effect begins. Set by a sequencer
        /// before Awake runs; see <see cref="KaleidoscopeIntroBootstrap"/>.
        /// </summary>
        public float StartDelay
        {
            get => startDelay;
            set => startDelay = value;
        }

        public float Duration
        {
            get => duration;
            set => duration = value;
        }

        public string EffectName => style == Style.Shorts ? "Shorts feed" : "Projector slides";

        public float PlaybackTime => overlay != null && overlay.gameObject.activeSelf ? overlay.PlaybackTime : float.NegativeInfinity;

        public bool IsPlaying => overlay != null && overlay.IsPlaying;

        public void PlayFromStart(float delay = 0f)
        {
            // Before Start has run there is nothing to restart; the scheduled
            // delay covers it.
            if (overlay == null)
            {
                startDelay = delay;
                return;
            }

            overlay.Restart(delay);

            if (player != null)
            {
                player.time = 0;
                player.Play();
            }
        }

        public void StopNow()
        {
            if (voices != null)
                foreach (var v in voices)
                    v.Stop();
            if (overlay != null)
                overlay.Stop();
            if (player != null)
                player.Pause();
        }

        // Start, not Awake: AddComponent runs Awake synchronously, so a sequencer
        // adding this component could never set StartDelay in time. Every Awake in
        // the scene precedes every Start, which makes the hand-off safe.
        void Start()
        {
            var shaderName = style == Style.Shorts ? "IMETINHUMAN/VFX/Shorts Feed" : "IMETINHUMAN/VFX/Projector Slideshow";
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning($"{shaderName} shader was not found.", this);
                enabled = false;
                return;
            }

            material = new Material(shader) { name = $"Runtime {style}" };
            material.SetVector(ShaderIds.AtlasGrid,
                               new Vector4(Mathf.Max(1, atlasGrid.x), Mathf.Max(1, atlasGrid.y), 0f, 0f));
            material.SetFloat(ShaderIds.VideoReady, 0f);
            material.SetFloat(ShaderIds.VideoGain, videoBrightness);
            material.SetFloat(ShaderIds.SecondsPerSlide, secondsPerSlide);
            material.SetFloat(ShaderIds.SwapSeconds, style == Style.Shorts ? swipeSeconds : swapSeconds);
            material.SetFloat(ShaderIds.ScreenHalfHeight, screenHalfHeight);
            material.SetFloat(ShaderIds.ScreenCorner, screenCorner);
            material.SetColor(ShaderIds.PhoneColor, phoneColor);
            material.SetFloat(ShaderIds.Bezel, bezel);
            material.SetFloat(ShaderIds.CaptionShade, captionShade);
            material.SetFloat(ShaderIds.GateSize, gateSize);
            material.SetFloat(ShaderIds.CornerRadius, cornerRadius);
            material.SetColor(ShaderIds.FrameColor, frameColor);
            material.SetFloat(ShaderIds.FrameWidth, frameWidth);
            material.SetFloat(ShaderIds.FrameOverlap, frameOverlap);
            material.SetFloat(ShaderIds.MountShadow, mountShadow);
            material.SetColor(ShaderIds.LampColor, lampColor);
            material.SetFloat(ShaderIds.LampBrightness, lampBrightness);
            material.SetFloat(ShaderIds.GlowSize, spillSize);
            material.SetFloat(ShaderIds.GlowStrength, spillStrength);
            material.SetFloat(ShaderIds.GlowOpacity, spillOpacity);
            material.SetFloat(ShaderIds.GlowEdgeColour, spillFromEdge);
            material.SetFloat(ShaderIds.Flicker, lampFlicker);
            material.SetFloat(ShaderIds.SwapFlicker, swapFlicker);
            material.SetFloat(ShaderIds.Hotspot, hotspot);
            material.SetColor(ShaderIds.BackgroundColor, backgroundColor);
            material.SetFloat(ShaderIds.BackgroundOpacity,
                MixedReality.RoomBehind ? passthroughBackgroundOpacity : backgroundOpacity);

            if (targetCamera == null)
                targetCamera = GetComponent<Camera>();
            if (targetCamera == null)
                targetCamera = Camera.main;

            overlay = OverlayQuad.Spawn("Projector Overlay", targetCamera, material,
                                        startDelay, duration, distance);
            if (style == Style.Shorts && handheld)
                // Wide enough for the phone and the light around it.
                overlay.ConfigureHandheld(handDistance, handDrop,
                                          new Vector2(handHeight * 0.95f, handHeight), handFollowAngle);
            else
                overlay.ConfigurePlacement(anchoring, viewSize, followAngle);

            // The overlay renders its own copy of the material. The atlas, the
            // ready fade and the clock are set after this point, so they must go
            // to that copy -- written to the original, the quad never sees them.
            var template = material;
            material = overlay.RuntimeMaterial;
            Destroy(template);

            BuildPlayer();
            BuildAudio();
        }

        // Two sources on the phone itself, so the sound comes from where it is
        // and one clip can fade into the next.
        void BuildAudio()
        {
            if (shotAudio == null || shotAudio.Length == 0)
                return;

            voices = new AudioSource[2];
            for (var i = 0; i < voices.Length; i++)
            {
                var source = overlay.gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = true;
                source.spatialBlend = spatialBlend;
                source.minDistance = 0.3f;
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.volume = 0f;
                voices[i] = source;
            }
        }

        // Each clip's sound starts with its swipe, at the point in its shot the
        // tile is showing, and loops with it: tile and sound share one length.
        void UpdateAudio()
        {
            if (voices == null)
                return;

            var playing = overlay != null && overlay.isActiveAndEnabled && overlay.PlaybackTime >= 0f && ready > 0f;
            var fadeStep = Time.deltaTime / Mathf.Max(audioCrossfade, 0.01f);

            if (!playing)
            {
                foreach (var v in voices)
                {
                    v.volume = Mathf.MoveTowards(v.volume, 0f, fadeStep);
                    if (v.volume <= 0f && v.isPlaying)
                        v.Stop();
                }
                audioSlide = -1;
                return;
            }

            // The shader's own period, so sound changes exactly with the swipe.
            var swap = style == Style.Shorts ? swipeSeconds : swapSeconds;
            var period = Mathf.Max(secondsPerSlide, swap + 0.1f);
            var slide = Mathf.FloorToInt(overlay.PlaybackTime / period);
            if (slide != audioSlide)
            {
                audioSlide = slide;
                var tiles = Mathf.Max(1, atlasGrid.x * atlasGrid.y);
                var clip = shotAudio[slide % tiles % shotAudio.Length];
                if (clip != null)
                {
                    voice = 1 - voice;
                    var v = voices[voice];
                    v.clip = clip;
                    v.volume = 0f;
                    v.Play();
                    // Seek after Play, which starts from the top.
                    var videoTime = player != null ? (float)player.time : 0f;
                    v.time = Mathf.Repeat(videoTime, clip.length);
                }
            }

            // The newest clip fades up, the one before it fades out.
            voices[voice].volume = Mathf.MoveTowards(voices[voice].volume, volume * overlay.CurrentAlpha, fadeStep);
            var other = voices[1 - voice];
            other.volume = Mathf.MoveTowards(other.volume, 0f, fadeStep);
            if (other.volume <= 0f && other.isPlaying)
                other.Stop();
        }

        void BuildPlayer()
        {
            // No footage assigned: _VideoReady stays at zero, so the gate shows
            // only lamp light.
            if (!HasSource)
                return;

            var width = atlasClip != null && atlasClip.width > 0 ? (int)atlasClip.width : videoResolution.x;
            var height = atlasClip != null && atlasClip.height > 0 ? (int)atlasClip.height : videoResolution.y;

            var host = new GameObject("Projector Video Atlas");
            host.transform.SetParent(transform, false);

            // Mips are the spill: its colour is read from the small end of the
            // chain, where each tile has averaged down to a few texels.
            target = new RenderTexture(width, height, 0,
                                       RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "Projector Atlas RT",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                useMipMap = true,
                autoGenerateMips = true
            };
            target.Create();

            player = host.AddComponent<VideoPlayer>();
            player.playOnAwake = false;

            // Every tile wraps at the same instant by construction, so the atlas
            // loops without a visible reset.
            player.isLooping = true;
            player.waitForFirstFrame = false;
            player.skipOnDrop = true;
            player.renderMode = VideoRenderMode.RenderTexture;
            player.targetTexture = target;
            player.audioOutputMode = playAudio ? VideoAudioOutputMode.Direct : VideoAudioOutputMode.None;

            if (atlasClip != null)
            {
                player.source = VideoSource.VideoClip;
                player.clip = atlasClip;
            }
            else
            {
                player.source = VideoSource.Url;
                player.url = atlasUrl;
            }

            // Errors from the decoder are otherwise silent: the gate just stays
            // empty with nothing in the console to say why.
            player.errorReceived += (source, message) =>
                Debug.LogError($"Projector video error: {message}", this);
            player.prepareCompleted += source =>
            {
                Debug.Log($"Projector video prepared after {Time.realtimeSinceStartup - prepareStarted:0.00}s.", this);
                source.Play();
            };

            material.SetTexture(ShaderIds.VideoAtlas, target);
            RequestPrepare();
        }

        void RequestPrepare()
        {
            prepareStarted = Time.realtimeSinceStartup;
            lastFrame = -1;
            lastFrameChange = prepareStarted;
            player.Prepare();
        }

        void Update()
        {
            UpdateAudio();

            if (material == null || overlay == null || !overlay.isActiveAndEnabled)
                return;

            // The slide clock runs from the effect's own start, so the first
            // slide drops in as the effect appears and restarts restart it.
            material.SetFloat(ShaderIds.ShowTime, Mathf.Max(overlay.PlaybackTime, 0f));

            // Stop advancing once the overlay has faded out; otherwise the atlas
            // keeps burning decode budget behind a fully transparent quad.
            if (player == null)
                return;

            var now = Time.realtimeSinceStartup;

            if (!player.isPrepared)
            {
                // The first open of a clip in a session can stall in the platform
                // decoder. Rather than leave the gate empty until the next run,
                // start the prepare over.
                if (now - prepareStarted > PrepareTimeout)
                {
                    Debug.LogWarning($"Projector video not prepared after {PrepareTimeout:0}s; retrying.", this);
                    player.Stop();
                    RequestPrepare();
                }
                return;
            }

            if (!player.isPlaying)
                player.Play();

            // Show footage only once frames are actually arriving. A player can
            // report prepared and playing while the render texture still holds
            // nothing, which is what an empty gate is.
            var frame = player.frame;
            if (frame != lastFrame)
            {
                lastFrame = frame;
                lastFrameChange = now;
            }
            else if (now - lastFrameChange > StallTimeout)
            {
                Debug.LogWarning($"Projector video stalled on frame {frame}; restarting playback.", this);
                player.Stop();
                RequestPrepare();
                return;
            }

            if (frame < 1 || ready >= 1f)
                return;

            ready = Mathf.MoveTowards(ready, 1f, Time.deltaTime / Mathf.Max(videoFadeIn, 0.01f));
            material.SetFloat(ShaderIds.VideoReady, ready);
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

            // material is the overlay's instance now; the overlay destroys it.
        }

        static class ShaderIds
        {
            public static readonly int VideoAtlas = Shader.PropertyToID("_VideoAtlas");
            public static readonly int AtlasGrid = Shader.PropertyToID("_AtlasGrid");
            public static readonly int VideoReady = Shader.PropertyToID("_VideoReady");
            public static readonly int VideoGain = Shader.PropertyToID("_VideoGain");
            public static readonly int ShowTime = Shader.PropertyToID("_ShowTime");
            public static readonly int SecondsPerSlide = Shader.PropertyToID("_SecondsPerSlide");
            public static readonly int SwapSeconds = Shader.PropertyToID("_SwapSeconds");
            public static readonly int ScreenHalfHeight = Shader.PropertyToID("_ScreenHalfHeight");
            public static readonly int ScreenCorner = Shader.PropertyToID("_ScreenCorner");
            public static readonly int PhoneColor = Shader.PropertyToID("_PhoneColor");
            public static readonly int Bezel = Shader.PropertyToID("_Bezel");
            public static readonly int CaptionShade = Shader.PropertyToID("_CaptionShade");
            public static readonly int GateSize = Shader.PropertyToID("_GateSize");
            public static readonly int CornerRadius = Shader.PropertyToID("_CornerRadius");
            public static readonly int FrameColor = Shader.PropertyToID("_FrameColor");
            public static readonly int FrameWidth = Shader.PropertyToID("_FrameWidth");
            public static readonly int FrameOverlap = Shader.PropertyToID("_FrameOverlap");
            public static readonly int MountShadow = Shader.PropertyToID("_MountShadow");
            public static readonly int LampColor = Shader.PropertyToID("_LampColor");
            public static readonly int LampBrightness = Shader.PropertyToID("_LampBrightness");
            public static readonly int GlowSize = Shader.PropertyToID("_GlowSize");
            public static readonly int GlowStrength = Shader.PropertyToID("_GlowStrength");
            public static readonly int GlowOpacity = Shader.PropertyToID("_GlowOpacity");
            public static readonly int GlowEdgeColour = Shader.PropertyToID("_GlowEdgeColour");
            public static readonly int Flicker = Shader.PropertyToID("_Flicker");
            public static readonly int SwapFlicker = Shader.PropertyToID("_SwapFlicker");
            public static readonly int Hotspot = Shader.PropertyToID("_Hotspot");
            public static readonly int BackgroundColor = Shader.PropertyToID("_BackgroundColor");
            public static readonly int BackgroundOpacity = Shader.PropertyToID("_BackgroundOpacity");
        }
    }
}
