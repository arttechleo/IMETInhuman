using UnityEngine;

namespace ImetInHuman.XR
{
    /// <summary>
    /// Whether the app is running over passthrough.
    ///
    /// On the Quest build the room is composited by the headset runtime behind the
    /// app layer, so effects cannot sample it: there is nothing in the opaque
    /// texture to refract or blur, and anything opaque the app draws hides the
    /// room. Effects read this to pick their passthrough look. In the Editor and
    /// on PC the app renders its own background and keeps the full look.
    /// </summary>
    public static class MixedReality
    {
        public static bool PassthroughActive => Application.platform == RuntimePlatform.Android;

        /// <summary>
        /// PCVR: a headset is rendering, but on the PC, in the virtual room (the
        /// HDRI sky) rather than over passthrough. Effects drop their own
        /// backdrops here too -- the room is behind them -- but, unlike
        /// passthrough, can sample it: it is rendered, so it is in the opaque texture.
        /// </summary>
        public static bool VirtualRoom => !PassthroughActive && UnityEngine.XR.XRSettings.isDeviceActive;

        /// <summary>Something real or virtual is behind the effects, not an empty screen.</summary>
        public static bool RoomBehind => PassthroughActive || VirtualRoom;
    }
}
