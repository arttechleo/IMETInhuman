using ImetInHuman.XR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Standalone rain-on-glass effect. Owns its own quad and material; shares
    /// nothing with the kaleidoscope.
    ///
    /// The shader needs both the opaque and depth textures to refract correctly --
    /// depth is what tells a drop how far the background is, and therefore how much
    /// to bend. Without it the effect degrades to rim and specular only, so this
    /// component checks the active pipeline and sets the keyword accordingly rather
    /// than sampling textures that were never rendered.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RainWindowOverlayController : MonoBehaviour, IIntroEffect
    {
        [Header("Placement")]
        [SerializeField] Camera targetCamera;
        [SerializeField] float startDelay = 30f;
        [SerializeField] float duration = 30f;
        [Tooltip("Metres from the eye. World-anchored, this is how far away the pane " +
                 "stands; close panes swing a lot as the head moves.")]
        [SerializeField] float distance = 0.35f;

        [Tooltip("World: placed in front of the viewer when it starts, then stays in the " +
                 "room. Head: follows every head movement.")]
        [SerializeField] CameraLockedOverlay.Anchoring anchoring = CameraLockedOverlay.Anchoring.Booth;
        [Tooltip("Share of the view it covers when placed. 1 fills it.")]
        [SerializeField, Range(0.1f, 1.5f)] float viewSize = 1f;
        [Tooltip("World only: glide back in front of the viewer once they have turned " +
                 "this many degrees away. 0 stays put.")]
        [SerializeField, Range(0f, 180f)] float followAngle;
        [Tooltip("Booth: metres from the viewer to the glass all round.")]
        [SerializeField, Range(0.4f, 3f)] float boothRadius = 1f;
        [Tooltip("Booth: height of the glass, from the floor.")]
        [SerializeField, Range(1f, 4f)] float boothHeight = 2.6f;
        [Tooltip("Booth: close it with a glass floor and ceiling, beaded with drops that sit " +
                 "round and still -- on horizontal glass water has no downhill to run.")]
        [SerializeField] bool boothCaps = true;
        [Tooltip("Booth: rain falling all around outside the glass, splashing on the floor and " +
                 "on the roof overhead, with its sound -- so the booth reads as shelter.")]
        [SerializeField] bool stormOutside = true;
        [Tooltip("Booth without caps: how far down from its top rim the rain fades out, in booth heights.")]
        [SerializeField, Range(0f, 0.5f)] float boothTopFade = 0.12f;

        [Tooltip("World only: how far in from the pane's edges the rain fades out, in " +
                 "pane heights, so the border is soft when the head turns to it.")]
        [SerializeField, Range(0f, 0.3f)] float edgeFade = 0.08f;

        [Header("Airplane Window")]
        [SerializeField] bool windowAperture;
        [SerializeField] Color frameColor = new Color(0.34f, 0.34f, 0.36f, 1f);
        [SerializeField, Range(0.2f, 1.2f)] float apertureWidth = 0.82f;
        [SerializeField, Range(0.2f, 1.2f)] float apertureHeight = 0.95f;
        [SerializeField, Range(2f, 8f)] float cornerRoundness = 2.6f;

        [Header("Water Physics")]
        [SerializeField, Range(1f, 1.8f)] float indexOfRefraction = 1.333f;
        [Tooltip("Clean glass sheds water at ~25 deg. Weathered or waxed glass beads at 70-100 deg.")]
        [SerializeField, Range(10f, 110f)] float contactAngle = 74f;
        [SerializeField, Range(0f, 1f)] float smoothness = 0.97f;
        [SerializeField, Range(0f, 4f)] float thickness = 1f;

        [Header("Static Beads")]
        [Tooltip("Beads never move: below a size threshold a drop cannot overcome contact-line pinning, which is most of what sits on a wet window.")]
        [SerializeField, Range(4f, 60f)] float beadDensity = 36f;
        [Tooltip("Skews the population towards small drops. A flat distribution reads as polka dots.")]
        [SerializeField, Range(0.25f, 6f)] float sizeBias = 2.0f;
        [SerializeField, Range(0.001f, 0.3f)] float edgeSoftness = 0.05f;
        [SerializeField, Range(0f, 4f)] float normalStrength = 1f;
        [Tooltip("Drops three bead layers to two. The first thing to trade on standalone HMDs.")]
        [SerializeField] bool reducedLayers;
        [Tooltip("Reduced layers on the headset regardless of the setting above. The layer " +
                 "dropped is the finest one, mostly beads a pixel or two across at Quest resolution.")]
        [SerializeField] bool passthroughReducedLayers = true;

        [Header("Runners")]
        [Tooltip("How many vertical lanes can carry a running drop. Runners cross the entire pane, not one grid cell.")]
        [SerializeField, Range(1f, 40f)] float runnerColumns = 11f;
        [Tooltip("Lower is slower and longer-lived. One full descent takes roughly 1/speed seconds.")]
        [SerializeField, Range(0f, 2f)] float runnerSpeed = 0.11f;
        [SerializeField, Range(0.002f, 0.08f)] float runnerStartSize = 0.012f;
        [Tooltip("How much a runner grows as it absorbs the beads in its path. Growth is what drives the acceleration.")]
        [SerializeField, Range(0f, 6f)] float accretion = 2.2f;
        [SerializeField, Range(0f, 2f)] float trackStrength = 0.6f;

        [Header("Optics")]
        [SerializeField, Range(0f, 3f)] float refractionScale = 1f;
        [SerializeField, Range(0.05f, 20f)] float maxBackgroundDistance = 4f;
        [Tooltip("A drop images a far wider angle than the screen holds. This caps the deflection so the excess does not smear edge texels.")]
        [SerializeField, Range(0.01f, 1f)] float maxDeflection = 0.35f;
        [Tooltip("Splits refraction into three taps. Two extra dependent samples per pixel; the first optical feature to drop on standalone HMDs.")]
        [SerializeField] bool chromaticDispersion = true;
        [SerializeField, Range(0f, 4f)] float dispersion = 1f;

        [Header("Stylisation")]
        [SerializeField] Color waterTint = new Color(0.72f, 0.88f, 1f, 1f);
        [Tooltip("Water this thin is optically colourless. This dial is an artistic override, not physics.")]
        [SerializeField, Range(0f, 1f)] float tintAmount = 0.1f;
        [SerializeField, Range(0f, 2f)] float environmentReflection = 1f;
        [Tooltip("Ambient probe light entering at the contact line and trail film -- the only parts of a drop rough enough to scatter.")]
        [SerializeField, Range(0f, 2f)] float ambientScatter = 0.35f;
        [SerializeField, Range(0f, 1f)] float opacity = 1f;

        [Header("Condensation")]
        [Tooltip("Fog forming on the pane under the drops. Drops stay clear lenses " +
                 "through it and runners wipe clear tracks.")]
        [SerializeField] bool condensation = true;

        [Tooltip("Second of the rain effect at which fog starts to form.")]
        [SerializeField, Range(0f, 60f)] float fogStartSecond = 5f;

        [Tooltip("Second of the rain effect by which the fog has finished growing.")]
        [SerializeField, Range(0f, 60f)] float fogFullSecond = 25f;

        [Tooltip("Seconds the fog takes to appear at its start coverage, so it " +
                 "mists in rather than popping on.")]
        [SerializeField, Range(0f, 5f)] float fogAppearSeconds = 1.5f;

        [Tooltip("Fraction of the pane fogged when the fog first forms.")]
        [SerializeField, Range(0f, 1f)] float fogStartCoverage = 0.15f;

        [Tooltip("Fraction of the pane fogged once it has finished growing.")]
        [SerializeField, Range(0f, 1f)] float fogEndCoverage = 0.75f;

        [SerializeField, Range(0f, 1f)] float fogOpacity = 0.92f;
        [Tooltip("Fog opacity on the headset. Fog there is a flat milky layer over " +
                 "the room rather than a blur of it, so it needs to be thinner to still read as fog.")]
        [SerializeField, Range(0f, 1f)] float passthroughFogOpacity = 0.55f;

        [Header("Headset Camera")]
        [Tooltip("On the headset, refract and blur the real room from the colour camera " +
                 "(PassthroughCameraFeed). Off, drops are rims and highlights only and fog is flat.")]
        [SerializeField] bool useRoomCamera = true;
        [Tooltip("How far the view behind the fog is blurred, in screen fractions.")]
        [SerializeField, Range(0f, 0.05f)] float fogBlur = 0.014f;
        [Tooltip("0 is a plain blur; 1 is a flat milky grey.")]
        [SerializeField, Range(0f, 1f)] float fogMilkiness = 0.38f;
        [SerializeField] Color fogColor = new Color(0.86f, 0.89f, 0.92f, 1f);

        Material material;
        CameraLockedOverlay overlay;

        readonly Vector4[] runnerHeadsA = new Vector4[RainRunnerHeads.Slots];
        readonly Vector4[] runnerHeadsB = new Vector4[RainRunnerHeads.Slots];

        bool RoomCamera => MixedReality.PassthroughActive && useRoomCamera;

        // Flat fog over passthrough has to be thin to still read as fog; fog that
        // blurs the room from the camera can be as dense as it is on screen.
        float FogOpacity => MixedReality.PassthroughActive && !useRoomCamera ? passthroughFogOpacity : fogOpacity;

        /// <summary>
        /// Seconds from scene start before the effect begins. Set by a sequencer
        /// before Start runs; see <see cref="KaleidoscopeIntroBootstrap"/>.
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

        public string EffectName => "Rain droplets";

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
        }

        public void StopNow()
        {
            if (overlay != null)
                overlay.Stop();
        }

        // Start, not Awake: AddComponent runs Awake synchronously, so a sequencer
        // adding this component could never set StartDelay in time. Every Awake in
        // the scene precedes every Start, which makes the hand-off safe.
        void Start()
        {
            var shader = Shader.Find("IMETINHUMAN/VFX/Rain Window PBR Overlay");
            if (shader == null)
            {
                Debug.LogWarning("Rain Window PBR Overlay shader was not found.", this);
                enabled = false;
                return;
            }

            material = new Material(shader) { name = "Runtime Rain Window Overlay" };

            material.SetFloat(ShaderIds.IOR, indexOfRefraction);
            material.SetFloat(ShaderIds.ContactAngle, contactAngle);
            material.SetFloat(ShaderIds.Smoothness, smoothness);
            material.SetFloat(ShaderIds.Thickness, thickness);

            material.SetFloat(ShaderIds.DropDensity, beadDensity);
            material.SetFloat(ShaderIds.SizeBias, sizeBias);
            material.SetFloat(ShaderIds.EdgeSoftness, edgeSoftness);
            material.SetFloat(ShaderIds.NormalStrength, normalStrength);

            material.SetFloat(ShaderIds.RunnerColumns, runnerColumns);
            material.SetFloat(ShaderIds.RunnerSpeed, runnerSpeed);
            material.SetFloat(ShaderIds.RunnerSize, runnerStartSize);
            material.SetFloat(ShaderIds.Accretion, accretion);
            material.SetFloat(ShaderIds.TrailStrength, trackStrength);

            material.SetColor(ShaderIds.FrameColor, frameColor);
            material.SetFloat(ShaderIds.WindowWidth, apertureWidth);
            material.SetFloat(ShaderIds.WindowHeight, apertureHeight);
            material.SetFloat(ShaderIds.WindowRound, cornerRoundness);

            material.SetFloat(ShaderIds.RefractionScale, refractionScale);
            material.SetFloat(ShaderIds.MaxTravel, maxBackgroundDistance);
            material.SetFloat(ShaderIds.MaxDeflection, maxDeflection);
            material.SetFloat(ShaderIds.Dispersion, dispersion);

            material.SetColor(ShaderIds.BaseTint, waterTint);
            material.SetFloat(ShaderIds.Stylize, tintAmount);
            material.SetFloat(ShaderIds.EnvReflection, environmentReflection);
            material.SetFloat(ShaderIds.AmbientScatter, ambientScatter);
            material.SetFloat(ShaderIds.Opacity, opacity);

            material.SetFloat(ShaderIds.FogOpacity, FogOpacity);
            material.SetFloat(ShaderIds.FogBlur, fogBlur);
            material.SetFloat(ShaderIds.FogMilk, fogMilkiness);
            material.SetColor(ShaderIds.FogColor, fogColor);
            // No fog until fogStartSecond; Update takes it from there.
            material.SetFloat(ShaderIds.FogCoverage, 0f);

            var refractionAvailable = SceneRefractionAvailable(out var reason);
            SetKeyword(material, "_REFRACTION", ShaderIds.RefractionEnabled, refractionAvailable);
            SetKeyword(material, "_DISPERSION", ShaderIds.DispersionEnabled, chromaticDispersion && refractionAvailable);
            SetKeyword(material, "_DROPS_LOW", ShaderIds.DropsLow,
                reducedLayers || (passthroughReducedLayers && MixedReality.PassthroughActive));
            SetKeyword(material, "_WINDOW_FRAME", ShaderIds.WindowFrameEnabled, windowAperture);
            if (RoomCamera)
                material.EnableKeyword("_PASSTHROUGH_CAMERA");
            else
                material.DisableKeyword("_PASSTHROUGH_CAMERA");

            if (!refractionAvailable)
                Debug.LogWarning($"Rain overlay refraction disabled: {reason}", this);

            if (targetCamera == null)
                targetCamera = GetComponent<Camera>();
            if (targetCamera == null)
                targetCamera = Camera.main;

            var booth = anchoring == CameraLockedOverlay.Anchoring.Booth;
            material.SetFloat(ShaderIds.EdgeFade, anchoring == CameraLockedOverlay.Anchoring.World ? edgeFade : 0f);
            // With a ceiling the wall runs up to meet it; without, it fades out.
            material.SetFloat(ShaderIds.TopFade, booth && !boothCaps ? boothTopFade : 0f);
            // The booth is a surface in the room: nearer real things stay in front
            // of it. A pane pinned to the eye is always on top.
            material.SetFloat(ShaderIds.ZTest, (float)(booth ? CompareFunction.LessEqual : CompareFunction.Always));
            material.SetVector(ShaderIds.PaneSize, booth
                ? new Vector4(2f * Mathf.PI * boothRadius, boothHeight, 0f, 0f)
                : Vector4.zero);

            if (RoomCamera)
                PassthroughCameraFeed.EnsureExists(targetCamera);

            overlay = OverlayQuad.Spawn("Rain Window Overlay", targetCamera, material, startDelay, duration, distance);
            if (booth)
            {
                overlay.ConfigureBooth(boothRadius, boothHeight, boothCaps);

                // The caps share the walls' material; their own size, no runners
                // and round beads come from a property block. Runner heads are
                // written for the walls only.
                var capProperties = new MaterialPropertyBlock();
                capProperties.SetVector(ShaderIds.PaneSize, new Vector4(boothRadius * 2f, boothRadius * 2f, 0f, 0f));
                capProperties.SetFloat(ShaderIds.CellAspect, 1f / ReferenceAspect);
                capProperties.SetFloat(ShaderIds.Horizontal, 1f);
                foreach (var cap in overlay.BoothCaps)
                    cap.SetPropertyBlock(capProperties);

                // On its own object: this component sits on the camera, whose
                // AudioListener would otherwise take the storm's sound filter and
                // run the whole app's audio through it.
                if (stormOutside)
                {
                    var storm = new GameObject("Rain Storm");
                    storm.transform.SetParent(transform, false);
                    storm.AddComponent<RainStorm>().Build(overlay, boothRadius, boothHeight);
                }
            }
            else
                overlay.ConfigurePlacement(anchoring, viewSize, followAngle);
        }

        /// <summary>
        /// Refraction reads _CameraOpaqueTexture and _CameraDepthTexture. Sampling
        /// either when the pipeline did not render it returns black, not nothing.
        /// </summary>
        static bool SceneRefractionAvailable(out string reason)
        {
            // The room is composited behind the app, so the opaque texture holds
            // none of it. Refracting would bend empty black.
            if (MixedReality.PassthroughActive)
            {
                reason = "running over passthrough, which the app cannot sample.";
                return false;
            }

            var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (asset == null)
            {
                reason = "the active render pipeline is not URP.";
                return false;
            }

            if (!asset.supportsCameraOpaqueTexture)
            {
                reason = $"'{asset.name}' has Opaque Texture disabled.";
                return false;
            }

            if (!asset.supportsCameraDepthTexture)
            {
                reason = $"'{asset.name}' has Depth Texture disabled.";
                return false;
            }

            reason = null;
            return true;
        }

        static void SetKeyword(Material target, string keyword, int toggleId, bool enabled)
        {
            target.SetFloat(toggleId, enabled ? 1f : 0f);
            if (enabled)
                target.EnableKeyword(keyword);
            else
                target.DisableKeyword(keyword);
        }

        void Update()
        {
            if (overlay == null || !overlay.isActiveAndEnabled)
                return;

            // The overlay renders its own copy of the material, so everything
            // animated has to go to that copy.
            var target = overlay.RuntimeMaterial;
            if (target == null)
                return;

            UpdateRunners(target);

            if (!condensation)
                return;

            // Timed from the rain effect's own start, so restarting the effect
            // restarts the fog. Clear until fogStartSecond, then mists in at the
            // start coverage and grows, easing in and out so it creeps rather
            // than advancing at a fixed rate, until fogFullSecond. Holds after.
            var since = overlay.PlaybackTime - fogStartSecond;
            if (since < 0f)
            {
                target.SetFloat(ShaderIds.FogCoverage, 0f);
                return;
            }

            var growth = Mathf.Clamp01(since / Mathf.Max(fogFullSecond - fogStartSecond, 0.01f));
            var coverage = Mathf.Lerp(fogStartCoverage, fogEndCoverage, Mathf.SmoothStep(0f, 1f, growth));
            var appear = fogAppearSeconds > 0f ? Mathf.Clamp01(since / fogAppearSeconds) : 1f;

            target.SetFloat(ShaderIds.FogCoverage, Mathf.Max(coverage, 0.002f));
            target.SetFloat(ShaderIds.FogOpacity, FogOpacity * appear);
        }

        // Where every runner is this frame, for the shader to draw. Both lane sets
        // mirror AddRunnerSet in the shader: the second is sparser, slower,
        // bigger and grows more, offset so its lanes fall between the first's.
        void UpdateRunners(Material target)
        {
            var pane = overlay.PaneSize;
            var aspect = Mathf.Max(pane.x, 1e-4f) / Mathf.Max(pane.y, 1e-4f);

            // Drop grids widen with the surface so drops keep their spacing on
            // the wide booth; the counts are whole so it closes without a seam.
            // Must match the shader's CellCount exactly.
            var cellAspect = Mathf.Max(aspect / ReferenceAspect, 0.05f);
            target.SetFloat(ShaderIds.CellAspect, cellAspect);
            var mergeCells = new Vector2(CellCount(beadDensity * 0.85f * cellAspect), beadDensity * 0.7f);
            var columnsA = CellCount(runnerColumns * cellAspect);
            var columnsB = CellCount(runnerColumns * cellAspect * 0.6f);
            var time = Time.time;

            RainRunnerHeads.Compute(runnerHeadsA, columnsA, 0f, time, runnerSpeed,
                                    accretion, runnerStartSize, mergeCells, sizeBias, aspect);
            RainRunnerHeads.Compute(runnerHeadsB, columnsB, 3.7f, time,
                                    runnerSpeed * 0.72f, accretion * 1.3f, runnerStartSize * 1.5f,
                                    mergeCells, sizeBias, aspect);

            target.SetVectorArray(ShaderIds.RunnerHeadsA, runnerHeadsA);
            target.SetVectorArray(ShaderIds.RunnerHeadsB, runnerHeadsB);
        }

        // Width over height of the head-locked pane the drop densities were tuned on.
        const float ReferenceAspect = 1.25f;

        // The shader's CellCount: floor(x + 0.5), at least one.
        static float CellCount(float x) => Mathf.Max(Mathf.Floor(x + 0.5f), 1f);

        void OnDestroy()
        {
            if (material != null)
                Destroy(material);
        }

        static class ShaderIds
        {
            public static readonly int IOR = Shader.PropertyToID("_IOR");
            public static readonly int ContactAngle = Shader.PropertyToID("_ContactAngle");
            public static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
            public static readonly int Thickness = Shader.PropertyToID("_Thickness");

            public static readonly int DropDensity = Shader.PropertyToID("_DropDensity");
            public static readonly int SizeBias = Shader.PropertyToID("_SizeBias");
            public static readonly int EdgeSoftness = Shader.PropertyToID("_EdgeSoftness");
            public static readonly int NormalStrength = Shader.PropertyToID("_NormalStrength");
            public static readonly int DropsLow = Shader.PropertyToID("_DropsLow");

            public static readonly int RunnerColumns = Shader.PropertyToID("_RunnerColumns");
            public static readonly int RunnerSpeed = Shader.PropertyToID("_RunnerSpeed");
            public static readonly int RunnerSize = Shader.PropertyToID("_RunnerSize");
            public static readonly int Accretion = Shader.PropertyToID("_Accretion");
            public static readonly int TrailStrength = Shader.PropertyToID("_TrailStrength");
            public static readonly int RunnerHeadsA = Shader.PropertyToID("_RunnerHeadsA");
            public static readonly int RunnerHeadsB = Shader.PropertyToID("_RunnerHeadsB");

            public static readonly int RefractionEnabled = Shader.PropertyToID("_RefractionEnabled");
            public static readonly int RefractionScale = Shader.PropertyToID("_RefractionScale");
            public static readonly int MaxTravel = Shader.PropertyToID("_MaxTravel");
            public static readonly int MaxDeflection = Shader.PropertyToID("_MaxDeflection");
            public static readonly int DispersionEnabled = Shader.PropertyToID("_DispersionEnabled");
            public static readonly int Dispersion = Shader.PropertyToID("_Dispersion");

            public static readonly int WindowFrameEnabled = Shader.PropertyToID("_WindowFrameEnabled");
            public static readonly int FrameColor = Shader.PropertyToID("_FrameColor");
            public static readonly int WindowWidth = Shader.PropertyToID("_WindowWidth");
            public static readonly int WindowHeight = Shader.PropertyToID("_WindowHeight");
            public static readonly int WindowRound = Shader.PropertyToID("_WindowRound");

            public static readonly int BaseTint = Shader.PropertyToID("_BaseTint");
            public static readonly int Stylize = Shader.PropertyToID("_Stylize");
            public static readonly int EnvReflection = Shader.PropertyToID("_EnvReflection");
            public static readonly int AmbientScatter = Shader.PropertyToID("_AmbientScatter");
            public static readonly int Opacity = Shader.PropertyToID("_Opacity");
            public static readonly int EdgeFade = Shader.PropertyToID("_EdgeFade");
            public static readonly int TopFade = Shader.PropertyToID("_TopFade");
            public static readonly int ZTest = Shader.PropertyToID("_ZTest");
            public static readonly int PaneSize = Shader.PropertyToID("_PaneSize");
            public static readonly int CellAspect = Shader.PropertyToID("_CellAspect");
            public static readonly int Horizontal = Shader.PropertyToID("_Horizontal");

            public static readonly int FogCoverage = Shader.PropertyToID("_FogCoverage");
            public static readonly int FogOpacity = Shader.PropertyToID("_FogOpacity");
            public static readonly int FogBlur = Shader.PropertyToID("_FogBlur");
            public static readonly int FogMilk = Shader.PropertyToID("_FogMilk");
            public static readonly int FogColor = Shader.PropertyToID("_FogColor");
        }
    }
}
