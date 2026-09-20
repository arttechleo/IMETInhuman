Shader "IMETINHUMAN/VFX/Rain Window Overlay"
{
    // Cheap sibling of "Rain Window PBR Overlay". Same drop field, but no scene
    // sampling and a fixed tangent-space key light -- built for the Mobile
    // pipeline asset, which ships with Opaque and Depth Texture off.
    Properties
    {
        [Header(Palette)]
        _RainTint ("Rain Tint", Color) = (0.72, 0.88, 1.0, 1.0)
        _ShadowTint ("Shadow Tint", Color) = (0.02, 0.025, 0.028, 1.0)
        _Opacity ("Opacity", Range(0, 1)) = 0.7
        _Alpha ("Runtime Alpha", Range(0, 1)) = 1

        [Header(Static Beads)]
        _DropletDensity ("Bead Density", Range(4, 60)) = 26
        _SizeBias ("Small Drop Bias", Range(0.25, 6)) = 2.6
        _ContactAngle ("Contact Angle (deg)", Range(10, 110)) = 74
        _EdgeSoftness ("Edge Antialias", Range(0.001, 0.3)) = 0.05
        _NormalStrength ("Normal Strength", Range(0, 4)) = 1.0

        [Header(Runners)]
        _RunnerColumns ("Runner Columns", Range(1, 40)) = 11
        _RunnerSpeed ("Runner Speed", Range(0, 2)) = 0.11
        _RunnerSize ("Runner Start Size", Range(0.002, 0.08)) = 0.012
        _Accretion ("Growth While Falling", Range(0, 6)) = 2.2
        _TrailWeight ("Wet Track Strength", Range(0, 2)) = 0.6

        [Header(Shading)]
        _SpecularIntensity ("Water Specular", Range(0, 4)) = 2.2
        _HighlightSharpness ("Highlight Sharpness", Range(0.5, 8)) = 4.5
        _RimDepth ("Droplet Rim Depth", Range(0, 3)) = 1.4
        _LightDir ("Key Light Direction", Vector) = (-0.42, 0.68, 0.58, 0)
        _CryMood ("Nature Crying Mood", Range(0, 1)) = 0.32

        [Header(Framing)]
        [Toggle(_WINDOW_MASK)] _WindowMaskEnabled ("Airplane Window Mask", Float) = 1
        _WindowMask ("Window Mask Blend", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _WINDOW_MASK

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/IMH_WaterDrops.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _RainTint;
                half4 _ShadowTint;
                half4 _LightDir;
                half  _Opacity;
                half  _Alpha;
                half  _DropletDensity;
                half  _SizeBias;
                half  _ContactAngle;
                half  _EdgeSoftness;
                half  _NormalStrength;
                half  _RunnerColumns;
                half  _RunnerSpeed;
                half  _RunnerSize;
                half  _Accretion;
                half  _TrailWeight;
                half  _SpecularIntensity;
                half  _HighlightSharpness;
                half  _RimDepth;
                half  _CryMood;
                half  _WindowMaskEnabled;
                half  _WindowMask;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Uniform across the draw; skips the whole field while faded out.
                half fade = _Opacity * _Alpha;
                if (fade <= 0.002h)
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);

                float time = _Time.y;
                float2 uv = input.uv;

                float minRadius = max(fwidth(uv.x), fwidth(uv.y));

                IMH_WaterField field = IMH_InitWaterField();
                float swept = 0.0;

                // Runners first: they decide which beads have been absorbed.
                IMH_AddRunner(uv, _RunnerColumns, 0.0, time, _RunnerSpeed,
                              _Accretion, _RunnerSize, _ContactAngle, _EdgeSoftness,
                              minRadius, _TrailWeight,
                              float2(_DropletDensity * 0.85, _DropletDensity * 0.7), _SizeBias,
                              field, swept);

                IMH_AddBeadLayer(uv, float2(_DropletDensity * 1.5, _DropletDensity * 1.25),
                                 float2(3.31, 8.07), 1.0, _SizeBias * 1.2,
                                 _ContactAngle, _EdgeSoftness, minRadius, swept, field);

                IMH_AddBeadLayer(uv, float2(_DropletDensity * 0.85, _DropletDensity * 0.7),
                                 float2(0.0, 0.0), 1.0, _SizeBias,
                                 _ContactAngle, _EdgeSoftness, minRadius, swept, field);

                half3 normalTS = IMH_WaterNormalTS(field, _NormalStrength);

                // Fixed key light in tangent space. Cheap, stable, and gives the
                // consistent crescent highlight that reads as water on glass.
                half3 lightDir = normalize(_LightDir.xyz);
                half3 viewDir  = half3(0.0h, 0.0h, 1.0h);
                half3 halfDir  = normalize(lightDir + viewDir);

                half ndh = saturate(dot(normalTS, halfDir));
                half ndv = saturate(dot(normalTS, viewDir));

                half specular = pow(ndh, 44.0h + _HighlightSharpness * 18.0h) * _SpecularIntensity;
                half fresnel = pow(1.0h - ndv, 3.0h);

                // The gradient is a true slope, so its magnitude peaks at exactly
                // tan(contact angle) on the contact line of every drop. That is the
                // rim, for free.
                half tanTheta = tan(radians(clamp(_ContactAngle, 10.0h, 100.0h)));
                half slope = saturate(length(half2(field.gradient)) / max(tanTheta, 0.1h));
                half rims = saturate((field.rim * 0.7h + slope * 0.6h) * _RimDepth);

                half coverage = saturate(field.coverage);
                half highlights = saturate(specular + fresnel * 0.38h * coverage);
                half shade = saturate(field.trail * 0.25h + rims * 0.85h);

                half3 color = _RainTint.rgb * 0.18h * saturate(coverage * 0.35h + field.trail * 0.08h);
                color += _ShadowTint.rgb * shade * 0.64h;
                color += half3(1.0h, 1.0h, 1.0h) * highlights * 1.95h;
                color += _RainTint.rgb * field.trail * 0.12h;
                color = lerp(color, color * half3(0.72h, 0.82h, 0.88h), _CryMood * 0.35h);

                half frameMask = 1.0h;
                #if defined(_WINDOW_MASK)
                {
                    float2 windowUv = input.uv * 2.0 - 1.0;
                    windowUv.x = (windowUv.x - 0.18) * 0.82;
                    half oval = (half)smoothstep(1.04, 0.91, length(windowUv * float2(0.82, 1.18)));
                    half rightFrame  = (half)(smoothstep(0.72, 0.98, input.uv.x) * (1.0 - smoothstep(0.985, 1.0, input.uv.x)));
                    half bottomFrame = 1.0h - (half)smoothstep(0.0, 0.05, input.uv.y);
                    frameMask = saturate(oval * (1.0h - rightFrame * 0.82h) * (1.0h - bottomFrame * 0.95h));
                    frameMask = lerp(1.0h, frameMask, _WindowMask);
                }
                #endif

                half surface = saturate(coverage * 0.35h + rims * 0.6h
                                      + field.trail * 0.4h + highlights * 0.5h);
                half alpha = fade * input.color.a * frameMask * surface;

                return half4(color * alpha, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
