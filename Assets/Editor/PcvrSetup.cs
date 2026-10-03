using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.OpenXR;

/// <summary>
/// Sets the project up for PCVR over Quest Link: the virtual room and XR on
/// Windows. The Quest build is untouched -- passthrough there replaces the sky.
///
///   Sky      the night-street HDRI as a cubemap skybox, which also lights and
///            reflects the scene, so the rain has a world to refract.
///   XR       OpenXR as the Windows loader (it had none) with the Quest
///            controller profiles; Quest Link must be the PC's OpenXR runtime.
///   Quality  a "PC" quality level on PC_RPAsset (opaque and depth textures for
///            the rain), Windows only; "Mobile" stays Android only, and the
///            default pipeline is the Mobile one so Android builds stop carrying
///            PC variants. ImetInHuman.XR.PcvrSetup switches to "PC" off-Android.
/// </summary>
static class PcvrSetupEditor
{
    const string Hdri = "Assets/Environment/cobblestone_street_night_2k.hdr";
    const string SkyMaterial = "Assets/Environment/Night Street Sky.mat";

    static readonly string[] StandaloneFeatures = { "OculusTouchControllerProfile", "MetaQuestTouchPlusControllerProfile" };

    [MenuItem("IMETINHUMAN/PCVR/Set Up Virtual Room")]
    public static void Run()
    {
        SetUpSky();
        SetUpXr();
        SetUpQuality();
        AssetDatabase.SaveAssets();
    }

    const string PcPipeline = "Assets/Settings/PC_RPAsset.asset";
    const string MobilePipeline = "Assets/Settings/Mobile_RPAsset.asset";

    [MenuItem("IMETINHUMAN/PCVR/Set Up Quality Levels")]
    public static void SetUpQuality()
    {
        var pc = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(PcPipeline);
        var mobile = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(MobilePipeline);
        if (pc == null || mobile == null)
        {
            Debug.LogWarning($"PCVR setup: {PcPipeline} or {MobilePipeline} is missing; quality levels left alone.");
            return;
        }

        var quality = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset")[0]);
        var levels = quality.FindProperty("m_QualitySettings");
        int pcIndex = -1, mobileIndex = -1;
        for (var i = 0; i < levels.arraySize; i++)
        {
            var name = levels.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue;
            if (name == "PC") pcIndex = i;
            if (name == "Mobile") mobileIndex = i;
        }
        if (mobileIndex < 0)
        {
            Debug.LogWarning("PCVR setup: no \"Mobile\" quality level to base \"PC\" on; quality levels left alone.");
            return;
        }

        if (pcIndex < 0)
        {
            // A copy of Mobile, after it (levels run low to high).
            levels.InsertArrayElementAtIndex(mobileIndex);
            pcIndex = mobileIndex + 1;
            var level = levels.GetArrayElementAtIndex(pcIndex);
            level.FindPropertyRelative("name").stringValue = "PC";
            level.FindPropertyRelative("customRenderPipeline").objectReferenceValue = pc;
            var excluded = level.FindPropertyRelative("excludedTargetPlatforms");
            excluded.ClearArray();
            excluded.InsertArrayElementAtIndex(0);
            excluded.GetArrayElementAtIndex(0).stringValue = "Android";
            quality.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("PCVR setup: added the \"PC\" quality level (PC_RPAsset, Windows only).");
        }

        // The fallback pipeline is also what build stripping keeps variants for;
        // with PC as the default, Android builds carried Forward+, SSAO and the rest.
        if (GraphicsSettings.defaultRenderPipeline != mobile)
        {
            GraphicsSettings.defaultRenderPipeline = mobile;
            Debug.Log("PCVR setup: default render pipeline is now Mobile_RPAsset.");
        }
        AssetDatabase.SaveAssets();
    }

    static void SetUpSky()
    {
        if (AssetImporter.GetAtPath(Hdri) is TextureImporter importer && importer.textureShape != TextureImporterShape.TextureCube)
        {
            importer.textureShape = TextureImporterShape.TextureCube;
            importer.generateCubemap = TextureImporterGenerateCubemap.AutoCubemap;
            importer.sRGBTexture = false;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }

        var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(Hdri);
        if (cube == null)
        {
            Debug.LogWarning($"PCVR setup: {Hdri} is not a cubemap yet; run IMETINHUMAN > PCVR > Set Up Virtual Room again.");
            return;
        }

        var material = AssetDatabase.LoadAssetAtPath<Material>(SkyMaterial);
        if (material == null)
        {
            material = new Material(Shader.Find("Skybox/Cubemap")) { name = "Night Street Sky" };
            AssetDatabase.CreateAsset(material, SkyMaterial);
        }
        material.SetTexture("_Tex", cube);
        material.SetFloat("_Exposure", 1f);
        EditorUtility.SetDirty(material);

        RenderSettings.skybox = material;
        RenderSettings.ambientMode = AmbientMode.Skybox;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        DynamicGI.UpdateEnvironment();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("PCVR setup: night-street sky set.");
    }

    static void SetUpXr()
    {
        if (!EditorBuildSettings.TryGetConfigObject(UnityEngine.XR.Management.XRGeneralSettings.settingsKey,
                                                    out XRGeneralSettingsPerBuildTarget perTarget) || perTarget == null)
        {
            Debug.LogWarning("PCVR setup: no XR settings; enable XR Plug-in Management for Windows once by hand.");
            return;
        }

        var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Standalone);
        if (settings == null || settings.Manager == null)
        {
            Debug.LogWarning("PCVR setup: no Windows XR settings; open Project Settings > XR Plug-in Management > PC tab once.");
            return;
        }

        if (!settings.Manager.activeLoaders.Any(l => l != null && l.GetType().Name == "OpenXRLoader"))
            XRPackageMetadataStore.AssignLoader(settings.Manager, "UnityEngine.XR.OpenXR.OpenXRLoader", BuildTargetGroup.Standalone);
        settings.InitManagerOnStart = true;
        EditorUtility.SetDirty(settings);

        var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
        if (openXr != null)
        {
            foreach (var feature in openXr.GetFeatures())
            {
                if (StandaloneFeatures.Contains(feature.GetType().Name) && !feature.enabled)
                {
                    feature.enabled = true;
                    EditorUtility.SetDirty(feature);
                }
            }
            EditorUtility.SetDirty(openXr);
        }
        Debug.Log("PCVR setup: OpenXR on for Windows. Set Meta Quest Link as the PC's OpenXR runtime.");
    }
}

/// <summary>Adds the PC quality level once on the next refresh, then deletes its marker.</summary>
[InitializeOnLoad]
static class PcvrQualityOnce
{
    const string Self = "Assets/Editor/PcvrQualityOnce.run";

    static PcvrQualityOnce()
    {
        if (!System.IO.File.Exists(Self))
            return;
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            AssetDatabase.DeleteAsset(Self);
            PcvrSetupEditor.SetUpQuality();
        };
    }
}

/// <summary>Runs the PCVR setup once on the next refresh, then deletes itself.</summary>
[InitializeOnLoad]
static class PcvrSetupOnce
{
    const string Self = "Assets/Editor/PcvrSetupOnce.run";

    static PcvrSetupOnce()
    {
        if (!System.IO.File.Exists(Self))
            return;
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            AssetDatabase.DeleteAsset(Self);
            PcvrSetupEditor.Run();
        };
    }
}
