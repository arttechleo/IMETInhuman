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
