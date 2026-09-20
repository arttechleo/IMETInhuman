using UnityEngine;

namespace ImetInHuman.XR
{
    /// <summary>
    /// On PC (the Editor, or a Windows build over Quest Link) the headset shows a
    /// virtual room -- the HDRI sky -- instead of passthrough. The PC quality
    /// level is the one whose render pipeline makes the opaque and depth
    /// textures the rain refracts through; the Editor, set to the Android build
    /// target, would otherwise run with the Quest's lighter settings.
    /// </summary>
    static class PcvrSetup
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void UsePcQuality()
        {
            if (Application.platform == RuntimePlatform.Android)
                return;

            var names = QualitySettings.names;
            for (var i = 0; i < names.Length; i++)
            {
                if (names[i] == "PC")
                {
                    QualitySettings.SetQualityLevel(i, true);
                    Debug.Log("PCVR: using the PC quality level (rain refracts the virtual room).");
                    return;
                }
            }
        }
    }
}
