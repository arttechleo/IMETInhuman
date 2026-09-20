using UnityEngine;
using UnityEngine.UI;

namespace ImetInHuman.VFX
{
    [RequireComponent(typeof(Graphic))]
    public sealed class TimedKaleidoscopeOverlay : MonoBehaviour
    {
        [SerializeField] float duration = 30f;
        [SerializeField] float startDelay;
        [SerializeField] float fadeIn = 1.25f;
        [SerializeField] float fadeOut = 3f;
        [SerializeField] Material overlayMaterial;

        Material runtimeMaterial;
        float elapsed;

        public void Configure(float overlayDuration, Material material, float delay = 0f)
        {
            duration = overlayDuration;
            overlayMaterial = material;
            startDelay = delay;
            ApplyMaterial();
            SetAlpha(0f);
        }

        void Awake()
        {
            ApplyMaterial();
            SetAlpha(0f);
        }

        void Update()
        {
            elapsed += Time.deltaTime;

            if (elapsed < startDelay)
            {
                SetAlpha(0f);
                return;
            }

            var playbackTime = elapsed - startDelay;

            if (playbackTime >= duration)
            {
                SetAlpha(0f);
                gameObject.SetActive(false);
                return;
            }

            var inAlpha = fadeIn <= 0f ? 1f : Mathf.Clamp01(playbackTime / fadeIn);
            var outStart = Mathf.Max(0f, duration - fadeOut);
            var outAlpha = playbackTime < outStart || fadeOut <= 0f ? 1f : 1f - Mathf.Clamp01((playbackTime - outStart) / fadeOut);
            SetAlpha(Mathf.Min(inAlpha, outAlpha));
        }

        void OnDestroy()
        {
            if (runtimeMaterial != null)
                Destroy(runtimeMaterial);
        }

        void SetAlpha(float alpha)
        {
            if (runtimeMaterial != null)
                runtimeMaterial.SetFloat("_Alpha", alpha);
        }

        void ApplyMaterial()
        {
            var graphic = GetComponent<Graphic>();
            if (graphic == null)
                return;

            if (runtimeMaterial != null)
                Destroy(runtimeMaterial);

            runtimeMaterial = overlayMaterial != null ? new Material(overlayMaterial) : new Material(graphic.material);
            graphic.material = runtimeMaterial;
        }
    }
}
