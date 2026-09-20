#ifndef IMH_NOISE_INCLUDED
#define IMH_NOISE_INCLUDED

// Dave Hoskins style hashes. Unlike frac(p.x * p.y) chains these keep a flat
// distribution at large cell coordinates, which matters because the drop grids
// are driven by uv * density and wander far from the origin.

float IMH_Hash21(float2 p)
{
    float3 p3 = frac(p.xyx * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float2 IMH_Hash22(float2 p)
{
    float3 p3 = frac(p.xyx * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

float3 IMH_Hash32(float2 p)
{
    float3 p3 = frac(p.xyx * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yxz + 33.33);
    return frac((p3.xxy + p3.yzz) * p3.zyx);
}

float IMH_Noise21(float2 p)
{
    float2 id = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);

    float a = IMH_Hash21(id);
    float b = IMH_Hash21(id + float2(1.0, 0.0));
    float c = IMH_Hash21(id + float2(0.0, 1.0));
    float d = IMH_Hash21(id + float2(1.0, 1.0));

    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// Value noise plus its analytic gradient. Returns (value, ddx, ddy) so callers
// never have to pay for extra taps just to build a normal.
float3 IMH_NoiseGrad21(float2 p)
{
    float2 id = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float2 du = 6.0 * f * (1.0 - f);

    float a = IMH_Hash21(id);
    float b = IMH_Hash21(id + float2(1.0, 0.0));
    float c = IMH_Hash21(id + float2(0.0, 1.0));
    float d = IMH_Hash21(id + float2(1.0, 1.0));

    float k1 = b - a;
    float k2 = c - a;
    float k3 = a - b - c + d;

    return float3(a + k1 * u.x + k2 * u.y + k3 * u.x * u.y,
                  du.x * (k1 + k3 * u.y),
                  du.y * (k2 + k3 * u.x));
}

float IMH_Fbm21(float2 p, int octaves, float lacunarity, float gain)
{
    float sum = 0.0;
    float amp = 0.5;
    UNITY_UNROLL
    for (int i = 0; i < octaves; i++)
    {
        sum += IMH_Noise21(p) * amp;
        p *= lacunarity;
        amp *= gain;
    }
    return sum;
}

// Distance to the nearest line of a triangular lattice. Returning a distance
// rather than a pre-thresholded mask lets the caller width-correct with fwidth,
// which is what keeps the lines one pixel wide at every zoom instead of
// dissolving into sparkle.
float IMH_TriGridDist(float2 p)
{
    const float SQRT3_2 = 0.86602540378;
    float2 q = float2(p.x + p.y * 0.5, p.y * SQRT3_2);
    float2 cell = abs(frac(q) - 0.5);
    float lineA = cell.x;
    float lineB = abs(cell.x * 0.5 + cell.y * SQRT3_2);
    float lineC = abs(cell.x * 0.5 - cell.y * SQRT3_2);
    return min(lineA, min(lineB, lineC));
}

// d/dx smoothstep(a, b, x). Used to keep height-field gradients analytic where
// a smoothstep is part of the height function.
float IMH_SmoothstepDeriv(float a, float b, float x)
{
    float range = b - a;
    float t = saturate((x - a) / (abs(range) < 1e-6 ? 1e-6 : range));
    return 6.0 * t * (1.0 - t) / (abs(range) < 1e-6 ? 1e-6 : range);
}

float2 IMH_Rotate2D(float2 p, float angle)
{
    float s, c;
    sincos(angle, s, c);
    return float2(c * p.x - s * p.y, s * p.x + c * p.y);
}

#define IMH_TAU 6.28318530718
#define IMH_PI  3.14159265359

#endif // IMH_NOISE_INCLUDED
