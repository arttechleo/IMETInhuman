Shader "IMETINHUMAN/VFX/Rain Particles"
{
    // The storm outside the booth: falling streaks and the rings they leave
    // where they land.
    //
    // A raindrop is a tiny lens, so a streak is not grey: it is the room behind
    // it, bent and brightened. Streaks take their colour from the room panorama
    // in the direction they are seen (RoomEnvironmentCapture), falling back to
    // a cool grey until it exists. Rings are drawn from the particle's uv, so
    // no textures are needed.
    Properties
    {
        [Enum(Streak, 0, Ring, 1)] _Shape ("Shape", Float) = 0
        _Tint ("Tint", Color) = (0.8, 0.86, 0.92, 1)
        _RoomLight ("Room Light In Streaks", Range(0, 3)) = 1.4
        _Opacity ("Opacity", Range(0, 1)) = 0.5
        _Intensity ("Runtime Intensity", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            // Before the booth glass, which draws at the transparent queue, so
            // the drops on the glass stay in front of the storm.
            "Queue" = "Transparent-10"
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
            #include "Include/IMH_PassthroughCamera.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 uv         : TEXCOORD0;
                half3  room       : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                half  _Shape;
                half  _RoomLight;
                half  _Opacity;
                half  _Intensity;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.color = input.color;
                output.uv = input.uv;

                // The room seen through the drop, once per vertex: a streak is a
                // few pixels wide, and this is its only texture read.
                float3 view = positionWS - GetCameraPositionWS();
                half3 room = IMH_RoomEnv(view, 2.0) * _RoomLight;
                output.room = lerp(_Tint.rgb, room + _Tint.rgb * 0.25h, (half)_PtRoomEnvReady);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half coverage;
                if (_Shape < 0.5h)
                {
                    // Streak: soft all round. The stretched billboard makes it long
                    // and thin, whichever uv axis it stretches along.
                    half2 edge = (half2)(1.0 - abs(input.uv * 2.0 - 1.0));
                    coverage = smoothstep(0.0h, 0.8h, edge.x) * smoothstep(0.0h, 0.8h, edge.y);
                }
                else
                {
                    // Ring: a thin circle, as a drop's splash spreads on a surface.
                    half r = (half)length(input.uv * 2.0 - 1.0);
                    coverage = smoothstep(0.62h, 0.78h, r) * (1.0h - smoothstep(0.82h, 1.0h, r));
                }

                half alpha = coverage * input.color.a * _Opacity * _Intensity;
                return half4(input.room * input.color.rgb * alpha, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
