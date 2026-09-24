using UnityEngine;
using UnityEngine.Rendering;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// A tunnel of star streaks along this object's +Z (HyperjumpStreaks.shader):
    /// the jump the concert footage makes around Luke. <see cref="Intensity"/> 0
    /// hides it; <see cref="Speed"/> positive streams the stars towards the
    /// near end (moving forward), negative away (pulling back).
    /// </summary>
    public sealed class HyperjumpTunnel : MonoBehaviour
    {
        [SerializeField, Range(50, 2000)] int streaks = 700;
        [SerializeField] float innerRadius = 1.2f;
        [SerializeField] float outerRadius = 9f;
        [SerializeField] float near = -3f;
        [SerializeField] float far = 45f;

        Material material;
        MeshRenderer meshRenderer;
        float intensity;
        float speed = 0.6f;

        public float Intensity
        {
            get => intensity;
            set
            {
                intensity = Mathf.Clamp01(value);
                if (material != null)
                    material.SetFloat(Ids.Intensity, intensity);
                if (meshRenderer != null)
                    meshRenderer.enabled = intensity > 0.001f;
            }
        }

        public float Speed
        {
            get => speed;
            set
            {
                speed = value;
                if (material != null)
                    material.SetFloat(Ids.Speed, speed);
            }
        }

        void Awake()
        {
            var shader = Shader.Find("IMETINHUMAN/VFX/Hyperjump Streaks");
            if (shader == null)
            {
                Debug.LogWarning("Hyperjump Streaks shader was not found.", this);
                enabled = false;
                return;
            }
            material = new Material(shader) { name = "Hyperjump Streaks" };
            material.SetFloat(Ids.Near, near);
            material.SetFloat(Ids.Far, far);

            gameObject.AddComponent<MeshFilter>().sharedMesh = BuildMesh();
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            Intensity = 0f;
            Speed = speed;
        }

        Mesh BuildMesh()
        {
            var random = new System.Random(7);
            var positions = new Vector3[streaks * 4];
            var corners = new Vector2[streaks * 4];
            var data = new Vector4[streaks * 4];
            var indices = new int[streaks * 6];
            for (var i = 0; i < streaks; i++)
            {
                // Area-uniform across the ring, so the middle is not crowded.
                var r = Mathf.Sqrt(Mathf.Lerp(innerRadius * innerRadius, outerRadius * outerRadius,
                                              (float)random.NextDouble()));
                var streak = new Vector4((float)random.NextDouble() * Mathf.PI * 2f, r,
                                         (float)random.NextDouble(), 0.4f + 0.6f * (float)random.NextDouble());
                for (var k = 0; k < 4; k++)
                {
                    var v = i * 4 + k;
                    corners[v] = new Vector2((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? 0f : 1f);
                    data[v] = streak;
                }
                var t = i * 6;
                var b = i * 4;
                indices[t] = b; indices[t + 1] = b + 1; indices[t + 2] = b + 2;
                indices[t + 3] = b + 1; indices[t + 4] = b + 3; indices[t + 5] = b + 2;
            }

            var mesh = new Mesh { name = "Hyperjump Streaks" };
            mesh.SetVertices(positions);
            mesh.SetUVs(0, corners);
            mesh.SetUVs(1, data);
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
            // Placed on the GPU; never culled.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 200f);
            return mesh;
        }

        void OnDestroy()
        {
            if (material != null)
                Destroy(material);
            var filter = GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
                Destroy(filter.sharedMesh);
        }

        static class Ids
        {
            public static readonly int Intensity = Shader.PropertyToID("_Intensity");
            public static readonly int Speed = Shader.PropertyToID("_Speed");
            public static readonly int Near = Shader.PropertyToID("_Near");
            public static readonly int Far = Shader.PropertyToID("_Far");
        }
    }
}
