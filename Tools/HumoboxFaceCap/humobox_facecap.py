bl_info = {
    "name": "Humobox FaceCap",
    "author": "Claude (for arttechleo)",
    "version": (1, 0, 0),
    "blender": (4, 4, 0),
    "location": "3D Viewport > Sidebar > FaceCap",
    "description": "Webcam face capture (MediaPipe tracker over UDP) driving Humobox, with voice-over recording",
    "category": "Animation",
}

import json
import math
import os
import socket
import subprocess
import sys
import time

import bpy
from bpy.props import (BoolProperty, FloatProperty, IntProperty, PointerProperty, StringProperty)

# Channel name -> (bipolar, label). Blender custom props are "fc_" + name.
CHANNELS = {
    "jaw": (False, "Jaw open"),
    "smile": (False, "Smile"),
    "pucker": (False, "Pucker"),
    "funnel": (False, "Funnel \"O\""),
    "stretch": (False, "Stretch \"EE\""),
    "upperUp": (False, "Upper lip up"),
    "lowerDown": (False, "Lower lip down"),
    "press": (False, "Press \"M/B/P\""),
    "frown": (False, "Frown"),
    "mouthShift": (True, "Mouth slide"),
    "mouthTilt": (True, "Mouth tilt"),
    "blink": (False, "Blink"),
    "wide": (False, "Eyes wide"),
    "squint": (False, "Squint"),
    "lookX": (True, "Look left/right"),
    "lookY": (True, "Look up/down"),
    "sniff": (False, "Nose scrunch"),
    "noseFlare": (False, "Nostril flare"),
    "noseTwitch": (True, "Nose twitch"),
    "ear": (False, "Ear flap (brows)"),
    "earLift": (True, "Ear up/down (brows)"),
    "tongueOut": (False, "Tongue out"),
    "tongueX": (True, "Tongue left/right"),
    "tongueY": (True, "Tongue up/down"),
    "tongueTip": (False, "Tongue tip (speech)"),
    "headYaw": (True, "Head turn → stretch"),
    "headRoll": (True, "Head tilt → tilt"),
    "spin": (True, "Spin (quarter turns)"),
}
UNBOUNDED = {"spin"}  # not clipped to -1..1
BIPOLAR = [n for n, (b, _) in CHANNELS.items() if b]
# Objects whose drivers read the fc_ props (re-validated when going live).
DRIVEN_OBJECTS = ("Armature", "FC_Head_AI", "FC_Spin_AI", "Teeth_AI", "LowerTeeth_AI", "Tongue_AI")
# Pose-bone channels driven from the fc_ props; stripped from takes so old keys don't stack.
FACE_BONES = ("Ear", "Nose", "LidUpper", "LidLower", "Mouth", "EyeLook")
DEFAULT_DIR = r"S:\Projects\2026\CapStone\FaceCap_AI"


class _State:
    sock = None
    proc = None
    live = False
    last_packet = 0.0
    fps_est = 0.0
    pkt_times = []
    tongue_on = False
    last_take_info = None
    latest = None          # last face "v" dict (raw tracker values)
    face_ok = False
    recording = False      # between rec_start sent and rec_stop sent
    waiting_stop = False   # rec_stop sent, waiting for the tracker's ack
    samples = []           # (t, {channel: processed value}) while recording
    rec_start_frame = 1
    rec_path = ""
    rec_sent_at = 0.0
    message = ""
    last_take = ""


S = _State()


# ---------------------------------------------------------------- settings

def _gain_props():
    return {f"gain_{n}": FloatProperty(name=CHANNELS[n][1], default=1.0, min=0.0, soft_max=3.0)
            for n in CHANNELS}


def _invert_props():
    defaults = {"lookX": True}
    return {f"inv_{n}": BoolProperty(name="Invert", default=defaults.get(n, False)) for n in BIPOLAR}


FaceCapSettings = type("FaceCapSettings", (bpy.types.PropertyGroup,), {"__annotations__": {
    "armature": PointerProperty(name="Armature", type=bpy.types.Object,
                                poll=lambda self, o: o.type == 'ARMATURE'),
    "base_action": PointerProperty(name="Base action", type=bpy.types.Action,
                                   description="Body animation each take starts from"),
    "tracker_dir": StringProperty(name="Tracker folder", subtype='DIR_PATH', default=DEFAULT_DIR),
    "camera": IntProperty(name="Camera", default=0, min=0),
    "mic": StringProperty(name="Mic", default="", description="Device index or name; empty = default"),
    "port": IntProperty(name="Data port", default=9870),
    "control_port": IntProperty(name="Control port", default=9871),
    "latency_ms": FloatProperty(name="Video latency (ms)", default=60.0, min=0.0, max=500.0,
                                description="Camera delay; shifts face keys earlier to match the voice"),
    "play_while_recording": BoolProperty(name="Play timeline while recording", default=False),
    "phoneme_pass": BoolProperty(name="Tongue from speech after takes", default=True,
                                 description="After each take, find th/l/t/d/n sounds in the recording "
                                             "(Allosaurus, ~20-60 s in the background) and key tongue-tip peeks"),
    "voice_jaw": FloatProperty(name="Voice → jaw", default=0.35, min=0.0, max=1.0,
                               description="Mic loudness opens the jaw at least this much (helps lip sync)"),
    "take_counter": IntProperty(default=0),
    "show_gains": BoolProperty(name="Gains", default=False),
    **_gain_props(),
    **_invert_props(),
}})


def _settings(context=None):
    return (context or bpy.context).scene.facecap


def _armature(context=None):
    st = _settings(context)
    arm = st.armature or bpy.data.objects.get("Armature")
    return arm if arm and arm.type == 'ARMATURE' else None


def _process(st, raw):
    out = {}
    for n, (bipolar, _) in CHANNELS.items():
        v = float(raw.get(n, 0.0)) * getattr(st, f"gain_{n}")
        if bipolar:
            if getattr(st, f"inv_{n}"):
                v = -v
            if n not in UNBOUNDED:
                v = max(-1.0, min(1.0, v))
        else:
            v = max(0.0, min(1.0, v))
        out[n] = v
    voice = float(raw.get("voice", 0.0))
    if st.voice_jaw > 0 and voice > 0:
        out["jaw"] = max(out["jaw"], min(1.0, voice * st.voice_jaw))
    return out


def _apply(arm, vals):
    for n, v in vals.items():
        arm["fc_" + n] = v
    arm.update_tag()


def _revalidate_drivers():
    """Drivers flagged invalid once (e.g. evaluated before a prop existed) stay dead; revive them."""
    ids = [bpy.data.objects.get(n) for n in DRIVEN_OBJECTS]
    ids += [o.data.shape_keys for o in bpy.data.objects if o.type == 'MESH' and o.data.shape_keys]
    for idb in ids:
        ad = getattr(idb, "animation_data", None) if idb else None
        if ad:
            for fc in ad.drivers:
                if not fc.driver.is_valid:
                    fc.driver.is_valid = True
            idb.update_tag()


def _redraw():
    wm = bpy.context.window_manager
    for w in wm.windows:
        for a in w.screen.areas:
            if a.type in {'VIEW_3D', 'SEQUENCE_EDITOR', 'DOPESHEET_EDITOR', 'GRAPH_EDITOR'}:
                a.tag_redraw()


# ---------------------------------------------------------------- network

def _send(cmd):
    st = _settings()
    s = S.sock or socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.sendto(json.dumps(cmd).encode(), ("127.0.0.1", st.control_port))
    except OSError as e:
        S.message = f"Send failed: {e}"


def _open_socket(port):
    if S.sock:
        return True
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.bind(("127.0.0.1", port))
    except OSError as e:
        S.message = f"Port {port} busy: {e}"
        return False
    s.setblocking(False)
    S.sock = s
    return True


def _control_port_free(port):
    """False if something (a running tracker) already listens on the control port."""
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.bind(("127.0.0.1", port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def _tracker_died_during_take(scene):
    """Watchdog: keep what was recorded, then bring the tracker back."""
    S.recording = False
    S.waiting_stop = False
    has_audio = bool(S.rec_path) and os.path.exists(S.rec_path)
    _finalize_take(scene, None, 0.0, has_audio)
    S.message = "Tracker stopped mid-take - take saved up to that point; relaunching tracker"
    S.proc = None

    def relaunch():
        try:
            bpy.ops.facecap.launch_tracker()
        except Exception as e:
            S.message = f"Relaunch failed: {e}"
        return None

    bpy.app.timers.register(relaunch, first_interval=1.0)


def _close_socket():
    if S.sock:
        S.sock.close()
        S.sock = None


def _tick():
    if not S.live or not S.sock:
        return None
    try:
        return _tick_inner()
    except Exception as e:  # never let one bad packet/bake kill the timer
        import traceback
        traceback.print_exc()
        S.message = f"Error: {e}"
        return 1.0 / 60.0


def _tick_inner():
    try:
        scene = bpy.context.scene
        st = scene.facecap
    except AttributeError:
        return 0.1
    if (S.recording or S.waiting_stop) and S.proc is not None and S.proc.poll() is not None:
        _tracker_died_during_take(scene)
        return None if not S.live else 1.0 / 60.0
    n_face = 0
    while S.sock:  # socket closes when a take finishes (live pauses)
        try:
            data, _ = S.sock.recvfrom(65536)
        except (BlockingIOError, ConnectionResetError):
            break
        except OSError:
            break
        try:
            m = json.loads(data.decode())
        except ValueError:
            continue
        kind = m.get("type")
        if kind == "face":
            n_face += 1
            S.last_packet = time.time()
            S.face_ok = m.get("ok", False)
            if S.face_ok:
                S.latest = m["v"]
            else:
                # No face: hold the last face values, but button/audio-driven channels
                # (tongue, spin, voice) still come through.
                held = dict(S.latest) if S.latest else {n: 0.0 for n in CHANNELS}
                for n in ("tongueOut", "tongueX", "tongueY", "tongueTip", "spin", "voice"):
                    if n in m["v"]:
                        held[n] = m["v"][n]
                S.latest = held
            if S.recording and m.get("rec"):
                S.samples.append((m["t"], _process(st, S.latest)))
        elif kind == "rec_started":
            S.message = "Recording" + ("" if m.get("audio") else " (no mic!)")
        elif kind == "rec_stopped":
            if S.waiting_stop:
                S.waiting_stop = False
                _finalize_take(scene, m.get("t0"), m.get("duration", 0.0), m.get("audio", False))
        elif kind == "pong":
            S.message = "Tracker connected" + ("" if m.get("calibrated") else " - not calibrated yet")
        elif kind == "status":
            if m.get("msg") == "calibrated":
                S.message = "Calibrated"
            elif m.get("msg") == "tongue_calibrated":
                S.message = "Tongue calibrated"
            elif m.get("msg") == "tongue_failed":
                S.message = "Tongue not found - see debug_tongue.png"
            elif m.get("msg") in ("tongue_on", "tongue_off"):
                S.tongue_on = m["msg"] == "tongue_on"
            elif m.get("msg") == "tracker_quit":
                S.message = "Tracker closed"
    now = time.time()
    S.pkt_times = [t for t in S.pkt_times if now - t < 1.0] + [now] * n_face
    S.fps_est = len(S.pkt_times)
    if S.waiting_stop and time.time() - S.rec_sent_at > 3.0:
        # Tracker never answered: bake the face keys without audio.
        S.waiting_stop = False
        _finalize_take(scene, None, 0.0, False)
    # Drive the rig only with fresh data and never over timeline playback,
    # otherwise the last live pose would mask the recorded take.
    playing = any(w.screen.is_animation_playing for w in bpy.context.window_manager.windows)
    if S.latest is not None and n_face and not S.waiting_stop and (S.recording or not playing):
        arm = _armature()
        if arm:
            _apply(arm, _process(st, S.latest))
    _redraw()
    return 1.0 / 60.0


# ---------------------------------------------------------------- takes

def _fcurves(action):
    """All F-Curves of an action (Blender 4.4+ layered actions, or legacy)."""
    if hasattr(action, "layers") and action.layers:
        return [(cb, fc) for l in action.layers for s in l.strips for cb in s.channelbags for fc in cb.fcurves]
    return [(action, fc) for fc in getattr(action, "fcurves", [])]


def _is_face_path(path):
    return path.startswith('["fc_') or any(path.startswith(f'pose.bones["{b}"]') for b in FACE_BONES)


def _has_face_curves(action):
    return any(fc.data_path.startswith('["fc_') for _, fc in _fcurves(action))


def _use_base_action(arm, st):
    ad = arm.animation_data or arm.animation_data_create()
    if st.base_action is None and ad.action and not _has_face_curves(ad.action):
        st.base_action = ad.action
    if st.base_action and ad.action != st.base_action:
        ad.action = st.base_action
        if hasattr(ad, "action_slot") and st.base_action.slots:
            ad.action_slot = st.base_action.slots[0]


def _finalize_take(scene, t0, duration, has_audio):
    st = scene.facecap
    arm = _armature()
    samples = sorted(S.samples)
    S.samples = []
    if not arm or len(samples) < 2:
        S.message = "Take discarded: no face data"
        return
    fps = scene.render.fps / scene.render.fps_base
    lat = st.latency_ms / 1000.0
    ts = [t - lat for t, _ in samples]
    if t0 is None:
        t0 = ts[0]
    end_t = t0 + duration if has_audio and duration > 0 else ts[-1]
    start = S.rec_start_frame
    n_frames = max(1, int(math.ceil((end_t - t0) * fps)) + 1)

    # Linear resample onto whole frames.
    frames = {n: [] for n in CHANNELS}
    j = 0
    for i in range(n_frames):
        tau = t0 + i / fps
        while j < len(ts) - 2 and ts[j + 1] < tau:
            j += 1
        ta, tb = ts[j], ts[j + 1]
        a = 0.0 if tb <= ta else min(1.0, max(0.0, (tau - ta) / (tb - ta)))
        va, vb = samples[j][1], samples[j + 1][1]
        for n in CHANNELS:
            frames[n].append(va[n] + (vb[n] - va[n]) * a)

    st.take_counter += 1
    name = f"FaceTake_{st.take_counter:03d}"
    base = st.base_action
    act = base.copy() if base else bpy.data.actions.new(name)
    act.name = name
    act.use_fake_user = True
    for owner, fc in _fcurves(act):
        if _is_face_path(fc.data_path):
            owner.fcurves.remove(fc)
    ad = arm.animation_data or arm.animation_data_create()
    ad.action = act
    if hasattr(ad, "action_slot"):
        if not act.slots:
            ad.action_slot = act.slots.new(id_type='OBJECT', name=arm.name)
        else:
            ad.action_slot = act.slots[0]
    for n in CHANNELS:
        path = f'["fc_{n}"]'
        if hasattr(act, "fcurve_ensure_for_datablock"):
            fc = act.fcurve_ensure_for_datablock(arm, path)
        else:
            fc = act.fcurves.new(path)
        fc.group = None
        kp = fc.keyframe_points
        kp.add(n_frames)
        co = []
        for i, v in enumerate(frames[n]):
            co += [start + i, v]
        kp.foreach_set("co", co)
        kp.foreach_set("interpolation", [bpy.types.Keyframe.bl_rna.properties["interpolation"]
                                         .enum_items["LINEAR"].value] * n_frames)
        fc.update()

    last = start + n_frames - 1
    if scene.frame_end < last:
        scene.frame_end = last

    if has_audio and S.rec_path and os.path.exists(S.rec_path):
        se = scene.sequence_editor or scene.sequence_editor_create()
        strips = se.strips if hasattr(se, "strips") else se.sequences  # 4.4+ renamed
        for s in strips:
            if s.name.startswith("FaceTake_"):
                s.mute = True
        used = {s.channel for s in strips}
        ch = next(c for c in range(1, 129) if c not in used)
        snd = strips.new_sound(name, bpy.path.relpath(S.rec_path) if bpy.data.filepath else S.rec_path, ch, start)
        snd.show_waveform = True
        try:
            scene.sync_mode = 'AUDIO_SYNC'
        except TypeError:
            pass
    S.last_take = name
    S.message = f"{name}: frames {start}-{last}" + ("" if has_audio else " (no audio)") + \
        " - live paused for playback (Record/Live resumes)"
    # Pause live so playback shows the take, not the camera.
    S.live = False
    _close_socket()
    scene.frame_set(start)
    S.last_take_info = (name, S.rec_path, start)
    if has_audio and st.phoneme_pass:
        _start_phoneme_pass(name, S.rec_path, start)


# ---------------------------------------------------------------- speech -> tongue tip

# Phones that show the tongue tip, and how much (IPA from Allosaurus)
TIP_PHONES = {"θ": 1.0, "ð": 1.0, "l": 0.8, "t": 0.45, "d": 0.45, "n": 0.45}


def _start_phoneme_pass(action_name, wav, start_frame):
    st = _settings()
    d = bpy.path.abspath(st.tracker_dir)
    py = os.path.join(d, ".venv", "Scripts", "python.exe")
    script = os.path.join(d, "phonemes.py")
    if not (os.path.exists(py) and os.path.exists(script) and os.path.exists(wav)):
        S.message = "Phoneme pass skipped (phonemes.py / wav missing)"
        return
    out = os.path.splitext(wav)[0] + "_phones.json"
    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    proc = subprocess.Popen([py, "-W", "ignore", script, wav, out], cwd=d, creationflags=flags,
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    S.message = f"{action_name}: finding tongue sounds in the voice..."

    def poll():
        if proc.poll() is None:
            return 1.0
        try:
            n = _apply_phonemes(action_name, out, start_frame)
            S.message = f"{action_name}: tongue keyed on {n} speech sounds"
        except Exception as e:
            S.message = f"Phoneme pass failed: {e}"
        _redraw()
        return None

    bpy.app.timers.register(poll, first_interval=2.0)


def _apply_phonemes(action_name, json_path, start_frame):
    """Merge tongue-tip peeks from timed phones into the take's fc_tongueTip curve (max)."""
    act = bpy.data.actions.get(action_name)
    if not act:
        raise RuntimeError("take action not found")
    with open(json_path, encoding="utf-8") as f:
        phones = json.load(f)
    fc = next((fc for _, fc in _fcurves(act) if fc.data_path == '["fc_tongueTip"]'), None)
    if fc is None or not len(fc.keyframe_points):
        raise RuntimeError("take has no tongueTip curve")
    scene = bpy.context.scene
    fps = scene.render.fps / scene.render.fps_base
    kp = fc.keyframe_points
    co = [0.0] * (2 * len(kp))
    kp.foreach_get("co", co)
    frames, vals = co[0::2], co[1::2]
    n = 0
    for t0, dur, ph in phones:
        w = TIP_PHONES.get(ph)
        if not w:
            continue
        n += 1
        a, b = t0 - 0.06, t0 + max(dur, 0.06) + 0.10   # ease in 60 ms before, out 100 ms after
        for i, fr in enumerate(frames):
            t = (fr - start_frame) / fps
            if a <= t <= b:
                if t < t0:
                    e = (t - a) / 0.06
                elif t > t0 + max(dur, 0.06):
                    e = 1 - (t - t0 - max(dur, 0.06)) / 0.10
                else:
                    e = 1.0
                e = e * e * (3 - 2 * e)
                vals[i] = max(vals[i], w * e)
    co[1::2] = vals
    kp.foreach_set("co", co)
    fc.update()
    return n


class FACECAP_OT_phoneme_pass(bpy.types.Operator):
    bl_idname = "facecap.phoneme_pass"
    bl_label = "Tongue From Speech (last take)"
    bl_description = "Re-run the th/l/t/d/n speech analysis on the last take's recording"

    def execute(self, context):
        info = getattr(S, "last_take_info", None)
        if not info:
            self.report({'ERROR'}, "No take with audio yet")
            return {'CANCELLED'}
        _start_phoneme_pass(*info)
        return {'FINISHED'}


# ---------------------------------------------------------------- operators

class FACECAP_OT_launch_tracker(bpy.types.Operator):
    bl_idname = "facecap.launch_tracker"
    bl_label = "Launch Tracker"
    bl_description = "Start tracker.py (webcam + mic) in its own console window"

    def execute(self, context):
        st = _settings(context)
        d = bpy.path.abspath(st.tracker_dir)
        py = os.path.join(d, ".venv", "Scripts", "python.exe")
        if not os.path.exists(py):
            py = os.path.join(d, ".venv", "bin", "python")
        script = os.path.join(d, "tracker.py")
        if not (os.path.exists(py) and os.path.exists(script)):
            self.report({'ERROR'}, f"tracker.py / .venv not found in {d}")
            return {'CANCELLED'}
        if (S.proc and S.proc.poll() is None) or not _control_port_free(st.control_port):
            self.report({'INFO'}, "Tracker already running")
            if not S.live:
                bpy.ops.facecap.live_toggle()
            return {'FINISHED'}
        cmd = [py, script, "--camera", str(st.camera), "--port", str(st.port),
               "--control-port", str(st.control_port)]
        if st.mic.strip():
            cmd += ["--mic", st.mic.strip()]
        # Log to tracker.log (no console window) so errors can be read afterwards.
        flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
        # Append (don't overwrite): a crash trace must survive the relaunch.
        log_path = os.path.join(d, "tracker.log")
        if os.path.exists(log_path) and os.path.getsize(log_path) > 5_000_000:
            os.replace(log_path, log_path + ".old")
        log = open(log_path, "a", encoding="utf-8")
        env = dict(os.environ, PYTHONUNBUFFERED="1")
        S.proc = subprocess.Popen(cmd, cwd=d, creationflags=flags, stdout=log, stderr=subprocess.STDOUT, env=env)
        if not S.live:
            bpy.ops.facecap.live_toggle()
        S.message = "Tracker starting (takes a few seconds)..."
        bpy.app.timers.register(lambda: (_send({"cmd": "ping"}), None)[1], first_interval=4.0)
        return {'FINISHED'}


class FACECAP_OT_stop_tracker(bpy.types.Operator):
    bl_idname = "facecap.stop_tracker"
    bl_label = "Stop Tracker"

    def execute(self, context):
        _send({"cmd": "quit"})
        if S.proc:
            try:
                S.proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                S.proc.terminate()
            S.proc = None
        return {'FINISHED'}


class FACECAP_OT_live_toggle(bpy.types.Operator):
    bl_idname = "facecap.live_toggle"
    bl_label = "Live"
    bl_description = "Receive tracker data and drive the rig live"

    def execute(self, context):
        st = _settings(context)
        if S.live:
            if S.recording:
                bpy.ops.facecap.record_stop()
            S.live = False
            _close_socket()
            S.message = "Live off"
            return {'FINISHED'}
        if not _open_socket(st.port):
            self.report({'ERROR'}, S.message)
            return {'CANCELLED'}
        arm = _armature(context)
        if not arm:
            self.report({'ERROR'}, "Pick the armature")
            return {'CANCELLED'}
        _use_base_action(arm, st)  # a take's fc_ keys would fight the live values
        _revalidate_drivers()
        S.live = True
        S.message = "Live - waiting for tracker"
        bpy.app.timers.register(_tick, first_interval=0.05)
        _send({"cmd": "ping"})
        return {'FINISHED'}


class FACECAP_OT_calibrate(bpy.types.Operator):
    bl_idname = "facecap.calibrate"
    bl_label = "Calibrate Neutral"
    bl_description = "Hold a relaxed, neutral face looking at the camera for ~1.5 s"

    def execute(self, context):
        _send({"cmd": "calibrate"})
        S.message = "Calibrating - hold a neutral face"
        return {'FINISHED'}


class FACECAP_OT_calibrate_tongue(bpy.types.Operator):
    bl_idname = "facecap.calibrate_tongue"
    bl_label = "Calibrate Tongue"
    bl_description = "Stick your tongue straight out and hold it for ~1 s (do Calibrate Neutral first)"

    def execute(self, context):
        _send({"cmd": "calibrate_tongue"})
        S.message = "Tongue calib - stick it out and hold"
        return {'FINISHED'}


class FACECAP_OT_tongue_toggle(bpy.types.Operator):
    bl_idname = "facecap.tongue_toggle"
    bl_label = "Tongue Out"
    bl_description = "Toggle the tongue out/in (or hold Right Ctrl / mouse back button)"

    def execute(self, context):
        _send({"cmd": "tongue_toggle"})
        return {'FINISHED'}


class FACECAP_OT_spin_reset(bpy.types.Operator):
    bl_idname = "facecap.spin_reset"
    bl_label = "Reset Spin"
    bl_description = "Turn Humobox back to its starting side"

    def execute(self, context):
        _send({"cmd": "spin_reset"})
        arm = _armature(context)
        if arm and "fc_spin" in arm:
            arm["fc_spin"] = 0.0
            arm.update_tag()
        return {'FINISHED'}


class FACECAP_OT_snapshot(bpy.types.Operator):
    bl_idname = "facecap.snapshot"
    bl_label = "Tongue Debug Snapshot"
    bl_description = "Save debug_tongue.png in the tracker folder showing what the tongue detector sees"

    def execute(self, context):
        _send({"cmd": "snapshot"})
        S.message = "Saved debug_tongue.png"
        return {'FINISHED'}


class FACECAP_OT_record_start(bpy.types.Operator):
    bl_idname = "facecap.record_start"
    bl_label = "Record"
    bl_description = "Record face + voice from the current frame"

    def execute(self, context):
        st = _settings(context)
        if not S.live:
            bpy.ops.facecap.live_toggle()
            if not S.live:
                return {'CANCELLED'}
        if time.time() - S.last_packet > 1.0:
            self.report({'ERROR'}, "No data from tracker - launch it first")
            return {'CANCELLED'}
        arm = _armature(context)
        _use_base_action(arm, st)
        blend = os.path.splitext(os.path.basename(bpy.data.filepath))[0] or "untitled"
        folder = bpy.path.abspath("//FaceCap_audio") if bpy.data.filepath else os.path.join(
            bpy.path.abspath(st.tracker_dir), "audio")
        S.rec_path = os.path.join(folder, f"{blend}_FaceTake_{st.take_counter + 1:03d}.wav")
        S.rec_start_frame = context.scene.frame_current
        S.samples = []
        S.recording = True
        _send({"cmd": "rec_start", "path": S.rec_path})
        if st.play_while_recording and not context.screen.is_animation_playing:
            bpy.ops.screen.animation_play()
        S.message = "Recording..."
        return {'FINISHED'}


class FACECAP_OT_record_stop(bpy.types.Operator):
    bl_idname = "facecap.record_stop"
    bl_label = "Stop"

    def execute(self, context):
        if not S.recording:
            return {'CANCELLED'}
        S.recording = False
        S.waiting_stop = True
        S.rec_sent_at = time.time()
        _send({"cmd": "rec_stop", "path": S.rec_path})
        if context.screen.is_animation_playing:
            bpy.ops.screen.animation_cancel(restore_frame=False)
        S.message = "Baking take..."
        return {'FINISHED'}


class FACECAP_OT_reset(bpy.types.Operator):
    bl_idname = "facecap.reset"
    bl_label = "Neutral Pose"
    bl_description = "Zero all face controls and go back to the base action"

    def execute(self, context):
        arm = _armature(context)
        if arm:
            _use_base_action(arm, _settings(context))
            _apply(arm, {n: 0.0 for n in CHANNELS})
        S.latest = None
        return {'FINISHED'}


# ---------------------------------------------------------------- UI

class FACECAP_PT_panel(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "FaceCap"
    bl_label = "Humobox FaceCap"

    def draw(self, context):
        st = _settings(context)
        arm = _armature(context)
        L = self.layout
        col = L.column(align=True)
        col.prop(st, "armature")
        col.prop(st, "base_action")

        box = L.box()
        running = S.proc is not None and S.proc.poll() is None
        row = box.row(align=True)
        row.operator("facecap.launch_tracker", icon='OUTLINER_OB_CAMERA', depress=running)
        row.operator("facecap.stop_tracker", text="", icon='X')
        row = box.row(align=True)
        row.operator("facecap.live_toggle", text="Live ON" if S.live else "Live OFF",
                     icon='REC' if S.live else 'PLAY', depress=S.live)
        row.operator("facecap.calibrate", icon='USER')
        row = box.row(align=True)
        row.operator("facecap.tongue_toggle", text="Tongue Out" if not S.tongue_on else "Tongue In",
                     icon='MOD_SMOOTH', depress=S.tongue_on)
        row.operator("facecap.spin_reset", icon='FILE_REFRESH')
        box.label(text="Hold R-Ctrl / mouse back button: tongue", icon='MOUSE_MOVE')
        age = time.time() - S.last_packet
        status = ("receiving %.0f fps" % S.fps_est if age < 1 else "no data") if S.live else "idle"
        if S.live and age < 1 and not S.face_ok:
            status += " - NO FACE"
        box.label(text=f"Tracker: {status}")
        if S.message:
            box.label(text=S.message, icon='INFO')

        box = L.box()
        row = box.row(align=True)
        row.scale_y = 1.6
        if S.recording:
            row.alert = True
            row.operator("facecap.record_stop", icon='SNAP_FACE', text="Stop (%.1fs)" % (
                (S.samples[-1][0] - S.samples[0][0]) if len(S.samples) > 1 else 0.0))
        else:
            row.operator("facecap.record_start", icon='REC')
        box.prop(st, "latency_ms")
        box.prop(st, "voice_jaw", slider=True)
        row = box.row(align=True)
        row.prop(st, "phoneme_pass")
        row.operator("facecap.phoneme_pass", text="", icon='SPEAKER')
        box.prop(st, "play_while_recording")
        if S.last_take:
            box.label(text=f"Last take: {S.last_take}", icon='ACTION')
        box.operator("facecap.reset", icon='LOOP_BACK')

        if arm:
            box = L.box()
            box.prop(st, "show_gains", icon='TRIA_DOWN' if st.show_gains else 'TRIA_RIGHT', emboss=False,
                     text="Channels (live value / gain)")
            if st.show_gains:
                for n, (bipolar, label) in CHANNELS.items():
                    row = box.row(align=True)
                    split = row.split(factor=0.55, align=True)
                    if "fc_" + n in arm:
                        split.prop(arm, f'["fc_{n}"]', text=label, slider=True)
                    else:
                        split.label(text=f"{label} (missing)")
                    split.prop(st, f"gain_{n}", text="")
                    if bipolar:
                        row.prop(st, f"inv_{n}", text="", icon='ARROW_LEFTRIGHT')
            box = L.box()
            col = box.column(align=True)
            col.prop(st, "tracker_dir", text="")
            row = col.row(align=True)
            row.prop(st, "camera")
            row.prop(st, "mic")
            row = col.row(align=True)
            row.prop(st, "port")
            row.prop(st, "control_port")


CLASSES = (FaceCapSettings, FACECAP_OT_launch_tracker, FACECAP_OT_stop_tracker, FACECAP_OT_live_toggle,
           FACECAP_OT_calibrate, FACECAP_OT_calibrate_tongue, FACECAP_OT_tongue_toggle,
           FACECAP_OT_spin_reset, FACECAP_OT_snapshot, FACECAP_OT_phoneme_pass,
           FACECAP_OT_record_start, FACECAP_OT_record_stop, FACECAP_OT_reset,
           FACECAP_PT_panel)


def register():
    for c in CLASSES:
        bpy.utils.register_class(c)
    bpy.types.Scene.facecap = PointerProperty(type=FaceCapSettings)


def unregister():
    S.live = False
    _close_socket()
    if bpy.app.timers.is_registered(_tick):
        bpy.app.timers.unregister(_tick)
    del bpy.types.Scene.facecap
    for c in reversed(CLASSES):
        bpy.utils.unregister_class(c)


if __name__ == "__main__" or __name__.endswith(".py"):
    # Run from the Text Editor, or auto-run as a registered text block in the .blend.
    try:
        unregister()
    except Exception:
        pass
    register()
