Shader "IMETINHUMAN/VFX/Shorts Feed"
{
    // The art clips as a feed of shorts on a phone: each plays full screen in
    // portrait, and the next arrives the way a thumb brings it -- a small drag,
    // then a fling that overshoots and snaps into place, the next clip rising
    // from below as the current one leaves at the top. A thin progress bar runs
    // along the bottom of the screen, under the soft dark gradient shorts put
    // behind their captions. The phone's light falls on the room around it.
    //
    // Clips come from the same atlas as the projector: one VideoPlayer, one
    // looping square tile per clip, centre-cropped here to 9:16.
    Properties
    {
        [Header(Video)]
        [NoScaleOffset] _VideoAtlas ("Video Atlas", 2D) = "black" {}
        _AtlasGrid ("Atlas Grid (cols, rows)", Vector) = (3, 2, 0, 0)
        _VideoReady ("Video Ready", Range(0, 1)) = 0
        _VideoGain ("Video Brightness", Range(0, 2)) = 1
        _ShowTime ("Seconds Since Start (runtime)", Float) = 0

        [Header(Feed)]
        _SecondsPerSlide ("Seconds Per Clip", Range(1, 30)) = 5
        _SwapSeconds ("Swipe Seconds", Range(0.2, 2)) = 0.6
        _ScreenHalfHeight ("Screen Half Height (quad heights)", Range(0.2, 1)) = 0.8
        _ScreenCorner ("Screen Corner Radius (of screen width)", Range(0, 0.3)) = 0.12
        _CardGap ("Gap Between Clips (of screen height)", Range(0, 0.1)) = 0.015

        [Header(Phone)]
        _PhoneColor ("Phone Colour", Color) = (0.035, 0.035, 0.04, 1)
        _RimColor ("Rim Highlight", Color) = (0.55, 0.55, 0.6, 1)
        _Bezel ("Bezel (of screen width)", Range(0, 0.15)) = 0.045
        _CaptionShade ("Caption Gradient", Range(0, 1)) = 0.45
        _ProgressColor ("Progress Bar", Color) = (1, 1, 1, 0.9)

        [Header(Light)]
        _GlowSize ("Spill Size (of screen height)", Range(0.02, 1)) = 0.25
        _GlowStrength ("Spill Strength", Range(0, 2)) = 0.7
        _GlowOpacity ("Spill Opacity", Range(0, 1)) = 0.35
        _GlowEdgeColour ("Spill From Nearest Edge", Range(0, 1)) = 0.6

        [Header(Background)]
        _BackgroundColor ("Background Colour", Color) = (0.05, 0.05, 0.055, 1)
        _BackgroundOpacity ("Background Opacity", Range(0, 1)) = 0.9

        [Header(Framing)]
        _Opacity ("Opacity", Range(0, 1)) = 1
        _Alpha ("Runtime Alpha", Range(0, 1)) = 1
        _QuadFeather ("Quad Edge Feather (uv)", Range(0.001, 0.3)) = 0.08
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
            Blend One OneMinusSrcAlpha    // premultiplied

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
                float  aspect     : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_VideoAtlas); SAMPLER(sampler_VideoAtlas);

            CBUFFER_START(UnityPerMaterial)
                float4 _VideoAtlas_TexelSize;
                half4  _AtlasGrid;
                half4  _PhoneColor;
                half4  _RimColor;
                half4  _ProgressColor;
                half4  _BackgroundColor;
                half   _VideoReady;
                half   _VideoGain;
                float  _ShowTime;
                half   _SecondsPerSlide;
                half   _SwapSeconds;
                half   _ScreenHalfHeight;
                half   _ScreenCorner;
                half   _CardGap;
                half   _Bezel;
                half   _CaptionShade;
                half   _GlowSize;
                half   _GlowStrength;
                half   _GlowOpacity;
                half   _GlowEdgeColour;
                half   _BackgroundOpacity;
                half   _Opacity;
                half   _Alpha;
                half   _QuadFeather;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;

                float sx = length(float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20));
                float sy = length(float3(unity_ObjectToWorld._m01, unity_ObjectToWorld._m11, unity_ObjectToWorld._m21));
                output.aspect = sx / max(sy, 1e-4);
                return output;
            }

            // Signed distance to a rounded rectangle of half extents h, corner r.
            float RoundedRect(float2 p, float2 h, float r)
            {
                float2 q = abs(p) - (h - r);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            // One tile of the atlas at a 0..1 position and mip level; inset so
            // bilinear never reaches the neighbouring clip. Row 0 is the top of
            // the video, and uv runs bottom-up.
            half3 SampleTile(float tile, float2 local, float lod)
            {
                float2 grid = max((float2)_AtlasGrid.xy, 1.0);
                float2 cell = float2(fmod(tile, grid.x), floor(tile / grid.x));
                cell.y = grid.y - 1.0 - cell.y;
                float2 inset = _VideoAtlas_TexelSize.xy * 2.0 * exp2(lod) * grid;
                local = clamp(local, inset, 1.0 - inset);
                return SAMPLE_TEXTURE2D_LOD(_VideoAtlas, sampler_VideoAtlas, (cell + local) / grid, lod).rgb;
            }

            // How far the feed has scrolled through a swipe, 0 to 1: the thumb
            // drags a little, then flings; the clip overshoots and settles.
            float Swipe(float t)
            {
                t = saturate(t);
                const float drag = 0.2;
                if (t < drag)
                    return 0.05 * smoothstep(0.0, 1.0, t / drag);
                float u = (t - drag) / (1.0 - drag) - 1.0;
                const float c1 = 1.4;
                const float c3 = c1 + 1.0;
                float back = 1.0 + c3 * u * u * u + c1 * u * u;   // ease-out-back
                return lerp(0.05, 1.0, back);
            }

            // A clip on the screen, `rise` screen heights above its resting place.
            // Portrait: the square tile is centre-cropped to 9:16.
            half3 Clip(float tile, float2 screenUv, float rise, half3 screenColour, out bool covered)
            {
                float2 card = screenUv - float2(0.0, rise);
                covered = all(card > 0.0) && all(card < 1.0);
                if (!covered)
                    return screenColour;
                float2 local = float2(0.5 + (card.x - 0.5) * (9.0 / 16.0), card.y);
                half3 video = SampleTile(tile, local, 0.0) * _VideoGain;
                // The dark wash shorts put behind their captions.
                video *= (half)lerp(1.0 - _CaptionShade, 1.0, smoothstep(0.0, 0.28, card.y));
                return lerp(screenColour, video, saturate(_VideoReady));
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half fade = _Opacity * _Alpha;
                if (fade <= 0.002h)
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);

                // Quad space in half-heights: y -1..1, x as wide as the quad is.
                float2 p = input.uv * 2.0 - 1.0;
                p.x *= input.aspect;

                float2 screenHalf = float2(_ScreenHalfHeight * (9.0 / 16.0), _ScreenHalfHeight);
                float corner = _ScreenCorner * screenHalf.x * 2.0;
                float bezel = _Bezel * screenHalf.x * 2.0;
                float screenD = RoundedRect(p, screenHalf, corner);
                float phoneD = RoundedRect(p, screenHalf + bezel, corner + bezel);
                float aa = max(fwidth(phoneD), 1e-4);
                float2 screenUv = p / (2.0 * screenHalf) + 0.5;

                // ---- which clip, and where the feed is in its swipe ------------
                float period = max(_SecondsPerSlide, _SwapSeconds + 0.1);
                float t = max(_ShowTime, 0.0);
                float slide = floor(t / period);
                float intoSlide = t - slide * period;
                float tileCount = max(_AtlasGrid.x, 1.0) * max(_AtlasGrid.y, 1.0);
                float tileNow = fmod(slide, tileCount);
                float tileOut = fmod(slide + tileCount - 1.0, tileCount);

                // The swipe opens each clip; the first one simply appears.
                float scroll = slide < 0.5 ? 1.0 : Swipe(intoSlide / max(_SwapSeconds, 0.01));
                float pitch = 1.0 + _CardGap;   // one clip plus the gap, in screen heights

                half3 screenColour = half3(0.0h, 0.0h, 0.0h);
                bool coveredOut, coveredNow;
                screenColour = Clip(tileOut, screenUv, scroll * pitch, screenColour, coveredOut);
                screenColour = Clip(tileNow, screenUv, (scroll - 1.0) * pitch, screenColour, coveredNow);

                // Progress through the current clip, along the bottom edge.
                float barY = screenUv.y;
                float progress = saturate(intoSlide / period);
                if (barY < 0.012 && barY > 0.004)
                {
                    half filled = (half)step(screenUv.x, progress);
                    screenColour = lerp(screenColour, _ProgressColor.rgb,
                                        _ProgressColor.a * lerp(0.35h, 1.0h, filled));
                }

                // ---- light off the screen onto the room ------------------------
                float mip = log2(max(_VideoAtlas_TexelSize.w / max(_AtlasGrid.y, 1.0), 1.0));
                half3 meanColour = SampleTile(tileNow, 0.5, mip) * _VideoGain;
                float2 edgeLocal = float2(0.5 + (saturate(screenUv.x) - 0.5) * (9.0 / 16.0), saturate(screenUv.y));
                half3 edgeColour = SampleTile(tileNow, edgeLocal, max(mip - 3.0, 0.0)) * _VideoGain;
                half3 spillColour = lerp(meanColour, edgeColour, _GlowEdgeColour) * saturate(_VideoReady);

                float beyond = max(phoneD, 0.0);
                half spill = (half)(exp(-beyond / max(_GlowSize * screenHalf.y * 2.0, 1e-3)) * step(0.0, phoneD + aa));
                float2 toQuadEdge = min(input.uv, 1.0 - input.uv);
                half quadFade = (half)smoothstep(0.0, _QuadFeather, min(toQuadEdge.x, toQuadEdge.y));
                spill *= quadFade;

                // ---- composite, premultiplied ----------------------------------
                half3 colour = _BackgroundColor.rgb * _BackgroundOpacity * quadFade;
                half alpha = _BackgroundOpacity * quadFade;

                half spillA = spill * _GlowOpacity;
                colour = spillColour * spill * _GlowStrength + colour * (1.0h - spillA);
                alpha = spillA + alpha * (1.0h - spillA);

                // Phone body, with a thin lit rim at its outer edge.
                half body = (half)(1.0 - smoothstep(-aa, aa, phoneD));
                half rim = (half)(1.0 - smoothstep(0.0, aa * 2.5, -phoneD));
                half3 phone = lerp(_PhoneColor.rgb, _RimColor.rgb, rim * 0.6h);
                colour = lerp(colour, phone, body);
                alpha = lerp(alpha, 1.0h, body);

                // Screen.
                half screen = (half)(1.0 - smoothstep(-aa, aa, screenD));
                colour = lerp(colour, screenColour, screen);

                return half4(colour * fade, alpha * fade);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
