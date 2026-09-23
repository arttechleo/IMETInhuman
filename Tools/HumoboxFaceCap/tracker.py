"""Humobox FaceCap tracker.

Webcam -> MediaPipe Face Landmarker -> Humobox control channels -> UDP to Blender.
Also records the microphone to WAV when Blender asks, timestamped on the same
clock as the face packets so Blender can line the keyframes up with the audio.

Run:   .venv\\Scripts\\python.exe tracker.py            (defaults: camera 0, default mic)
       .venv\\Scripts\\python.exe tracker.py --list-devices
Keys in the preview window:  C = calibrate neutral face,  T = toggle tongue out,  Shift+Q = quit.
Tongue: hold Right Ctrl or the mouse back/thumb button (works whichever window has focus);
        change with --tongue-keys, or use --tongue-detect for the experimental camera detector.
"""

import argparse
import json
import math
import os
import queue
import socket
import sys
import threading
import time

import cv2
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
CALIB_PATH = os.path.join(HERE, "calib.json")
COLORS_PATH = os.path.join(HERE, "colors.json")
MODEL_PATH = os.path.join(HERE, "face_landmarker.task")

# Output channels, in the order they are drawn in the preview.
# (name, bipolar, one-euro min_cutoff)
CHANNELS = [
    ("jaw", False, 3.0),
    ("smile", False, 1.5),
    ("pucker", False, 2.5),
    ("funnel", False, 2.5),
    ("stretch", False, 2.5),
    ("upperUp", False, 2.5),
    ("lowerDown", False, 2.5),
    ("press", False, 3.0),
    ("frown", False, 1.5),
    ("mouthShift", True, 1.2),
    ("mouthTilt", True, 1.2),
    ("blink", False, 5.0),
    ("wide", False, 2.0),
    ("squint", False, 2.0),
    ("lookX", True, 2.0),
    ("lookY", True, 2.0),
    ("sniff", False, 2.0),
    ("noseFlare", False, 2.0),
    ("noseTwitch", True, 1.5),
    ("ear", False, 1.5),
    ("earLift", True, 1.5),
    # From tongue.py (colour-based; MediaPipe has no tongue blendshape).
    ("tongueOut", False, 2.0),
    ("tongueX", True, 1.2),
    ("tongueY", True, 1.2),
    # Head pose (from the facial transformation matrix).
    ("headYaw", True, 1.5),
    ("headRoll", True, 1.5),
]

# Fast head turn -> Humobox spins a quarter turn.
SPIN_SPEED_DEG = 170.0   # yaw speed (deg/s) that counts as a "flick"
SPIN_MIN_YAW_DEG = 12.0  # ...and the head must actually be turned this far
SPIN_COOLDOWN = 0.9      # seconds between spins
SPIN_EASE = 7.0          # higher = faster spin animation


def head_angles(m):
    """Yaw and roll in degrees from MediaPipe's 4x4 facial transformation matrix."""
    r = np.asarray(m)[:3, :3]
    f = r @ np.array([0.0, 0.0, 1.0])
    right = r @ np.array([1.0, 0.0, 0.0])
    yaw = math.degrees(math.atan2(f[0], f[2]))
    roll = math.degrees(math.atan2(right[1], right[0]))
    pitch = math.degrees(math.atan2(f[1], math.hypot(f[0], f[2])))
    return yaw, roll, pitch


TONGUE_OUT_S = 0.45  # seconds to fully stick out
TONGUE_IN_S = 0.35   # seconds to fully retract

# Windows virtual-key codes for --tongue-keys
VK_NAMES = {"RCONTROL": 0xA3, "LCONTROL": 0xA2, "RSHIFT": 0xA1, "LSHIFT": 0xA0, "RMENU": 0xA5,
            "XBUTTON1": 0x05, "XBUTTON2": 0x06, "MBUTTON": 0x04, "SPACE": 0x20,
            "F8": 0x77, "F9": 0x78, "F10": 0x79, "PAUSE": 0x13, "SCROLL": 0x91}


def key_down(vk):
    """Global (focus-independent) key / mouse button state."""
    if sys.platform != "win32" or not vk:
        return False
    import ctypes
    return bool(ctypes.windll.user32.GetAsyncKeyState(vk) & 0x8000)


def smoothstep(a, b, x):
    t = min(max((x - a) / (b - a), 0.0), 1.0)
    return t * t * (3 - 2 * t)


def derive(r):
    """Map neutral-subtracted MediaPipe blendshapes (r[name] >= 0) to Humobox channels.

    Values are roughly 0..1 (or -1..1 for bipolar ones) but not clipped here;
    Blender applies per-channel gain and clips.
    Lateral channels use the subject's own left/right; Blender has invert toggles.
    """
    avg = lambda a, b: 0.5 * (r[a] + r[b])
    brow_up = max(r["browInnerUp"], avg("browOuterUpLeft", "browOuterUpRight"))
    return {
        "jaw": r["jawOpen"] / 0.45,
        "smile": avg("mouthSmileLeft", "mouthSmileRight") / 0.6,
        "pucker": r["mouthPucker"] / 0.6,
        "funnel": r["mouthFunnel"] / 0.45,
        "stretch": avg("mouthStretchLeft", "mouthStretchRight") / 0.35,
        "upperUp": avg("mouthUpperUpLeft", "mouthUpperUpRight") / 0.4,
        "lowerDown": avg("mouthLowerDownLeft", "mouthLowerDownRight") / 0.4,
        "press": max(avg("mouthPressLeft", "mouthPressRight"),
                     avg("mouthRollUpper", "mouthRollLower"), r["mouthClose"]) / 0.35,
        "frown": avg("mouthFrownLeft", "mouthFrownRight") / 0.35,
        "mouthShift": (r["mouthRight"] - r["mouthLeft"]) / 0.5,
        "mouthTilt": (r["mouthSmileRight"] - r["mouthSmileLeft"]) / 0.4,
        # Dead zone + S-curve: relaxed eyes stay fully open, blinks close fully.
        "blink": smoothstep(0.2, 0.85, avg("eyeBlinkLeft", "eyeBlinkRight") / 0.6),
        "wide": avg("eyeWideLeft", "eyeWideRight") / 0.4,
        "squint": avg("eyeSquintLeft", "eyeSquintRight") / 0.5,
        "lookX": ((r["eyeLookOutRight"] + r["eyeLookInLeft"])
                  - (r["eyeLookOutLeft"] + r["eyeLookInRight"])) / 2 / 0.5,
        "lookY": ((r["eyeLookUpLeft"] + r["eyeLookUpRight"])
                  - (r["eyeLookDownLeft"] + r["eyeLookDownRight"])) / 2 / 0.4,
        # sniff / noseFlare / noseTwitch come from nose_metrics() (landmarks), not the
        # blendshapes: MediaPipe's noseSneer mostly follows the upper lip.
        # Brows drive the ear: raise = flap + lift, lower = ear drops.
        "ear": brow_up / 0.5,
        "earLift": (brow_up - avg("browDownLeft", "browDownRight")) / 0.4,
    }


def nose_metrics(p):
    """Raw nose geometry from Face Mesh landmarks (pixel coords), scale-free.

    length: nasion->subnasale over forehead->nasion (both foreshorten alike with pitch)
    width:  alar width over inner-eye-corner distance (both foreshorten alike with yaw)
    tipx:   sideways offset of the nose tip from the nasion, along the eye axis
    """
    d = lambda a, b: float(np.hypot(*(p[a] - p[b])))
    eye = p[362] - p[133]
    s = float(np.hypot(*eye)) or 1.0
    ex = eye / s
    return {
        "length": d(168, 2) / max(d(10, 168), 1.0),
        # Several alar landmark pairs; the widest-moving one wins in nose_channels().
        "w1": d(64, 294) / s, "w2": d(98, 327) / s, "w3": d(129, 358) / s, "w4": d(48, 278) / s,
        "tipx": float(np.dot(p[1] - p[168], ex)) / s,
    }


def nostril_darkness(frame, p):
    """Visible nostril area (dark pixels under the nose tip) over nose-width^2.

    A webcam below eye level sees into the nostrils; they open up when inhaling.
    """
    xs = [p[i][0] for i in (64, 294, 98, 327)]
    x0, x1 = int(min(xs)), int(max(xs))
    y0, y1 = int(p[1][1]), int(p[2][1] + 0.25 * (p[2][1] - p[1][1]) + 2)
    h, w = frame.shape[:2]
    x0, x1, y0, y1 = max(0, x0), min(w, x1), max(0, y0), min(h, y1)
    if x1 - x0 < 4 or y1 - y0 < 3:
        return 0.0
    L = cv2.cvtColor(frame[y0:y1, x0:x1], cv2.COLOR_BGR2LAB)[..., 0].astype(np.float32)
    thr = 0.55 * float(np.percentile(L, 75))  # relative to the lit nose skin around it
    return float((L < thr).sum()) / float((x1 - x0) ** 2)


class Drift:
    """Slowly adapting baseline: posture / head-pitch drift is removed, quick events pass.

    For one-sided events (value rises during the event) the baseline follows drops
    quickly and rises only slowly, so a held scrunch isn't absorbed immediately.
    """

    def __init__(self, tau_up=6.0, tau_down=0.8, bipolar=False):
        self.tau_up, self.tau_down, self.bipolar = tau_up, tau_down, bipolar
        self.b = None
        self.t = None

    def __call__(self, x, t):
        if self.b is None:
            self.b, self.t = x, t
            return 0.0
        dt = max(1e-3, t - self.t)
        self.t = t
        tau = self.tau_up if (x > self.b or self.bipolar) else self.tau_down
        self.b += (x - self.b) * (1 - math.exp(-dt / tau))
        return x - self.b


def nose_channels(m, n0, yaw_rel_deg, dark=None, pitch_rel_deg=0.0, drift=None, t=0.0):
    """Nose channels relative to the neutral metrics n0 (from calibration).

    drift: dict of Drift baselines (see main) that remove slow posture changes.
    """
    if not n0:
        return {"sniff": 0.0, "noseFlare": 0.0, "noseTwitch": 0.0}
    dz = lambda x, z=0.15: max(0.0, (x - z) / (1 - z))  # dead zone against landmark jitter
    hp = (lambda k, x: drift[k](x, t)) if drift else (lambda k, x: x)
    scrunch = hp("scrunch", (n0["length"] - m["length"]) / n0["length"] / 0.05)   # 5% shorter = full
    widths = [(m[k] - n0[k]) / n0[k] for k in ("w1", "w2", "w3", "w4") if k in n0]
    flare = dz(hp("width", (max(widths) if widths else 0.0) / 0.05), 0.2)         # 5% wider = full
    # Nostril opening seen by the camera (only valid near the calibrated head pitch).
    if dark is not None and n0.get("dark", 0) > 0 and abs(pitch_rel_deg) < 8:
        flare = max(flare, dz(hp("dark", (dark - n0["dark"]) / n0["dark"] / 0.8), 0.25))  # +80% = full
    # Turning the head moves the (protruding) tip sideways too: compensate.
    twitch = hp("twitch", (m["tipx"] - n0["tipx"] - 0.3 * math.sin(math.radians(yaw_rel_deg))) / 0.06)
    tw = math.copysign(dz(abs(twitch), 0.25), twitch)
    return {"sniff": dz(scrunch), "noseFlare": flare, "noseTwitch": tw}


def voice_level(rms):
    """Mic RMS -> 0..1 (-50 dBFS silent .. -15 dBFS loud)."""
    db = 20 * math.log10(rms + 1e-7)
    return min(max((db + 50) / 35, 0.0), 1.0)


class OneEuro:
    """One Euro filter: smooth when still, responsive when moving."""

    def __init__(self, min_cutoff=1.5, beta=0.4, d_cutoff=1.0):
        self.min_cutoff, self.beta, self.d_cutoff = min_cutoff, beta, d_cutoff
        self.x = self.dx = self.t = None

    @staticmethod
    def _alpha(cutoff, dt):
        tau = 1.0 / (2 * math.pi * cutoff)
        return 1.0 / (1.0 + tau / dt)

    def __call__(self, x, t):
        if self.t is None:
            self.x, self.dx, self.t = x, 0.0, t
            return x
        dt = max(t - self.t, 1e-4)
        self.t = t
        a_d = self._alpha(self.d_cutoff, dt)
        self.dx = a_d * (x - self.x) / dt + (1 - a_d) * self.dx
        cutoff = self.min_cutoff + self.beta * abs(self.dx)
        a = self._alpha(cutoff, dt)
        self.x = a * x + (1 - a) * self.x
        return self.x


class AudioRecorder:
    """Keeps a mic stream open; writes to WAV only while recording."""

    def __init__(self, device=None, samplerate=None):
        import sounddevice as sd
        import soundfile as sf

        self.sd, self.sf = sd, sf
        info = sd.query_devices(device, "input")
        self.samplerate = int(samplerate or info["default_samplerate"])
        self.device_name = info["name"]
        self.q = queue.Queue()
        self.recording = False
        self.t0 = None  # perf_counter time of the first recorded sample
        self.frames = 0
        self.file = None
        self.lock = threading.Lock()
        self.level = 0.0
        self.floor_db = -60.0
        self.sniff = 0.0
        self.th = 0.0
        self.stream = sd.InputStream(device=device, channels=1, samplerate=self.samplerate,
                                     dtype="float32", callback=self._cb)
        self.stream.start()
        threading.Thread(target=self._writer, daemon=True).start()

    def _cb(self, indata, frames, time_info, status):
        now = time.perf_counter()
        x = indata[:, 0]
        self.level = float(np.sqrt(np.mean(x ** 2)))
        # Sniff detection: nasal inhale = noisy, high-frequency-heavy, above the room floor.
        db = 20 * math.log10(self.level + 1e-7)
        # Noise floor: drops instantly, rises slowly (~10 dB per 10 s).
        self.floor_db = min(db, self.floor_db + 0.01 * frames / 441)
        spec = np.abs(np.fft.rfft(x * np.hanning(len(x)))) ** 2
        freqs = np.fft.rfftfreq(len(x), 1 / self.samplerate)
        tot = spec[freqs > 80].sum() + 1e-12
        hf = spec[freqs > 2000].sum() / tot
        loud = min(max((db - self.floor_db - 8) / 14, 0.0), 1.0)
        self.sniff = loud * min(max((hf - 0.35) / 0.3, 0.0), 1.0)
        # "th" (tongue between the teeth): WEAK, spectrally FLAT, unvoiced frication.
        # /s/ is louder and peaky in 4-8 kHz; vowels have little energy above 2 kHz.
        band = spec[(freqs > 1500) & (freqs < 8000)] + 1e-12
        flat = float(np.exp(np.mean(np.log(band))) / np.mean(band))
        rel = db - self.floor_db
        weak = smoothstep(6, 10, rel) * (1 - smoothstep(22, 30, rel))
        self.th = weak * smoothstep(0.25, 0.45, flat) * smoothstep(0.45, 0.6, hf)
        if self.recording:
            if self.t0 is None:
                # Wall time of this block's first sample (block just finished filling).
                self.t0 = now - frames / self.samplerate
            self.q.put(indata.copy())

    def _writer(self):
        while True:
            block = self.q.get()
            with self.lock:
                if block is None:
                    if self.file:
                        self.file.close()
                        self.file = None
                    continue
                if self.file:
                    self.file.write(block)
                    self.frames += len(block)

    def start(self, path):
        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
        with self.lock:
            self.file = self.sf.SoundFile(path, "w", samplerate=self.samplerate, channels=1,
                                          subtype="PCM_16")
            self.frames = 0
        self.t0 = None
        self.recording = True

    def stop(self):
        self.recording = False
        self.q.put(None)
        # Let the writer drain.
        deadline = time.time() + 2
        while self.file is not None and time.time() < deadline:
            time.sleep(0.01)
        return self.t0, self.frames / self.samplerate


def open_camera(index, width, height):
    # MSMF delivers 30 fps at 720p here; DirectShow only ~10.
    backends = [cv2.CAP_MSMF, cv2.CAP_DSHOW, cv2.CAP_ANY] if sys.platform == "win32" else [cv2.CAP_ANY]
    for be in backends:
        cap = cv2.VideoCapture(index, be)
        if cap.isOpened():
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, width)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
            cap.set(cv2.CAP_PROP_FPS, 30)
            ok, _ = cap.read()
            if ok:
                return cap
        cap.release()
    raise SystemExit(f"Could not open camera {index}")


def draw_preview(img, vals, fps, status, rec, level, face_ok):
    h, w = img.shape[:2]
    panel_w = 230
    canvas = np.zeros((h, w + panel_w, 3), np.uint8)
    canvas[:, :w] = img
    x0 = w + 10
    y = 24
    for name, bipolar, _ in CHANNELS:
        v = vals.get(name, 0.0)
        cv2.putText(canvas, name, (x0, y + 4), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (200, 200, 200), 1)
        bx, bw = x0 + 88, 120
        cv2.rectangle(canvas, (bx, y - 7), (bx + bw, y + 5), (60, 60, 60), -1)
        if bipolar:
            c = bx + bw // 2
            e = int(c + max(-1, min(1, v)) * bw / 2)
            cv2.rectangle(canvas, (min(c, e), y - 7), (max(c, e), y + 5), (80, 200, 255), -1)
        else:
            cv2.rectangle(canvas, (bx, y - 7), (bx + int(max(0, min(1, v)) * bw), y + 5),
                          (80, 220, 120), -1)
        y += 24
    y += 6
    cv2.putText(canvas, f"{fps:4.1f} fps", (x0, y), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (200, 200, 200), 1)
    y += 20
    lv = int(min(1.0, level * 8) * 120)
    cv2.putText(canvas, "mic", (x0, y + 4), cv2.FONT_HERSHEY_SIMPLEX, 0.42, (200, 200, 200), 1)
    cv2.rectangle(canvas, (x0 + 88, y - 7), (x0 + 88 + lv, y + 5), (0, 200, 255), -1)
    y += 24
    if not face_ok:
        cv2.putText(canvas, "NO FACE", (x0, y), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 0, 255), 2)
        y += 24
    if status:
        cv2.putText(canvas, status, (10, h - 12), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 255, 255), 2)
    if rec:
        cv2.circle(canvas, (24, 24), 10, (0, 0, 255), -1)
        cv2.putText(canvas, "REC", (40, 31), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 0, 255), 2)
    cv2.putText(canvas, "C: calibrate  hold R-Ctrl / mouse-back: tongue  T: toggle  Shift+Q: quit", (10, 22 if not rec else 56),
                cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1)
    return canvas


def main():
    import faulthandler
    faulthandler.enable()  # native crashes (camera driver, MediaPipe) still leave a trace in the log
    print(f"==== tracker start {time.ctime()} ====", flush=True)
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--camera", type=int, default=0)
    ap.add_argument("--width", type=int, default=1280)
    ap.add_argument("--height", type=int, default=720)
    ap.add_argument("--mic", default=None, help="input device index or name (default: system default)")
    ap.add_argument("--no-audio", action="store_true")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=9870, help="Blender receiver port (face data out)")
    ap.add_argument("--control-port", type=int, default=9871, help="port this tracker listens on for commands")
    ap.add_argument("--smooth", type=float, default=1.0, help=">1 smoother, <1 snappier")
    ap.add_argument("--no-preview", action="store_true")
    ap.add_argument("--list-devices", action="store_true")
    ap.add_argument("--tongue-keys", default="RCONTROL,XBUTTON1",
                    help="keys/mouse buttons that stick the tongue out while held (names or VK codes)")
    ap.add_argument("--tongue-detect", action="store_true",
                    help="experimental: detect the tongue from the camera instead of a button")
    args = ap.parse_args()

    if args.list_devices:
        import sounddevice as sd
        print(sd.query_devices())
        return

    import mediapipe as mp
    from mediapipe.tasks import python as mp_tasks
    from mediapipe.tasks.python import vision

    if not os.path.exists(MODEL_PATH):
        raise SystemExit(f"Missing model: {MODEL_PATH}")
    landmarker = vision.FaceLandmarker.create_from_options(vision.FaceLandmarkerOptions(
        base_options=mp_tasks.BaseOptions(model_asset_path=MODEL_PATH),
        running_mode=vision.RunningMode.VIDEO,
        num_faces=1,
        output_face_blendshapes=True,
        output_facial_transformation_matrixes=True,
    ))

    mic = args.mic
    if mic is not None and mic.isdigit():
        mic = int(mic)
    audio = None
    if not args.no_audio:
        try:
            audio = AudioRecorder(mic)
            print(f"[audio] {audio.device_name} @ {audio.samplerate} Hz")
        except Exception as e:  # keep tracking even without a mic
            print(f"[audio] disabled: {e}")

    cap = open_camera(args.camera, args.width, args.height)
    print(f"[camera] {int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))}x{int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))}")

    out = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ctl = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    ctl.bind(("127.0.0.1", args.control_port))
    ctl.setblocking(False)
    dest = (args.host, args.port)

    def send(msg):
        try:
            out.sendto(json.dumps(msg).encode(), dest)
        except OSError:
            pass


    sys.path.insert(0, HERE)
    from tongue import TongueDetector
    tongue = TongueDetector(COLORS_PATH)
    tongue_mask, tongue_off = None, (0, 0)
    tongue_on = False
    tongue_latch, tongue_p, tongue_t = False, 0.0, 0.0
    tip_env, th_now = 0.0, 0.0
    nose_drift = {"scrunch": Drift(), "width": Drift(), "dark": Drift(), "twitch": Drift(3.0, 3.0, bipolar=True)}
    tongue_vks = [VK_NAMES.get(k.strip().upper(), 0) or int(k, 0) for k in args.tongue_keys.split(",") if k.strip()]
    print(f"[tongue] {'colour detection' if args.tongue_detect else 'hold ' + args.tongue_keys}")
    tongue_calib_t0 = 0.0
    DEBUG_PNG = os.path.join(HERE, "debug_tongue.png")

    neutral = {}
    if os.path.exists(CALIB_PATH):
        with open(CALIB_PATH) as f:
            neutral = json.load(f)
        print("[calib] loaded neutral face")
    calib_frames = []  # non-empty list while calibrating
    calibrating = False
    filters = {n: OneEuro(min_cutoff=mc / args.smooth, beta=0.5) for n, _, mc in CHANNELS}
    voice_filter = OneEuro(min_cutoff=4.0, beta=1.0)
    vals = {n: 0.0 for n, _, _ in CHANNELS}
    vals["spin"] = 0.0
    status, status_until = "", 0.0
    if not neutral:
        status, status_until = "Look neutral at the camera and press C to calibrate", time.time() + 30
    t_start = time.perf_counter()
    last_ts = -1
    fps, fps_t, fps_n = 0.0, time.time(), 0
    seq = 0

    # Head pose / spin state
    prev_yaw, prev_t = None, None
    yaw_speed = 0.0
    spin_target = spin_now = 0.0
    last_spin = 0.0
    pts = None
    print(f"[net] sending to {dest}, listening for commands on {args.control_port}")

    def start_tongue_calib():
        nonlocal status, status_until, tongue_calib_t0
        tongue.start_tongue_calibration()
        tongue_calib_t0 = time.time()
        status, status_until = "Tongue calib: stick tongue OUT and DOWN over your lower lip, hold", time.time() + 6

    def save_debug(frame, pts, note):
        """Snapshot of what the tongue detector sees, for troubleshooting."""
        cv2.imwrite(os.path.join(HERE, "debug_raw.png"), frame)
        img = frame.copy()
        x0, y0, x1, y1 = tongue._roi(frame, pts)
        tv, m, (ox, oy) = tongue.measure(frame, pts)
        if m is not None:
            sub = img[oy:oy + m.shape[0], ox:ox + m.shape[1]]
            sub[m > 0] = (0.5 * sub[m > 0] + (127, 0, 127)).astype(np.uint8)
        cv2.rectangle(img, (x0, y0), (x1, y1), (0, 255, 255), 1)
        for i in (13, 14, 17, 61, 291):
            cv2.circle(img, tuple(int(v) for v in pts[i]), 3, (0, 255, 0), -1)
        txt = f"{note}  " + "  ".join(f"{k}={v:.2f}" for k, v in tv.items())
        refs = "  ".join(f"{k}={np.round(v).astype(int).tolist()}" for k, v in tongue.refs.items())
        cv2.putText(img, txt, (10, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)
        cv2.putText(img, refs, (10, 60), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 255), 1)
        cv2.imwrite(DEBUG_PNG, img)
        print(f"[debug] {txt} | {refs} -> {DEBUG_PNG}", flush=True)

    try:
        while True:
            ok, frame = cap.read()
            t_frame = time.perf_counter()
            if not ok:
                time.sleep(0.01)
                continue
            frame = cv2.flip(frame, 1)  # mirror view
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            ts = int((t_frame - t_start) * 1000)
            if ts <= last_ts:
                ts = last_ts + 1
            last_ts = ts
            res = landmarker.detect_for_video(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb), ts)
            face_ok = bool(res.face_blendshapes)

            if face_ok:
                try:
                    raw = {c.category_name: c.score for c in res.face_blendshapes[0]}
                    h, w = frame.shape[:2]
                    pts = np.array([(lm.x * w, lm.y * h) for lm in res.face_landmarks[0]], np.float32)
                    yaw, roll, pitch = head_angles(res.facial_transformation_matrixes[0]) \
                        if res.facial_transformation_matrixes else (0.0, 0.0, 0.0)
                    nm = nose_metrics(pts)
                    nm["dark"] = nostril_darkness(frame, pts)
                    if calibrating:
                        calib_frames.append((raw, yaw, roll, nm, pitch))
                        if len(calib_frames) >= 45:
                            neutral = {k: float(np.median([f[0][k] for f in calib_frames])) for k in raw}
                            neutral["_yaw"] = float(np.median([f[1] for f in calib_frames]))
                            neutral["_roll"] = float(np.median([f[2] for f in calib_frames]))
                            neutral["_pitch"] = float(np.median([f[4] for f in calib_frames]))
                            neutral["_nose"] = {k: float(np.median([f[3][k] for f in calib_frames]))
                                                for k in nm}
                            with open(CALIB_PATH, "w") as f:
                                json.dump(neutral, f, indent=1)
                            tongue.calibrate_neutral(frame, pts)
                            calibrating, calib_frames = False, []
                            for flt in filters.values():
                                flt.t = None
                            status, status_until = "Calibrated", time.time() + 2
                            if not tongue.ready:
                                status, status_until = "Calibrated - now press T with tongue out", time.time() + 5
                            send({"type": "status", "msg": "calibrated"})
                    if tongue.calibrating:
                        if tongue.feed_tongue_calibration(frame, pts):
                            status, status_until = "Tongue calibrated", time.time() + 2
                            send({"type": "status", "msg": "tongue_calibrated"})
                            save_debug(frame, pts, "tongue calibrated")
                        elif time.time() - tongue_calib_t0 > 6:
                            tongue.calibrating = False
                            status, status_until = "Tongue calib failed: " + (tongue.last_error or "?"), time.time() + 5
                            send({"type": "status", "msg": "tongue_failed"})
                            save_debug(frame, pts, "tongue calib FAILED")
                    r = {k: max(0.0, v - neutral.get(k, 0.0)) for k, v in raw.items()}
                    d = derive(r)
                    if args.tongue_detect:
                        tv, tongue_mask, tongue_off = tongue.measure(frame, pts)
                        # Hysteresis: the tongue pops out only on a clear detection and stays
                        # out through brief dropouts, instead of flickering.
                        if tongue_on and tv["tongueOut"] < 0.12:
                            tongue_on = False
                        elif not tongue_on and tv["tongueOut"] > 0.3:
                            tongue_on = True
                        if not tongue_on:
                            tv = {k: 0.0 for k in tv}
                    else:
                        tv = {"tongueOut": 0.0, "tongueX": 0.0, "tongueY": 0.0}  # set below
                    d.update(tv)

                    # Head: yaw stretches, roll tilts; a fast yaw flick spins a quarter turn.
                    yaw_rel = yaw - neutral.get("_yaw", 0.0)
                    roll_rel = roll - neutral.get("_roll", 0.0)
                    d["headYaw"] = yaw_rel / 30.0
                    d["headRoll"] = roll_rel / 25.0
                    d.update(nose_channels(nm, neutral.get("_nose"), yaw_rel, nm["dark"],
                                           pitch - neutral.get("_pitch", pitch), nose_drift, t_frame))
                    # Lip gap (inner lips) tells a nasal sniff (lips shut) from "th" (lips apart).
                    lip_gap = float(np.hypot(*(pts[13] - pts[14]))) / max(1.0, float(np.hypot(*(pts[61] - pts[291]))))
                    # Nasal inhale heard on the mic, only while the mouth is closed.
                    if audio and d["jaw"] < 0.15 and lip_gap < 0.03:
                        d["noseFlare"] = max(d["noseFlare"], audio.sniff)
                    # "th" heard, lips slightly apart, lower lip not tucked under the teeth (f/v)
                    th_now = (audio.th if audio and lip_gap >= 0.03 and raw.get("mouthRollLower", 0) < 0.25
                              else 0.0)
                    if prev_yaw is not None:
                        dt = max(t_frame - prev_t, 1e-3)
                        yaw_speed = 0.5 * yaw_speed + 0.5 * (yaw - prev_yaw) / dt
                        if (abs(yaw_speed) > SPIN_SPEED_DEG and abs(yaw_rel) > SPIN_MIN_YAW_DEG
                                and np.sign(yaw_speed) == np.sign(yaw_rel)
                                and time.time() - last_spin > SPIN_COOLDOWN):
                            spin_target += float(np.sign(yaw_speed))
                            last_spin = time.time()
                            print(f"[spin] {'+' if yaw_speed > 0 else '-'}90  (speed {yaw_speed:.0f} deg/s)")
                    prev_yaw, prev_t = yaw, t_frame

                    for n, _, _ in CHANNELS:
                        vals[n] = filters[n](d[n], t_frame)
                except Exception:
                    import traceback
                    traceback.print_exc()

            # Ease the spin angle toward its target (in quarter turns).
            spin_now += (spin_target - spin_now) * (1 - math.exp(-SPIN_EASE / 30.0))
            if abs(spin_target - spin_now) < 1e-3:
                spin_now = spin_target
            vals["spin"] = spin_now
            vals["voice"] = voice_filter(voice_level(audio.level), t_frame) if audio else 0.0

            # Tongue on a button: held key / mouse button (or the Blender toggle) eases it out.
            if not args.tongue_detect:
                held = tongue_latch or any(key_down(vk) for vk in tongue_vks)
                dt_t = min(0.1, t_frame - tongue_t) if tongue_t else 0.0
                tongue_t = t_frame
                rate = 1.0 / (TONGUE_OUT_S if held else TONGUE_IN_S)
                tongue_p = min(1.0, tongue_p + rate * dt_t) if held else max(0.0, tongue_p - rate * dt_t)
                vals["tongueOut"] = smoothstep(0.0, 1.0, tongue_p)
                # Gentle side-to-side sway once fully out (up/down would hit teeth/lips).
                vals["tongueX"] = 0.22 * math.sin(2 * math.pi * 0.4 * t_frame) * smoothstep(0.85, 1.0, tongue_p)
                vals["tongueY"] = 0.0
            # Speech tongue-tip peek: fast attack, slower release envelope.
            k_tip = 0.55 if th_now > tip_env else 0.18
            tip_env += (th_now - tip_env) * k_tip
            vals["tongueTip"] = tip_env if face_ok else 0.0
            seq += 1
            send({"type": "face", "t": t_frame, "seq": seq, "ok": face_ok,
                  "rec": bool(audio and audio.recording), "v": vals})

            # Commands from Blender
            while True:
                try:
                    data, _ = ctl.recvfrom(65536)
                except (BlockingIOError, ConnectionResetError):
                    break
                try:
                    cmd = json.loads(data.decode())
                except ValueError:
                    continue
                c = cmd.get("cmd")
                if c == "ping":
                    send({"type": "pong", "audio": bool(audio), "calibrated": bool(neutral),
                          "tongue": tongue.ready})
                elif c == "calibrate":
                    calibrating, calib_frames = True, []
                    status, status_until = "Calibrating... hold a neutral face", time.time() + 3
                elif c == "calibrate_tongue":
                    start_tongue_calib()
                elif c == "tongue_toggle":
                    tongue_latch = not tongue_latch
                    send({"type": "status", "msg": "tongue_on" if tongue_latch else "tongue_off"})
                elif c == "snapshot" and face_ok and pts is not None:
                    save_debug(frame, pts, "snapshot")
                elif c == "spin_reset":
                    spin_target = spin_now = 0.0
                elif c == "rec_start":
                    path = cmd["path"]
                    if audio:
                        audio.start(path)
                    status, status_until = "", 0
                    send({"type": "rec_started", "path": path, "audio": bool(audio), "t": time.perf_counter()})
                    print(f"[rec] start {path}")
                elif c == "rec_stop":
                    t0, dur = (audio.stop() if audio and audio.recording else (None, 0.0))
                    send({"type": "rec_stopped", "path": cmd.get("path"), "t0": t0, "duration": dur,
                          "audio": bool(audio)})
                    print(f"[rec] stop, {dur:.2f}s audio")
                elif c == "quit":
                    print("[exit] quit command from Blender", flush=True)
                    raise KeyboardInterrupt

            fps_n += 1
            if time.time() - fps_t >= 1.0:
                fps, fps_t, fps_n = fps_n / (time.time() - fps_t), time.time(), 0

            if not args.no_preview:
                if time.time() > status_until:
                    status = ""
                if face_ok and tongue_mask is not None:
                    ox, oy = tongue_off
                    mh, mw_ = tongue_mask.shape
                    sub = frame[oy:oy + mh, ox:ox + mw_]
                    sub[tongue_mask > 0] = (0.5 * sub[tongue_mask > 0] + (127, 0, 127)).astype(np.uint8)
                img = draw_preview(frame, vals, fps, status, bool(audio and audio.recording),
                                   audio.level if audio else 0.0, face_ok)
                cv2.imshow("Humobox FaceCap", img)
                k = cv2.waitKey(1) & 0xFF
                if k == ord("Q"):  # Shift+Q only: a stray q/Esc used to end takes by accident
                    print("[exit] Shift+Q in preview window", flush=True)
                    break
                if k == ord("c"):
                    calibrating, calib_frames = True, []
                    status, status_until = "Calibrating... hold a neutral face", time.time() + 3
                if k == ord("t"):
                    if args.tongue_detect:
                        start_tongue_calib()
                    else:
                        tongue_latch = not tongue_latch
                if k == ord("s") and face_ok and pts is not None:
                    save_debug(frame, pts, "snapshot")
    except KeyboardInterrupt:
        pass
    except Exception:
        import traceback
        print(f"[exit] CRASH {time.ctime()}", flush=True)
        traceback.print_exc()
        sys.stdout.flush()
        raise
    finally:
        print(f"[exit] tracker stopped {time.ctime()}", flush=True)
        if audio and audio.recording:
            audio.stop()
        send({"type": "status", "msg": "tracker_quit"})
        cap.release()
        cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
