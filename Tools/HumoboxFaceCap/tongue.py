"""Tongue detection for the Humobox tracker.

MediaPipe's face blendshapes have no tongue channel, and tongue and lips share
the same colour, so colour alone can't tell them apart. Two cues that can:

  1. Geometry: Face Mesh tracks the lip outline. Anything lip/tongue-coloured
     OUTSIDE the outer lip contour (below or above it) and connected to the lips
     can only be the tongue sticking out.
  2. Shine: a wet tongue throws small specular highlights (bright, colourless
     spots brighter than their surroundings). Highlights outside the mouth
     opening, above the lips' own neutral shine, mean tongue.

C (neutral, mouth closed) samples lip colour, skin colour (left/right cheek and
chin separately, for side lighting) and the resting levels of both cues.
T (tongue out and down over the lower lip, optional) records how strong both
cues get for this user/lighting, so 1.0 = "tongue fully out".
"""

import json
import os

import cv2
import numpy as np

# MediaPipe Face Mesh landmark indices
MOUTH_L, MOUTH_R = 61, 291
UPPER_OUTER, UPPER_INNER, LOWER_INNER, LOWER_OUTER = 0, 13, 14, 17
CHIN = 152
INNER_LIP = [78, 191, 80, 81, 82, 13, 312, 311, 310, 415, 308, 324, 318, 402, 317, 14, 87, 178, 88, 95]
OUTER_LIP = [61, 185, 40, 39, 37, 0, 267, 269, 270, 409, 291, 375, 321, 405, 314, 17, 84, 181, 91, 146]
SKIN_KEYS = ("skin_l", "skin_r", "skin_c")

LAB_W = np.array([0.5, 1.0, 1.0], np.float32)  # down-weight lightness (shading)
# Defaults (areas are in mouth-width^2 units) until a T calibration overrides them.
RED_FULL = 0.06
SPEC_FULL = 0.004


class TongueDetector:
    def __init__(self, path):
        self.path = path
        self.refs = {}
        if os.path.exists(path):
            with open(path) as f:
                self.refs = {k: np.array(v, np.float32) for k, v in json.load(f).items()}
        if "red_full" not in self.refs:  # left over from the colour-only detector
            for k in ("base", "tongue"):
                self.refs.pop(k, None)
        self._samples = []
        self.calibrating = False
        self.last_error = ""

    # ------------------------------------------------------------ helpers
    @staticmethod
    def _mw(p):
        return float(np.hypot(*(p[MOUTH_R] - p[MOUTH_L])))

    @staticmethod
    def _disc_median(lab, c, r):
        h, w = lab.shape[:2]
        x0, x1 = int(max(0, c[0] - r)), int(min(w, c[0] + r + 1))
        y0, y1 = int(max(0, c[1] - r)), int(min(h, c[1] + r + 1))
        if x1 <= x0 or y1 <= y0:
            return None
        return np.median(lab[y0:y1, x0:x1].reshape(-1, 3), axis=0)

    def _ref(self, k, default):
        v = self.refs.get(k)
        return float(np.asarray(v).ravel()[0]) if v is not None else default

    def _save(self):
        with open(self.path, "w") as f:
            json.dump({k: np.asarray(v).tolist() for k, v in self.refs.items()}, f, indent=1)

    @property
    def ready(self):
        return all(k in self.refs for k in ("lip",) + SKIN_KEYS)

    def _roi(self, frame, p):
        h, w = frame.shape[:2]
        mw = self._mw(p)
        mx = (p[MOUTH_L][0] + p[MOUTH_R][0]) / 2
        x0 = int(max(0, mx - 0.65 * mw))
        x1 = int(min(w, mx + 0.65 * mw))
        y0 = int(max(0, p[UPPER_OUTER][1] - 0.45 * mw))
        y1 = int(min(h, p[LOWER_OUTER][1] + 0.9 * mw))
        return x0, y0, x1, max(y1, y0 + 2)

    def _cues(self, frame, p):
        """Raw cue areas (mouth-width^2 units) + pixel masks, all in ROI coords."""
        mw = self._mw(p)
        x0, y0, x1, y1 = self._roi(frame, p)
        roi = frame[y0:y1, x0:x1]
        shape = roi.shape[:2]
        off = np.array([x0, y0], np.float32)
        poly = lambda idx: [np.round(p[idx] - off).astype(np.int32)]
        outer = np.zeros(shape, np.uint8)
        cv2.fillPoly(outer, poly(OUTER_LIP), 1)
        inner = np.zeros(shape, np.uint8)
        cv2.fillPoly(inner, poly(INNER_LIP), 1)
        k = max(3, int(0.03 * mw) | 1)
        outer_d = cv2.dilate(outer, np.ones((k, k), np.uint8))
        # Band over the mouth width, above and below the lips
        band = np.zeros(shape, np.uint8)
        mx = (p[MOUTH_L][0] + p[MOUTH_R][0]) / 2 - x0
        top = int(max(0, p[UPPER_OUTER][1] - 0.22 * mw - y0))  # stay clear of the nose
        band[top:, int(max(0, mx - 0.45 * mw)):int(mx + 0.45 * mw)] = 1
        out_zone = band & (1 - outer_d)          # outside the lips entirely
        lip_ring = outer & (1 - inner)

        lab = cv2.cvtColor(roi, cv2.COLOR_BGR2LAB).astype(np.float32)
        d = lambda ref: np.sqrt((((lab - ref) * LAB_W) ** 2).sum(axis=-1))
        ds = np.minimum.reduce([d(self.refs[s]) for s in SKIN_KEYS])
        dl = d(self.refs["lip"])
        L = lab[..., 0]
        # 1) lip/tongue-coloured pixels outside the lip contour, connected to the lips
        # Closer to the lip colour than to ANY skin sample (no redness rule: under some
        # lighting lips are barely redder than skin).
        reddish = ((dl < 0.9 * ds) & (L > 40)).astype(np.uint8)
        red_out = reddish & out_zone
        red_out = cv2.morphologyEx(red_out, cv2.MORPH_OPEN, np.ones((k, k), np.uint8))
        n, lbl, stats, _ = cv2.connectedComponentsWithStats(red_out, 8)
        touch = cv2.dilate(outer, np.ones((3 * k, 3 * k), np.uint8))
        keep = np.zeros(shape, np.uint8)
        for i in range(1, n):
            comp = lbl == i
            if (comp & (touch > 0)).any():
                keep[comp] = 1
        # 2) specular highlights: brighter than the local surroundings, colourless
        blur = cv2.GaussianBlur(L, (0, 0), max(1.0, 0.04 * mw))
        chroma = np.hypot(lab[..., 1] - 128, lab[..., 2] - 128)
        spec = ((L > blur + 18) & (L > 150) & (chroma < 25)).astype(np.uint8)
        # Only highlights on the lips or on the protruding tongue count.
        near = cv2.dilate(keep, np.ones((2 * k, 2 * k), np.uint8)) | lip_ring
        spec_out = spec & (near > 0).astype(np.uint8)
        a2 = mw * mw
        return keep.sum() / a2, spec_out.sum() / a2, keep, spec_out, (x0, y0)

    # ------------------------------------------------------------ calibration
    def calibrate_neutral(self, frame, p):
        """Mouth closed: lip colour (lip ring), skin colour (each cheek + chin), cue baselines."""
        lab = cv2.cvtColor(frame, cv2.COLOR_BGR2LAB).astype(np.float32)
        mw = self._mw(p)
        ring = np.zeros(lab.shape[:2], np.uint8)
        cv2.fillPoly(ring, [np.round(p[OUTER_LIP]).astype(np.int32)], 1)
        cv2.fillPoly(ring, [np.round(p[INNER_LIP]).astype(np.int32)], 0)
        if ring.sum() > 20:
            self.refs["lip"] = np.median(lab[ring > 0], axis=0)
        for k, c in (("skin_l", p[205]), ("skin_r", p[425]), ("skin_c", (p[LOWER_OUTER] + p[CHIN]) / 2)):
            v = self._disc_median(lab, c, 0.07 * mw)
            if v is not None:
                self.refs[k] = v
        if self.ready:
            red, spec, *_ = self._cues(frame, p)
            self.refs["red0"] = np.array([red], np.float32)
            self.refs["spec0"] = np.array([spec], np.float32)
        self._save()

    def start_tongue_calibration(self):
        self._samples = []
        self.calibrating = True
        self.last_error = ""

    def feed_tongue_calibration(self, frame, p):
        """Tongue out and down over the lower lip: learn how strong the cues get. True when done."""
        if not self.ready:
            self.last_error = "press C (neutral) first"
            return False
        red, spec, *_ = self._cues(frame, p)
        red -= self._ref("red0", 0.0)
        spec -= self._ref("spec0", 0.0)
        if red > 0.01 or spec > 0.0008:
            self._samples.append((red, spec))
        else:
            self.last_error = "no tongue outside the lips - stick it further out and down"
        if len(self._samples) >= 15:
            s = np.array(self._samples)
            # 1.0 should be reached a bit before the strongest pose
            self.refs["red_full"] = np.array([max(0.015, 0.8 * np.percentile(s[:, 0], 75))], np.float32)
            self.refs["spec_full"] = np.array([max(0.001, 0.8 * np.percentile(s[:, 1], 75))], np.float32)
            self.calibrating = False
            self.refs.pop("base", None)
            r = self.measure(frame, p)[0]
            self.refs["base"] = np.array([r["tongueX"], r["tongueY"], 0.0], np.float32)
            self._save()
            return True
        return False

    # ------------------------------------------------------------ measurement
    def measure(self, frame, p):
        """Returns ({tongueOut, tongueX, tongueY}, mask, (x0, y0)) — mask is in ROI coords."""
        zero = {"tongueOut": 0.0, "tongueX": 0.0, "tongueY": 0.0}
        if not self.ready or self._mw(p) < 10:
            return zero, None, (0, 0)
        mw = self._mw(p)
        red, spec, red_mask, spec_mask, (x0, y0) = self._cues(frame, p)
        red_t = max(0.0, red - self._ref("red0", 0.0)) / self._ref("red_full", RED_FULL)
        spec_t = max(0.0, spec - self._ref("spec0", 0.0)) / self._ref("spec_full", SPEC_FULL)
        # Geometry is the main cue; shine confirms it (shine alone gives at most half).
        out = max(red_t, 0.5 * (red_t + min(spec_t, 2.0)))
        mask = (red_mask | spec_mask).astype(np.uint8)
        if out < 0.05 or not mask.any():
            return zero, mask if mask.any() else None, (x0, y0)
        ys, xs = np.nonzero(mask)
        cx, cy = xs.mean() + x0, ys.mean() + y0
        mx = (p[MOUTH_L][0] + p[MOUTH_R][0]) / 2
        my = (p[UPPER_INNER][1] + p[LOWER_INNER][1]) / 2
        tx = (cx - mx) / (0.4 * mw)
        ty = -(cy - my) / (0.4 * mw)
        base = self.refs.get("base")
        if base is not None:
            tx, ty = tx - float(base[0]), ty - float(base[1])
        gate = min(1.0, out / 0.3)  # direction only matters once the tongue is out
        return ({"tongueOut": float(out), "tongueX": float(tx * gate), "tongueY": float(ty * gate)},
                mask, (x0, y0))
