# Humobox FaceCap — runbook

Webcam + mic puppeteering of **Humobox** in Blender, recorded as takes with voice-over.
Pipeline: `tracker.py` (webcam → MediaPipe → control channels, mic → WAV) → UDP → Blender addon `humobox_facecap.py` → `fc_*` props on the Armature → drivers → bones and shape keys.

- **Blend file:** `S:\Projects\2026\CapStone\Humobox_FaceCap_AI.blend`. Any file an AI edits gets an `_AI` copy; never edit the `…WORKING.blend` original.
- **Tools folder:** `S:\Projects\2026\CapStone\FaceCap_AI\`
- **Takes audio:** `S:\Projects\2026\CapStone\FaceCap_audio\`

To get takes into Unity (Quest 3 / PICO, URP), see `..\Unity_Export_AI\README_UNITY.md`.

## Files

| File | What |
|---|---|
| `tracker.py` | Camera/mic process. It writes `tracker.log`, which is appended to and records the exit reason. |
| `humobox_facecap.py` | Blender addon. It is also stored in the .blend as a registered text block, so it auto-loads when scripts are trusted. |
| `phonemes.py` | Offline phoneme pass on a take WAV (Allosaurus) for the tongue-tip keys. |
| `tongue.py` | Experimental camera tongue detector (off; `--tongue-detect`). |
| `calib.json` / `colors.json` | Neutral-face calibration, written by **C**. |
| `.venv` | Python 3.12 (uv): mediapipe, opencv, sounddevice, soundfile, torch-cpu, allosaurus. |

## Manual run

1. Open the blend and allow **Auto Run Python Scripts**. The **FaceCap** tab appears in the 3D View sidebar.
2. Click **Launch Tracker**. A preview window opens after about 5 s and Live turns on (about 30 fps).
3. Press **C** in the preview window (or click *Calibrate Neutral*). Relax your face, close your mouth and look where you'll be looking while performing.
4. **Record** → perform → **Stop**.
   - The take becomes `FaceTake_###`: a copy of `ArmatureAction` plus per-frame `fc_*` keys and a WAV strip in the VSE.
   - Live then pauses so playback shows the take.
   - The speech tongue pass adds keys 20–60 s later.
5. Click **Stop Tracker** (or press **Shift+Q** in the preview window) to release the camera and mic.

### Controls while live

| Action | Effect |
|---|---|
| Hold **Right Ctrl** or the **mouse back button** (global) | Tongue out; eases out and back in |
| **T** in the preview / *Tongue Out* button | Toggle the tongue |
| Fast head flick left/right | Box spins 90° (*Reset Spin* to undo) |
| Head turn / tilt | Box stretches / tilts, relative to the scene camera |
| Brows up / down | Ear flaps and lifts / drops |
| Nose scrunch, nostril flare, sniff (mouth closed) | Nose wrinkles / nostrils widen |
| "th", "l", "t", "d", "n" sounds | Tongue tip peeks: live for "th", after the take for all of them |

## What drives what

Every `fc_*` value lives on the **Armature** object and is keyed in takes. The drivers are simple expressions.

- **Mouth:** jaw, smile, pucker, funnel, stretch, upperUp, lowerDown, press, frown, mouthShift, mouthTilt.
  - These drive shape keys on `Humobox` (the skin) and `Cube.005` (the lips), and the Mouth bone.
- **Eye:** blink (`Blink` key), wide/squint (lid bones), lookX/lookY (`EyeLook` bone).
- **Nose:** sniff (`NoseScrunch` plus a Nose bone lift), noseFlare (`NostrilWiden`, `Sniff`), noseTwitch (Nose bone).
- **Ear:** ear (flap), earLift (up/down) on the `Ear` bone.
- **Tongue:** tongueOut, tongueTip, tongueX, tongueY.
  - These drive `Tongue_AI` keys and delta transforms.
  - Staging: the mouth opens → the tongue slides forward → it drapes.
  - It is checked collision-free against the lips and teeth.
- **Head:** headYaw, headRoll and spin drive the `FC_Head_AI` → `FC_Spin_AI` empties above `Empty`.
- **Mouth extras:** `Teeth_AI`, `LowerTeeth_AI` and `MouthCavity_AI` are bone-parented to Mouth. The teeth narrow during "O"/pucker.

## Tuning (Channels panel)

Each channel has a **gain** and, for bipolar channels, an **invert** toggle.

- **Video latency** (60 ms) shifts face keys against the voice.
- **Voice → jaw** (0.35) adds mic loudness to the jaw.
- Tracker constants are at the top of `tracker.py`: `SPIN_*`, `TONGUE_*`, and the thresholds in `nose_channels`.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Playback shows no face animation, only sound | The armature has `ArmatureAction` assigned. Assign the `FaceTake_###` action. |
| The face ignores the take while playing | Live is on; turn **Live OFF**. It pauses automatically after a take. |
| Some part stops following | A driver has a stale invalid flag. Toggle Live (this re-validates), or in Python: `for f in id.animation_data.drivers: f.driver.is_valid = True` |
| Tracker vanished mid-take | The take is auto-saved up to that point and the tracker relaunches. Read `tracker.log` (`[exit]` lines). |
| Nose or eyes constantly "on" | Recalibrate (**C**) in your performing posture. |
| "Port busy" | A tracker is already running; don't launch twice. |

## Notes for AI-driven runs (Blender MCP)

- Operators: `bpy.ops.facecap.launch_tracker()`, `live_toggle()`, `calibrate()`, `record_start()`, `record_stop()`, `stop_tracker()`, `tongue_toggle()`, `spin_reset()`, `phoneme_pass()`.
- Runtime state:

  ```python
  S = [c for c in bpy.types.Operator.__subclasses__() if c.__name__ == 'FACECAP_OT_live_toggle'][-1].execute.__globals__['S']
  ```

  Fields: `live`, `recording`, `face_ok`, `fps_est`, `message`, `last_take`, `proc`.
- Reload after editing the addon: turn Live off first, send `{"cmd":"quit"}` to UDP 127.0.0.1:9871 to stop the tracker, write the source into the text block `humobox_facecap.py`, then `exec(compile(src, name, "exec"), {"__name__": "__main__"})`.
- Wait for the tracker by polling `tracker.log` for `listening for commands`. Camera start takes about 5–10 s.
- Gotchas:
  - New drivers or driver variables often come up `is_valid=False`: reset the flag.
  - Setting `matrix_world` on a bone-parented object whose `delta_location` is driven bakes the delta into `location`. Set `location` directly.
  - The lips mesh is an open shell: test collisions with `BVHTree.overlap`, not inside/outside checks.
  - Shape key edits go to the key blocks. Never apply them.

## What is in this repository

Source only: the addon, the tracker, and the two offline passes. Everything
else the pipeline needs is generated, private or large, and stays on the
artist's machine in `S:\Projects\2026\CapStone\FaceCap_AI\`:

| Left out | Why, and how to get it back |
|---|---|
| `face_landmarker.task` | MediaPipe's model, 3.7 MB. Download the Face Landmarker bundle from Google's MediaPipe models page into this folder. |
| `calib.json`, `colors.json` | A calibration of one face under one light. Press **C** (and **T** for the tongue) to write your own. |
| `.venv` | `uv venv --python 3.12` then install mediapipe, opencv-python, sounddevice, soundfile, torch (cpu) and allosaurus. |
| `tracker.log`, `debug_*.png` | Run output; the debug frames are webcam stills. |
| `Humobox_FaceCap_AI.blend`, `FaceCap_audio\` | The rig and the recorded takes: hundreds of MB, and the rig carries the addon as a text block. |
