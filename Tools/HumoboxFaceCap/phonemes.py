"""Timed phonemes for a recorded take (offline, more accurate than the live "th" detector).

    .venv\\Scripts\\python.exe phonemes.py take.wav out.json

Writes [[start_s, dur_s, phone], ...] using Allosaurus (universal phone recogniser,
English inventory). Humobox uses it to key tongue-tip peeks on th/l/t/d/n sounds.
"""

import json
import os
import sys
import tempfile

import numpy as np
import soundfile as sf


def to_16k_mono(path):
    x, sr = sf.read(path, dtype="float32", always_2d=True)
    x = x.mean(axis=1)
    if sr != 16000:
        n = int(round(len(x) * 16000 / sr))
        x = np.interp(np.linspace(0, len(x) - 1, n), np.arange(len(x)), x).astype(np.float32)
    fd, tmp = tempfile.mkstemp(suffix=".wav")
    os.close(fd)
    sf.write(tmp, x, 16000, subtype="PCM_16")
    return tmp


def main():
    wav, out = sys.argv[1], sys.argv[2]
    from allosaurus.app import read_recognizer
    tmp = to_16k_mono(wav)
    try:
        model = read_recognizer()
        text = model.recognize(tmp, "eng", timestamp=True)
    finally:
        os.remove(tmp)
    phones = []
    for line in text.strip().splitlines():
        parts = line.split()
        if len(parts) >= 3:
            phones.append([float(parts[0]), float(parts[1]), parts[2]])
    with open(out, "w", encoding="utf-8") as f:
        json.dump(phones, f, ensure_ascii=False)
    print(f"{len(phones)} phones -> {out}")


if __name__ == "__main__":
    main()
