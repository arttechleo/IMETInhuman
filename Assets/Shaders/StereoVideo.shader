Shader "IMETINHUMAN/VFX/Stereo Video"
{
    // A side-by-side stereo video with alpha, each eye shown its own half.
    //
    // The source is a stereo pair with a real alpha channel (ProRes 4444), which
    // the headset cannot decode. StereoVideoPlayer's transcode packs it into one
    // ordinary video: colour on top (left eye | right eye), the alpha below it
    // as grey in the same layout. Here each eye reads its half of the colour
    // and its half of the alpha, and the subject is keyed over passthrough.
    // The colour was rendered premultiplied, and is composited that way.
    Properties
    {
        [NoScaleOffset] _MainTex ("Packed Video", 2D) = "black" {}
        _Opacity ("Opacity", Range(0, 1)) = 1
        _Alpha ("Runtime Alpha", Range(0, 1)) = 1
        [Toggle] _SwapEyes ("Swap Eyes", Float) = 0
        _AlphaFloor ("Alpha Black Point", Range(0, 0.2)) = 0.03
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            Blend One OneMinusSrcAlpha    // premultiplied
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                half _Opacity;
                half _Alpha;
                half _SwapEyes;
                half _AlphaFloor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // 0 left, 1 right. Outside XR this is 0: the left eye's view.
                #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    float eye = (float)unity_StereoEyeIndex;
                #else
                    float eye = 0.0;
                #endif
                if (_SwapEyes > 0.5h)
                    eye = 1.0 - eye;

                float x = input.uv.x * 0.5 + eye * 0.5;
                // Top half colour, bottom half alpha; the video's top is uv y = 1.
                half3 colour = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, float2(x, 0.5 + input.uv.y * 0.5)).rgb;
                half grey = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, float2(x, input.uv.y * 0.5)).r;

                // The alpha plane came through an sRGB target like the colour did;
                // undo that, then clear the codec's lifted blacks.
                half alpha = saturate((LinearToSRGB(half3(grey, grey, grey)).r - _AlphaFloor) / (1.0h - _AlphaFloor));
                half fade = _Opacity * _Alpha;
                return half4(colour * fade, alpha * fade);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
