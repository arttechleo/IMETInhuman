using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace ImetInHuman.XR
{
    /// <summary>
    /// Asks for Android runtime permissions one at a time.
    ///
    /// Android shows one permission dialog at a time, and a request made while
    /// another is open is dropped rather than queued -- so two components each
    /// asking at startup (scene data for the table, the camera for the effects)
    /// would leave one of them waiting forever. Everything goes through here.
    /// Outside an Android player every request is answered false at once.
    /// </summary>
    public static class HeadsetPermissions
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        static readonly Queue<(string permission, Action<bool> done)> pending = new();
        static bool asking;
#endif

        public static bool Has(string permission)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(permission);
#else
            return false;
#endif
        }

        /// <summary>Calls <paramref name="done"/> with whether the user granted it.</summary>
        public static void Request(string permission, Action<bool> done)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Has(permission))
            {
                done?.Invoke(true);
                return;
            }
            pending.Enqueue((permission, done));
            AskNext();
#else
            done?.Invoke(false);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        static void AskNext()
        {
            if (asking || pending.Count == 0)
                return;

            var (permission, done) = pending.Dequeue();
            if (Has(permission))
            {
                done?.Invoke(true);
                AskNext();
                return;
            }

            asking = true;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => Finish(permission, done, true);
            callbacks.PermissionDenied += _ => Finish(permission, done, false);
            Permission.RequestUserPermission(permission, callbacks);
        }

        static void Finish(string permission, Action<bool> done, bool granted)
        {
            asking = false;
            if (!granted)
                Debug.LogWarning($"Permission denied: {permission}");
            done?.Invoke(granted);
            AskNext();
        }
#endif
    }
}
