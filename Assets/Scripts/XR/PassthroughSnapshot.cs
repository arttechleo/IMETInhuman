using System.IO;
using UnityEngine;

namespace ImetInHuman.XR
{
    /// <summary>
    /// Saves the passthrough colour camera's own frame -- flat, unwarped, the
    /// full camera resolution -- to files/snaps every few seconds, for looking
    /// at what the headset sees from a desk. Off unless the app is launched
    /// with <c>--es snapEvery 5</c> (seconds). Reads the global _PtCameraTex
    /// the camera feed publishes, so it costs nothing when the feed is off.
    /// </summary>
    public sealed class PassthroughSnapshot : MonoBehaviour
    {
        static readonly int CameraTex = Shader.PropertyToID("_PtCameraTex");

        public float every = 5f;
        public int max = 200;

        float next;
        int count;
        Texture2D readback;

        void Update()
        {
            if (Time.time < next || count >= max)
                return;
            next = Time.time + Mathf.Max(0.5f, every);
            var src = Shader.GetGlobalTexture(CameraTex);
            if (src == null || src.width < 16)
                return;

            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var was = RenderTexture.active;
            RenderTexture.active = rt;
            if (readback == null || readback.width != rt.width || readback.height != rt.height)
                readback = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            readback.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(rt);

            var dir = Path.Combine(Application.persistentDataPath, "snaps");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"snap_{count:000}.jpg"), readback.EncodeToJPG(92));
            count++;
        }
    }
}
