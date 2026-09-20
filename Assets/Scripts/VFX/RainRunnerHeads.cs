using UnityEngine;

namespace ImetInHuman.VFX
{
    /// <summary>
    /// Where every rain runner's head is this frame, and how big it has grown.
    ///
    /// A runner's position is a pure function of time: its descent is replayed
    /// from its release, merging with each large bead in its path. The shader
    /// used to do that replay in every pixel -- the same answer, millions of times
    /// a frame, and nearly all of its cost. Here it runs once per lane per frame
    /// and the shader reads the result.
    ///
    /// This is a line-for-line port of IMH_RunnerHead and its helpers in
    /// IMH_WaterDrops.hlsl and IMH_Noise.hlsl. The beads a runner merges with are
    /// the ones the shader draws, so the two must agree: change one, change the other.
    /// </summary>
    public static class RainRunnerHeads
    {
        /// <summary>Slots per lane set; matches RUNNER_SLOTS in the shader.</summary>
        public const int Slots = 48;

        /// <summary>
        /// Fills <paramref name="heads"/> with (headY, radius, releaseIndex, life)
        /// per lane, slot i holding column floor(phaseOffset) - 1 + i. Life 0 means
        /// no runner in that lane right now.
        /// </summary>
        public static void Compute(Vector4[] heads, float columns, float phaseOffset, float time,
                                   float speed, float accretion, float baseRadius,
                                   Vector2 beadCells, float beadSizeBias, float aspect)
        {
            var firstCol = Mathf.Floor(phaseOffset) - 1f;
            var lastCol = Mathf.Floor(columns + phaseOffset) + 1f;

            for (var i = 0; i < heads.Length; i++)
            {
                var col = firstCol + i;
                heads[i] = col <= lastCol
                    ? Column(col, columns, phaseOffset, time, speed, accretion, baseRadius,
                             beadCells, beadSizeBias, aspect)
                    : Vector4.zero;
            }
        }

        // IMH_AddRunnerColumn up to the draw.
        static Vector4 Column(float col, float columns, float phaseOffset, float time,
                              float speed, float accretion, float baseRadius,
                              Vector2 beadCells, float beadSizeBias, float aspect)
        {
            var ra = Hash22(col * 1.7f + 3.1f, col * 1.7f + 3.1f);
            var rb = Hash22(col * 1.7f + 91.3f, col * 1.7f + 91.3f);
            if (ra.x < 0.42f)
                return Vector4.zero;

            var lane = (col + 0.5f + (rb.y - 0.5f) * 0.55f - phaseOffset) / Mathf.Max(columns, 1e-3f);

            var rate = speed * Lerp(0.6f, 1.5f, ra.y);
            var period = 0.9f / Mathf.Max(rate, 1e-3f);
            var release = time / period + rb.x * 11f;
            var cycle = Frac(release);
            var releaseIndex = Mathf.Floor(release);

            if (!RunnerHead(lane, rb.x, cycle * period, rate, baseRadius, beadCells, beadSizeBias, aspect,
                            out var headY, out var radius))
                return Vector4.zero;

            var fallen = Saturate(1f - headY);
            radius *= 1f + accretion * 0.12f * fallen;

            var life = SmoothStep(0f, 0.04f, cycle) * (1f - SmoothStep(0.95f, 1f, cycle));
            return new Vector4(headY, radius, releaseIndex, life);
        }

        // IMH_RunnerHead.
        static bool RunnerHead(float lane, float seed, float elapsed, float baseSpeed,
                               float baseRadius, Vector2 beadCells, float beadSizeBias, float aspect,
                               out float headY, out float radius)
        {
            var volume = baseRadius * baseRadius * baseRadius;
            radius = baseRadius;
            headY = 1f + baseRadius;

            var timeLeft = elapsed;
            var cells = Vector2.Max(beadCells, Vector2.one);
            var rowHeight = 1f / cells.y;
            var topRow = Mathf.Floor(cells.y - 1e-3f);

            for (var k = 0; k < 64; k++)
            {
                var row = topRow - k;
                if (row < -1f)
                    break;

                beadY[0] = beadY[1] = beadY[2] = -1f;
                beadVolume[0] = beadVolume[1] = beadVolume[2] = 0f;
                if (row >= 0f)
                {
                    var centreCol = Mathf.Floor(TrackX(lane, (row + 0.5f) * rowHeight, seed) * cells.x);
                    for (var c = 0; c < 3; c++)
                    {
                        BeadInCell(centreCol + c - 1f, row, cells, beadSizeBias, aspect,
                                   out var centerUv, out var radiusUv, out var presence);

                        var beadR = Mathf.Min(radiusUv.x, radiusUv.y);
                        var dx = Mathf.Abs(centerUv.x - TrackX(lane, centerUv.y, seed)) * aspect;
                        if (presence > 0.5f && centerUv.y < headY && dx < radius * 1.2f + beadR * 0.5f)
                        {
                            beadY[c] = centerUv.y;
                            beadVolume[c] = beadR * beadR * beadR;
                        }
                    }

                    // Highest first.
                    SwapIfHigher(beadY, beadVolume, 0, 1);
                    SwapIfHigher(beadY, beadVolume, 1, 2);
                    SwapIfHigher(beadY, beadVolume, 0, 1);
                }

                for (var b = 0; b < 3; b++)
                {
                    if (beadVolume[b] <= 0f)
                        continue;

                    var beadSpeed = baseSpeed * Mathf.Min(Mathf.Pow(radius / baseRadius, 1.5f), 6f);
                    if (Advance(ref headY, ref timeLeft, beadY[b], beadSpeed))
                        return true;

                    volume += beadVolume[b];
                    radius = Mathf.Pow(volume, 1f / 3f);
                }

                var rowSpeed = baseSpeed * Mathf.Min(Mathf.Pow(radius / baseRadius, 1.5f), 6f);
                if (Advance(ref headY, ref timeLeft, row * rowHeight, rowSpeed))
                    return true;
            }

            return headY > -radius * 2f;
        }

        // Scratch for RunnerHead; main thread only, and no garbage per frame.
        static readonly float[] beadY = new float[3];
        static readonly float[] beadVolume = new float[3];

        static void SwapIfHigher(float[] y, float[] v, int a, int b)
        {
            if (y[b] <= y[a])
                return;
            (y[a], y[b]) = (y[b], y[a]);
            (v[a], v[b]) = (v[b], v[a]);
        }

        // IMH_RunnerAdvance.
        static bool Advance(ref float y, ref float timeLeft, float yTarget, float speed)
        {
            var need = (y - yTarget) / speed;
            if (need >= timeLeft)
            {
                y -= timeLeft * speed;
                timeLeft = 0f;
                return true;
            }
            timeLeft -= need;
            y = yTarget;
            return false;
        }

        // IMH_BeadInCell with offset 0, the merge layer's; only what the merge needs.
        static void BeadInCell(float idX, float idY, Vector2 cells, float sizeBias, float aspect,
                               out Vector2 centerUv, out Vector2 radius, out float presence)
        {
            var r0 = Hash22(idX, idY);
            var r1 = Hash22(idX + 17.31f, idY + 17.31f);
            var r2 = Hash22(idX + 53.77f, idY + 53.77f);

            presence = SmoothStep(0.18f, 0.45f, r1.x);

            var sizeRand = Mathf.Pow(Saturate(r2.x), Mathf.Max(sizeBias, 0.25f));
            var rCell = Lerp(0.055f, 0.30f, sizeRand);

            var width = Lerp(0.95f, 1.1f, r1.y);
            var elong = Lerp(1.05f, 1.3f, r2.y) + 0.15f * sizeRand;
            radius = rCell / cells.y * new Vector2(width, width * elong);

            var marginX = Mathf.Min(radius.x / Mathf.Max(aspect, 1e-3f) * cells.x, 0.45f);
            var marginY = Mathf.Min(radius.y * cells.y, 0.45f);
            centerUv = new Vector2(
                (idX + Lerp(marginX, 1f - marginX, r0.x)) / cells.x,
                (idY + Lerp(marginY, 1f - marginY, r0.y)) / cells.y);
        }

        // IMH_RunnerTrackX.
        static float TrackX(float lane, float y, float seed)
        {
            return lane
                 + KinkNoise(y * 9f + seed * 17f, seed * 31f + 1.3f) * 0.028f
                 + KinkNoise(y * 31f + seed * 7f, seed * 53f + 4.1f) * 0.003f;
        }

        // IMH_KinkNoise.
        static float KinkNoise(float p, float seed)
        {
            var i = Mathf.Floor(p);
            var a = Hash21(i, seed);
            var b = Hash21(i + 1f, seed);
            var t = Frac(p);
            return Lerp(a, b, t * t * (3f - 2f * t)) - 0.5f;
        }

        // IMH_Hash21: p3 = frac(p.xyx * 0.1031); p3 += dot(p3, p3.yzx + 33.33).
        static float Hash21(float px, float py)
        {
            var x = Frac(px * 0.1031f);
            var y = Frac(py * 0.1031f);
            var z = Frac(px * 0.1031f);
            var d = x * (y + 33.33f) + y * (z + 33.33f) + z * (x + 33.33f);
            x += d; y += d; z += d;
            return Frac((x + y) * z);
        }

        // IMH_Hash22: p3 = frac(p.xyx * (0.1031, 0.1030, 0.0973)).
        static Vector2 Hash22(float px, float py)
        {
            var x = Frac(px * 0.1031f);
            var y = Frac(py * 0.1030f);
            var z = Frac(px * 0.0973f);
            var d = x * (y + 33.33f) + y * (z + 33.33f) + z * (x + 33.33f);
            x += d; y += d; z += d;
            return new Vector2(Frac((x + y) * z), Frac((x + z) * y));
        }

        // HLSL semantics, which differ from Mathf's: unclamped lerp, and
        // smoothstep over saturate((x - a) / (b - a)).
        static float Frac(float x) => x - Mathf.Floor(x);
        static float Saturate(float x) => Mathf.Clamp01(x);
        static float Lerp(float a, float b, float t) => a + (b - a) * t;

        static float SmoothStep(float a, float b, float x)
        {
            var t = Saturate((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
