Shader "IMETINHUMAN/VFX/Resonance Air"
{
    // Sound made visible in the air of the real room.
    //
    // Every syllable a speaker makes (ResonanceField.cs listens to their
    // voice) sends out a spherical wave from their mouth: a thin shell that
    // grows at a walking pace and fades as it goes. Seen from inside the room,
    // a shell is a bubble, and a bubble shows itself only where you look along
    // its skin -- so each wave is drawn as the ring where the view ray grazes
    // it, found exactly by the ray's closest approach to the wave's centre (no
    // marching: eight waves, eight little sums a pixel).
    //
    // What the ring does is bend the room: the passthrough camera image
    // (IMH_PassthroughCamera, as the approved raindrops use) is sampled along
    // a ray pushed outward across the shell, the way a soap film or heat haze
    // lenses what is behind it. A faint thin-film tint rides the rim. Where the
    // wavefront passes, droplets hanging in the air catch it and glint -- the
    // "raindrops" the sound shakes loose.
    //
    // Drawn on an inward sphere around the viewer, so it lives in the room.
    // Alpha stays at zero away from the rings: there the real passthrough shows
    // untouched. No shader_feature keywords (materials are made in code, so
    // they would be stripped from the player build).
    Properties
    {
        _Alpha ("Runtime Alpha", Range(0, 1)) = 1
        _Width ("Ring Width (metres off the skin)", Range(0.005, 0.3)) = 0.035
        _Bend ("Refraction Strength", Range(0, 0.4)) = 0.12
        _Rim ("Rim Tint", Range(0, 2)) = 0.2
        _Drops ("Droplet Glints", Range(0, 2)) = 0.9
        _DropDensity ("Droplets Per Square Metre Of Wave", Range(10, 2000)) = 380
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+30"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Resonance"
            Tags { "LightMode" = "UniversalForward" }

            // As the water: colour blends, alpha accumulates (the headset shows
            // the real room wherever alpha is under one).
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Front

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/IMH_PassthroughCamera.hlsl"

            #define WAVES 8

            CBUFFER_START(UnityPerMaterial)
                half  _Alpha;
                half  _Width;
                half  _Bend;
                half  _Rim;
                half  _Drops;
                half  _DropDensity;
            CBUFFER_END

            // Per wave: xyz centre, w radius now (metres); amplitude 0..1 (0 = unused).
            float4 _Waves[WAVES];
            float  _WaveAmp[WAVES];

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(input.positionOS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float Hash31(float3 p)
            {
                p = frac(p * 0.1031);
                p += dot(p, p.yzx + 33.33);
                return frac((p.x + p.y) * p.z);
            }

            // Thin-film colour, kept to a whisper: mostly white light off the skin,
            // a soap bubble's shift only hinted at (a full rainbow read as a neon halo).
            half3 Film(float x)
            {
                return half3(0.85 + 0.15 * cos(6.2832 * (x + float3(0.0, 0.33, 0.67))));
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 eye = GetCameraPositionWS();
                float3 dir = normalize(input.positionWS - eye);

                float  ring = 0.0;        // how much skin the ray grazes
                float3 push = 0.0;        // sum of outward bends
                half3  tint = 0.0;
                float  glint = 0.0;

                [unroll]
                for (int i = 0; i < WAVES; i++)
                {
                    float a = _WaveAmp[i];
                    if (a <= 0.001)
                        continue;
                    float3 c = _Waves[i].xyz;
                    float  r = _Waves[i].w;

                    // Closest approach of the ray to the wave's centre.
                    float  t = max(dot(c - eye, dir), 0.0);
                    float3 q = eye + dir * t;
                    float  d = length(q - c);
                    // Grazing the skin: d near r. The width grows a little with
                    // the wave, as a real front spreads.
                    float  w = _Width * (1.0 + 0.35 * r);
                    float  x = (d - r) / w;
                    float  g = exp(-x * x) * a;
                    if (t <= 0.0 || g < 1e-3)
                        continue;

                    ring += g;
                    float3 outward = (q - c) / max(d, 1e-4);
                    // Just inside the skin the room bends inward, just outside outward:
                    // the derivative of the ring, which is what makes it read as a lens.
                    push += outward * (-2.0 * x) * g;
                    tint += Film(r * 1.7 + d * 3.0) * (half)g;

                    // Droplets riding this wavefront: hashed cells on the sphere
                    // where the ray meets it. One in a few cells holds a drop.
                    float disc = r * r - d * d;
                    if (disc > 0.0)
                    {
                        float3 hit = eye + dir * (t - sqrt(disc));
                        float3 n = normalize(hit - c);
                        float cell = rsqrt(max(_DropDensity, 1.0));
                        float3 id = floor(n * r / cell);
                        float h = Hash31(id + i * 17.0);
                        float3 local = frac(n * r / cell) - 0.5;
                        float spot = saturate(1.0 - length(local) * 4.0);
                        glint += step(0.82, h) * spot * spot * a * saturate(1.0 - r * 0.25);
                    }
                }

                ring = saturate(ring);
                if (ring < 0.01 && glint < 0.01)
                    return half4(0, 0, 0, 0);

                // The room through the bent air.
                float3 bent = normalize(dir + push * _Bend);
                bool valid;
                half3 room = IMH_PtSampleRay(eye, bent, valid);
                float haveRoom = valid ? (float)_PtReady : 0.0;

                half3 rim = tint / max(ring, 1e-3) * (half)(_Rim * 0.35);
                half3 drops = (half3)(glint * _Drops) * half3(0.9, 0.97, 1.0);

                // With the room: bent room + rim. Without (no camera yet): rim only.
                half3 colour = room * (half)haveRoom + rim * (half)ring + drops;
                float alpha = saturate(ring * lerp(0.55, 0.9, haveRoom) + glint * 0.8) * _Alpha;
                return half4(colour, alpha);
            }
            ENDHLSL
        }
    }
}
