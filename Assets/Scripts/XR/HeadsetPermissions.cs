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
        // Which request is open: a late or doubled answer to an older one is ignored.
        static int askToken;
        static string askingFor;
        static Action<bool> askingDone;

        // If neither callback ever fires (the dialog torn down by a pause), the
        // queue would wait forever and every later request with it. Back in
        // focus with a request still open, answer it from the permission state.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void WatchFocus()
        {
            Application.focusChanged += async focused =>
            {
                if (!focused || !asking)
                    return;
                var token = askToken;
                // Room for the real callback, which usually follows the focus change.
                await System.Threading.Tasks.Task.Delay(1000);
                if (asking && token == askToken)
                {
                    Debug.LogWarning($"Permission request for {askingFor} got no answer; using the current state.");
                    Finish(token, askingFor, askingDone, Has(askingFor));
                }
            };
        }
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
            var token = ++askToken;
            askingFor = permission;
            askingDone = done;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => Finish(token, permission, done, true);
            callbacks.PermissionDenied += _ => Finish(token, permission, done, false);
            Permission.RequestUserPermission(permission, callbacks);
        }

        static void Finish(int token, string permission, Action<bool> done, bool granted)
        {
            if (!asking || token != askToken)
                return;
            asking = false;
            askingDone = null;
            if (!granted)
                Debug.LogWarning($"Permission denied: {permission}");
            done?.Invoke(granted);
            AskNext();
        }
#endif
    }
}
