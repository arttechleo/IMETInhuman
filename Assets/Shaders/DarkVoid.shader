Shader "IMETINHUMAN/VFX/Dark Void"
{
    // The dark the figure stands in: a black shell around the viewer that takes
    // the room away. On passthrough it is the only way to get darkness at all --
    // the headset composites the real room behind the app, so the app has to
    // cover it. Drawn before everything else, writing depth, so nothing of the
    // room survives and everything of the piece is drawn over it.
    //
    // Not quite black at the floor: a breath of blue keeps it from reading as a
    // dead pixel, which is what tells the eye it is a space rather than a hole.
    Properties
    {
        _Alpha ("Runtime Alpha", Range(0, 1)) = 1
        _Void ("Void", Color) = (0, 0, 0, 1)
        _Floor ("Towards The Floor", Color) = (0.004, 0.008, 0.013, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry-100"
            "RenderType" = "Opaque"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Void"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            ZTest LEqual
            Cull Front          // seen from the inside

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half  _Alpha;
                half4 _Void;
                half4 _Floor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionOS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionOS = input.positionOS.xyz;
                output.positionCS = TransformWorldToHClip(TransformObjectToWorld(input.positionOS.xyz));
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half down = (half)saturate(-normalize(input.positionOS).y);
                half3 color = lerp(_Void.rgb, _Floor.rgb, down * down);
                return half4(color, _Alpha);
            }
            ENDHLSL
        }
    }
}
