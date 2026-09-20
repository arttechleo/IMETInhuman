using System.IO;
using System.Xml;
using UnityEditor.Android;

/// <summary>
/// Declares the permissions the passthrough camera feed asks for. Unity adds the
/// plain Android camera permission on its own when WebCamTexture is used, but
/// Meta's headset-camera permission is not something it knows about, and a
/// permission missing from the manifest can never be granted at runtime.
/// </summary>
sealed class PassthroughCameraManifest : IPostGenerateGradleAndroidProject
{
    const string AndroidNs = "http://schemas.android.com/apk/res/android";

    static readonly string[] Permissions =
    {
        "android.permission.CAMERA",
        "horizonos.permission.HEADSET_CAMERA",
        // Pinching the splat capture about needs the headset's hand tracking.
        "com.oculus.permission.HAND_TRACKING",
    };

    // Hands are used when there are no controllers, so the app must not require them.
    static readonly string[] OptionalFeatures = { "oculus.software.handtracking" };

    public int callbackOrder => 100;

    public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
    {
        var path = Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(path))
            return;

        var doc = new XmlDocument();
        doc.Load(path);
        var manifest = doc.DocumentElement;

        foreach (var permission in Permissions)
        {
            var present = false;
            foreach (XmlElement e in manifest.GetElementsByTagName("uses-permission"))
                present |= e.GetAttribute("name", AndroidNs) == permission;
            if (present)
                continue;

            var element = doc.CreateElement("uses-permission");
            element.SetAttribute("name", AndroidNs, permission);
            manifest.AppendChild(element);
        }

        foreach (var feature in OptionalFeatures)
        {
            var present = false;
            foreach (XmlElement e in manifest.GetElementsByTagName("uses-feature"))
                present |= e.GetAttribute("name", AndroidNs) == feature;
            if (present)
                continue;

            var element = doc.CreateElement("uses-feature");
            element.SetAttribute("name", AndroidNs, feature);
            element.SetAttribute("required", AndroidNs, "false");
            manifest.AppendChild(element);
        }

        doc.Save(path);
    }
}
