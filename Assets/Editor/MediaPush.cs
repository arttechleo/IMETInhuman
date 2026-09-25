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
/// only needs pushing again when it changes. Files already on the headset at
/// the same size are skipped, so pushing again only sends what is new.
/// </summary>
static class MediaPush
{
    const string MediaRoot = "../IMETINHUMAN_Media";
    static readonly string[] Folders = { "SplatSeq", "Stereo", "LukeSeq", "ChairSeq", "StageSeq" };

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
            var onHeadset = RemoteSizes(adb, $"{destination}/{folder}");
            var all = Directory.GetFiles(source);
            var files = System.Array.FindAll(all, f =>
                !onHeadset.TryGetValue(Path.GetFileName(f), out var size) || size != new FileInfo(f).Length);
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
                Debug.Log($"Media push: {folder}: {files.Length} sent, {all.Length - files.Length} already there.");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }
    }

    // Name -> size of every file in a folder on the headset.
    static System.Collections.Generic.Dictionary<string, long> RemoteSizes(string adb, string folder)
    {
        var sizes = new System.Collections.Generic.Dictionary<string, long>();
        using var process = Process.Start(new ProcessStartInfo(adb, $"shell ls -l {folder}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        // -rw-rw---- 1 u0_a123 ext_data_rw 2880008 2026-09-23 16:40 000020.spl
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 8 && long.TryParse(parts[4], out var size))
                sizes[parts[parts.Length - 1]] = size;
        }
        return sizes;
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
