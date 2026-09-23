Shader "IMETINHUMAN/VFX/Dark Water"
{
    // The shallow water the figure stands in, lit as water actually is.
    //
    // Water is a dielectric: it reflects about 2% of what meets it head-on and
    // nearly all of it at a grazing angle. That one fact is the whole look.
    // Straight down at your feet the surface is black and the reflection barely
    // registers; further out, where the surface is seen edge-on, the reflection
    // takes over and the pool turns to a mirror. Get that curve right and the
    // eye reads water without being told.
    //
    // So this pass is not the reflection -- the splats draw that underneath,
    // upside down -- it is the water ON TOP of it: black, at an alpha of
    // 1 - F, letting exactly the reflected fraction through. Over that goes a
    // GGX highlight on the ripples, the one thing in the void with a light of
    // its own, and the ring spreading from where the figure stands.
    //
    // The same reckoning as the raindrops (RainWindowPbrOverlay): Schlick
    // Fresnel on an F0 of 0.02, GGX for the highlight, and a roughness that
    // grows with distance, so far water scatters where near water mirrors.
    Properties
    {
        _Alpha ("Runtime Alpha", Range(0, 1)) = 1
        _Deep ("Deep Water", Color) = (0.004, 0.008, 0.011, 1)
        _Roughness ("Surface Roughness", Range(0.002, 0.6)) = 0.02
        [Tooltip] _F0 ("Reflectance Head-On", Range(0.02, 0.2)) = 0.055
        _KeyLight ("Key Light Colour", Color) = (0.55, 0.72, 0.85, 1)
        _KeyDirection ("Key Light Direction", Vector) = (0.3, 0.85, -0.45, 0)
        _KeyStrength ("Key Light Strength", Range(0, 8)) = 1.1
        _RippleScale ("Ripple Size", Float) = 2.6
        _RippleSpeed ("Ripple Speed", Range(0, 4)) = 0.9
        _RippleSteepness ("Ripple Steepness", Range(0, 0.4)) = 0.018
        _MirrorGain ("Mirror Gain (stylised)", Range(1, 24)) = 5
        _Fade ("Mirror Fades Out Over Metres", Float) = 9
        _Rings ("Rings From The Figure", Range(0, 1)) = 0.18
        _FigureXZ ("Figure Position (xz)", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            // After the splats, so the reflection drawn below shows through it.
            "Queue" = "Transparent+20"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Water"
            Tags { "LightMode" = "UniversalForward" }

            // Colour blends as usual; alpha accumulates instead of being mixed.
            // Plain SrcAlpha blending pulls the destination alpha DOWN (0.55
            // over 1 leaves 0.75), and the headset shows the real room wherever
            // alpha is under one -- which is why the room came back through the
            // water while the sky above it stayed black.
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half  _Alpha;
                half4 _Deep;
                half  _Roughness;
                half  _F0;
                half4 _KeyLight;
                float4 _KeyDirection;
                half  _KeyStrength;
                half  _MirrorGain;
                float _RippleScale;
                float _RippleSpeed;
                float _RippleSteepness;
                float _Fade;
                half  _Rings;
                float4 _FigureXZ;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
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
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                return output;
            }

            // Slope of the surface: two crossing swells, and rings spreading
            // from where the figure stands. The same waves the reflection below
            // is swayed by, so surface and reflection move together.
            float2 Slope(float2 p)
            {
                float t = _Time.y * _RippleSpeed;
                float2 q = p * _RippleScale;
                float2 slope = float2(cos(q.x * 1.1 + t * 1.7) * 1.1 + cos(q.y * 1.6 - t * 1.1) * 0.6,
                                      cos(q.y * 0.9 - t * 1.3) * 1.1 + cos(q.x * 1.8 + t * 0.9) * 0.6);

                float2 toFigure = p - _FigureXZ.xz;
                float r = length(toFigure);
                if (_Rings > 0.001h && r > 1e-3)
                {
                    float ring = cos(r * 9.0 - t * 3.0) * exp(-r * 0.7);
                    slope += normalize(toFigure) * ring * _Rings * 2.0;
                }
                return slope * _RippleSteepness;
            }

            // GGX: the spread of microfacet slopes, which is what makes a
            // highlight on water a long streak rather than a dot.
            half SpecularGGX(half3 n, half3 v, half3 l, half roughness)
            {
                half3 h = SafeNormalize(v + l);
                half ndh = saturate(dot(n, h));
                half ndv = saturate(dot(n, v)) + 1e-4h;
                half ndl = saturate(dot(n, l));
                half a = max(roughness * roughness, 1e-3h);
                half a2 = a * a;
                half d = ndh * ndh * (a2 - 1.0h) + 1.0h;
                half distribution = a2 / max(3.14159h * d * d, 1e-5h);
                // Smith visibility, the usual fast approximation.
                half gv = ndl * (ndv * (1.0h - a) + a);
                half gl = ndv * (ndl * (1.0h - a) + a);
                half visibility = 0.5h / max(gv + gl, 1e-5h);
                return distribution * visibility * ndl;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 toEye = GetCameraPositionWS() - input.positionWS;
                half3 v = (half3)normalize(toEye);
                float2 slope = Slope(input.positionWS.xz);
                half3 n = (half3)normalize(float3(-slope.x, 1.0, -slope.y));

                // Schlick. Water is 2% head-on; a polished dark floor, which is
                // what the reference actually is, runs nearer 5-6%, and that is
                // the difference between a reflection you have to look for and
                // one that reads at a glance.
                half ndv = saturate(dot(n, v));
                half fresnel = _F0 + (1.0h - _F0) * pow(1.0h - ndv, 5.0h);

                // Rougher with distance: a far pixel holds many ripples, and
                // what it reflects is scattered rather than mirrored.
                float away = length(toEye);
                half roughness = saturate(_Roughness + (half)(away * 0.006));

                // The mirror holds near the figure and gives out with distance,
                // so the pool ends in the dark rather than at an edge.
                half reach = (half)saturate(1.0 - away / max(_Fade, 0.5));

                // Physically, standing over your own reflection you would
                // barely see it: a few percent at this angle. The reference
                // shot gets its mirror from a camera almost on the surface,
                // which a viewer standing in the room cannot have. So the
                // Fresnel curve is kept -- faint underfoot, rising outwards,
                // which is what reads as water -- and lifted bodily by a gain.
                // One honest exaggeration rather than a fake reflection.
                half mirror = saturate(fresnel * _MirrorGain) * reach;

                // The highlight dies with the reflection. Left to run to the
                // horizon it draws a bright rim right round the pool, which
                // reads as a lid on the dark -- the one thing the reference
                // never shows. Only the water near the figure catches light.
                half3 l = (half3)normalize(_KeyDirection.xyz);
                half3 highlight = _KeyLight.rgb * _KeyStrength
                                  * SpecularGGX(n, v, l, roughness) * fresnel * reach;

                // Black water over the reflection, letting the reflected
                // fraction through: colour = reflection * F + deep * (1 - F).
                half3 color = _Deep.rgb + highlight;
                half alpha = saturate(1.0h - mirror) * _Alpha;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
