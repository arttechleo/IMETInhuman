#ifndef IMH_PASSTHROUGH_CAMERA_INCLUDED
#define IMH_PASSTHROUGH_CAMERA_INCLUDED

// ---------------------------------------------------------------------------
// The real room, from the headset's colour camera (PassthroughCameraFeed.cs).
//
// On Quest the passthrough the viewer sees is composited behind the app and
// can never be sampled. The camera image can: it is the same room, from a lens
// a few centimetres from the eye, a little behind in time. Effects that bend or
// blur the room -- a drop's lens, fog, a kaleidoscope's mirrors -- sample it
// here, reprojected so a ray from the eye lands on the right camera pixel.
//
// The room has no depth here, so every ray is taken out to one assumed
// distance, _PtDepth. Things near that distance line up with the passthrough
// around them; nearer or farther ones are off by the parallax between the eye
// and the camera lens -- a few pixels, and only visible through a drop.
//
// Everything is global and set once a frame by the feed. _PtReady stays 0
// until frames arrive (no permission yet, or not on a headset), and effects
// fall back to their look without the room.
// ---------------------------------------------------------------------------

// SRGBToLinear. Shaders that include URP's Lighting.hlsl get it anyway; the
// capture pass includes only Core.hlsl, which does not bring it in.
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

TEXTURE2D(_PtCameraTex);
SAMPLER(sampler_PtCameraTex);

float4x4 _PtWorldToCamera;   // world -> camera space, +z along the optical axis
float4   _PtUvFromCamera;    // uv = xy / z * _PtUvFromCamera.xy + _PtUvFromCamera.zw
float    _PtReady;           // 0..1, fades in once the camera is streaming
float    _PtDepth;           // assumed distance to the room, metres
float    _PtExposure;        // brightness match against the passthrough
float    _PtDecodeSrgb;      // 1 when the camera texture arrives sRGB-encoded

// Camera texture uv of a world point. `valid` is false behind the camera or
// outside its image.
float2 IMH_PtWorldToUv(float3 positionWS, out bool valid)
{
    float3 c = mul(_PtWorldToCamera, float4(positionWS, 1.0)).xyz;
    float2 uv = c.xy / max(c.z, 1e-4) * _PtUvFromCamera.xy + _PtUvFromCamera.zw;
    valid = c.z > 1e-3 && all(uv > 0.0) && all(uv < 1.0);
    return uv;
}

// Explicit LOD: refraction scatters neighbouring pixels across the image, and
// the camera texture has no mips for derivatives to choose between anyway.
half3 IMH_PtSample(float2 uv)
{
    half3 c = SAMPLE_TEXTURE2D_LOD(_PtCameraTex, sampler_PtCameraTex, saturate(uv), 0).rgb;
    if (_PtDecodeSrgb > 0.5)
        c = SRGBToLinear(c);
    return c * (half)_PtExposure;
}

// The room seen along a ray from `origin`, taken out to the assumed distance.
half3 IMH_PtSampleRay(float3 origin, float3 direction, out bool valid)
{
    float2 uv = IMH_PtWorldToUv(origin + direction * _PtDepth, valid);
    return IMH_PtSample(uv);
}

// ---------------------------------------------------------------------------
// The room all around, not just what the camera sees now (RoomEnvironmentCapture.cs).
//
// Every frame the camera's view is projected into a panorama around the head,
// so as the viewer looks about, the whole room fills in. Reflections sample it
// in their true direction -- a drop beside the viewer reflects what is behind
// them -- and its mips are the room's light scattered, which is what fog is.
// Equirectangular: u around (atan2 x, z), v up (asin y), so the writer and the
// reader share one mapping and no cubemap face convention can flip it.
// ---------------------------------------------------------------------------

TEXTURE2D(_PtRoomEnv);
SAMPLER(sampler_PtRoomEnv);
float _PtRoomEnvReady;   // 0 until the capture has run; then 1
float _PtRoomEnvMips;    // mip count, for blur in mip units

float2 IMH_DirectionToRoomUv(float3 direction)
{
    float3 d = normalize(direction);
    return float2(atan2(d.x, d.z) * (0.5 / PI) + 0.5, asin(clamp(d.y, -1.0, 1.0)) / PI + 0.5);
}

float3 IMH_RoomUvToDirection(float2 uv)
{
    float lon = (uv.x - 0.5) * (2.0 * PI);
    float lat = (uv.y - 0.5) * PI;
    float c = cos(lat);
    return float3(sin(lon) * c, sin(lat), cos(lon) * c);
}

// Already linear and exposure-matched: the capture wrote it through IMH_PtSample.
half3 IMH_RoomEnv(float3 direction, float lod)
{
    return SAMPLE_TEXTURE2D_LOD(_PtRoomEnv, sampler_PtRoomEnv, IMH_DirectionToRoomUv(direction), lod).rgb;
}

// ---------------------------------------------------------------------------
// Virtual things in the room (HumoboxMirror.cs).
//
// The headset camera sees only the real room, so the Humobox would be missing
// from every drop. A mirror camera at the head renders just the virtual objects,
// and effects look them up by world position; because that camera sits where
// the rays start, a point's direction is all that matters, not its distance.
// Alpha is coverage: 0 where there is nothing virtual.
// ---------------------------------------------------------------------------

TEXTURE2D(_VirtualTex);
SAMPLER(sampler_VirtualTex);
float4x4 _WorldToVirtual;   // the mirror camera's projection x view, OpenGL convention
float _VirtualReady;        // 0 while nothing virtual is shown

half4 IMH_VirtualAt(float3 positionWS)
{
    if (_VirtualReady <= 0.0)
        return half4(0.0h, 0.0h, 0.0h, 0.0h);
    float4 c = mul(_WorldToVirtual, float4(positionWS, 1.0));
    if (c.w <= 1e-4)
        return half4(0.0h, 0.0h, 0.0h, 0.0h);
    float2 uv = c.xy / c.w * 0.5 + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0))
        return half4(0.0h, 0.0h, 0.0h, 0.0h);
    return SAMPLE_TEXTURE2D_LOD(_VirtualTex, sampler_VirtualTex, uv, 0) * (half)_VirtualReady;
}

#endif // IMH_PASSTHROUGH_CAMERA_INCLUDED
