using System.Collections.Generic;
using ImetInHuman.XR;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Covers the real room in moss while the rain falls (RoomMoss.shader).
    ///
    /// The room is the headset's scan of it: Meta's room mesh from Space Setup,
    /// through AR Foundation meshing, which needs the scene permission. The moss
    /// creeps in from the floor once the rain has begun, climbs the walls and
    /// furniture, reaches the ceiling, and draws back as the rain ends. Where it
    /// has not reached, nothing is drawn and the room shows as it is.
    ///
    /// In the Editor without a headset, a stand-in room box is used instead, so
    /// the look can be judged in Play mode. In the PCVR virtual room there is no
    /// real room to cover, so nothing is drawn.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoomMoss : MonoBehaviour
    {
        const string ScenePermission = "com.oculus.permission.USE_SCENE";

        [SerializeField] RainWindowOverlayController rain;

        [Header("Timing (seconds of the rain)")]
        [SerializeField] float growStart = 2f;
        [Tooltip("Share of the rain's length by which the moss has covered everything.")]
        [SerializeField, Range(0.1f, 1f)] float fullAt = 0.65f;
        [SerializeField] float recedeSeconds = 5f;

        [Header("Look")]
        [SerializeField] float tileSize = 0.6f;
        [SerializeField, Range(0f, 3f)] float brightness = 1.1f;
        [SerializeField] int textureSize = 512;

        [Header("Editor Stand-In Room")]
        [SerializeField] Vector3 standInSize = new(5f, 2.6f, 5f);

        Material material;
        Texture2D albedo, normal;
        ARMeshManager meshManager;
        ARPlaneManager planeManager;
        MeshFilter template;
        readonly Dictionary<TrackableId, Transform> planePatches = new();
        float scanStarted = -1f;
        bool warnedNoMesh;
        bool boundsFound;
        readonly List<Renderer> renderers = new();
        float floorY, ceilingY = 2.6f;
        bool shown = true;

        void Start()
        {
            if (rain == null)
                rain = GetComponent<RainWindowOverlayController>();
            var shader = Shader.Find("IMETINHUMAN/VFX/Room Moss");
            if (rain == null || shader == null || MixedReality.VirtualRoom)
            {
                if (shader == null)
                    Debug.LogWarning("Room Moss shader was not found.", this);
                enabled = false;
                return;
            }

            BuildTextures();
            material = new Material(shader) { name = "Room Moss" };
            material.SetTexture(Ids.MossTex, albedo);
            material.SetTexture(Ids.MossNormal, normal);
            material.SetFloat(Ids.TileSize, tileSize);
            material.SetFloat(Ids.Brightness, brightness);

            var eye = Camera.main != null ? Camera.main.transform : null;
            floorY = eye != null ? eye.position.y - 1.6f : 0f;
            ceilingY = floorY + standInSize.y;

            if (MixedReality.PassthroughActive)
                StartRoomScan();
            else
                BuildStandInRoom(eye);
            SetShown(false);
        }

        // Meta's room mesh, one renderer per mesh the runtime reports.
        void StartRoomScan()
        {
            var origin = FindFirstObjectByType<XROrigin>();
            if (origin == null)
            {
                Debug.LogWarning("Room moss: no XR Origin, so no room mesh.", this);
                return;
            }

            var templateObject = new GameObject("Room Moss Mesh", typeof(MeshFilter), typeof(MeshRenderer));
            templateObject.SetActive(false);
            templateObject.transform.SetParent(transform, false);
            var templateRenderer = templateObject.GetComponent<MeshRenderer>();
            templateRenderer.sharedMaterial = material;
            templateRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            templateRenderer.receiveShadows = false;
            template = templateObject.GetComponent<MeshFilter>();

            // Made inactive, so it starts with its prefab already set.
            var managerObject = new GameObject("Room Moss Scan");
            managerObject.SetActive(false);
            managerObject.transform.SetParent(origin.transform, false);
            meshManager = managerObject.AddComponent<ARMeshManager>();
            meshManager.meshPrefab = template;
            meshManager.normals = true;
            meshManager.meshInfosChanged.AddListener(OnMeshesChanged);

            planeManager = FindFirstObjectByType<ARPlaneManager>();
            scanStarted = Time.time;

            HeadsetPermissions.Request(ScenePermission, granted =>
            {
                if (granted)
                {
                    managerObject.SetActive(true);
                    if (planeManager != null)
                    {
                        planeManager.enabled = true;
                        planeManager.trackablesChanged.AddListener(OnPlanesChanged);
                    }
                    Debug.Log("Room moss: scanning the room (mesh, and planes as a fallback).", this);
                }
                else
                {
                    Debug.LogWarning("Scene permission denied: no room for the moss to grow on.", this);
                }
            });
        }

        // Fallback: Meta reports a room mesh only when Space Setup has scanned
        // one. Without it there are still the surfaces marked in Space Setup --
        // walls, floor, ceiling, desks -- so the moss grows on those instead.
        void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            foreach (var plane in args.added)
                PatchPlane(plane);
            foreach (var plane in args.updated)
                PatchPlane(plane);
            foreach (var removed in args.removed)
                if (planePatches.Remove(removed.Key, out var patch) && patch != null)
                {
                    renderers.Remove(patch.GetComponent<Renderer>());
                    Destroy(patch.gameObject);
                }
            UpdateRoomBounds();
        }

        void PatchPlane(ARPlane plane)
        {
            if (meshRenderersFound)
                return;   // the real room mesh turned up; it is better than this

            if (!planePatches.TryGetValue(plane.trackableId, out var patch) || patch == null)
            {
                var go = new GameObject($"Room Moss Plane {plane.trackableId}");
                go.transform.SetParent(plane.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = PlaneQuad();
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.enabled = shown;
                renderers.Add(r);
                patch = go.transform;
                planePatches[plane.trackableId] = patch;
                Debug.Log($"Room moss: growing on a {plane.alignment} surface, {plane.size.x:0.0} x {plane.size.y:0.0} m.", this);
            }

            // The plane's own space: x across, z along, y its normal.
            patch.localPosition = new Vector3(plane.centerInPlaneSpace.x, 0f, plane.centerInPlaneSpace.y);
            patch.localRotation = Quaternion.identity;
            patch.localScale = new Vector3(Mathf.Max(plane.size.x, 0.05f), 1f, Mathf.Max(plane.size.y, 0.05f));
        }

        static Mesh planeQuad;

        // A unit square lying in the plane's own xz, facing along its normal.
        static Mesh PlaneQuad()
        {
            if (planeQuad != null)
                return planeQuad;
            planeQuad = new Mesh { name = "Room Moss Plane Quad" };
            planeQuad.SetVertices(new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(-0.5f, 0f, 0.5f),
                new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, -0.5f)
            });
            planeQuad.SetNormals(new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up });
            planeQuad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            return planeQuad;
        }

        bool meshRenderersFound;

        // The runtime reports which meshes changed; the manager keeps the
        // GameObjects it made for them, and every one of those wants the moss.
        void OnMeshesChanged(ARMeshInfosChangedEventArgs args)
        {
            foreach (var filter in meshManager.meshes)
                Track(filter);
            renderers.RemoveAll(r => r == null);
            // The room mesh covers everything the planes do and more; drop them.
            if (!meshRenderersFound && renderers.Count > 0)
            {
                meshRenderersFound = true;
                foreach (var patch in planePatches.Values)
                    if (patch != null)
                    {
                        renderers.Remove(patch.GetComponent<Renderer>());
                        Destroy(patch.gameObject);
                    }
                planePatches.Clear();
            }
            UpdateRoomBounds();
            Debug.Log($"Room moss: room mesh has {renderers.Count} part(s), floor {floorY:0.00} m, ceiling {ceilingY:0.00} m.", this);
        }

        void Track(MeshFilter filter)
        {
            var r = filter.GetComponent<Renderer>();
            if (r == null || renderers.Contains(r))
                return;
            r.sharedMaterial = material;
            r.enabled = shown;
            renderers.Add(r);
        }

        // The floor and ceiling from the scan itself, so the growth runs from
        // the real floor to the real ceiling.
        void UpdateRoomBounds()
        {
            var any = false;
            var bounds = new Bounds();
            foreach (var r in renderers)
            {
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (!any || bounds.size.y < 1.5f)
                return;
            floorY = bounds.min.y;
            ceilingY = bounds.max.y;
            boundsFound = true;
        }

        // An inward-facing box around the viewer, for judging the look in the Editor.
        void BuildStandInRoom(Transform eye)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Room Moss Stand-In Room";
            Destroy(go.GetComponent<Collider>());
            var mesh = Instantiate(go.GetComponent<MeshFilter>().sharedMesh);
            var triangles = mesh.triangles;
            for (var i = 0; i < triangles.Length; i += 3)
                (triangles[i + 1], triangles[i + 2]) = (triangles[i + 2], triangles[i + 1]);
            mesh.triangles = triangles;
            var normals = mesh.normals;
            for (var i = 0; i < normals.Length; i++)
                normals[i] = -normals[i];
            mesh.normals = normals;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;

            var centre = eye != null ? eye.position : Vector3.zero;
            go.transform.SetParent(transform, true);
            go.transform.position = new Vector3(centre.x, floorY + standInSize.y * 0.5f, centre.z);
            go.transform.localScale = standInSize;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderers.Add(r);
        }

        void Update()
        {
            var t = rain.PlaybackTime;
            var d = rain.Duration;
            float growth = 0f, wet = 0f;
            if (!float.IsNegativeInfinity(t) && t >= 0f)
            {
                var full = Mathf.Max(d * fullAt, growStart + 1f);
                growth = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(growStart, full, t));
                // Draws back over the rain's last seconds.
                growth *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(d, d - recedeSeconds, t));
                wet = Mathf.Clamp01(t / 3f) * Mathf.Clamp01((d - t) / 2f + 0.3f);
            }

            SetShown(growth > 0.001f);
            if (!shown)
                return;
            material.SetFloat(Ids.Growth, growth);
            material.SetFloat(Ids.Wet, wet);
            material.SetFloat(Ids.FloorY, floorY);
            material.SetFloat(Ids.CeilingY, ceilingY);
        }

        void SetShown(bool value)
        {
            if (value == shown)
                return;
            shown = value;
            foreach (var r in renderers)
                if (r != null)
                    r.enabled = value;
            if (value && MixedReality.PassthroughActive && renderers.Count == 0 && !warnedNoMesh)
            {
                warnedNoMesh = true;
                Debug.LogWarning($"Room moss: nothing to grow on {Time.time - scanStarted:0}s after asking -- " +
                                 "no room mesh and no surfaces. Run Space Setup on the headset.", this);
            }
            else if (value && !boundsFound)
            {
                Debug.Log($"Room moss: growing from {floorY:0.00} m to {ceilingY:0.00} m (guessed, no bounds yet).", this);
            }
        }

        // One tileable patch of moss: tiny tufts packed into cushions, height in
        // alpha; its normal map from the same height.
        void BuildTextures()
        {
            var n = Mathf.Clamp(Mathf.ClosestPowerOfTwo(textureSize), 64, 1024);
            var height = new float[n * n];
            var tint = new float[n * n];
            var random = new System.Random(1717);

            var cushions = Cells(12, random, out var cushionSeed);
            var tufts = Cells(72, random, out var tuftSeed);
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var u = (x + 0.5f) / n;
                    var v = (y + 0.5f) / n;
                    // Cushions: soft domes; tufts: small tight bumps on them.
                    var cushion = Worley(u, v, 12, cushions, out _);
                    var tuft = Worley(u, v, 72, tufts, out var tuftId);
                    var dome = Mathf.Clamp01(1f - cushion * 1.25f);
                    var bump = Mathf.Clamp01(1f - tuft * 1.6f);
                    var h = dome * 0.55f + bump * bump * 0.45f + dome * bump * 0.25f;
                    height[y * n + x] = Mathf.Clamp01(h);
                    tint[y * n + x] = tuftSeed[tuftId];
                }
            }

            var colors = new Color32[n * n];
            var normals = new Color32[n * n];
            var dark = new Color(0.09f, 0.16f, 0.05f);
            var bright = new Color(0.42f, 0.55f, 0.14f);
            var yellow = new Color(0.62f, 0.62f, 0.18f);
            var brown = new Color(0.30f, 0.22f, 0.10f);
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var i = y * n + x;
                    var h = height[i];
                    var c = Color.Lerp(dark, bright, Mathf.SmoothStep(0f, 1f, h));
                    var s = tint[i];
                    if (s > 0.9f) c = Color.Lerp(c, yellow, 0.5f * h);
                    else if (s < 0.06f) c = Color.Lerp(c, brown, 0.6f);
                    c *= 0.85f + 0.3f * s;
                    colors[i] = new Color(c.r, c.g, c.b, h);

                    // Wrapped differences, so the normal map tiles too.
                    var dx = height[y * n + (x + 1) % n] - height[y * n + (x + n - 1) % n];
                    var dy = height[((y + 1) % n) * n + x] - height[((y + n - 1) % n) * n + x];
                    var nv = new Vector3(-dx * n * 0.02f, -dy * n * 0.02f, 1f).normalized;
                    normals[i] = new Color(nv.x * 0.5f + 0.5f, nv.y * 0.5f + 0.5f, 1f, 1f);
                }
            }

            albedo = new Texture2D(n, n, TextureFormat.RGBA32, true, false)
            {
                name = "Room Moss Albedo", wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear, anisoLevel = 4
            };
            albedo.SetPixels32(colors);
            albedo.Apply(true, true);

            normal = new Texture2D(n, n, TextureFormat.RGBA32, true, true)
            {
                name = "Room Moss Normal", wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear, anisoLevel = 4
            };
            normal.SetPixels32(normals);
            normal.Apply(true, true);
        }

        // One random point per cell of a cells x cells grid, and a random value per cell.
        static Vector2[] Cells(int cells, System.Random random, out float[] seeds)
        {
            var points = new Vector2[cells * cells];
            seeds = new float[cells * cells];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = new Vector2((float)random.NextDouble(), (float)random.NextDouble());
                seeds[i] = (float)random.NextDouble();
            }
            return points;
        }

        // Distance to the nearest point, in cell widths, wrapping at the edges.
        static float Worley(float u, float v, int cells, Vector2[] points, out int nearest)
        {
            var px = u * cells;
            var py = v * cells;
            var cx = Mathf.FloorToInt(px);
            var cy = Mathf.FloorToInt(py);
            var best = float.MaxValue;
            nearest = 0;
            for (var oy = -1; oy <= 1; oy++)
            {
                for (var ox = -1; ox <= 1; ox++)
                {
                    var gx = cx + ox;
                    var gy = cy + oy;
                    var id = ((gy % cells + cells) % cells) * cells + (gx % cells + cells) % cells;
                    var q = points[id];
                    var ddx = gx + q.x - px;
                    var ddy = gy + q.y - py;
                    var dist = ddx * ddx + ddy * ddy;
                    if (dist < best)
                    {
                        best = dist;
                        nearest = id;
                    }
                }
            }
            return Mathf.Sqrt(best);
        }

        void OnDestroy()
        {
            if (meshManager != null)
                meshManager.meshInfosChanged.RemoveListener(OnMeshesChanged);
            if (material != null) Destroy(material);
            if (albedo != null) Destroy(albedo);
            if (normal != null) Destroy(normal);
        }

        static class Ids
        {
            public static readonly int MossTex = Shader.PropertyToID("_MossTex");
            public static readonly int MossNormal = Shader.PropertyToID("_MossNormal");
            public static readonly int TileSize = Shader.PropertyToID("_TileSize");
            public static readonly int Brightness = Shader.PropertyToID("_Brightness");
            public static readonly int Growth = Shader.PropertyToID("_Growth");
            public static readonly int Wet = Shader.PropertyToID("_Wet");
            public static readonly int FloorY = Shader.PropertyToID("_FloorY");
            public static readonly int CeilingY = Shader.PropertyToID("_CeilingY");
        }
    }
}
