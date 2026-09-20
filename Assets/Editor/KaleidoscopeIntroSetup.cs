using ImetInHuman.VFX;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class KaleidoscopeIntroSetup
{
    const string ScenePath = "Assets/Scenes/Intro.unity";
    const string ShaderPath = "Assets/Shaders/KaleidoscopeOverlay.shader";
    const string MaterialPath = "Assets/Materials/KaleidoscopeIntroOverlay.mat";

    [MenuItem("IMET IN HUMAN/Setup Kaleidoscope Intro Overlay")]
    public static void Setup()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);
        var material = EnsureMaterial();

        var existing = GameObject.Find("Kaleidoscope Intro Overlay");
        if (existing != null)
            Object.DestroyImmediate(existing);

        var canvasObject = new GameObject("Kaleidoscope Intro Overlay");
        var canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;
        canvasObject.AddComponent<CanvasScaler>();
        canvasObject.AddComponent<GraphicRaycaster>();

        var imageObject = new GameObject("Kaleidoscope Mask");
        imageObject.transform.SetParent(canvasObject.transform, false);

        var rect = imageObject.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var image = imageObject.AddComponent<Image>();
        image.raycastTarget = false;
        image.material = material;
        image.color = Color.white;

        var overlay = imageObject.AddComponent<TimedKaleidoscopeOverlay>();
        var serializedOverlay = new SerializedObject(overlay);
        serializedOverlay.FindProperty("duration").floatValue = 30f;
        serializedOverlay.FindProperty("fadeIn").floatValue = 1.25f;
        serializedOverlay.FindProperty("fadeOut").floatValue = 3f;
        serializedOverlay.FindProperty("overlayMaterial").objectReferenceValue = material;
        serializedOverlay.ApplyModifiedPropertiesWithoutUndo();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
    }

    static Material EnsureMaterial()
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (material != null)
            return material;

        var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
        material = new Material(shader)
        {
            name = "KaleidoscopeIntroOverlay"
        };

        material.SetColor("_TintA", new Color(0.05f, 0.95f, 1f, 1f));
        material.SetColor("_TintB", new Color(1f, 0.18f, 0.65f, 1f));
        material.SetColor("_TintC", new Color(1f, 0.86f, 0.18f, 1f));
        material.SetFloat("_Opacity", 0.82f);
        material.SetFloat("_Segments", 12f);
        material.SetFloat("_RotationSpeed", 0.14f);
        material.SetFloat("_PulseSpeed", 1.35f);
        material.SetFloat("_Zoom", 3.8f);
        material.SetFloat("_Feather", 0.18f);
        material.SetFloat("_CenterSize", 0.06f);
        material.SetFloat("_PassthroughMix", 0.42f);
        material.SetFloat("_TextureDensity", 10f);
        material.SetFloat("_MirrorSharpness", 2.8f);
        material.SetFloat("_Alpha", 1f);

        AssetDatabase.CreateAsset(material, MaterialPath);
        return material;
    }
}
