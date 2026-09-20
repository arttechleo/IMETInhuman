using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Testing aid: number keys jump straight to one intro effect, so each can be
    /// checked without sitting through the whole sequence.
    ///
    ///   1..9  play that effect alone, from its start (stops the others)
    ///   0     replay the full intro sequence
    ///   X     stop everything
    ///
    /// Effects are numbered in the order of the list below; left empty, it takes
    /// every <see cref="IIntroEffect"/> on this object in component order
    /// (kaleidoscope 1, rain 2). New effects only need to implement the interface.
    ///
    /// <see cref="KaleidoscopeIntroBootstrap"/> adds this in the Editor and in
    /// development builds only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EffectTestHotkeys : MonoBehaviour
    {
        [Tooltip("Effect for key 1, key 2, ... Each must implement IIntroEffect. " +
                 "Leave empty to use every effect on this object in component order.")]
        [SerializeField] List<MonoBehaviour> effects = new List<MonoBehaviour>();

        [SerializeField] bool showReadout = true;

        static readonly Key[] DigitKeys =
        {
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5,
            Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9
        };

        static readonly Key[] NumpadKeys =
        {
            Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4, Key.Numpad5,
            Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9
        };

        List<IIntroEffect> resolved;
        KaleidoscopeIntroBootstrap sequence;
        string lastAction = "";
        GUIStyle style;

        void Start()
        {
            sequence = GetComponent<KaleidoscopeIntroBootstrap>();

            resolved = effects.Count > 0
                ? effects.OfType<IIntroEffect>().ToList()
                : GetComponents<MonoBehaviour>().OfType<IIntroEffect>().ToList();

            var map = string.Join(", ", resolved.Select((e, i) => $"{i + 1} {e.EffectName} ({e.Duration:0}s)"));
            Debug.Log($"Effect test keys: {map}, 0 full sequence, X stop.", this);
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null || resolved == null)
                return;

            for (var i = 0; i < resolved.Count && i < DigitKeys.Length; i++)
            {
                if (keyboard[DigitKeys[i]].wasPressedThisFrame || keyboard[NumpadKeys[i]].wasPressedThisFrame)
                    PlaySolo(i);
            }

            if (keyboard.digit0Key.wasPressedThisFrame || keyboard.numpad0Key.wasPressedThisFrame)
                PlaySequence();

            if (keyboard.xKey.wasPressedThisFrame)
                StopAll();
        }

        void PlaySolo(int index)
        {
            for (var i = 0; i < resolved.Count; i++)
            {
                if (i != index)
                    resolved[i].StopNow();
            }

            resolved[index].PlayFromStart();
            lastAction = $"{index + 1} {resolved[index].EffectName}";
            Debug.Log($"Effect test: playing {resolved[index].EffectName} alone.", this);
        }

        void PlaySequence()
        {
            if (sequence == null)
                return;

            foreach (var effect in resolved)
                effect.StopNow();

            sequence.RestartSequence();
            lastAction = "0 Full sequence";
            Debug.Log($"Effect test: full sequence, {sequence.SequenceLength:0}s.", this);
        }

        void StopAll()
        {
            foreach (var effect in resolved)
                effect.StopNow();

            lastAction = "Stopped";
        }

        // Game view only; the headset does not render IMGUI.
        void OnGUI()
        {
            if (!showReadout || resolved == null)
                return;

            style ??= new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };

            var lines = new List<string> { $"<b>{lastAction}</b>" };
            for (var i = 0; i < resolved.Count; i++)
            {
                var e = resolved[i];
                var state = e.IsPlaying
                    ? $"{e.PlaybackTime:0.0} / {e.Duration:0}s"
                    : e.PlaybackTime < 0f && !float.IsNegativeInfinity(e.PlaybackTime) ? $"starts in {-e.PlaybackTime:0.0}s" : "-";
                lines.Add($"{i + 1}  {e.EffectName}   {state}");
            }
            lines.Add("0  full sequence    X  stop");

            var text = string.Join("\n", lines);
            var size = style.CalcSize(new GUIContent(text));
            GUI.Box(new Rect(8, 8, size.x + 16, size.y + 12), GUIContent.none);
            GUI.Label(new Rect(16, 14, size.x, size.y), text, style);
        }
    }
}
