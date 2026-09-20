using UnityEngine;
using UnityEngine.Rendering;

namespace ImetInHuman.VFX
{
    /// <summary>Builds the camera-locked quad every full-view effect renders on.</summary>
    public static class OverlayQuad
    {
        public static CameraLockedOverlay Spawn(string overlayName, Camera camera, Material material,
                                                float startDelay, float duration, float distance)
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = overlayName;

            var collider = quad.GetComponent<Collider>();
            if (collider != null)
                Object.Destroy(collider);

            // A quad pinned to the eye must never cast, receive, or be culled by
            // occlusion; all three would flicker as the head moves.
            var meshRenderer = quad.GetComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            // Probes stay on: the water is lit by the scene it sits in, and turning
            // these off is what makes an overlay read as pasted on rather than
            // present in the room.
            meshRenderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            meshRenderer.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            meshRenderer.allowOcclusionWhenDynamic = false;

            var overlay = quad.AddComponent<CameraLockedOverlay>();
            overlay.Configure(camera, material, startDelay, duration, distance);
            return overlay;
        }
    }
}
