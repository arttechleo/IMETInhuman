using UnityEngine;
using UnityEngine.Serialization;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Runs the intro effects in order: the opening videos, the rain, then the
    /// Gaussian-splat sequence and the stereo video.
    ///
    /// The two effects are fully independent components and neither knows about
    /// the other. This sequencer only decides when each one starts, by writing
    /// their delays before their own Awake runs -- hence the execution order
    /// attribute, which is load-bearing.
    ///
    /// It is also the scene entry point kept so the existing reference in
    /// Intro.unity still resolves. Add <see cref="KaleidoscopeVideoOverlay"/> or
    /// <see cref="RainWindowOverlayController"/> to this object yourself to
    /// configure them in the inspector -- video clips, drop density and so on.
    /// Whatever is already present is sequenced rather than replaced.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public sealed class KaleidoscopeIntroBootstrap : MonoBehaviour
    {
        public enum Mode { FullIntro, CompareSplatsAndStereo, SplatsHandTest }

        [Tooltip("Full intro: videos, rain, splats, stereo. Compare: only the splat sequence " +
                 "and the stereo video, one after the other, looping, to compare the two. " +
                 "Splats hand test: the splat sequence alone, looping, for pinching it about.")]
        [SerializeField] Mode mode = Mode.FullIntro;
        [Tooltip("Compare mode: seconds of nothing between the two, so each reads on its own.")]
        [SerializeField] float compareGap = 2f;

        [Tooltip("Opens on a choice -- the whole piece, or the capture on its own -- and holds " +
                 "everything back until it is made.")]
        [SerializeField] bool showStartMenu = true;

        [SerializeField] bool playKaleidoscope = true;
        [SerializeField] bool playRain = true;
        [Tooltip("After the rain: a Gaussian-splat .ply sequence, frame by frame, no interpolation.")]
        [SerializeField] bool playSplatSequence = true;
        [Tooltip("Media folder of Luke's capture, played by the same splat player as its own chapter.")]
        [SerializeField] string lukeFolder = "LukeSeq";
        [Tooltip("With the splats: the room goes dark and the capture stands on water, reflected.")]
        [SerializeField] bool darkRoom = true;
        [Tooltip("After the splats: the side-by-side stereo video with alpha.")]
        [SerializeField] bool playStereoVideo = true;

        [Tooltip("Seconds before the first effect starts.")]
        [SerializeField] float introDelay;

        // Intro.unity already carries this value under its old name; migrate rather
        // than silently falling back to the initialiser.
        [FormerlySerializedAs("duration")]
        [SerializeField] float kaleidoscopeDuration = 30f;
        [SerializeField] float rainDuration = 30f;
        [Tooltip("Seconds before the rain ends at which the Humobox appears. Its take carries on " +
                 "after the rain has stopped.")]
        [SerializeField] float humoboxCue = 5f;
        [Tooltip("Seconds of quiet after the Humobox's take before the splats begin.")]
        [SerializeField] float humoboxTail = 2f;

        [Tooltip("Gap between the kaleidoscope ending and the rain starting. Negative overlaps them.")]
        [SerializeField] float gap;

        HumoboxRainCue humobox;
        KaleidoscopeVideoOverlay kaleidoscope;
        RainWindowOverlayController rain;
        SplatSequencePlayer splatSequence;
        StereoVideoPlayer stereoVideo;

        /// <summary>Seconds from the sequence start until the last effect ends.</summary>
        public float SequenceLength =>
            introDelay
            + (playKaleidoscope ? kaleidoscopeDuration + (playRain ? gap : 0f) : 0f)
            + (playRain ? rainDuration : 0f)
            + (splatSequence != null ? gap + splatSequence.Duration : 0f)
            + (stereoVideo != null ? gap + stereoVideo.Duration : 0f);

        bool Comparing => mode == Mode.CompareSplatsAndStereo;

        /// <summary>The splats alone, on a loop: everything else is out of the way.</summary>
        bool HandTesting => mode == Mode.SplatsHandTest;
        float loopAt = -1f;

        void Awake()
        {
            if (Comparing || HandTesting)
            {
                // Effects already in the scene start themselves in their own
                // Start; switch them off before it runs, not just unscheduled.
                foreach (var effect in GetComponents<KaleidoscopeVideoOverlay>())
                    effect.enabled = false;
                foreach (var effect in GetComponents<RainWindowOverlayController>())
                    effect.enabled = false;
                playKaleidoscope = false;
                playRain = false;
                playSplatSequence = true;
                playStereoVideo = !HandTesting;
                if (HandTesting)
                {
                    // The Humobox rides on the rain; without it, it never appears.
                    introDelay = 0f;
                    gap = 0f;
                }
            }

            if (playKaleidoscope)
            {
                kaleidoscope = GetComponent<KaleidoscopeVideoOverlay>();
                if (kaleidoscope == null)
                    kaleidoscope = gameObject.AddComponent<KaleidoscopeVideoOverlay>();
                kaleidoscope.Duration = kaleidoscopeDuration;
            }

            if (playRain)
            {
                rain = GetComponent<RainWindowOverlayController>();
                if (rain == null)
                    rain = gameObject.AddComponent<RainWindowOverlayController>();
                rain.Duration = rainDuration;
                // The Humobox comes in for the rain's last seconds and keeps
                // talking once it has stopped; what follows waits for the take.
                humobox = FindFirstObjectByType<HumoboxRainCue>();
                if (humobox != null)
                    humobox.SetCue(Mathf.Max(0f, rainDuration - humoboxCue));
                // Moss grows over the real room while it rains.
                if (GetComponent<RoomMoss>() == null)
                    gameObject.AddComponent<RoomMoss>();
            }

            if (playSplatSequence)
            {
                splatSequence = GetComponent<SplatSequencePlayer>();
                if (splatSequence == null)
                    splatSequence = gameObject.AddComponent<SplatSequencePlayer>();
                if (HandTesting)
                    splatSequence.Loop = true;

                // Pinch to move, turn and resize the capture. Switched off
                // for the locked chapters, where the hands do nothing.
                if (GetComponent<SplatHandGrab>() == null)
                    gameObject.AddComponent<SplatHandGrab>();

                // The dark, and the water the capture is reflected in.
                if (darkRoom && GetComponent<DarkRoom>() == null)
                    gameObject.AddComponent<DarkRoom>();
            }

            if (playStereoVideo)
            {
                stereoVideo = GetComponent<StereoVideoPlayer>();
                if (stereoVideo == null)
                    stereoVideo = gameObject.AddComponent<StereoVideoPlayer>();
            }

            // The menu holds everything until the viewer has chosen. The video
            // and rain effects start themselves in their own Start, so they are
            // switched off until then rather than started and stopped.
            if (showStartMenu && !Comparing && !HandTesting)
            {
                if (kaleidoscope != null)
                    kaleidoscope.enabled = false;
                if (rain != null)
                    rain.enabled = false;
                if (GetComponent<StartMenu>() == null)
                    gameObject.AddComponent<StartMenu>();
            }
            else
            {
                // Delays are written before the effects' own Start runs, which is
                // when they spawn their overlays with them.
                ForEachScheduled((effect, delay) => effect.PlayFromStart(delay));
            }

            // Raw passthrough camera frames to files/snaps (launch with --es snapEvery 5).
            if (float.TryParse(StartMenu.LaunchExtra("snapEvery"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var snapEvery) && snapEvery > 0f)
                gameObject.AddComponent<ImetInHuman.XR.PassthroughSnapshot>().every = snapEvery;

            // Number-key switching between effects, for testing in the Editor and
            // development builds only.
            if ((Application.isEditor || Debug.isDebugBuild) && GetComponent<EffectTestHotkeys>() == null)
                gameObject.AddComponent<EffectTestHotkeys>();
        }

        /// <summary>The pieces the start menu can play on their own.</summary>
        public enum Chapter
        {
            Everything, Shorts, Rain, Humobox,
            Splats, Stereo, DarkRoom,
            // Nothing responds to the hands, and nothing follows the head: the
            // captures stand where they are put while they are talked about.
            SplatsLocked, StereoLocked,
            // Luke's interview capture, with his voice.
            Luke,
            // Luke sits on a real seat found in the room, then jumps to the
            // stage chair for the guitar (LukeExperimentalChapter).
            LukeExperimental,
            // Parts of it on their own, for testing: the stage jump alone, the
            // seat and interview without the jump, and the seat-finder readout.
            LukeStage, LukeSeatInterview, LukeSeatDebug,
            // Intro: Pilot -- the Humobox lands in front of the viewer and keeps
            // them company for its take (HumoboxPilot), no rain.
            Pilot,
            // The pilot piece end to end: Shorts, rain, the Humobox (take 13) on a
            // table, resonance in the air, Luke's interview on a real seat, the
            // guitar on the stage. And the resonance alone, for testing.
            PilotSequence, ResonanceTest
        }

        static bool IsLukeExperimental(Chapter c) =>
            c is Chapter.LukeExperimental or Chapter.LukeStage or Chapter.LukeSeatInterview or Chapter.LukeSeatDebug;

        /// <summary>True while any part of the piece is still running.</summary>
        public bool Playing =>
            (kaleidoscope != null && kaleidoscope.IsPlaying)
            || (rain != null && rain.IsPlaying)
            || (splatSequence != null && splatSequence.IsPlaying)
            || (stereoVideo != null && stereoVideo.IsPlaying)
            || HumoboxRainCue.Speaking
            || (Pilot != null && Pilot.Playing)
            || sequence != Step.None
            || (lukeExperimental != null && lukeExperimental.Active);

        LukeExperimentalChapter lukeExperimental;

        enum Step { None, Intro, Humobox, Gap, Luke }
        Step sequence = Step.None;
        float stepSince;
        bool resonanceOnLuke, resonanceWholeTake;
        [Tooltip("Pilot sequence: seconds before the Humobox's take ends that the resonance begins.")]
        [SerializeField] float resonanceLead = 30f;
        [Tooltip("Pilot sequence: seconds into Luke's interview that the resonance fades.")]
        [SerializeField] float resonanceIntoLuke = 25f;

        ResonanceField resonance;
        ResonanceField Resonance
        {
            get
            {
                if (resonance == null)
                    resonance = GetComponent<ResonanceField>();
                if (resonance == null)
                    resonance = gameObject.AddComponent<ResonanceField>();
                return resonance;
            }
        }

        HumoboxPilot pilot;
        HumoboxPilot Pilot => pilot != null ? pilot : pilot = FindFirstObjectByType<HumoboxPilot>(FindObjectsInactive.Include);

        /// <summary>
        /// Plays one chapter on its own, for looking at a single part of the
        /// piece without sitting through the rest. Everything else is stopped
        /// first, so chapters can be switched between freely.
        /// </summary>
        public void Play(Chapter chapter)
        {
            StopEverything();

            // Luke's capture has a chapter of its own; everything else plays
            // the sequence set on the player.
            if (splatSequence != null)
                splatSequence.UseFolder(chapter == Chapter.Luke || IsLukeExperimental(chapter) ? lukeFolder : null);

            var dark = GetComponent<DarkRoom>();
            if (dark != null)
                dark.Active = chapter is Chapter.Everything or Chapter.DarkRoom;

            // Locked chapters are for talking over: hands do nothing to the
            // capture, and it never glides back into view by itself.
            var locked = chapter is Chapter.SplatsLocked or Chapter.StereoLocked || IsLukeExperimental(chapter);
            foreach (var grab in GetComponents<SplatHandGrab>())
                grab.enabled = !locked;
            if (stereoVideo != null)
                stereoVideo.Pinned = locked;

            switch (chapter)
            {
                case Chapter.Everything:
                    if (kaleidoscope != null)
                        kaleidoscope.enabled = true;
                    if (rain != null)
                        rain.enabled = true;
                    if (humobox != null)
                        humobox.SetCue(Mathf.Max(0f, rainDuration - humoboxCue));
                    ForEachScheduled((effect, delay) => effect.PlayFromStart(delay));
                    break;

                case Chapter.Shorts:
                    if (kaleidoscope == null)
                        break;
                    kaleidoscope.enabled = true;
                    kaleidoscope.PlayFromStart();
                    break;

                case Chapter.Rain:
                    if (rain == null)
                        break;
                    // No Humobox in this one: its cue is pushed past the end.
                    if (humobox != null)
                        humobox.SetCue(rainDuration + 1000f);
                    rain.enabled = true;
                    rain.PlayFromStart();
                    break;

                case Chapter.Humobox:
                    if (rain == null)
                        break;
                    // Straight to the entrance: it arrives a second in.
                    if (humobox != null)
                        humobox.SetCue(1f);
                    rain.enabled = true;
                    rain.PlayFromStart();
                    break;

                case Chapter.Splats:
                case Chapter.SplatsLocked:
                case Chapter.DarkRoom:
                case Chapter.Luke:
                    if (splatSequence == null)
                        break;
                    splatSequence.PlayFromStart();
                    break;

                case Chapter.Stereo:
                case Chapter.StereoLocked:
                    if (stereoVideo != null)
                        stereoVideo.PlayFromStart();
                    break;

                case Chapter.PilotSequence:
                    if (Pilot == null || kaleidoscope == null || rain == null)
                    {
                        Debug.LogWarning("Pilot sequence: needs the Shorts, the rain and the Humobox Pilot in the scene.", this);
                        break;
                    }
                    // The rain's own Humobox stays away; the pilot takes its place.
                    if (humobox != null)
                        humobox.SetCue(rainDuration + 1000f);
                    kaleidoscope.enabled = true;
                    rain.enabled = true;
                    kaleidoscope.PlayFromStart(introDelay);
                    rain.PlayFromStart(Mathf.Max(0f, introDelay + kaleidoscopeDuration + gap));
                    Enter(Step.Intro);
                    Debug.Log("Pilot sequence: Shorts, then rain.", this);
                    break;

                case Chapter.ResonanceTest:
                    if (Pilot == null)
                    {
                        Resonance.Listen(null, null);
                        break;
                    }
                    Pilot.Begin();
                    Enter(Step.Humobox);
                    resonanceWholeTake = true;   // straight away, for the length of the take
                    break;

                case Chapter.Pilot:
                    if (Pilot == null)
                    {
                        Debug.LogWarning("Intro: Pilot: no Humobox Pilot in the scene (IMETINHUMAN > Humobox > Add Humobox Pilot).", this);
                        break;
                    }
                    Pilot.Begin();
                    break;

                case Chapter.LukeExperimental:
                case Chapter.LukeStage:
                case Chapter.LukeSeatInterview:
                case Chapter.LukeSeatDebug:
                    BeginLuke(dark, chapter switch
                    {
                        Chapter.LukeStage => LukeExperimentalChapter.Mode.StageOnly,
                        Chapter.LukeSeatInterview => LukeExperimentalChapter.Mode.SeatInterview,
                        Chapter.LukeSeatDebug => LukeExperimentalChapter.Mode.SeatDebug,
                        _ => LukeExperimentalChapter.Mode.Full
                    });
                    break;
            }
        }

        void BeginLuke(DarkRoom dark, LukeExperimentalChapter.Mode mode)
        {
            if (splatSequence == null)
                return;
            SessionLog.Ensure();
            if (lukeExperimental == null)
                lukeExperimental = gameObject.AddComponent<LukeExperimentalChapter>();
            lukeExperimental.Begin(splatSequence, dark, mode);
        }

        void Enter(Step step)
        {
            sequence = step;
            stepSince = Time.time;
        }

        // The pilot sequence, one step at a time, each waiting on the last.
        void StepSequence()
        {
            switch (sequence)
            {
                case Step.Intro:
                    // As the old cue: the Humobox comes in for the rain's last seconds.
                    if (rain != null && rain.PlaybackTime >= rainDuration - humoboxCue)
                    {
                        Pilot.Begin();
                        Enter(Step.Humobox);
                        Debug.Log("Pilot sequence: the Humobox.", this);
                    }
                    break;

                case Step.Humobox:
                    if (Pilot.Landed && !Resonance.Playing && Pilot.TimeLeft <= (resonanceWholeTake ? float.MaxValue : resonanceLead))
                        Resonance.Listen(Pilot.transform, Pilot.Voice);
                    if (!Pilot.Playing)
                    {
                        Enter(Step.Gap);
                        if (resonanceWholeTake)
                        {
                            // The resonance test ends with the take.
                            Resonance.Stop();
                            sequence = Step.None;
                        }
                    }
                    break;

                case Step.Gap:
                    if (Time.time - stepSince >= humoboxTail)
                    {
                        // Luke's own chapter from here: find a seat, the interview,
                        // the guitar and the stage.
                        splatSequence?.UseFolder(lukeFolder);
                        foreach (var grab in GetComponents<SplatHandGrab>())
                            grab.enabled = false;
                        var dark = GetComponent<DarkRoom>();
                        if (dark != null)
                            dark.Active = false;
                        BeginLuke(dark, LukeExperimentalChapter.Mode.Full);
                        resonanceOnLuke = false;
                        Enter(Step.Luke);
                        Debug.Log("Pilot sequence: Luke.", this);
                    }
                    break;

                case Step.Luke:
                    if (!resonanceOnLuke && splatSequence != null && splatSequence.IsPlaying &&
                        splatSequence.PlaybackTime > 0.5f && splatSequence.Voice != null)
                    {
                        // His words ripple out from about where his mouth is.
                        Resonance.Listen(splatSequence.Voice.transform, splatSequence.Voice, new Vector3(0f, 1.05f, 0.05f));
                        resonanceOnLuke = true;
                    }
                    if (resonanceOnLuke && splatSequence.PlaybackTime > resonanceIntoLuke)
                        Resonance.Stop();
                    if (lukeExperimental == null || !lukeExperimental.Active)
                        if (Time.time - stepSince > 5f)
                            sequence = Step.None;
                    break;
            }
        }

        /// <summary>Stops every effect and takes the Humobox away.</summary>
        public void StopEverything()
        {
            sequence = Step.None;
            resonanceWholeTake = false;
            if (resonance != null)
                resonance.StopNow();
            if (lukeExperimental != null)
                lukeExperimental.End();
            if (kaleidoscope != null)
                kaleidoscope.StopNow();
            if (rain != null)
                rain.StopNow();
            if (splatSequence != null)
                splatSequence.StopNow();
            if (stereoVideo != null)
                stereoVideo.StopNow();
            foreach (var cue in FindObjectsByType<HumoboxRainCue>(FindObjectsSortMode.None))
                cue.EndNow();
            if (Pilot != null)
                Pilot.EndNow();
            loopAt = -1f;
        }

        /// <summary>What the start menu chose, when it only offers the two.</summary>
        public void Begin(bool fullExperience) =>
            Play(fullExperience ? Chapter.Everything : Chapter.Splats);

        /// <summary>Runs the whole intro again from its first effect.</summary>
        public void RestartSequence()
        {
            ForEachScheduled((effect, delay) => effect.PlayFromStart(delay));
        }

        void ForEachScheduled(System.Action<IIntroEffect, float> schedule)
        {
            var cursor = introDelay;

            if (kaleidoscope != null)
            {
                schedule(kaleidoscope, cursor);

                // Advance by the configured duration, not the component's own, so
                // the two stay in step if one is edited in the inspector.
                cursor += kaleidoscopeDuration + gap;
            }

            if (rain != null)
            {
                schedule(rain, Mathf.Max(0f, cursor));
                cursor += rainDuration + gap;
                // The take runs on past the rain; nothing follows until it ends.
                if (humobox != null)
                    cursor += Mathf.Max(0f, humobox.TakeEndsAtRainSecond - rainDuration) + humoboxTail;
            }

            // Each starts as the one before it ends, by its own length.
            var between = Comparing ? compareGap : gap;
            if (splatSequence != null && splatSequence.Duration > 0f)
            {
                schedule(splatSequence, Mathf.Max(0f, cursor));
                cursor += splatSequence.Duration + between;
            }

            if (stereoVideo != null)
            {
                schedule(stereoVideo, Mathf.Max(0f, cursor));
                cursor += stereoVideo.Duration + between;
            }

            // Compare mode plays the pair again and again.
            loopAt = Comparing ? Time.time + cursor : -1f;
        }

        void Update()
        {
            if (loopAt > 0f && Time.time >= loopAt)
                RestartSequence();
            if (sequence != Step.None)
                StepSequence();

            // The Humobox's take runs long past the rain; end it as the splats
            // begin, so each part plays on its own.
            if (splatSequence != null && splatSequence.IsPlaying && HumoboxRainCue.Speaking)
                foreach (var cue in FindObjectsByType<HumoboxRainCue>(FindObjectsSortMode.None))
                    cue.EndNow();
        }
    }
}
