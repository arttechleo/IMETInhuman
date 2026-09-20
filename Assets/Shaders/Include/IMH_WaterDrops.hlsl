#ifndef IMH_WATERDROPS_INCLUDED
#define IMH_WATERDROPS_INCLUDED

#include "IMH_Noise.hlsl"

// ---------------------------------------------------------------------------
// Water on glass: a static bead field plus runners that cross the whole pane.
//
// Everything is built from one primitive, IMH_AddCap, placed in uv space. Drop
// profiles are real spherical caps parameterised by contact angle t:
//
//     a = 1 / sin(t),  b = cos(t) / sin(t)
//     h(u) = r * ( sqrt(a^2 - u^2) - b )
//
// h reaches zero exactly at the contact line (u = 1) and its slope there is
// tan(t) -- finite, and the value the physics calls for.
//
// Placing caps in uv rather than in cell-local space is what lets a runner
// travel the full height of the pane. A cell-local drop can only ever move
// within its own cell before wrapping, which caps its visible life at one cell
// height no matter how the speed is tuned.
// ---------------------------------------------------------------------------

// Pane width over height. Drop radii are isotropic -- in units of the pane's
// height on both axes -- so a drop's shape on screen is what its radii say
// however wide the pane is; uv.x distances are scaled by this before they are
// compared to a radius. The fragment shader sets it before building the field.
static float IMH_Aspect = 1.0;

// 1 on a vertical pane, 0 on horizontal glass. Gravity pulls a drop on a wall
// down into a teardrop; on a floor or ceiling it has nowhere to pull, and the
// drop sits round. Left at 1, the bead shapes are exactly what the CPU runner
// port (RainRunnerHeads.cs) expects -- runners only ever run on walls.
static float IMH_Gravity = 1.0;

struct IMH_WaterField
{
    float  height;    // uv-space height of the water column
    float  coverage;  // 0..1 antialiased water mask
    float  rim;       // contact line / meniscus emphasis
    float  trail;     // deposited film behind a runner
    float  cleared;   // 0..1 path a runner has wiped through the condensation
    float2 gradient;  // d(height)/d(uv)
    float3 hessian;   // (d2h/dx2, d2h/dy2, d2h/dxdy) in uv space
};

IMH_WaterField IMH_InitWaterField()
{
    IMH_WaterField f;
    f.height   = 0.0;
    f.coverage = 0.0;
    f.rim      = 0.0;
    f.trail    = 0.0;
    f.cleared  = 0.0;
    f.gradient = float2(0.0, 0.0);
    f.hessian  = float3(0.0, 0.0, 0.0);
    return f;
}

// One drop at a uv position, with analytic first derivatives. radiusUv carries
// the elongation.
//
// `taper` narrows the drop towards its top, the way water on a vertical pane
// actually sits: gravity pools the volume in a round, heavy bottom while the
// upper contact line, where the drop started, is pulled in behind it -- the
// blunt U outline of every drop on a rained-on window. 0 is a plain rounded
// drop; 0.5 makes the top half as wide as the bottom.
//
// The outline is a capsule with unequal ends: a bottom circle as wide as the
// drop, a smaller top circle, and straight tapering sides between them, so the
// top stays round rather than coming to a point. radius.x is the half-width at
// the bottom, radius.y the half-height, both isotropic (see IMH_Aspect). The
// height profile is a spherical cap over the normalised distance from the
// capsule's axis, and the gradient follows it analytically, so the drop
// refracts as the shape it is.
//
// `wobble` makes the outline irregular. A drop's contact line pins on every
// speck and scratch of the glass, so real beads are lopsided blobs rather than
// clean ovals; each drop's own lumps are seeded from where it sits. Only the
// outline moves -- the slope is left as the regular cap's -- which is all the
// eye reads at this size.
void IMH_AddTaperedCap(float2 uv, float2 center, float2 radius, float taper,
                       float contactAngle, float edgeSoftness, float weight,
                       inout IMH_WaterField f, float wobble = 0.0)
{
    if (weight <= 0.0)
        return;

    // Isotropic space in units of the half-width: circles stay circles.
    float2 toIso = float2(IMH_Aspect, 1.0);
    float rx = max(radius.x, 1e-5);
    float2 s = (uv - center) * toIso / rx;
    float halfHeight = max(radius.y / rx, 1.0);
    float reach = 1.0 + wobble;       // lumps can push past the plain outline
    if (abs(s.x) >= reach || abs(s.y) >= halfHeight + wobble)
        return;

    float bottomR = 1.0;
    float topR = clamp(1.0 - taper, 0.2, 1.0);
    float bottomY = -halfHeight + bottomR;
    float topY = max(halfHeight - topR, bottomY);
    float len = topY - bottomY;

    // Nearest point on the axis and the drop's radius there.
    float t = len > 1e-4 ? saturate((s.y - bottomY) / len) : 0.0;
    float localR = lerp(bottomR, topR, t);
    float2 toAxis = s - float2(0.0, bottomY + t * len);
    float dist = length(toAxis);

    float u = dist / localR;

    if (wobble > 0.0)
    {
        float seed = frac(dot(center, float2(127.1, 311.7)));
        float around = atan2(s.y - (bottomY + topY) * 0.5, s.x);
        float lumps = sin(around * 2.0 + seed * IMH_TAU) * 0.6
                    + sin(around * 3.0 + seed * 17.0) * 0.4;
        u /= 1.0 + wobble * lumps;
    }

    if (u >= 1.0)
        return;                       // the cap is exactly zero outside u = 1

    float body = (1.0 - smoothstep(1.0 - edgeSoftness, 1.0, u)) * weight;
    if (body <= 0.0)
        return;

    // du/ds. On the round ends the axis point is fixed; along the sides it
    // slides with s.y, so the distance is horizontal and the radius changes.
    float2 dUds;
    if (t > 0.0 && t < 1.0)
        dUds = float2(sign(s.x) / localR, -dist / (localR * localR) * (topR - bottomR) / len);
    else
        dUds = toAxis / max(dist * localR, 1e-5);

    float theta = clamp(radians(contactAngle), 0.12, 1.55);
    float sinT, cosT;
    sincos(theta, sinT, cosT);
    float capA = 1.0 / sinT;
    float capB = cosT / sinT;

    float u2 = u * u;
    float hScale = rx;
    float D = sqrt(max(capA * capA - u2, 1e-5));

    // h = hScale (D - capB);  dh/du = -hScale u / D;  ds/duv = toIso / rx.
    float2 grad = (-hScale * u / D) * dUds * toIso / rx;

    f.height   += hScale * (D - capB) * body;
    f.coverage  = saturate(f.coverage + body);
    f.rim      += smoothstep(0.55, 1.0, u) * body;
    f.gradient += grad * body;
}

void IMH_AddCap(float2 uv, float2 center, float2 radiusUv, float contactAngle,
                float edgeSoftness, float weight, inout IMH_WaterField f)
{
    IMH_AddTaperedCap(uv, center, radiusUv, 0.0, contactAngle, edgeSoftness, weight, f);
}

// Static beads on a jittered grid. These never move: below a size threshold a
// drop cannot overcome contact-line pinning, and that is the overwhelming
// majority of what sits on a wet window.
//
//   sizeBias    >1 skews the population small. Rain on glass is numerically
//               dominated by beads near the resolution limit with a thin tail
//               of large ones; a flat distribution reads as polka dots.
//   minRadius   screen footprint of one texel. Beads smaller than this alias
//               into black speckle, so they are faded out before they reach it.
//   suppress    0..1 mask of beads a runner has already absorbed.
// Where the bead of one grid cell sits and how big it is. Shared by the bead
// layer that draws beads and the runners that merge with them, so a runner
// absorbs exactly the beads that are on screen.
//   presence  0..1; beads below about 0.5 are effectively absent.
//   taper     how far the bead narrows towards its top. Bigger beads carry more
//             weight into their base, so they taper more.
void IMH_BeadInCell(float2 id, float2 cells, float2 offset, float sizeBias,
                    out float2 centerUv, out float2 radius, out float presence,
                    out float taper)
{
    float2 r0 = IMH_Hash22(id);
    float2 r1 = IMH_Hash22(id + 17.31);
    float2 r2 = IMH_Hash22(id + 53.77);

    presence = smoothstep(0.18, 0.45, r1.x);

    float sizeRand = pow(saturate(r2.x), max(sizeBias, 0.25));
    float rCell = lerp(0.055, 0.30, sizeRand);

    taper = lerp(0.22, 0.45, frac(r1.x * 7.31 + r2.y * 3.17)) * lerp(0.75, 1.3, sizeRand) * IMH_Gravity;

    // Isotropic radii, sized off the cell height. Drops on a vertical pane are
    // taller than wide, bigger ones more so, as gravity draws them down.
    float width = lerp(0.95, 1.1, r1.y);
    float elong = lerp(1.0, lerp(1.05, 1.3, r2.y) + 0.15 * sizeRand, IMH_Gravity);
    radius = (rCell / cells.y) * float2(width, width * elong);

    // Keep the whole footprint inside its cell. Letting a drop straddle a cell
    // boundary cuts it in half, because only the cell under the pixel is ever
    // evaluated.
    float2 footprintUv = float2(radius.x / max(IMH_Aspect, 1e-3), radius.y);
    float2 margin = min(footprintUv * cells, 0.45);
    float2 centerCell = lerp(margin, 1.0 - margin, r0);
    centerUv = (id + centerCell - offset) / cells;
}

void IMH_AddBeadLayer(float2 uv, float2 cells, float2 offset, float weight,
                      float sizeBias, float contactAngle, float edgeSoftness,
                      float minRadius, float suppress, inout IMH_WaterField f)
{
    float2 id = floor(uv * cells + offset);

    float2 centerUv, radiusUv;
    float presence, taper;
    IMH_BeadInCell(id, cells, offset, sizeBias, centerUv, radiusUv, presence, taper);

    float active = presence * weight * (1.0 - saturate(suppress));
    if (active <= 0.0)
        return;

    // Fade below one texel rather than letting the cap alias.
    float rMin = min(radiusUv.x, radiusUv.y);
    active *= smoothstep(minRadius, minRadius * 2.2, rMin);
    if (active <= 0.0)
        return;

    IMH_AddTaperedCap(uv, centerUv, radiusUv, taper, contactAngle, edgeSoftness, active, f, 0.12);
}

// A drop that crosses the entire pane, accreting the beads in its path.
//
// Real behaviour, and what the brief asks for: the drop releases, runs down,
// sweeps up everything it touches, grows heavier, and accelerates. Gravity
// scales with volume while contact-line drag scales with the wetted width, so a
// growing drop keeps gaining -- the pow(cycle, >1) travel curve is that.
//
// `sweptMask` reports the track it has already cleared so the bead layers can
// erase what it absorbed, leaving the clean wet trail a real window shows.
// Moves a runner's head from y towards yTarget at a fixed speed, spending the
// time it has left. True once the time runs out, with y where the head is now.
bool IMH_RunnerAdvance(inout float y, inout float timeLeft, float yTarget, float speed)
{
    float need = (y - yTarget) / speed;
    if (need >= timeLeft)
    {
        y -= timeLeft * speed;
        timeLeft = 0.0;
        return true;
    }
    timeLeft -= need;
    y = yTarget;
    return false;
}

// Hashed points joined by an eased curve: the path still leans a different
// way at every point, but it bends through them instead of breaking at them.
float IMH_KinkNoise(float p, float seed)
{
    float i = floor(p);
    float a = IMH_Hash21(float2(i, seed));
    float b = IMH_Hash21(float2(i + 1.0, seed));
    float t = frac(p);
    return lerp(a, b, t * t * (3.0 - 2.0 * t)) - 0.5;
}

// Runner path centre at height y. A runner is deflected each time it meets a
// bead or a dry patch, so its path wanders -- but water carries momentum, and
// the wander is a smooth meander, not a zigzag of straight runs.
// RainRunnerHeads.cs mirrors this exactly.
float IMH_RunnerTrackX(float lane, float y, float seed)
{
    return lane
         + IMH_KinkNoise(y * 9.0 + seed * 17.0, seed * 31.0 + 1.3) * 0.028
         + IMH_KinkNoise(y * 31.0 + seed * 7.0, seed * 53.0 + 4.1) * 0.003;
}

// Walks one runner from its release at the top down to where it is now,
// merging with every large bead it touches on the way.
//
// Each merge adds the bead's volume, so the radius jumps by the cube root of
// the summed cubes, and speed goes with weight: past the size where surface
// tension stops holding it back, a drop's pull grows with volume while its
// drag grows with width, so each merge makes it lurch faster. The walk is
// replayed from the release every frame -- position is a pure function of
// time -- so no state is kept anywhere.
//
// Returns false once the runner has left the bottom of the pane.
bool IMH_RunnerHead(float lane, float seed, float elapsed, float baseSpeed,
                    float baseRadius, float2 beadCells, float beadSizeBias,
                    out float headY, out float radius)
{
    float volume = baseRadius * baseRadius * baseRadius;
    radius = baseRadius;
    headY = 1.0 + baseRadius;

    float timeLeft = elapsed;
    float2 cells = max(beadCells, 1.0);
    float rowHeight = 1.0 / cells.y;
    float topRow = floor(cells.y - 1e-3);

    [loop]
    for (int k = 0; k < 64; k++)
    {
        float row = topRow - k;
        if (row < -1.0)
            break;

        // Beads in this row that sit in the runner's way, in the order it
        // meets them. The lane's column and its two neighbours cover the
        // widest runner the merges can build.
        float3 beadY = float3(-1.0, -1.0, -1.0);
        float3 beadVolume = 0.0;
        if (row >= 0.0)
        {
            float centreCol = floor(IMH_RunnerTrackX(lane, (row + 0.5) * rowHeight, seed) * cells.x);
            [unroll]
            for (int c = 0; c < 3; c++)
            {
                float2 centerUv, radiusUv;
                float presence, beadTaper;
                IMH_BeadInCell(float2(centreCol + c - 1.0, row), cells, 0.0, beadSizeBias,
                               centerUv, radiusUv, presence, beadTaper);

                float beadR = min(radiusUv.x, radiusUv.y);
                float dx = abs(centerUv.x - IMH_RunnerTrackX(lane, centerUv.y, seed)) * IMH_Aspect;
                if (presence > 0.5 && centerUv.y < headY && dx < radius * 1.2 + beadR * 0.5)
                {
                    beadY[c] = centerUv.y;
                    beadVolume[c] = beadR * beadR * beadR;
                }
            }

            // Highest first.
            if (beadY.y > beadY.x) { beadY.xy = beadY.yx; beadVolume.xy = beadVolume.yx; }
            if (beadY.z > beadY.y) { beadY.yz = beadY.zy; beadVolume.yz = beadVolume.zy; }
            if (beadY.y > beadY.x) { beadY.xy = beadY.yx; beadVolume.xy = beadVolume.yx; }
        }

        [unroll]
        for (int b = 0; b < 3; b++)
        {
            if (beadVolume[b] <= 0.0)
                continue;

            float beadSpeed = baseSpeed * min(pow(radius / baseRadius, 1.5), 6.0);
            if (IMH_RunnerAdvance(headY, timeLeft, beadY[b], beadSpeed))
                return true;

            volume += beadVolume[b];
            radius = pow(volume, 1.0 / 3.0);
        }

        float rowSpeed = baseSpeed * min(pow(radius / baseRadius, 1.5), 6.0);
        if (IMH_RunnerAdvance(headY, timeLeft, row * rowHeight, rowSpeed))
            return true;
    }

    // Still time left after the last row: the runner is gone off the bottom.
    return headY > -radius * 2.0;
}

// Which lane a runner column runs down, and its random draws. Shared by the
// per-pixel path below and RainRunnerHeads.cs, which must agree on it.
//   ra.x  below 0.42, the column carries no runner.
//   ra.y  speed variation.   rb.x  seed and release phase.
void IMH_RunnerLane(float col, float columns, float phaseOffset,
                    out float lane, out float2 ra, out float2 rb)
{
    ra = IMH_Hash22(col * 1.7 + 3.1);
    rb = IMH_Hash22(col * 1.7 + 91.3);
    lane = (col + 0.5 + (rb.y - 0.5) * 0.55 - phaseOffset) / max(columns, 1e-3);
}

// Draws one runner whose head is known: the drop, the rivulet it drags, the
// beads the rivulet breaks into, and the path it wiped.
//   radius  its radius now, merges and mist included.
//   life    0..1 fade at release and at the sill.
void IMH_DrawRunner(float2 uv, float col, float lane, float seed,
                    float headY, float radius, float releaseIndex, float life,
                    float baseRadius, float contactAngle, float edgeSoftness,
                    float minRadius, float trailStrength,
                    inout IMH_WaterField f, inout float sweptMask)
{
    // A running drop is the most pronounced teardrop on the pane: long, with a
    // heavy round front and its upper contact line dragged in behind it.
    float2 radiusUv = radius * float2(1.0, 1.45);
    float trackX = IMH_RunnerTrackX(lane, headY, seed);

    float weight = life * smoothstep(minRadius, minRadius * 2.2, min(radiusUv.x, radiusUv.y));
    if (weight <= 0.0)
        return;

    IMH_AddTaperedCap(uv, float2(trackX, headY), radiusUv, 0.65, contactAngle, edgeSoftness,
                      weight, f);

    // Everything behind the head follows the path the drop actually took --
    // its zigzag at each height, scaled to how big it was when it passed there.
    float above = step(headY, uv.y);
    float age = uv.y - headY;                    // distance back up the path
    float pathX = IMH_RunnerTrackX(lane, uv.y, seed);
    float dx = abs(uv.x - pathX) * IMH_Aspect;   // isotropic, like the widths

    float grownAt = saturate((1.0 - uv.y) / max(1.0 - headY, 1e-3));
    float passingRadius = lerp(baseRadius, radius, grownAt);

    // ---- rivulet ----------------------------------------------------------
    // A runner drags a thin thread of water behind it, far narrower than
    // itself. Fresh behind the head it is continuous; further back it has had
    // time to thin, neck and break into a chain of beads, so gaps open up in
    // it the older it gets.
    float breakup = smoothstep(0.12, 0.45, age);
    float gap = IMH_Noise21(float2(uv.y * 38.0 + col * 2.1, releaseIndex * 0.7 + col));
    float intact = above * (1.0 - step(gap, breakup * 0.85));

    float rivW = max(passingRadius * 0.14, minRadius * 1.5);
    float rivU = dx / rivW;
    if (intact > 0.0 && rivU < 1.0 && uv.y > headY + radiusUv.y * 0.5)
    {
        // Cross-section is a cylinder cap at the same contact angle as the
        // drops, so it lenses the scene into the thin bright-cored line with
        // dark edges a real rivulet shows.
        float theta = clamp(radians(contactAngle), 0.12, 1.55);
        float sinT, cosT;
        sincos(theta, sinT, cosT);
        float capA = 1.0 / sinT;
        float D = sqrt(max(capA * capA - rivU * rivU, 1e-5));

        float body = (1.0 - smoothstep(1.0 - edgeSoftness * 3.0, 1.0, rivU)) * intact * weight;
        f.height   += rivW * (D - cosT / sinT) * body;
        f.coverage  = saturate(f.coverage + body);
        f.rim      += smoothstep(0.55, 1.0, rivU) * body;
        f.gradient += float2(-rivU / D * sign(uv.x - pathX) * IMH_Aspect, 0.0) * body;
    }

    // ---- droplets where it broke ------------------------------------------
    // Where the thread has necked off, it leaves beads. They sit on the glass
    // where they were left -- fixed in pane space, not following the head --
    // and each release lays a fresh pattern.
    if (uv.y > headY + radiusUv.y && breakup > 0.0)
    {
        const float trailCells = 70.0;
        float slot = floor(uv.y * trailCells);
        float2 pick = IMH_Hash22(float2(col * 13.1 + releaseIndex * 7.7, slot));
        float2 jitter = IMH_Hash22(float2(slot, col * 5.3 + releaseIndex * 3.9));

        if (pick.x > 1.0 - 0.6 * breakup)
        {
            float slotY = (slot + 0.3 + jitter.y * 0.4) / trailCells;

            float grownThere = saturate((1.0 - slotY) / max(1.0 - headY, 1e-3));
            float slotRadius = lerp(baseRadius, radius, grownThere);
            float beadR = min(slotRadius * lerp(0.2, 0.36, pick.y), 0.44 / trailCells);

            float beadX = IMH_RunnerTrackX(lane, slotY, seed)
                        + (jitter.x - 0.5) * 0.3 * slotRadius / max(IMH_Aspect, 1e-3);
            float beadWeight = weight * smoothstep(minRadius, minRadius * 2.2, beadR);
            IMH_AddTaperedCap(uv, float2(beadX, slotY), beadR * float2(1.0, 1.15), 0.3,
                              contactAngle, edgeSoftness, beadWeight, f, 0.1);
        }
    }

    // ---- the path it wiped -------------------------------------------------
    // Through condensation the runner also leaves an absence: the fog it
    // wiped off, as wide as the drop itself, with the rivulet running down the
    // middle of the clear lane.

    // A wiped edge is never ruled: the fog it pushed aside rims it unevenly.
    float edgeNoise = IMH_Noise21(float2(uv.y * 60.0 + col * 3.7, releaseIndex * 1.3));
    float wipeW = passingRadius * lerp(0.85, 1.2, edgeNoise);
    float wiped = (1.0 - smoothstep(wipeW * 0.7, wipeW * 1.15, dx)) * above * weight;
    f.cleared = max(f.cleared, wiped * saturate(trailStrength / 0.6));

    // Everything the runner merged with is gone from its path. The swept band
    // reaches as wide as the merge test plus a bead's own width, so an absorbed
    // bead disappears whole rather than being cut in half.
    float sweepW = passingRadius * 1.2 + 0.012;
    float sweptBand = 1.0 - smoothstep(sweepW, sweepW + 0.008, dx);
    sweptMask = max(sweptMask, sweptBand * above * weight);
}


void IMH_AddRunnerColumn(float2 uv, float col, float columns, float phaseOffset, float time,
                         float speed, float accretion, float baseRadius,
                         float contactAngle, float edgeSoftness, float minRadius,
                         float trailStrength, float2 beadCells, float beadSizeBias,
                         inout IMH_WaterField f, inout float sweptMask)
{
    float lane;
    float2 ra, rb;
    IMH_RunnerLane(col, columns, phaseOffset, lane, ra, rb);

    // Not every column is running at any given moment.
    if (ra.x < 0.42)
        return;

    // One release per cycle. Merging speeds a runner up enough that it reaches
    // the sill in well under the 1 / rate an unmerged one would take, so the
    // cycle is set just past a typical merged descent: lanes are rarely idle,
    // and the rare runner that found little to merge with fades out low on the
    // pane instead.
    float rate = speed * lerp(0.6, 1.5, ra.y);
    float period = 0.9 / max(rate, 1e-3);
    float release = time / period + rb.x * 11.0;
    float cycle = frac(release);
    float releaseIndex = floor(release);

    float headY, radius;
    if (!IMH_RunnerHead(lane, rb.x, cycle * period, rate, baseRadius, beadCells, beadSizeBias,
                        headY, radius))
        return;

    // Fine mist too small to draw still adds up along the way.
    float fallen = saturate(1.0 - headY);
    radius *= 1.0 + accretion * 0.12 * fallen;

    // Ease in at release and out at the sill so runners never pop.
    float life = smoothstep(0.0, 0.04, cycle) * (1.0 - smoothstep(0.95, 1.0, cycle));

    IMH_DrawRunner(uv, col, lane, rb.x, headY, radius, releaseIndex, life, baseRadius,
                   contactAngle, edgeSoftness, minRadius, trailStrength, f, sweptMask);
}

// The same runner with its head computed once a frame on the CPU
// (RainRunnerHeads.cs) rather than replayed from its release in every pixel --
// the replay walks every bead row above the head, and was nearly all of this
// shader's cost. head = (headY, radius, releaseIndex, life); life 0 is no runner.
void IMH_AddRunnerFromHead(float2 uv, float col, float columns, float phaseOffset, float4 head,
                           float baseRadius, float contactAngle, float edgeSoftness,
                           float minRadius, float trailStrength,
                           inout IMH_WaterField f, inout float sweptMask)
{
    if (head.w <= 0.0)
        return;

    float lane;
    float2 ra, rb;
    IMH_RunnerLane(col, columns, phaseOffset, lane, ra, rb);

    // Nothing a runner draws reaches further from its lane than the track's
    // zigzag (0.017 uv at most) plus about 1.6 radii: the wiped path, the swept
    // band and the rivulet's stray beads. Most pixels are outside every lane
    // but their own, so this skips the rest.
    float reach = (abs(uv.x - lane) - 0.017) * IMH_Aspect;
    if (reach > head.y * 1.6 + 0.025)
        return;

    IMH_DrawRunner(uv, col, lane, rb.x, head.x, head.y, head.z, head.w, baseRadius,
                   contactAngle, edgeSoftness, minRadius, trailStrength, f, sweptMask);
}

// Sweeps the neighbouring lanes as well as the one under this pixel. Accretion
// grows a runner past its own lane width, so evaluating only the column the
// pixel falls in would slice the drop off at the lane boundary. The per-column
// early-outs keep the two extra lanes close to free.
void IMH_AddRunner(float2 uv, float columns, float phaseOffset, float time,
                   float speed, float accretion, float baseRadius,
                   float contactAngle, float edgeSoftness, float minRadius,
                   float trailStrength, float2 beadCells, float beadSizeBias,
                   inout IMH_WaterField f, inout float sweptMask)
{
    float col = floor(uv.x * columns + phaseOffset);

    UNITY_UNROLL
    for (int i = -1; i <= 1; i++)
    {
        IMH_AddRunnerColumn(uv, col + i, columns, phaseOffset, time, speed, accretion,
                            baseRadius, contactAngle, edgeSoftness, minRadius,
                            trailStrength, beadCells, beadSizeBias, f, sweptMask);
    }
}

float3 IMH_WaterNormalTS(IMH_WaterField f, float strength)
{
    return normalize(float3(-f.gradient * strength, 1.0));
}

#endif // IMH_WATERDROPS_INCLUDED
