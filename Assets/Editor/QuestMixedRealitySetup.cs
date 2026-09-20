using System.Linq;
using ImetInHuman.VFX;
using ImetInHuman.XR;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR;

/// <summary>
/// Prepares the intro scene and the Android settings for a Meta Quest 3
/// mixed-reality build: passthrough behind the effects, the Humobox on a real
/// table. Safe to run again; it only adds what is missing.
///
/// What it cannot do is turn on OpenXR for Android and the Meta Quest feature
/// group -- do that in Project Settings > XR Plug-in Management, then run
/// Project Validation's Fix All. Once those exist, it switches on the OpenXR
/// features the scene relies on.
/// </summary>
static class QuestMixedRealitySetup
{
    const string MobilePipeline = "Assets/Settings/Mobile_RPAsset.asset";
    const string IntroScene = "Assets/Scenes/IntroCopy.unity";
    const string DefaultTemplateId = "com.UnityTechnologies.com.unity.template.urpblank";

    [MenuItem("IMETINHUMAN/Quest/Set Up Mixed Reality (Scene + Build Settings)")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("Quest setup: leave Play mode first.");
            return;
        }

        if (SetUpScene())
            ApplyBuildSettings();
    }

    static bool SetUpScene()
    {
        var scene = SceneManager.GetActiveScene();
        var cam = Camera.main;
        var offset = cam != null ? cam.transform.parent : null;
        var originTransform = offset != null ? offset.parent : null;
        if (originTransform == null)
        {
            Debug.LogError("Quest setup: expected Main Camera under 'XR Origin/Camera Offset'.");
            return false;
        }

        // Rig: the existing XR Origin gets the component that makes it one, and
        // the camera gets head tracking -- it had neither.
        var origin = GetOrAdd<XROrigin>(originTransform.gameObject);
        origin.Origin = originTransform.gameObject;
        origin.CameraFloorOffsetObject = offset.gameObject;
        origin.Camera = cam;
        origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
        // Only used without floor tracking, e.g. in the Editor: keep the old eye height.
        origin.CameraYOffset = offset.localPosition.y;

        if (cam.GetComponent<TrackedPoseDriver>() == null)
        {
            var head = Undo.AddComponent<TrackedPoseDriver>(cam.gameObject);
            head.positionInput = new InputActionProperty(new InputAction("Head Position",
                InputActionType.Value, "<XRHMD>/centerEyePosition", expectedControlType: "Vector3"));
            head.rotationInput = new InputActionProperty(new InputAction("Head Rotation",
                InputActionType.Value, "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion"));
        }

        // Passthrough: AR Foundation's camera manager is what turns it on under
        // Meta OpenXR; PassthroughCamera makes the clear transparent on device.
        GetOrAdd<ARCameraManager>(cam.gameObject);
        GetOrAdd<PassthroughCamera>(cam.gameObject);
        GetOrAdd<HeadsetFoveation>(cam.gameObject);

        if (Object.FindFirstObjectByType<ARSession>() == null)
        {
            var session = new GameObject("AR Session");
            Undo.RegisterCreatedObjectUndo(session, "Add AR Session");
            SceneManager.MoveGameObjectToScene(session, scene);
            session.AddComponent<ARSession>();
        }

        // Table: planes from Space Setup, anchors to pin the box.
        var planes = GetOrAdd<ARPlaneManager>(originTransform.gameObject);
        // Walls too, not just tables and the floor: the moss grows on whatever
        // surfaces the headset reports when it has no room mesh to use.
        planes.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
        GetOrAdd<ARAnchorManager>(originTransform.gameObject);

        var table = GetOrAdd<HumoboxTableAnchor>(originTransform.gameObject);
        var tableSo = new SerializedObject(table);
        tableSo.FindProperty("planeManager").objectReferenceValue = planes;
        tableSo.ApplyModifiedPropertiesWithoutUndo();

        var cues = Object.FindObjectsByType<HumoboxRainCue>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var cue in cues)
        {
            var so = new SerializedObject(cue);
            so.FindProperty("table").objectReferenceValue = table;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
        if (cues.Length == 0)
            Debug.LogWarning("Quest setup: no Humobox in the scene. Run IMETINHUMAN > Humobox > Add Humobox To Rain (Take 4), then this again.");

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"Quest setup: '{scene.name}' rigged for passthrough and table anchoring. Save the scene to keep it.");
        return true;
    }

    static void ApplyBuildSettings()
    {
        var android = NamedBuildTarget.Android;

        // Horizon OS needs API 32 at least; IL2CPP and ARM64 are Quest's only runtime.
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
        if (PlayerSettings.GetApplicationIdentifier(android) == DefaultTemplateId)
            PlayerSettings.SetApplicationIdentifier(android, "space.liminal.imetinhuman");

        // The HDR colour buffer has no alpha, and passthrough needs alpha to know
        // where the app is see-through. MSAA 4x is the Quest norm.
        var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(MobilePipeline);
        if (pipeline != null)
        {
            pipeline.supportsHDR = false;
            pipeline.msaaSampleCount = 4;
            EditorUtility.SetDirty(pipeline);
        }
        else
        {
            Debug.LogWarning($"Quest setup: {MobilePipeline} not found; turn HDR off in the Android URP asset yourself.");
        }

        // The effects find their shaders by name at runtime, which a build strips
        // unless they are always included.
        var graphics = AssetDatabase.LoadAssetAtPath<Object>("ProjectSettings/GraphicsSettings.asset");
        var graphicsSo = new SerializedObject(graphics);
        var included = graphicsSo.FindProperty("m_AlwaysIncludedShaders");
        var shaders = AssetDatabase.FindAssets("t:Shader", new[] { "Assets/Shaders" })
            .Select(g => AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(s => s != null);
        foreach (var shader in shaders)
        {
            var present = Enumerable.Range(0, included.arraySize)
                .Any(i => included.GetArrayElementAtIndex(i).objectReferenceValue == shader);
            if (present)
                continue;
            included.InsertArrayElementAtIndex(included.arraySize);
            included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = shader;
        }
        graphicsSo.ApplyModifiedPropertiesWithoutUndo();

        // The intro scene first, so it is what the headset opens.
        var scenes = EditorBuildSettings.scenes.Where(s => s.path != IntroScene).ToList();
        scenes.Insert(0, new EditorBuildSettingsScene(IntroScene, true));
        EditorBuildSettings.scenes = scenes.ToArray();

        EnableOpenXRFeatures();

        AssetDatabase.SaveAssets();
        Debug.Log("Quest setup: Android settings applied (API 32, IL2CPP, ARM64, Vulkan, HDR off, MSAA 4x, " +
                  "effect shaders always included, IntroCopy first in the build).");
    }

    // Planes and anchors are what find and hold the table; without them the
    // plane manager runs but the runtime never reports a surface. Foveated
    // rendering spends fewer pixels at the edge of each eye, where the full-view
    // effects cost the most for the least. Meshing gives the room scan the moss
    // grows over.
    static readonly string[] RequiredFeatures = { "ARPlaneFeature", "ARAnchorFeature", "FoveatedRenderingFeature", "ARMeshFeature", "HandTracking" };

    static void EnableOpenXRFeatures()
    {
        var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (settings == null)
        {
            Debug.LogWarning("Quest setup: no OpenXR settings for Android yet. Enable OpenXR in XR Plug-in Management, then run this again.");
            return;
        }

        foreach (var name in RequiredFeatures)
        {
            var feature = settings.GetFeatures().FirstOrDefault(f => f.GetType().Name == name);
            if (feature == null)
            {
                Debug.LogWarning($"Quest setup: OpenXR feature {name} not found for Android.");
                continue;
            }
            if (!feature.enabled)
            {
                feature.enabled = true;
                EditorUtility.SetDirty(feature);
                Debug.Log($"Quest setup: enabled OpenXR feature {name} for Android.");
            }
        }
        EditorUtility.SetDirty(settings);
    }

    static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var existing = go.GetComponent<T>();
        return existing != null ? existing : Undo.AddComponent<T>(go);
    }
}

/// <summary>Runs the setup once on the next Editor refresh, then deletes its marker.</summary>
[InitializeOnLoad]
static class QuestMixedRealitySetupOnce
{
    const string Marker = "Assets/Editor/QuestMixedRealitySetup.run";

    static QuestMixedRealitySetupOnce()
    {
        if (!System.IO.File.Exists(Marker))
            return;
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (SceneManager.GetActiveScene().name != "IntroCopy")
            {
                Debug.LogWarning("Quest setup skipped: open IntroCopy and use IMETINHUMAN > Quest > Set Up Mixed Reality.");
                return;
            }
            AssetDatabase.DeleteAsset(Marker);
            QuestMixedRealitySetup.Run();
        };
    }
}
