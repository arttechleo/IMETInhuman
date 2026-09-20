Shader "IMETINHUMAN/VFX/Room Moss"
{
    // Moss over the real room during the rain: drawn on the headset's scan of
    // the room (walls, floor, ceiling, furniture), everywhere the moss has
    // reached and nowhere else, so passthrough shows through the rest.
    //
    // Growth is a front that rises through the room: floor and low corners
    // first, up the walls, over the tops of furniture, the ceiling last. The
    // front is broken up by a slow large-scale clump field and, at its very
    // edge, by the tufts themselves -- the tallest tufts reach new ground
    // first -- so it creeps in patches rather than as a line. Young moss at
    // the front is brighter and yellower than the old, dark carpet behind it.
    //
    // Lighting comes from the room: ambient from the room panorama in the
    // surface's direction (RoomEnvironmentCapture), when there is one. While
    // the rain falls the moss is wet: a sheen of the room on the tuft tops.
    //
    // Textures are one tileable moss patch (RoomMoss.cs builds it), projected
    // on the two axes the surface faces most, so the scan needs no uvs.
    Properties
    {
        _MossTex ("Moss (RGB albedo, A height)", 2D) = "gray" {}
        _MossNormal ("Moss Normal (RG)", 2D) = "bump" {}
        _TileSize ("Patch Size (m)", Float) = 0.6
        _ClumpSize ("Clump Size (m)", Float) = 3.5
        _Growth ("Growth", Range(0, 1)) = 0
        _Wet ("Wet", Range(0, 1)) = 0
        _FloorY ("Floor Height", Float) = 0
        _CeilingY ("Ceiling Height", Float) = 2.6
        _OldTint ("Old Moss", Color) = (0.55, 0.75, 0.40, 1)
        _YoungTint ("Young Moss", Color) = (1.15, 1.25, 0.55, 1)
        _Brightness ("Brightness", Range(0, 3)) = 1.1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Moss"
            Tags { "LightMode" = "UniversalForward" }

            // The ragged front is antialiased by MSAA coverage, not blending, so
            // the moss stays opaque, depth-sorted, and cheap.
            AlphaToMask On
            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/IMH_PassthroughCamera.hlsl"

            TEXTURE2D(_MossTex);    SAMPLER(sampler_MossTex);
            TEXTURE2D(_MossNormal); SAMPLER(sampler_MossNormal);

            CBUFFER_START(UnityPerMaterial)
                float4 _MossTex_ST;
                float4 _MossNormal_ST;
                float  _TileSize;
                float  _ClumpSize;
                half   _Growth;
                half   _Wet;
                float  _FloorY;
                float  _CeilingY;
                half4  _OldTint;
                half4  _YoungTint;
                half   _Brightness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Grown out a little from the surface, so it sits on the scan
                // rather than fighting it, and reads as a layer with thickness.
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz) + normalWS * 0.01;
                output.positionWS = positionWS;
                output.normalWS = (half3)normalWS;
                output.positionCS = TransformWorldToHClip(positionWS);
                return output;
            }

            // The two projections the surface faces most, weighted; the third is
            // never worth its fetch.
            struct Biplanar { float2 uvA, uvB; half wA, wB; half3 axisA, axisB; };

            Biplanar Project(float3 p, half3 n)
            {
                half3 a = abs(n);
                Biplanar b;
                // Largest and middle axes.
                if (a.x >= a.y && a.x >= a.z)
                {
                    b.uvA = p.zy; b.axisA = half3(sign(n.x), 0, 0); b.wA = a.x;
                    if (a.y >= a.z) { b.uvB = p.xz; b.axisB = half3(0, sign(n.y), 0); b.wB = a.y; }
                    else            { b.uvB = p.xy; b.axisB = half3(0, 0, sign(n.z)); b.wB = a.z; }
                }
                else if (a.y >= a.z)
                {
                    b.uvA = p.xz; b.axisA = half3(0, sign(n.y), 0); b.wA = a.y;
                    if (a.x >= a.z) { b.uvB = p.zy; b.axisB = half3(sign(n.x), 0, 0); b.wB = a.x; }
                    else            { b.uvB = p.xy; b.axisB = half3(0, 0, sign(n.z)); b.wB = a.z; }
                }
                else
                {
                    b.uvA = p.xy; b.axisA = half3(0, 0, sign(n.z)); b.wA = a.z;
                    if (a.x >= a.y) { b.uvB = p.zy; b.axisB = half3(sign(n.x), 0, 0); b.wB = a.x; }
                    else            { b.uvB = p.xz; b.axisB = half3(0, sign(n.y), 0); b.wB = a.y; }
                }
                // Sharpened, so the seam between projections is narrow.
                half wA = pow(b.wA, 6.0h), wB = pow(b.wB, 6.0h);
                half sum = max(wA + wB, 1e-4h);
                b.wA = wA / sum;
                b.wB = wB / sum;
                return b;
            }

            // A tangent-space normal from one projection, into world space
            // (whiteout-style: the texture tilt added to the axis).
            half3 ToWorld(half2 tilt, half3 axis)
            {
                half3 t = axis.x != 0 ? half3(0, tilt.y, tilt.x)
                        : axis.y != 0 ? half3(tilt.x, 0, tilt.y)
                                      : half3(tilt.x, tilt.y, 0);
                return axis + t;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 n = normalize(input.normalWS);
                float3 p = input.positionWS;
                Biplanar proj = Project(p, n);

                // Where the front is: low first, the ceiling last; upward faces
                // (floor, tabletops) a little ahead of walls at the same height,
                // and the large clumps pulling patches ahead or holding them back.
                // A blurred mip: the cushions, not the tufts.
                half clump = SAMPLE_TEXTURE2D_LOD(_MossTex, sampler_MossTex, proj.uvA / _ClumpSize + 0.37, 4).a;
                clump = saturate((clump - 0.5h) * 3.0h + 0.5h);
                half height01 = (half)saturate((p.y - _FloorY) / max(_CeilingY - _FloorY, 0.5));
                half arrival = height01 * 0.72h - n.y * 0.08h + (clump - 0.5h) * 0.45h + 0.12h;

                float2 uvA = proj.uvA / _TileSize, uvB = proj.uvB / _TileSize;
                half4 mossA = SAMPLE_TEXTURE2D(_MossTex, sampler_MossTex, uvA);
                half4 mossB = SAMPLE_TEXTURE2D(_MossTex, sampler_MossTex, uvB);
                half4 moss = mossA * proj.wA + mossB * proj.wB;

                // The tallest tufts reach new ground first.
                half front = _Growth * 1.15h - arrival + (moss.a - 0.5h) * 0.12h;
                if (_Growth <= 0.001h)
                    front = -1.0h;
                half coverage = saturate(front / max(fwidth(front), 1e-4h) + 0.5h);
                clip(coverage - 0.01h);

                half2 tiltA = SAMPLE_TEXTURE2D(_MossNormal, sampler_MossNormal, uvA).rg * 2.0h - 1.0h;
                half2 tiltB = SAMPLE_TEXTURE2D(_MossNormal, sampler_MossNormal, uvB).rg * 2.0h - 1.0h;
                half3 nm = normalize(ToWorld(tiltA, proj.axisA) * proj.wA + ToWorld(tiltB, proj.axisB) * proj.wB);
                // Back onto the scan's side, whichever projection was used.
                nm = normalize(nm * sign(dot(nm, n) + 1e-3h) + n * 0.6h);

                // Young at the front, old behind it.
                half age = saturate(front * 5.0h);
                half3 albedo = moss.rgb * lerp(_YoungTint.rgb, _OldTint.rgb, age);
                // Thin at the front: the scan's own colour would show between
                // tufts; darker grooves stand in for it.
                albedo *= lerp(0.55h, 1.0h, saturate(age * 2.0h + moss.a));

                // Room light around the surface, else a soft light from above.
                half ready = (half)_PtRoomEnvReady;
                half3 ambient = lerp(half3(0.55h, 0.55h, 0.58h), IMH_RoomEnv(nm, 5.0) * 1.3h, ready);
                half key = saturate(dot(nm, normalize(half3(0.25h, 1.0h, 0.15h)))) * 0.35h + 0.65h;
                half3 color = albedo * ambient * key * _Brightness;

                // Wet sheen: the room reflected off the tuft tops, strongest at
                // grazing angles; tiny beads glint on the tallest tips.
                float3 view = normalize(p - GetCameraPositionWS());
                half3 r = reflect((half3)view, nm);
                half fresnel = 0.04h + 0.96h * pow(1.0h - saturate(dot(-(half3)view, nm)), 5.0h);
                half3 sky = lerp(half3(0.6h, 0.65h, 0.7h), IMH_RoomEnv(r, 2.0), ready);
                half tips = smoothstep(0.55h, 0.9h, moss.a);
                color += sky * fresnel * tips * _Wet * 0.9h;
                half glint = smoothstep(0.93h, 0.99h, moss.a) * pow(saturate(dot(r, -(half3)view) * 0.5h + 0.5h), 8.0h);
                color += sky * glint * _Wet * 1.5h;

                return half4(color, coverage);
            }
            ENDHLSL
        }
    }
}
