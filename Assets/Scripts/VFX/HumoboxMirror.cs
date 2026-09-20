using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Lets the rain see the Humobox.
    ///
    /// The drops refract and reflect the room from the headset camera, which
    /// sees only the real room -- so the Humobox was missing from every drop,
    /// and fog in front of it hid it outright. This renders just the Humobox,
    /// from the viewer's head, into a small texture each frame, published as
    /// _VirtualTex with the matrix that finds a world point in it (see
    /// IMH_VirtualAt in IMH_PassthroughCamera.hlsl). Because the camera sits
    /// where every ray starts, a point's direction is all it needs.
    ///
    /// The Humobox is moved onto its own layer so this camera sees nothing else;
    /// the main camera still draws every layer.
    /// </summary>
    public sealed class HumoboxMirror : MonoBehaviour
    {
        /// <summary>A layer no one else uses, for the Humobox alone.</summary>
        public const int Layer = 30;

        [Tooltip("Texture size. The Humobox is a few pixels across in each drop.")]
        [SerializeField] int resolution = 320;

        [Tooltip("Render every this many frames. A drop's tiny image of the Humobox " +
                 "does not need the display rate, and the headset's GPU is near its limit in the rain.")]
        [SerializeField, Range(1, 4)] int everyNthFrame = 2;

        [Tooltip("Margin around the Humobox, so its stretch and spin stay in frame.")]
        [SerializeField, Range(1f, 2.5f)] float framing = 1.6f;

        Transform target;
        Camera mirror;
        RenderTexture texture;
        bool showing;

        /// <summary>Starts or stops showing <paramref name="subject"/> to the rain.</summary>
        public void Show(Transform subject)
        {
            target = subject;
            showing = subject != null;
            if (showing)
            {
                SetLayer(subject, Layer);
                EnsureCamera();
            }
            if (mirror != null)
                mirror.enabled = showing;
            if (!showing)
                Shader.SetGlobalFloat(Ids.Ready, 0f);
        }

        void EnsureCamera()
        {
            if (mirror != null)
                return;

            texture = new RenderTexture(resolution, resolution, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "Humobox Mirror"
            };
            texture.Create();

            var host = new GameObject("Humobox Mirror Camera");
            host.transform.SetParent(transform, false);
            mirror = host.AddComponent<Camera>();
            mirror.clearFlags = CameraClearFlags.SolidColor;
            mirror.backgroundColor = Color.clear;
            mirror.cullingMask = 1 << Layer;
            mirror.targetTexture = texture;
            mirror.allowHDR = false;
            mirror.allowMSAA = false;
            mirror.nearClipPlane = 0.05f;
            mirror.farClipPlane = 30f;
            // Before the main camera, so the rain reads this frame's image.
            mirror.depth = (Camera.main != null ? Camera.main.depth : 0f) - 1f;

            var data = mirror.GetUniversalAdditionalCameraData();
            data.renderShadows = false;
            data.renderPostProcessing = false;
            data.requiresColorTexture = false;
            data.requiresDepthTexture = false;
            // One mono image, not one per eye: this camera only feeds the rain.
            data.allowXRRendering = false;

            Shader.SetGlobalTexture(Ids.Texture, texture);
        }

        void LateUpdate()
        {
            if (!showing || mirror == null || target == null || Camera.main == null)
                return;

            // From the head, straight at the Humobox, just wide enough for it.
            var eye = Camera.main.transform.position;
            var bounds = WorldBounds(target);
            var toTarget = bounds.center - eye;
            var distance = Mathf.Max(toTarget.magnitude, 0.1f);
            var radius = bounds.extents.magnitude * framing;
            var halfAngle = Mathf.Asin(Mathf.Clamp(radius / distance, 0.01f, 0.98f)) * Mathf.Rad2Deg;

            // Render on even frames only; the room capture takes the odd ones, so
            // the two extra passes never land in the same frame.
            var renderNow = Time.frameCount % Mathf.Max(everyNthFrame, 1) == 0;
            mirror.enabled = renderNow;
            if (!renderNow)
                return;

            mirror.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(toTarget / distance, Vector3.up));
            mirror.fieldOfView = Mathf.Clamp(halfAngle * 2f, 5f, 150f);
            mirror.aspect = 1f;

            // OpenGL-convention projection: the shader maps clip xy / w to uv
            // directly, and Unity keeps a sampled render texture's uv the same
            // way round on every platform.
            var worldToVirtual = mirror.projectionMatrix * mirror.worldToCameraMatrix;
            Shader.SetGlobalMatrix(Ids.WorldToVirtual, worldToVirtual);
            Shader.SetGlobalFloat(Ids.Ready, 1f);
        }

        static Bounds WorldBounds(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return new Bounds(root.position, Vector3.one * 0.3f);
            var bounds = renderers[0].bounds;
            foreach (var r in renderers)
                bounds.Encapsulate(r.bounds);
            return bounds;
        }

        static void SetLayer(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            foreach (Transform child in root)
                SetLayer(child, layer);
        }

        void OnDisable() => Shader.SetGlobalFloat(Ids.Ready, 0f);

        void OnDestroy()
        {
            if (texture != null)
            {
                texture.Release();
                Destroy(texture);
            }
        }

        static class Ids
        {
            public static readonly int Texture = Shader.PropertyToID("_VirtualTex");
            public static readonly int WorldToVirtual = Shader.PropertyToID("_WorldToVirtual");
            public static readonly int Ready = Shader.PropertyToID("_VirtualReady");
        }
    }
}
