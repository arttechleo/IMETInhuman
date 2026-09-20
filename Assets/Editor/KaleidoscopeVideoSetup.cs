using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ImetInHuman.VFX;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Video;
using Debug = UnityEngine.Debug;

/// <summary>
/// Builds the kaleidoscope video atlas and wires it into the intro scene.
///
/// The source is a montage of separate shots, one per art (sculpture, perfume,
/// cooking, conducting, painting...). The atlas cuts it at its shot changes and
/// gives every shot its own tile, looping that shot alone, so each cell of the
/// kaleidoscope shows one art rather than a slice of the montage drifting across
/// cuts. Everything is packed into one file so the app decodes once -- a
/// VideoPlayer per cell runs a headset out of hardware decoders almost
/// immediately.
/// </summary>
public static class KaleidoscopeVideoSetup
{
    const string ScenePath = "Assets/Scenes/Intro.unity";
    const string AtlasPath = "Assets/Video/Kaleidoscope/KaleidoscopeAtlas.mp4";
    const string DefaultSource = @"S:\Users\arttechleo\Downloads\IMETINHUMANKaleidoscope.mp4";

    // 3 x 512 = 1536 wide for six shots. Bigger starts running into per-device
    // decoder limits on Android, and a cell never covers enough screen to want more.
    const int TilePixels = 512;
    const int Fps = 24;

    // Frame-difference threshold for a shot change. Hard cuts score well above
    // this; camera moves and lighting shifts inside a shot stay under it.
    const float CutThreshold = 0.25f;

    // Shots shorter than this are merged into the previous one: flash frames and
    // dissolves otherwise become tiles that loop a blink.
    const float MinShotSeconds = 1.5f;

    // Trimmed off both ends of every shot so no tile loops a frame of its
    // neighbour, which encoders often smear across a cut.
    const float ShotTrim = 0.09f;

    // Horizontal centre of each shot's square crop, 0 = left edge, 1 = right.
    // The source is 16:9 and cells are square; most subjects sit centred, but the
    // painter stands right of frame and a centred crop cuts her in half.
    static readonly Dictionary<int, float> CropFocus = new Dictionary<int, float>
    {
        { 5, 0.72f },
    };

    [MenuItem("IMET IN HUMAN/Wire Kaleidoscope Atlas")]
    public static void Wire()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        WireActiveScene();
    }

    /// <summary>
    /// Wires the atlas into the open scene when it has an intro bootstrap, so a
    /// working copy of the intro gets the footage too; otherwise opens Intro.unity.
    /// Saves the scene without prompting.
    /// </summary>
    public static bool WireActiveScene()
    {
        var atlas = AssetDatabase.LoadAssetAtPath<VideoClip>(AtlasPath);
        if (atlas == null)
        {
            Debug.LogError($"No atlas at {AtlasPath}. Run IMET IN HUMAN/Rebuild Kaleidoscope Atlas first.");
            return false;
        }

        var scene = SceneManager.GetActiveScene();
        var bootstrap = Object.FindAnyObjectByType<KaleidoscopeIntroBootstrap>(FindObjectsInactive.Include);
        if (bootstrap == null && scene.path != ScenePath)
        {
            scene = EditorSceneManager.OpenScene(ScenePath);
            bootstrap = Object.FindAnyObjectByType<KaleidoscopeIntroBootstrap>(FindObjectsInactive.Include);
        }

        if (bootstrap == null)
        {
            Debug.LogError($"{scene.path} has no KaleidoscopeIntroBootstrap to attach the overlay to.");
            return false;
        }

        // The bootstrap adds this component at runtime when it is missing, but a
        // runtime-added one has no atlas assigned and no way to reach one. Adding
        // it to the scene is what makes the footage survive into the build.
        var overlay = bootstrap.GetComponent<KaleidoscopeVideoOverlay>();
        if (overlay == null)
            overlay = Undo.AddComponent<KaleidoscopeVideoOverlay>(bootstrap.gameObject);

        // The grid is read back off the atlas itself, so it cannot drift from how
        // the file was built.
        var grid = new Vector2Int(Mathf.Max(1, (int)atlas.width / TilePixels),
                                  Mathf.Max(1, (int)atlas.height / TilePixels));

        var so = new SerializedObject(overlay);
        so.FindProperty("atlasClip").objectReferenceValue = atlas;
        so.FindProperty("atlasUrl").stringValue = string.Empty;
        so.FindProperty("atlasGrid").vector2IntValue = grid;
        so.FindProperty("videoResolution").vector2IntValue =
            new Vector2Int((int)atlas.width, (int)atlas.height);
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(overlay);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log($"Wired {grid.x}x{grid.y} kaleidoscope clips ({atlas.width}x{atlas.height} atlas) "
                + $"into {bootstrap.name} in {scene.path}.", overlay);
        return true;
    }

    [MenuItem("IMET IN HUMAN/Rebuild Kaleidoscope Atlas")]
    public static void RebuildAtlas()
    {
        var source = EditorUtility.OpenFilePanel("Kaleidoscope source video",
                                                 Path.GetDirectoryName(DefaultSource) ?? "",
                                                 "mp4,mov,m4v,webm");
        if (string.IsNullOrEmpty(source))
            return;

        var duration = ProbeDuration(source);
        if (duration <= 0f)
        {
            Debug.LogError($"Could not read a duration from {source}. Is ffprobe on PATH?");
            return;
        }

        EditorUtility.DisplayProgressBar("Kaleidoscope", "Finding shot changes...", 0.2f);
        List<Vector2> shots;
        try
        {
            shots = DetectShots(source, duration);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        var output = Path.GetFullPath(AtlasPath);
        if (!BuildAtlas(source, output, shots, duration))
            return;

        AssetDatabase.ImportAsset(AtlasPath, ImportAssetOptions.ForceUpdate);
        Debug.Log($"Rebuilt {AtlasPath} from {Path.GetFileName(source)}: {shots.Count} shots, "
                + $"{duration:0.00}s loop. Run Wire Kaleidoscope Atlas next.");
    }

    static float ProbeDuration(string source)
    {
        var args = "-v error -show_entries format=duration -of csv=p=0 \"" + source + "\"";
        var stdout = Run("ffprobe", args, out var ok);
        if (!ok)
            return 0f;

        return float.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : 0f;
    }

    /// <summary>Shot spans as (start, end) seconds, in source order.</summary>
    static List<Vector2> DetectShots(string source, float duration)
    {
        var inv = CultureInfo.InvariantCulture;
        var args = $"-hide_banner -i \"{source}\" -an "
                 + $"-vf \"select='gt(scene,{CutThreshold.ToString(inv)})',showinfo\" -f null -";

        // ffmpeg reports filter output on stderr and exits 0, so read both.
        var log = Run("ffmpeg", args, out _, keepStderr: true);

        var cuts = Regex.Matches(log, @"pts_time:([\d.]+)")
            .Cast<Match>()
            .Select(m => float.Parse(m.Groups[1].Value, inv))
            .ToList();

        var edges = new List<float> { 0f };
        foreach (var cut in cuts.Append(duration))
        {
            if (cut - edges[edges.Count - 1] < MinShotSeconds)
            {
                if (cut >= duration && edges.Count > 1)
                    edges[edges.Count - 1] = duration;
                continue;
            }
            edges.Add(cut);
        }
        if (edges[edges.Count - 1] < duration)
            edges.Add(duration);

        var shots = new List<Vector2>();
        for (var i = 0; i + 1 < edges.Count; i++)
            shots.Add(new Vector2(edges[i], edges[i + 1]));
        return shots;
    }

    static bool BuildAtlas(string source, string output, List<Vector2> shots, float duration)
    {
        var inv = CultureInfo.InvariantCulture;
        var count = shots.Count;
        var columns = Mathf.CeilToInt(Mathf.Sqrt(count));
        var rows = Mathf.CeilToInt(count / (float)columns);
        var loop = duration.ToString("0.###", inv);

        var chains = new StringBuilder($"[0:v]split={count}");
        for (var i = 0; i < count; i++)
            chains.Append($"[s{i}]");
        chains.Append(';');

        for (var i = 0; i < count; i++)
        {
            var start = shots[i].x + ShotTrim;
            var end = shots[i].y - ShotTrim;
            var frames = Mathf.Max(1, Mathf.FloorToInt((end - start) * Fps));
            var focus = CropFocus.TryGetValue(i, out var f) ? f : 0.5f;

            // Cut the shot out, square-crop it around its subject, then loop it for
            // the length of the atlas. Every tile loops on its own shot, so the
            // footage in a cell never jumps into a different art.
            chains.Append($"[s{i}]trim=start={start.ToString("0.###", inv)}:end={end.ToString("0.###", inv)},"
                        + $"setpts=PTS-STARTPTS,fps={Fps},"
                        + $"crop=ih:ih:'clip(iw*{focus.ToString("0.###", inv)}-ih/2,0,iw-ih)':0,"
                        + $"scale={TilePixels}:{TilePixels}:flags=lanczos,format=yuv420p,"
                        + $"loop=loop=-1:size={frames}:start=0,setpts=N/{Fps}/TB,trim=duration={loop}[v{i}];");
        }

        // A grid with more cells than shots is padded black; the shader only
        // picks from real tiles when the grid matches the shot count, so keep
        // counts that fill it (4, 6, 9, 12...) where the edit allows.
        var layout = string.Join("|", Enumerable.Range(0, count)
            .Select(i => $"{i % columns * TilePixels}_{i / columns * TilePixels}"));
        var inputs = string.Concat(Enumerable.Range(0, count).Select(i => $"[v{i}]"));
        chains.Append($"{inputs}xstack=inputs={count}:layout={layout}:fill=black[out]");

        var args = new StringBuilder("-hide_banner -v error -y");
        args.Append($" -i \"{source}\"");
        args.Append($" -filter_complex \"{chains}\" -map \"[out]\" -an -t {loop}");

        // Main profile without B-frames: Media Foundation skews timestamps on
        // B-frame streams. The metadata filter tags BT.709 so it does not guess.
        args.Append(" -c:v libx264 -profile:v main -bf 0 -preset medium -crf 20 -pix_fmt yuv420p");
        args.Append(" -bsf:v h264_metadata=colour_primaries=1:transfer_characteristics=1:matrix_coefficients=1");
        args.Append(" -x264-params keyint=48:min-keyint=24 -movflags +faststart");
        args.Append($" \"{output}\"");

        EditorUtility.DisplayProgressBar("Kaleidoscope",
                                         $"Encoding {count}-shot atlas ({columns}x{rows})...", 0.5f);
        try
        {
            var stderr = Run("ffmpeg", args.ToString(), out var ok);
            if (ok)
                return true;

            Debug.LogError($"ffmpeg failed building the atlas: {stderr}");
            return false;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    static string Run(string exe, string args, out bool ok, bool keepStderr = false)
    {
        var info = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(info);

            // Drain stderr on its own task: ffmpeg writes a lot there, and reading
            // the two pipes one after the other deadlocks once either buffer fills.
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = stderrTask.Result;
            process.WaitForExit();

            ok = process.ExitCode == 0;
            return ok && !keepStderr ? stdout : stdout + stderr;
        }
        catch (System.Exception e)
        {
            ok = false;
            return $"{exe} could not be started ({e.Message}). Install ffmpeg and put it on PATH.";
        }
    }
}
