Shader "IMETINHUMAN/VFX/Dark Water"
{
    // The shallow water the figure stands on in the dark: black, still, and
    // only there at all because of what moves on it.
    //
    // Nothing is lit here -- there is no light in the void -- so the surface is
    // read purely from its ripples: the faint sheen they turn towards the eye,
    // and the ring that spreads from where the figure stands. The reflection
    // itself is drawn under this surface by the splats (GaussianSplats.shader
    // with _Mirror), so this pass only tints and dims what shows through.
    Properties
    {
        _Alpha ("Runtime Alpha", Range(0, 1)) = 1
        _WaterColor ("Water", Color) = (0.008, 0.013, 0.016, 1)
        _Sheen ("Sheen", Range(0, 2)) = 0.5
        _SheenColor ("Sheen Colour", Color) = (0.45, 0.62, 0.68, 1)
        _RippleScale ("Ripple Size", Float) = 2.6
        _RippleSpeed ("Ripple Speed", Range(0, 4)) = 0.9
        _Fade ("Fade Out Over Metres", Float) = 5
        _Rings ("Rings From The Figure", Range(0, 1)) = 0.6
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

            Blend SrcAlpha OneMinusSrcAlpha
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
                half4 _WaterColor;
                half  _Sheen;
                half4 _SheenColor;
                float _RippleScale;
                float _RippleSpeed;
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

            // Slope of the surface at a point: the same two waves the reflection
            // is swayed by, plus rings spreading from where the figure stands.
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
                    // Rings that spread outwards and die away with distance.
                    float ring = cos(r * 9.0 - t * 3.0) * exp(-r * 0.7);
                    slope += normalize(toFigure) * ring * _Rings * 2.0;
                }
                return slope * 0.06;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float3 view = normalize(GetCameraPositionWS() - input.positionWS);
                float2 slope = Slope(input.positionWS.xz);
                float3 normalWS = normalize(float3(-slope.x, 1.0, -slope.y));

                // No light to reflect, so the sheen is the grazing angle alone:
                // where the ripples tip towards the eye, the surface shows.
                half ndv = (half)saturate(dot(normalWS, view));
                half grazing = pow(1.0h - ndv, 4.0h);
                half tilt = (half)saturate(length(slope) * 6.0);
                half3 sheen = _SheenColor.rgb * _Sheen * (grazing * 0.6h + tilt * grazing * 1.4h);

                // The pool ends in the dark rather than at an edge.
                float away = length(input.positionWS.xz - GetCameraPositionWS().xz);
                half reach = (half)saturate(1.0 - away / max(_Fade, 0.5));

                half3 color = _WaterColor.rgb + sheen;
                // Opaque underfoot, clearing towards the dark: the reflection
                // shows through where the water is thinnest.
                half alpha = saturate((0.55h + grazing * 0.35h) * reach) * _Alpha;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
