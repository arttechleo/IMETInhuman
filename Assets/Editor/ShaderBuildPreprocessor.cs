using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Keeps every effect shader in the build, every build.
///
/// The effects create their materials in code, through Shader.Find, so nothing
/// in the scene references them and the build strips them: in the player the
/// Find returns null and the effect quietly does not appear. The Quest setup
/// adds them to Always Included Shaders, but only when it is run, so a shader
/// written after the last run is missing from the next build -- which is what
/// happened to the dark room. This runs on every build instead.
/// </summary>
sealed class ShaderBuildPreprocessor : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        var shaders = AssetDatabase.FindAssets("t:Shader", new[] { "Assets/Shaders" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(AssetDatabase.LoadAssetAtPath<Shader>)
            .Where(s => s != null)
            .ToList();
        if (shaders.Count == 0)
            return;

        var settings = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
        var included = settings.FindProperty("m_AlwaysIncludedShaders");

        var have = new System.Collections.Generic.HashSet<Object>();
        for (var i = 0; i < included.arraySize; i++)
            have.Add(included.GetArrayElementAtIndex(i).objectReferenceValue);

        var added = 0;
        foreach (var shader in shaders.Where(s => !have.Contains(s)))
        {
            included.InsertArrayElementAtIndex(included.arraySize);
            included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = shader;
            added++;
        }
        if (added == 0)
            return;

        settings.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        Debug.Log($"Build: added {added} effect shader(s) to Always Included Shaders.");
    }
}
