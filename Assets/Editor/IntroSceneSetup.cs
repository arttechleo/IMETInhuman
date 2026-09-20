using System;
using System.Linq;
using ImetInHuman.XR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;

public static class IntroSceneSetup
{
    const string ScenePath = "Assets/Scenes/Intro.unity";

    [MenuItem("IMET IN HUMAN/Setup Intro XR Scene")]
    public static void CreateIntroScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "Intro";

        var rig = new GameObject("XR Origin");
        AddComponentIfAvailable(rig, "Unity.XR.CoreUtils.XROrigin, Unity.XR.CoreUtils");
        AddComponentIfAvailable(rig, "UnityEngine.XR.Interaction.Toolkit.Inputs.XRInputModalityManager, Unity.XR.Interaction.Toolkit");

        var cameraOffset = new GameObject("Camera Offset");
        cameraOffset.transform.SetParent(rig.transform, false);
        cameraOffset.transform.localPosition = new Vector3(0f, 1.45f, 0f);

        var cameraObject = new GameObject("Main Camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.SetParent(cameraOffset.transform, false);
        cameraObject.transform.localPosition = Vector3.zero;
        cameraObject.AddComponent<Camera>();
        cameraObject.AddComponent<AudioListener>();
        AddComponentIfAvailable(cameraObject, "UnityEngine.InputSystem.XR.TrackedPoseDriver, Unity.InputSystem");

        CreateHand("Left Hand Input", XRHandInputPose.Handedness.Left, cameraOffset.transform);
        CreateHand("Right Hand Input", XRHandInputPose.Handedness.Right, cameraOffset.transform);

        var interactionManager = new GameObject("XR Interaction Manager");
        AddComponentIfAvailable(interactionManager, "UnityEngine.XR.Interaction.Toolkit.Interactors.XRInteractionManager, Unity.XR.Interaction.Toolkit");
        AddComponentIfAvailable(interactionManager, "UnityEngine.XR.Interaction.Toolkit.XRInteractionManager, Unity.XR.Interaction.Toolkit");

        var eventSystem = new GameObject("EventSystem");
        eventSystem.AddComponent<EventSystem>();
        eventSystem.AddComponent<InputSystemUIInputModule>();

        var light = new GameObject("Key Light");
        var directionalLight = light.AddComponent<Light>();
        directionalLight.type = LightType.Directional;
        directionalLight.intensity = 1.15f;
        light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Intro Floor";
        floor.transform.localScale = new Vector3(4f, 1f, 4f);

        EditorSceneManager.SaveScene(scene, ScenePath);
        AddSceneToBuildSettings(ScenePath);
        AssetDatabase.SaveAssets();
    }

    static void CreateHand(string name, XRHandInputPose.Handedness handedness, Transform parent)
    {
        var hand = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        hand.name = name;
        hand.transform.SetParent(parent, false);
        hand.transform.localScale = Vector3.one * 0.09f;
        hand.transform.localPosition = new Vector3(handedness == XRHandInputPose.Handedness.Left ? -0.25f : 0.25f, -0.25f, 0.65f);

        var pose = hand.AddComponent<XRHandInputPose>();
        var serializedPose = new SerializedObject(pose);
        serializedPose.FindProperty("hand").enumValueIndex = (int)handedness;
        serializedPose.ApplyModifiedPropertiesWithoutUndo();
    }

    static void AddComponentIfAvailable(GameObject target, string assemblyQualifiedTypeName)
    {
        var type = Type.GetType(assemblyQualifiedTypeName);
        if (type != null && target.GetComponent(type) == null)
            target.AddComponent(type);
    }

    static void AddSceneToBuildSettings(string path)
    {
        var scenes = EditorBuildSettings.scenes.ToList();
        if (scenes.All(scene => scene.path != path))
        {
            scenes.Add(new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
