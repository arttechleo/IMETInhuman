using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds the Quest APK into BUILDS beside the project, from the menu or from
/// the command line:
///   Unity.exe -batchmode -quit -projectPath ... -buildTarget Android
///             -executeMethod QuestBuild.Build -logFile build.log
/// The large media is not in the APK; push it with IMETINHUMAN > Media >
/// Push To Headset.
/// </summary>
static class QuestBuild
{
    const string Output = "../BUILDS/IMETINHUMAN.apk";

    [MenuItem("IMETINHUMAN/Build/Quest APK")]
    public static void Build()
    {
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", Output));
        Directory.CreateDirectory(Path.GetDirectoryName(path));

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = path,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None
        });

        var summary = report.summary;
        Debug.Log($"Quest build: {summary.result}, {summary.totalErrors} errors, " +
                  $"{summary.totalSize / 1e6:F0} MB, {summary.totalTime.TotalSeconds:F0} s -> {path}");
        if (Application.isBatchMode)
            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
