using UnityEngine;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// The storm outside the glass booth, so the booth reads as shelter.
    ///
    /// Rain falls all around the viewer but never inside the booth; it rings the
    /// floor where it lands and splashes on the glass ceiling overhead. A soft
    /// procedural hiss with the ticks of single drops on the glass carries it
    /// in sound. Everything scales with the rain effect's fade, so the storm
    /// arrives and leaves with it.
    ///
    /// Built entirely in code: three particle systems on RainParticles.shader
    /// and a sound generated on the audio thread -- no assets.
    /// </summary>
    public sealed class RainStorm : MonoBehaviour
    {
        [Header("Falling Rain")]
        [Tooltip("Streaks per second at full strength.")]
        [SerializeField] float rainRate = 1400f;
        [Tooltip("How far out from the booth the rain falls, in metres.")]
        [SerializeField] float outerRadius = 5f;
        [SerializeField] float fallSpeed = 9f;
        [Tooltip("Height the rain falls from, above the floor.")]
        [SerializeField] float fallHeight = 4.5f;

        [Header("Splashes")]
        [SerializeField] float floorSplashRate = 380f;
        [SerializeField] float roofSplashRate = 140f;

        [Header("Sound")]
        [SerializeField, Range(0f, 1f)] float volume = 0.06f;   // ~-31 LUFS (0.35 measured -15.6 LUFS)
        [Tooltip("Storm volume while the Humobox speaks, as a share of normal, so its voice carries.")]
        [SerializeField, Range(0f, 1f)] float duckForVoice = 0.12f;
        [Tooltip("Single drops ticking on the glass, per second.")]
        [SerializeField] float ticksPerSecond = 22f;

        CameraLockedOverlay overlay;
        ParticleSystem rain, floorSplash, roofSplash;
        Material streakMaterial, ringMaterial;
        AudioSource sound;

        // Audio-thread state.
        volatile float audioGain;
        float lowL, lowR, tickEnvelope, tickPhase, tickFreq;
        uint noiseState = 0x9E3779B9u;
        int sampleRate = 48000;

        /// <summary>Raises the storm around a booth of this size.</summary>
        public void Build(CameraLockedOverlay rainOverlay, float boothRadius, float boothHeight)
        {
            overlay = rainOverlay;

            var shader = Shader.Find("IMETINHUMAN/VFX/Rain Particles");
            if (shader == null)
            {
                Debug.LogWarning("Rain Particles shader was not found.", this);
                enabled = false;
                return;
            }
            streakMaterial = new Material(shader) { name = "Rain Streaks" };
            streakMaterial.SetFloat(Ids.Shape, 0f);
            ringMaterial = new Material(shader) { name = "Rain Splashes" };
            ringMaterial.SetFloat(Ids.Shape, 1f);
            ringMaterial.SetFloat(Ids.Opacity, 0.35f);

            // Children of the booth, so the storm stands where the booth does.
            var root = overlay.transform;
            var ring = Mathf.Clamp01(1f - (boothRadius + 0.1f) / outerRadius);

            rain = MakeSystem("Rain", root, streakMaterial, ParticleSystemRenderMode.Stretch,
                              new Vector3(0f, fallHeight, 0f), outerRadius, ring, 1500);
            var main = rain.main;
            main.startLifetime = fallHeight / fallSpeed;
            main.startSpeed = 0f;
            main.startSize = 0.012f;
            main.startColor = new Color(1f, 1f, 1f, 0.8f);
            var velocity = rain.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);
            velocity.y = new ParticleSystem.MinMaxCurve(-fallSpeed, -fallSpeed * 0.85f);
            velocity.z = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);
            var stretch = rain.GetComponent<ParticleSystemRenderer>();
            stretch.velocityScale = 0.022f;
            stretch.lengthScale = 1f;

            floorSplash = MakeSystem("Floor Splashes", root, ringMaterial, ParticleSystemRenderMode.HorizontalBillboard,
                                     new Vector3(0f, 0.01f, 0f), outerRadius, ring, 400);
            SetupSplash(floorSplash, 0.12f);

            // On the roof glass, over the viewer's head.
            roofSplash = MakeSystem("Roof Splashes", root, ringMaterial, ParticleSystemRenderMode.HorizontalBillboard,
                                    new Vector3(0f, boothHeight + 0.005f, 0f), boothRadius * 0.98f, 1f, 200);
            SetupSplash(roofSplash, 0.07f);

            sampleRate = AudioSettings.outputSampleRate;
            sound = gameObject.AddComponent<AudioSource>();
            sound.playOnAwake = false;
            sound.loop = true;
            sound.spatialBlend = 0f;
            sound.volume = 1f;
            // A silent clip only to keep the source playing; the rain itself is
            // written in OnAudioFilterRead.
            sound.clip = AudioClip.Create("Rain Carrier", sampleRate, 2, sampleRate, false);
            sound.Play();

            SetIntensity(0f);
        }

        static ParticleSystem MakeSystem(string systemName, Transform parent, Material material,
                                         ParticleSystemRenderMode mode, Vector3 at, float radius,
                                         float radiusThickness, int maxParticles)
        {
            var go = new GameObject(systemName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            // A circle emitter lies in its local XY plane; turn it flat.
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

            var system = go.AddComponent<ParticleSystem>();
            system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = system.main;
            main.loop = true;
            main.playOnAwake = false;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;

            var shape = system.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = radius;
            shape.radiusThickness = radiusThickness;
            shape.arc = 360f;

            var emission = system.emission;
            emission.rateOverTime = 0f;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = mode;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            system.Play();
            return system;
        }

        static void SetupSplash(ParticleSystem system, float size)
        {
            var main = system.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.32f);
            main.startSpeed = 0f;
            main.startSize = size;
            var grow = system.sizeOverLifetime;
            grow.enabled = true;
            grow.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.25f, 1f, 1f));
            var fade = system.colorOverLifetime;
            fade.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                             new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            fade.color = gradient;
        }

        void Update()
        {
            if (overlay == null)
                return;
            SetIntensity(overlay.isActiveAndEnabled ? overlay.CurrentAlpha : 0f);
        }

        void SetIntensity(float intensity)
        {
            SetRate(rain, rainRate * intensity);
            SetRate(floorSplash, floorSplashRate * intensity);
            SetRate(roofSplash, roofSplashRate * intensity);
            if (streakMaterial != null)
                streakMaterial.SetFloat(Ids.Intensity, Mathf.Clamp01(intensity * 1.5f));
            if (ringMaterial != null)
                ringMaterial.SetFloat(Ids.Intensity, Mathf.Clamp01(intensity * 1.5f));
            audioGain = volume * intensity * (HumoboxRainCue.Speaking || HumoboxPilot.Speaking ? duckForVoice : 1f);
        }

        static void SetRate(ParticleSystem system, float rate)
        {
            if (system == null)
                return;
            var emission = system.emission;
            emission.rateOverTime = rate;
        }

        // Rain in sound: a soft band of noise for the downpour around, and single
        // drops ticking on the glass nearby -- each a short, decaying high chirp.
        void OnAudioFilterRead(float[] data, int channels)
        {
            var gain = audioGain;
            if (gain <= 0.0005f)
            {
                System.Array.Clear(data, 0, data.Length);
                return;
            }

            var tickChance = ticksPerSecond / sampleRate;
            var tickDecay = Mathf.Exp(-1f / (0.012f * sampleRate));

            for (var i = 0; i < data.Length; i += channels)
            {
                // Downpour: white noise, low-passed twice for a hiss, not a crackle.
                var nL = Noise();
                var nR = Noise();
                lowL += 0.18f * (nL - lowL);
                lowR += 0.18f * (nR - lowR);
                var hissL = (nL - lowL) * 0.35f + lowL * 0.5f;
                var hissR = (nR - lowR) * 0.35f + lowR * 0.5f;

                // Drops on the glass.
                if ((Noise() * 0.5f + 0.5f) < tickChance)
                {
                    tickEnvelope = 0.6f + 0.4f * (Noise() * 0.5f + 0.5f);
                    tickFreq = (2200f + 2600f * (Noise() * 0.5f + 0.5f)) * 2f * Mathf.PI / sampleRate;
                    tickPhase = 0f;
                }
                var tick = 0f;
                if (tickEnvelope > 0.001f)
                {
                    tickPhase += tickFreq;
                    tickFreq *= 0.9995f;   // pitch falls a little, as a drop does
                    tick = Mathf.Sin(tickPhase) * tickEnvelope * 0.5f;
                    tickEnvelope *= tickDecay;
                }

                data[i] = (hissL + tick) * gain;
                if (channels > 1)
                    data[i + 1] = (hissR + tick * 0.8f) * gain;
                for (var c = 2; c < channels; c++)
                    data[i + c] = data[i] * 0.5f;
            }
        }

        // xorshift, -1..1: cheap and allocation-free on the audio thread.
        float Noise()
        {
            noiseState ^= noiseState << 13;
            noiseState ^= noiseState >> 17;
            noiseState ^= noiseState << 5;
            return (noiseState & 0xFFFFFF) / (float)0x7FFFFF - 1f;
        }

        void OnDestroy()
        {
            if (streakMaterial != null)
                Destroy(streakMaterial);
            if (ringMaterial != null)
                Destroy(ringMaterial);
        }

        static class Ids
        {
            public static readonly int Shape = Shader.PropertyToID("_Shape");
            public static readonly int Opacity = Shader.PropertyToID("_Opacity");
            public static readonly int Intensity = Shader.PropertyToID("_Intensity");
        }
    }
}
