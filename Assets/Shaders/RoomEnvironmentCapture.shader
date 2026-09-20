Shader "Hidden/IMETINHUMAN/Room Environment Capture"
{
    // Paints what the headset camera sees now into the room panorama.
    //
    // Each texel of the panorama is a direction from the head. It is taken out
    // to the assumed room distance and looked up in the camera image; where the
    // camera sees it, the texel is blended toward that colour, softly at the
    // camera image's edges so seams never show. Everything else keeps what was
    // captured when the viewer last looked that way.
    Properties
    {
        _CaptureRate ("Blend Per Update", Range(0, 1)) = 0.35
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Capture"
            ZTest Always
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/IMH_PassthroughCamera.hlsl"

            float3 _EnvOrigin;
            float  _CaptureRate;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            // Full-screen triangle; its uv already accounts for render targets
            // that start at the top, so uv (0, 0) is where sampling finds it.
            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 direction = IMH_RoomUvToDirection(input.uv);

                // Virtual things first: the camera cannot see them, and without
                // them here no drop could reflect them, and fog would hide them.
                half4 virtualHit = IMH_VirtualAt(_EnvOrigin + direction);
                if (virtualHit.a > 0.01h)
                    return half4(virtualHit.rgb, virtualHit.a * 0.8h);

                bool valid;
                float2 uv = IMH_PtWorldToUv(_EnvOrigin + direction * _PtDepth, valid);
                if (!valid)
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);

                float2 toEdge = min(uv, 1.0 - uv);
                half weight = (half)smoothstep(0.0, 0.12, min(toEdge.x, toEdge.y));
                return half4(IMH_PtSample(uv), weight * (half)(_CaptureRate * _PtReady));
            }
            ENDHLSL
        }
    }

    Fallback Off
}
