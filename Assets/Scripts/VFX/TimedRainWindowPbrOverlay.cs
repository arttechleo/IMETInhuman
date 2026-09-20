using UnityEngine;

namespace ImetInHuman.VFX
{
    public sealed class TimedRainWindowPbrOverlay : MonoBehaviour
    {
        [SerializeField] Camera targetCamera;
        [SerializeField] Material rainMaterial;
        [SerializeField] float startDelay = 30f;
        [SerializeField] float duration = 30f;
        [SerializeField] float fadeIn = 1.25f;
        [SerializeField] float fadeOut = 3f;
        [SerializeField] float distance = 0.35f;
        [SerializeField, Range(1f, 1.6f)] float overscan = 1.12f;

        MeshRenderer meshRenderer;
        Material runtimeMaterial;
        float elapsed;

        // Generic camera-locked timed overlay; also drives the kaleidoscope quad.
        public void Configure(Camera camera, Material material, float delay, float overlayDuration, float planeDistance = -1f)
        {
            targetCamera = camera;
            rainMaterial = material;
            startDelay = delay;
            duration = overlayDuration;
            if (planeDistance > 0f)
                distance = planeDistance;
            ApplyMaterial();
            SetAlpha(0f);
        }

        void Awake()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            ApplyMaterial();
            SetAlpha(0f);
        }

        void LateUpdate()
        {
            if (targetCamera == null)
                targetCamera = Camera.main;

            FitToCamera();

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

        void FitToCamera()
        {
            if (targetCamera == null)
                return;

            // fieldOfView/aspect describe the mono camera. HMD frusta are asymmetric
            // and differ per eye, so fitting from them leaves the overlay short of
            // the view edges. Derive the union of both eye frusta instead.
            float left, right, bottom, top;
            if (targetCamera.stereoEnabled)
            {
                GetFrustumTangents(targetCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left),
                    out var l0, out var r0, out var b0, out var t0);
                GetFrustumTangents(targetCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right),
                    out var l1, out var r1, out var b1, out var t1);

                left = Mathf.Min(l0, l1);
                right = Mathf.Max(r0, r1);
                bottom = Mathf.Min(b0, b1);
                top = Mathf.Max(t0, t1);
            }
            else
            {
                GetFrustumTangents(targetCamera.projectionMatrix, out left, out right, out bottom, out top);
            }

            left *= distance;
            right *= distance;
            bottom *= distance;
            top *= distance;

            // Margin covers head rotation between LateUpdate and photon, so
            // reprojection never drags an overlay edge into view.
            var width = (right - left) * overscan;
            var height = (top - bottom) * overscan;
            var centerVS = new Vector3((left + right) * 0.5f, (bottom + top) * 0.5f, distance);

            transform.SetPositionAndRotation(
                targetCamera.transform.TransformPoint(centerVS),
                targetCamera.transform.rotation);
            transform.localScale = new Vector3(width, height, 1f);
        }

        // Frustum edges as tangents (multiply by depth for metres). Unity view space
        // looks down -Z, so x_ndc = (m00 * x + m02 * z) / -z.
        static void GetFrustumTangents(Matrix4x4 p, out float left, out float right, out float bottom, out float top)
        {
            left = (p.m02 - 1f) / p.m00;
            right = (p.m02 + 1f) / p.m00;
            bottom = (p.m12 - 1f) / p.m11;
            top = (p.m12 + 1f) / p.m11;
        }

        void ApplyMaterial()
        {
            meshRenderer = GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                return;

            if (runtimeMaterial != null)
                Destroy(runtimeMaterial);

            runtimeMaterial = rainMaterial != null ? new Material(rainMaterial) : meshRenderer.material;
            meshRenderer.sharedMaterial = runtimeMaterial;
        }

        void SetAlpha(float alpha)
        {
            if (runtimeMaterial != null)
                runtimeMaterial.SetFloat("_Alpha", alpha);
        }
    }
}
