using System.Linq;
using ImetInHuman.VFX;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

/// <summary>
/// Puts the Humobox into the active intro scene, cued to the rain effect:
///
///   Humobox                 HumoboxRainCue (follows the rain's playback time)
///     HumoboxPivot          inactive until the cue; HumoboxHead, PlayableDirector, AudioSource
///       Humobox_Master      the imported model
///
/// The director plays a Timeline built here from the take's animation clip and
/// voice recording. Running it again replaces the previous setup.
/// </summary>
static class HumoboxSceneSetup
{
    const string Root = "Assets/Humobox/";
    const string Take = "FaceTake_004";
    const string TimelinePath = Root + "Timelines/Humobox_" + Take + ".playable";

    [MenuItem("IMETINHUMAN/Humobox/Add Humobox To Rain (Take 4)")]
    public static void Run()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            Debug.LogWarning("Humobox setup: leave Play mode first.");
            return;
        }

        var master = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "Humobox_Master.fbx");
        var clip = AssetDatabase.LoadAllAssetsAtPath(Root + "Humobox@" + Take + ".fbx")
            .OfType<AnimationClip>()
            .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
        var voice = AssetDatabase.LoadAssetAtPath<AudioClip>(Root + "audio/" + Take + ".wav");
        var curves = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "curves/" + Take + "_head.json");

        if (master == null || clip == null)
        {
            Debug.LogError($"Humobox setup: missing {(master == null ? "Humobox_Master.fbx" : "the " + Take + " clip")} under {Root}.");
            return;
        }

        var scene = SceneManager.GetActiveScene();

        foreach (var old in Object.FindObjectsByType<HumoboxRainCue>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            Undo.DestroyObjectImmediate(old.gameObject);

        var root = new GameObject("Humobox");
        Undo.RegisterCreatedObjectUndo(root, "Add Humobox");
        SceneManager.MoveGameObjectToScene(root, scene);

        var pivot = new GameObject("HumoboxPivot");
        pivot.transform.SetParent(root.transform, false);

        var model = (GameObject)PrefabUtility.InstantiatePrefab(master, scene);
        model.transform.SetParent(pivot.transform, false);

        var animator = model.GetComponent<Animator>();
        if (animator == null)
            animator = model.AddComponent<Animator>();
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var size = MeasureSize(model);
        Debug.Log($"Humobox setup: model is {size.x:0.###} x {size.y:0.###} x {size.z:0.###} m.");

        var audio = pivot.AddComponent<AudioSource>();
        audio.playOnAwake = false;
        audio.spatialBlend = 1f;
        audio.minDistance = 1f;

        var timeline = BuildTimeline(clip, voice, out var faceTrack, out var voiceTrack);

        var director = pivot.AddComponent<PlayableDirector>();
        director.playableAsset = timeline;
        director.playOnAwake = false;
        director.extrapolationMode = DirectorWrapMode.Hold;
        director.SetGenericBinding(faceTrack, animator);
        if (voiceTrack != null)
            director.SetGenericBinding(voiceTrack, audio);

        var head = pivot.AddComponent<HumoboxHead>();
        head.director = director;
        head.headCurves = curves;

        var cue = root.AddComponent<HumoboxRainCue>();
        var so = new SerializedObject(cue);
        so.FindProperty("pivot").objectReferenceValue = pivot;
        so.FindProperty("director").objectReferenceValue = director;
        so.ApplyModifiedPropertiesWithoutUndo();

        pivot.SetActive(false);

        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = root;
        Debug.Log($"Humobox setup: added to '{scene.name}', {Take} ({clip.length:0.0}s) at rain second 15. Save the scene to keep it.", root);
    }

    static TimelineAsset BuildTimeline(AnimationClip clip, AudioClip voice,
        out AnimationTrack faceTrack, out AudioTrack voiceTrack)
    {
        var folder = System.IO.Path.GetDirectoryName(TimelinePath).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(Root.TrimEnd('/'), "Timelines");
        AssetDatabase.DeleteAsset(TimelinePath);

        var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
        AssetDatabase.CreateAsset(timeline, TimelinePath);

        faceTrack = timeline.CreateTrack<AnimationTrack>(null, "Face");
        // Keep the model where the scene put it; the clip only moves it relative to that.
        faceTrack.trackOffset = TrackOffset.ApplySceneOffsets;
        faceTrack.CreateClip(clip).displayName = Take;

        voiceTrack = null;
        if (voice != null)
        {
            voiceTrack = timeline.CreateTrack<AudioTrack>(null, "Voice");
            voiceTrack.CreateClip(voice).displayName = Take;
        }

        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();
        return timeline;
    }

    static Vector3 MeasureSize(GameObject model)
    {
        var renderers = model.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return Vector3.zero;
        var bounds = renderers[0].bounds;
        foreach (var r in renderers)
            bounds.Encapsulate(r.bounds);
        return bounds.size;
    }
}

/// <summary>Runs the setup once on the next Editor refresh, then deletes itself.</summary>
[InitializeOnLoad]
static class HumoboxSceneSetupOnce
{
    const string Marker = "Assets/Editor/HumoboxSceneSetup.run";

    static HumoboxSceneSetupOnce()
    {
        if (!System.IO.File.Exists(Marker))
            return;
        EditorApplication.delayCall += () =>
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "IntroCopy")
            {
                Debug.LogWarning("Humobox setup skipped: open IntroCopy and use IMETINHUMAN > Humobox > Add Humobox To Rain (Take 4).");
                return;
            }
            AssetDatabase.DeleteAsset(Marker);
            HumoboxSceneSetup.Run();
        };
    }
}
