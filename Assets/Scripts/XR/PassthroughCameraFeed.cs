using System.Collections;
using UnityEngine;

namespace ImetInHuman.XR
{
    /// <summary>
    /// Streams the headset's colour camera into the shaders, so effects can bend,
    /// blur and fold the real room rather than draw over it.
    ///
    /// Uses Meta's Passthrough Camera API (Quest 3 / 3S, Horizon OS v74+) through
    /// plain Android Camera2 and WebCamTexture, following Meta's own sample
    /// (Unity-PassthroughCameraApiSamples, PassthroughCameraUtils): the camera is
    /// found by Meta's extra metadata, its pose relative to the head comes from
    /// LENS_POSE_*, its projection from LENS_INTRINSIC_CALIBRATION.
    ///
    /// Each frame it publishes, as globals (see IMH_PassthroughCamera.hlsl):
    ///   _PtCameraTex       the camera image
    ///   _PtWorldToCamera   where that camera is, for reprojecting eye rays onto it
    ///   _PtUvFromCamera    its projection, in texture uv
    ///   _PtReady           0 until frames arrive, then fades to 1
    /// plus the tuning values below. Does nothing outside an Android player.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PassthroughCameraFeed : MonoBehaviour
    {
        public enum Lens { Left = 0, Right = 1 }

        const string CameraPermission = "android.permission.CAMERA";
        const string HeadsetCameraPermission = "horizonos.permission.HEADSET_CAMERA";
        const int Yuv420888 = 0x23;

        [Tooltip("Which of the two passthrough cameras to stream. One serves both eyes; " +
                 "each eye reprojects it from its own position.")]
        [SerializeField] Lens lens = Lens.Left;

        /// <summary>Which eye's camera this feed comes from.</summary>
        public Lens Eye => lens;

        [Tooltip("The room has no depth here, so eye rays are taken out to this many metres " +
                 "before being looked up in the camera. Set it to roughly where the room is.")]
        [SerializeField, Range(0.5f, 8f)] float assumedDepth = 2f;

        [Tooltip("How far behind the head pose the camera image is, in seconds. Raise it if " +
                 "the image inside the drops lags the room when you turn your head.")]
        [SerializeField, Range(0f, 0.15f)] float imageLatency;

        [Tooltip("Scales the camera image's brightness to match the passthrough around it.")]
        [SerializeField, Range(0.25f, 3f)] float exposure = 1f;

        [Tooltip("Decode the camera image from sRGB in the shader. Off: Unity already " +
                 "samples WebCamTexture as sRGB in a linear project, and decoding again " +
                 "applied the curve twice -- the room came out dark inside every drop. " +
                 "Turn on only if the room looks washed out inside the effects.")]
        [SerializeField] bool decodeSrgb;

        [Tooltip("Seconds the effects take to blend the room in once the camera starts.")]
        [SerializeField] float fadeIn = 0.5f;

        WebCamTexture texture;
        Pose headFromCamera;
        Vector4 uvFromCamera;
        float ready;
        bool streaming;

        // Head poses kept for the latency compensation: time, position, rotation.
        const int HistoryLength = 32;
        readonly float[] historyTime = new float[HistoryLength];
        readonly Vector3[] historyPosition = new Vector3[HistoryLength];
        readonly Quaternion[] historyRotation = new Quaternion[HistoryLength];
        int historyHead;
        int historyCount;

        /// <summary>
        /// Makes sure one feed, and the room panorama built from it, exist on the
        /// given camera when running over passthrough. Effects call this, so none of them has to be set up by hand.
        /// </summary>
        public static void EnsureExists(Camera camera)
        {
            if (!MixedReality.PassthroughActive)
                return;

            var feed = FindFirstObjectByType<PassthroughCameraFeed>();
            if (feed == null)
            {
                var host = camera != null ? camera.gameObject : Camera.main != null ? Camera.main.gameObject : null;
                if (host == null)
                    return;
                feed = host.AddComponent<PassthroughCameraFeed>();
            }

            // The room all around, built from the feed, for reflections and fog.
            if (feed.GetComponent<RoomEnvironmentCapture>() == null)
                feed.gameObject.AddComponent<RoomEnvironmentCapture>();
        }

        void Awake()
        {
            PublishSettings();
            Shader.SetGlobalFloat(Ids.Ready, 0f);
        }

        void Start()
        {
            if (!MixedReality.PassthroughActive)
                return;

            HeadsetPermissions.Request(CameraPermission, cameraGranted =>
            {
                if (!cameraGranted)
                    return;
                HeadsetPermissions.Request(HeadsetCameraPermission, headsetGranted =>
                {
                    if (headsetGranted)
                        StartCoroutine(StartStreaming());
                });
            });
        }

        void OnEnable() => Application.onBeforeRender += PublishPose;

        void OnDisable()
        {
            Application.onBeforeRender -= PublishPose;
            Shader.SetGlobalFloat(Ids.Ready, 0f);
        }

        void OnDestroy()
        {
            if (texture != null)
            {
                texture.Stop();
                Destroy(texture);
            }
        }

        void OnValidate() => PublishSettings();

        void LateUpdate()
        {
            RecordHeadPose();

            if (!streaming)
                return;

            // Only counts as ready once real frames arrive; a WebCamTexture reports
            // a 16x16 placeholder until then.
            if (texture.width > 16)
                ready = fadeIn > 0f ? Mathf.MoveTowards(ready, 1f, Time.deltaTime / fadeIn) : 1f;
            Shader.SetGlobalFloat(Ids.Ready, ready);

            PublishSettings();
            PublishPose();
        }

        IEnumerator StartStreaming()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Unity only lists cameras once the permission is in place; give it a frame.
            yield return null;

            if (!FindCamera(out var cameraId, out var index))
            {
                Debug.LogWarning("Passthrough camera: no Meta passthrough camera found. Needs a Quest 3 or 3S on Horizon OS v74 or later.", this);
                yield break;
            }

            var devices = WebCamTexture.devices;
            while (index >= devices.Length)
            {
                yield return null;
                devices = WebCamTexture.devices;
            }

            if (!ReadCalibration(cameraId, out var size))
                yield break;

            texture = new WebCamTexture(devices[index].name, size.x, size.y);
            texture.Play();

            while (texture.width <= 16)
                yield return null;

            ComputeUvFromCamera(cameraId);
            Shader.SetGlobalTexture(Ids.Texture, texture);
            streaming = true;
            Debug.Log($"Passthrough camera: streaming {lens} camera {cameraId} at {texture.width}x{texture.height}.", this);
#else
            yield break;
#endif
        }

        void PublishSettings()
        {
            Shader.SetGlobalFloat(Ids.Depth, assumedDepth);
            Shader.SetGlobalFloat(Ids.Exposure, exposure);
            Shader.SetGlobalFloat(Ids.DecodeSrgb, decodeSrgb ? 1f : 0f);
        }

        // Where the camera was when it took the image now in the texture. Called
        // again just before rendering, so it uses the pose the frame is drawn from.
        void PublishPose()
        {
            if (!streaming || Camera.main == null)
                return;

            SampleHeadPose(Time.realtimeSinceStartup - imageLatency, out var headPosition, out var headRotation);

            var position = headPosition + headRotation * headFromCamera.position;
            // Meta's camera frame has y down the image; the flip about x makes a
            // camera-space point (x, y, z) land at pixel (fx x/z + cx, fy y/z + cy)
            // with pixel y counted up from the bottom, as texture uv is.
            var rotation = headRotation * headFromCamera.rotation * Quaternion.Euler(180f, 0f, 0f);

            var worldToCamera = Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
            Shader.SetGlobalMatrix(Ids.WorldToCamera, worldToCamera);
            Shader.SetGlobalVector(Ids.UvFromCamera, uvFromCamera);
        }

        void RecordHeadPose()
        {
            var head = Camera.main != null ? Camera.main.transform : null;
            if (head == null)
                return;

            historyHead = (historyHead + 1) % HistoryLength;
            historyTime[historyHead] = Time.realtimeSinceStartup;
            historyPosition[historyHead] = head.position;
            historyRotation[historyHead] = head.rotation;
            historyCount = Mathf.Min(historyCount + 1, HistoryLength);
        }

        // The head pose at a past moment, interpolated between recorded frames.
        void SampleHeadPose(float time, out Vector3 position, out Quaternion rotation)
        {
            var head = Camera.main.transform;
            position = head.position;
            rotation = head.rotation;
            if (imageLatency <= 0f || historyCount < 2)
                return;

            for (var i = 0; i < historyCount - 1; i++)
            {
                var newer = (historyHead - i + HistoryLength) % HistoryLength;
                var older = (newer - 1 + HistoryLength) % HistoryLength;
                if (historyTime[older] > time)
                    continue;

                var span = historyTime[newer] - historyTime[older];
                var t = span > 1e-5f ? Mathf.Clamp01((time - historyTime[older]) / span) : 1f;
                position = Vector3.Lerp(historyPosition[older], historyPosition[newer], t);
                rotation = Quaternion.Slerp(historyRotation[older], historyRotation[newer], t);
                return;
            }

            // Older than anything kept: use the oldest.
            var oldest = (historyHead - historyCount + 1 + HistoryLength) % HistoryLength;
            position = historyPosition[oldest];
            rotation = historyRotation[oldest];
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject cameraManager;

        AndroidJavaObject CameraManager
        {
            get
            {
                if (cameraManager == null)
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    cameraManager = activity.Call<AndroidJavaObject>("getSystemService", "camera");
                }
                return cameraManager;
            }
        }

        // Meta tags its cameras with two one-element byte arrays: camera_source
        // (0 = passthrough) and position (0 = left, 1 = right). The index into
        // Camera2's id list is also the index into WebCamTexture.devices.
        bool FindCamera(out string cameraId, out int index)
        {
            var ids = CameraManager.Call<string[]>("getCameraIdList");
            for (var i = 0; i < ids.Length; i++)
            {
                using var characteristics = CameraManager.Call<AndroidJavaObject>("getCameraCharacteristics", ids[i]);
                using var keys = characteristics.Call<AndroidJavaObject>("getKeys");
                int? source = null, position = null;
                var count = keys.Call<int>("size");
                for (var k = 0; k < count; k++)
                {
                    using var key = keys.Call<AndroidJavaObject>("get", k);
                    var name = key.Call<string>("getName");
                    if (name == "com.meta.extra_metadata.camera_source")
                        source = FirstByte(characteristics, key);
                    else if (name == "com.meta.extra_metadata.position")
                        position = FirstByte(characteristics, key);
                }

                if (source == 0 && position == (int)lens)
                {
                    cameraId = ids[i];
                    index = i;
                    return true;
                }
            }

            cameraId = null;
            index = -1;
            return false;
        }

        static int? FirstByte(AndroidJavaObject characteristics, AndroidJavaObject key)
        {
            var value = characteristics.Call<sbyte[]>("get", key);
            return value != null && value.Length == 1 ? value[0] : null;
        }

        // Pose of the lens relative to the head, and the largest stream size.
        bool ReadCalibration(string cameraId, out Vector2Int size)
        {
            using var characteristics = CameraManager.Call<AndroidJavaObject>("getCameraCharacteristics", cameraId);

            var t = Get<float[]>(characteristics, "LENS_POSE_TRANSLATION");
            var r = Get<float[]>(characteristics, "LENS_POSE_ROTATION");
            // Android to Unity, as Meta's sample does it: z mirrored, and the
            // rotation given is camera-from-head, so it is inverted.
            var cameraFromHead = new Quaternion(-r[0], -r[1], r[2], r[3]);
            headFromCamera = new Pose(new Vector3(t[0], t[1], -t[2]), Quaternion.Inverse(cameraFromHead));

            size = Vector2Int.zero;
            using var map = Get<AndroidJavaObject>(characteristics, "SCALER_STREAM_CONFIGURATION_MAP");
            var sizes = map.Call<AndroidJavaObject[]>("getOutputSizes", Yuv420888);
            foreach (var s in sizes)
            {
                var candidate = new Vector2Int(s.Call<int>("getWidth"), s.Call<int>("getHeight"));
                if (candidate.x * candidate.y > size.x * size.y)
                    size = candidate;
                s.Dispose();
            }

            if (size == Vector2Int.zero)
            {
                Debug.LogWarning("Passthrough camera: it reports no stream sizes.", this);
                return false;
            }
            return true;
        }

        // Intrinsics are in pixels of the full sensor; the stream may be a
        // centred crop of it, scaled. Fold both into one uv mapping.
        void ComputeUvFromCamera(string cameraId)
        {
            using var characteristics = CameraManager.Call<AndroidJavaObject>("getCameraCharacteristics", cameraId);
            var k = Get<float[]>(characteristics, "LENS_INTRINSIC_CALIBRATION");   // fx, fy, cx, cy, skew
            using var sensor = Get<AndroidJavaObject>(characteristics, "SENSOR_INFO_PRE_CORRECTION_ACTIVE_ARRAY_SIZE");
            var sensorSize = new Vector2(sensor.Get<int>("right"), sensor.Get<int>("bottom"));

            var streamSize = new Vector2(texture.width, texture.height);
            var scale = Mathf.Max(streamSize.x / sensorSize.x, streamSize.y / sensorSize.y);
            var visible = streamSize / scale;                  // sensor pixels the stream covers
            var origin = (sensorSize - visible) * 0.5f;

            uvFromCamera = new Vector4(
                k[0] / visible.x, k[1] / visible.y,
                (k[2] - origin.x) / visible.x, (k[3] - origin.y) / visible.y);
        }

        static T Get<T>(AndroidJavaObject characteristics, string keyName)
        {
            using var keyClass = new AndroidJavaClass("android.hardware.camera2.CameraCharacteristics");
            using var key = keyClass.GetStatic<AndroidJavaObject>(keyName);
            return characteristics.Call<T>("get", key);
        }
#endif

        static class Ids
        {
            public static readonly int Texture = Shader.PropertyToID("_PtCameraTex");
            public static readonly int WorldToCamera = Shader.PropertyToID("_PtWorldToCamera");
            public static readonly int UvFromCamera = Shader.PropertyToID("_PtUvFromCamera");
            public static readonly int Ready = Shader.PropertyToID("_PtReady");
            public static readonly int Depth = Shader.PropertyToID("_PtDepth");
            public static readonly int Exposure = Shader.PropertyToID("_PtExposure");
            public static readonly int DecodeSrgb = Shader.PropertyToID("_PtDecodeSrgb");
        }
    }
}
