Shader "IMETINHUMAN/VFX/Hyperjump Streaks"
{
    // Star streaks flying past in a tunnel along the object's +Z, the look of a
    // jump to light speed. The mesh is HyperjumpTunnel.cs's: one quad per
    // streak, its place in the tunnel carried in uv2 (angle, radius, phase,
    // length). Everything moves in the vertex shader, so the mesh never
    // changes; the quads are real geometry in the room, so each eye sees them
    // with proper depth.
    Properties
    {
        _Intensity ("Intensity", Range(0, 1)) = 0
        _Speed ("Speed (tunnel lengths per second)", Float) = 0.6
        _Length ("Streak Length (m)", Float) = 3
        _Width ("Streak Width (m)", Float) = 0.012
        _Near ("Near End (m)", Float) = -2
        _Far ("Far End (m)", Float) = 40
        _Tint ("Tint", Color) = (0.75, 0.85, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+10"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Streaks"
            Tags { "LightMode" = "UniversalForward" }

            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half  _Intensity;
                float _Speed;
                float _Length;
                float _Width;
                float _Near;
                float _Far;
                half4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 corner     : TEXCOORD0;   // x across (-1..1), y along (0 tail .. 1 head)
                float4 streak     : TEXCOORD1;   // angle, radius, phase, length share
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4  color      : COLOR;
                float2 corner     : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float span = _Far - _Near;
                // Streaks run from the far end towards the viewer and wrap.
                float along = frac(input.streak.z - _Time.y * _Speed);
                float z = _Near + along * span;
                float len = _Length * input.streak.w * (0.3 + 0.7 * _Intensity);
                float2 ring = float2(cos(input.streak.x), sin(input.streak.x)) * input.streak.y;

                float3 tailOS = float3(ring, z + len);
                float3 headOS = float3(ring, z);
                float3 tailWS = TransformObjectToWorld(tailOS);
                float3 headWS = TransformObjectToWorld(headOS);
                float3 axis = headWS - tailWS;
                float3 mid = lerp(tailWS, headWS, input.corner.y);
                float3 toEye = GetCameraPositionWS() - mid;
                float3 side = normalize(cross(axis, toEye) + 1e-5);
                float3 pos = mid + side * input.corner.x * _Width;

                o.positionCS = TransformWorldToHClip(pos);
                // Bright head, fading tail; fade in from the far end and out
                // before a streak reaches the viewer.
                half ends = (half)(smoothstep(0.0, 0.15, along) * smoothstep(1.0, 0.7, along));
                o.color = half4(_Tint.rgb, _Intensity * ends * (half)(0.25 + 0.75 * input.corner.y));
                o.corner = input.corner;
                return o;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half across = (half)(1.0 - abs(input.corner.x));
                half a = input.color.a * across * across;
                if (a < 1.0h / 255.0h)
                    discard;
                return half4(input.color.rgb * a, a);
            }
            ENDHLSL
        }
    }
}
