// Import settings for the Humobox FBX/texture exports (Assets/Humobox/...).
//  - Humobox_Master.fbx   : Generic rig, blendshapes, URP Lit materials built by name, no animation
//  - Humobox@FaceTake_*.fbx: Generic animation clip named after the take (model part unused)
//  - textures/Humobox_*.png: normal map type, ASTC on Android (Quest 3 / Pico)
using UnityEditor;
using UnityEngine;
using UnityEditor.AssetImporters;

public class HumoboxImport : AssetPostprocessor
{
    const string Root = "Assets/Humobox/";

    bool IsHumobox => assetPath.StartsWith(Root);

    void OnPreprocessModel()
    {
        if (!IsHumobox || !(assetImporter is ModelImporter mi)) return;
        bool isTake = assetPath.Contains("@");
        mi.globalScale = 1f;
        mi.animationType = ModelImporterAnimationType.Generic;
        mi.optimizeGameObjects = false;
        mi.importBlendShapes = true;
        mi.importBlendShapeNormals = ModelImporterNormals.Calculate;
        mi.importNormals = ModelImporterNormals.Import;
        mi.importTangents = ModelImporterTangents.CalculateMikk;  // matches Blender's baked normal map
        mi.meshCompression = ModelImporterMeshCompression.Off;     // keep lip/blink shapes exact
        mi.materialImportMode = isTake ? ModelImporterMaterialImportMode.None
                                       : ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        mi.importAnimation = isTake;
        mi.resampleCurves = false;
        mi.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
        mi.animationRotationError = 0.2f;
        mi.animationPositionError = 0.2f;
        mi.animationScaleError = 0.2f;
    }

    void OnPreprocessAnimation()
    {
        if (!IsHumobox || !(assetImporter is ModelImporter mi) || !assetPath.Contains("@")) return;
        var clips = mi.defaultClipAnimations;
        string take = System.IO.Path.GetFileNameWithoutExtension(assetPath).Split('@')[1];
        foreach (var c in clips) { c.name = take; c.loopTime = false; }
        mi.clipAnimations = clips;
    }

    // URP Lit materials from the Blender material names
    void OnPreprocessMaterialDescription(MaterialDescription d, Material m, AnimationClip[] clips)
    {
        if (!IsHumobox) return;
        m.shader = Shader.Find("Universal Render Pipeline/Lit");
        void Col(Color c, float smooth, float metal = 0f)
        {
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", metal);
        }
        switch (d.materialName)
        {
            case "Humobox_Skin_Baked":
                Col(Color.white, 0.42f);
                var baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "textures/Humobox_BaseColor.png");
                var nrm = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "textures/Humobox_Normal.png");
                if (baseTex) m.SetTexture("_BaseMap", baseTex);
                if (nrm) { m.SetTexture("_BumpMap", nrm); m.EnableKeyword("_NORMALMAP"); }
                break;
            case "LipsandMouth": Col(new Color(0.55f, 0.04f, 0.05f), 0.6f); break;
            case "Teeth_AI": Col(new Color(0.93f, 0.9f, 0.84f), 0.7f); break;
            case "Tongue_AI": Col(new Color(0.86f, 0.44f, 0.46f), 0.55f); break;
            case "MouthCavity_AI": Col(new Color(0.02f, 0.004f, 0.006f), 0f); break;
            case "Metall_Earing": Col(new Color(0.85f, 0.85f, 0.85f), 0.85f, 1f); break;
            case "Eye_Texture_Approved":
                Col(Color.white, 0.85f);
                var eyeTex = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "textures/eye_texture.jpg");
                if (eyeTex) m.SetTexture("_BaseMap", eyeTex);
                break;
        }
    }

    void OnPreprocessTexture()
    {
        if (!IsHumobox || !(assetImporter is TextureImporter ti)) return;
        if (assetPath.EndsWith("_Normal.png")) ti.textureType = TextureImporterType.NormalMap;
        ti.sRGBTexture = !assetPath.EndsWith("_Normal.png") && !assetPath.EndsWith("_Roughness.png");
        ti.mipmapEnabled = true;
        var android = ti.GetPlatformTextureSettings("Android");
        android.overridden = true;
        android.maxTextureSize = 2048;
        android.format = assetPath.EndsWith("_Normal.png") ? TextureImporterFormat.ASTC_5x5
                                                            : TextureImporterFormat.ASTC_6x6;
        ti.SetPlatformTextureSettings(android);
    }
}
