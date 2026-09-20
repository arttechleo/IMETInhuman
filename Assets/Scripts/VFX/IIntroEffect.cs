namespace ImetInHuman.VFX
{
    /// <summary>
    /// An intro effect that can be started and stopped on demand, so effects can
    /// be tested one at a time without sitting through the whole sequence. See
    /// <see cref="EffectTestHotkeys"/>.
    /// </summary>
    public interface IIntroEffect
    {
        /// <summary>Shown in the test readout and the console.</summary>
        string EffectName { get; }

        /// <summary>Seconds the effect stays up once started, fades included.</summary>
        float Duration { get; }

        /// <summary>Seconds since the effect became visible; negative while it waits
        /// on its start delay, negative infinity when it is stopped or finished.</summary>
        float PlaybackTime { get; }

        bool IsPlaying { get; }

        /// <summary>Starts the effect after <paramref name="delay"/> seconds, from
        /// its beginning, whether or not it already ran.</summary>
        void PlayFromStart(float delay = 0f);

        /// <summary>Hides the effect immediately.</summary>
        void StopNow();
    }
}
