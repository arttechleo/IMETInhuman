using UnityEngine;
using UnityEngine.Rendering;

namespace ImetInHuman.XR
{
    /// <summary>
    /// Builds a 360-degree panorama of the room from the headset camera, as the
    /// viewer looks around.
    ///
    /// The camera only ever sees ahead, but reflections look everywhere: a drop
    /// on the glass beside the viewer reflects what is behind them. Each frame
    /// the part of the room the camera sees is painted into the panorama (see
    /// RoomEnvironmentCapture.shader); directions not yet seen keep a neutral
    /// grey until the viewer turns that way. Published as _PtRoomEnv, with mips,
    /// which double as the room's light scattered -- what fog shows.
    ///
    /// Runs after <see cref="PassthroughCameraFeed"/> each frame, so the camera
    /// pose it projects with is the one just published.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(PassthroughCameraFeed))]
    public sealed class RoomEnvironmentCapture : MonoBehaviour
    {
        [Tooltip("Panorama width in texels; height is half. Reflections in small drops " +
                 "need little: 1024 is about a third of a degree per texel.")]
        [SerializeField] int width = 1024;

        [Tooltip("How much of the new view each update blends in. Higher follows changes " +
                 "in the room faster; lower hides the camera's noise and lag.")]
        [SerializeField, Range(0.02f, 1f)] float captureRate = 0.35f;

        [Tooltip("Update every this many frames; the room changes slowly. Runs on odd " +
                 "frames, so it never shares a frame with the Humobox mirror.")]
        [SerializeField, Range(1, 4)] int everyNthFrame = 2;

        [Tooltip("What unseen directions show until the viewer looks that way.")]
        [SerializeField] Color unseen = new Color(0.22f, 0.22f, 0.22f, 1f);

        RenderTexture panorama;
        Material material;
        CommandBuffer commands;
        bool cleared;

        void Start()
        {
            if (!MixedReality.PassthroughActive)
                return;

            var shader = Shader.Find("Hidden/IMETINHUMAN/Room Environment Capture");
            if (shader == null)
            {
                Debug.LogWarning("Room environment capture shader was not found.", this);
                enabled = false;
                return;
            }

            material = new Material(shader) { name = "Room Environment Capture" };

            // sRGB storage for linear colour: 8 bits per channel without banding
            // in the darks. Repeat around, clamp at the poles.
            panorama = new RenderTexture(width, width / 2, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "Room Panorama",
                useMipMap = true,
                autoGenerateMips = false,
                wrapModeU = TextureWrapMode.Repeat,
                wrapModeV = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear
            };
            panorama.Create();

            commands = new CommandBuffer { name = "Room Environment Capture" };

            Shader.SetGlobalTexture(Ids.RoomEnv, panorama);
            Shader.SetGlobalFloat(Ids.RoomEnvMips, panorama.mipmapCount);
            Shader.SetGlobalFloat(Ids.RoomEnvReady, 0f);
        }

        void LateUpdate()
        {
            if (panorama == null || Camera.main == null)
                return;

            // Nothing to paint until the camera is streaming.
            if (Shader.GetGlobalFloat(Ids.PtReady) <= 0f)
                return;

            if (everyNthFrame > 1 && Time.frameCount % everyNthFrame != 1)
                return;

            material.SetFloat(Ids.CaptureRate, captureRate);
            material.SetVector(Ids.EnvOrigin, Camera.main.transform.position);

            commands.Clear();
            commands.SetRenderTarget(panorama);
            if (!cleared)
            {
                commands.ClearRenderTarget(false, true, unseen);
                cleared = true;
            }
            commands.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
            commands.GenerateMips(panorama);
            Graphics.ExecuteCommandBuffer(commands);

            Shader.SetGlobalFloat(Ids.RoomEnvReady, 1f);
        }

        void OnDisable() => Shader.SetGlobalFloat(Ids.RoomEnvReady, 0f);

        void OnDestroy()
        {
            commands?.Release();
            if (panorama != null)
            {
                panorama.Release();
                Destroy(panorama);
            }
            if (material != null)
                Destroy(material);
        }

        static class Ids
        {
            public static readonly int RoomEnv = Shader.PropertyToID("_PtRoomEnv");
            public static readonly int RoomEnvReady = Shader.PropertyToID("_PtRoomEnvReady");
            public static readonly int RoomEnvMips = Shader.PropertyToID("_PtRoomEnvMips");
            public static readonly int PtReady = Shader.PropertyToID("_PtReady");
            public static readonly int CaptureRate = Shader.PropertyToID("_CaptureRate");
            public static readonly int EnvOrigin = Shader.PropertyToID("_EnvOrigin");
        }
    }
}
