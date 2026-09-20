using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Android;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Copies the large media -- the converted splat sequence and the packed stereo
/// video -- from the media folder beside the project into the app's storage on
/// the connected headset. Kept out of the APK: builds stay small, and the media
/// only needs pushing again when it changes.
/// </summary>
static class MediaPush
{
    const string MediaRoot = "../IMETINHUMAN_Media";
    static readonly string[] Folders = { "SplatSeq", "Stereo" };

    [MenuItem("IMETINHUMAN/Media/Push To Headset")]
    static void Push()
    {
        var adb = Path.Combine(AndroidExternalToolsSettings.sdkRootPath, "platform-tools", "adb.exe");
        if (!File.Exists(adb))
        {
            Debug.LogError($"Media push: adb not found at {adb}.");
            return;
        }

        var id = PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android);
        var destination = $"/sdcard/Android/data/{id}/files";
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", MediaRoot));

        Run(adb, $"shell mkdir -p {destination}");
        foreach (var folder in Folders)
        {
            var source = Path.Combine(root, folder);
            if (!Directory.Exists(source))
            {
                Debug.LogWarning($"Media push: {source} does not exist; skipped.");
                continue;
            }
            // File by file in batches: pushing a whole directory into the
            // headset's app storage fails part-way ("failed to read copy response").
            Run(adb, $"shell mkdir -p {destination}/{folder}");
            var files = Directory.GetFiles(source);
            try
            {
                for (var i = 0; i < files.Length; i += 40)
                {
                    EditorUtility.DisplayProgressBar("Push media to headset", $"{folder}: {i}/{files.Length}",
                                                     (float)i / files.Length);
                    var batch = string.Join(" ", System.Linq.Enumerable.Select(
                        System.Linq.Enumerable.Take(System.Linq.Enumerable.Skip(files, i), 40), f => $"\"{f}\""));
                    Run(adb, $"push {batch} {destination}/{folder}/");
                }
                Debug.Log($"Media push: {folder}: {files.Length} files.");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }

    static string Run(string exe, string args)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        var lines = output.Trim().Split('\n');
        return lines.Length > 0 ? lines[lines.Length - 1] : "";
    }
}
