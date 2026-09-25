using UnityEngine;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Resonance in the air: whoever is speaking (the Humobox, then Luke) sends
    /// out a spherical wave with every syllable, drawn by the Resonance Air
    /// shader as a bubble of bent room with droplets glinting on its front.
    ///
    /// Listens to the speaker's own AudioSource (GetOutputData, so it follows
    /// exactly what is heard, whatever the take): a fast loudness envelope
    /// against a slow one marks each syllable; the louder, the stronger the
    /// wave. When the voice is quiet a slow idle pulse keeps the air alive.
    ///
    /// The waves stay where they were sent from, in the room; only the canvas
    /// they are drawn on (an inward sphere) moves with the viewer.
    /// </summary>
    public sealed class ResonanceField : MonoBehaviour
    {
        const int Waves = 8;

        [SerializeField] float canvasRadius = 6f;
        [Tooltip("Metres a second a wave grows. Real sound is 343; this is for the eye.")]
        [SerializeField] float waveSpeed = 0.9f;
        [SerializeField] float waveLife = 4.5f;
        [Tooltip("Fast loudness over slow loudness that counts as a new syllable.")]
        [SerializeField] float onsetRatio = 1.45f;
        [SerializeField] float minLoudness = 0.015f;
        [SerializeField] float minGap = 0.2f;
        [SerializeField] float loudnessToAmplitude = 7f;
        [Tooltip("Seconds between idle pulses while the voice is quiet; 0 for none.")]
        [SerializeField] float idleEvery = 1.6f;
        [SerializeField, Range(0f, 1f)] float idleAmplitude = 0.3f;
        [SerializeField] float fadeSeconds = 1.5f;

        Transform mouth;
        Vector3 mouthOffset;
        AudioSource voice;
        Material material;
        Transform canvas;
        readonly Vector4[] waves = new Vector4[Waves];
        readonly float[] amps = new float[Waves];
        readonly float[] born = new float[Waves];
        readonly float[] strength = new float[Waves];
        readonly Vector3[] origin = new Vector3[Waves];
        readonly float[] samples = new float[512];
        int next;
        float fast, slow, lastOnset = -10f, lastIdle, alpha, targetAlpha;
        bool running;

        public bool Playing => running || alpha > 0.001f;

        static readonly int WavesId = Shader.PropertyToID("_Waves");
        static readonly int AmpId = Shader.PropertyToID("_WaveAmp");
        static readonly int AlphaId = Shader.PropertyToID("_Alpha");

        /// <summary>Starts (or retargets) listening to <paramref name="source"/>, waves leaving <paramref name="from"/> + offset.</summary>
        public void Listen(Transform from, AudioSource source, Vector3 offset = default)
        {
            mouth = from;
            voice = source;
            mouthOffset = offset;
            if (!running)
            {
                EnsureCanvas();
                for (var i = 0; i < Waves; i++)
                    strength[i] = 0f;
                running = true;
            }
            targetAlpha = 1f;
            Debug.Log($"Resonance: listening to {(source != null ? source.name : "nothing (idle pulses)")} at {(from != null ? from.name : "-")}.", this);
        }

        /// <summary>Fades out; waves already in the air finish growing.</summary>
        public void Stop() => targetAlpha = 0f;

        /// <summary>Gone at once.</summary>
        public void StopNow()
        {
            targetAlpha = alpha = 0f;
            running = false;
            if (canvas != null)
                canvas.gameObject.SetActive(false);
        }

        void EnsureCanvas()
        {
            if (canvas == null)
            {
                var shader = Shader.Find("IMETINHUMAN/VFX/Resonance Air");
                if (shader == null)
                {
                    Debug.LogWarning("Resonance: shader not found.", this);
                    return;
                }
                material = new Material(shader) { name = "Resonance Air" };
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Resonance Canvas";
                Destroy(go.GetComponent<Collider>());
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                canvas = go.transform;
                canvas.localScale = Vector3.one * (canvasRadius * 2f);
            }
            canvas.gameObject.SetActive(true);
        }

        void Update()
        {
            if (!running || material == null)
                return;

            var eye = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            canvas.position = eye;

            alpha = Mathf.MoveTowards(alpha, targetAlpha, Time.deltaTime / Mathf.Max(fadeSeconds, 0.01f));
            if (alpha <= 0f && targetAlpha <= 0f)
            {
                StopNow();
                return;
            }

            Listen();

            var now = Time.time;
            for (var i = 0; i < Waves; i++)
            {
                var age = now - born[i];
                var life = Mathf.Clamp01(1f - age / waveLife);
                amps[i] = strength[i] * life * Mathf.Sqrt(life);
                waves[i] = new Vector4(origin[i].x, origin[i].y, origin[i].z, waveSpeed * Mathf.Max(age, 0f));
            }
            material.SetVectorArray(WavesId, waves);
            material.SetFloatArray(AmpId, amps);
            material.SetFloat(AlphaId, alpha);
        }

        void Listen()
        {
            var loud = 0f;
            if (voice != null && voice.isPlaying)
            {
                voice.GetOutputData(samples, 0);
                var sum = 0f;
                foreach (var s in samples)
                    sum += s * s;
                loud = Mathf.Sqrt(sum / samples.Length);
            }
            fast = Mathf.Lerp(fast, loud, 1f - Mathf.Exp(-Time.deltaTime * 30f));
            slow = Mathf.Lerp(slow, loud, 1f - Mathf.Exp(-Time.deltaTime * 2.5f));

            var now = Time.time;
            if (fast > minLoudness && fast > slow * onsetRatio && now - lastOnset > minGap)
            {
                lastOnset = now;
                Emit(Mathf.Clamp01(fast * loudnessToAmplitude));
            }
            else if (idleEvery > 0f && now - lastOnset > idleEvery && now - lastIdle > idleEvery)
            {
                lastIdle = now;
                Emit(idleAmplitude);
            }
        }

        void Emit(float amplitude)
        {
            var at = mouth != null ? mouth.TransformPoint(mouthOffset) : (Camera.main != null
                ? Camera.main.transform.position + Camera.main.transform.forward * 1.2f
                : Vector3.forward);
            origin[next] = at;
            born[next] = Time.time;
            strength[next] = Mathf.Max(0.15f, amplitude);
            next = (next + 1) % Waves;
        }

        void OnDestroy()
        {
            if (canvas != null)
                Destroy(canvas.gameObject);
            if (material != null)
                Destroy(material);
        }
    }
}
