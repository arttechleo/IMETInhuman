Shader "IMETINHUMAN/VFX/Kaleidoscope Overlay"
{
    // Video-first kaleidoscope: a flower of large petals, each one a window
    // playing one clip from the atlas, upright on screen so the footage stays
    // readable. A dimmer ring of petals sits behind, offset half a step, showing
    // other clips for depth.
    //
    // The kaleidoscope lives in the transitions. Every few seconds each petal's
    // footage folds into a mirrored kaleidoscope, swaps to the next clip at the
    // peak of the fold, and unfolds again, while the petal closes slightly and
    // re-opens and the flower turns one petal-step. The swaps ripple around the
    // flower. Between transitions the petals only breathe.
    //
    // Nothing decorative is drawn: a flat background, a thin outline per petal,
    // and a soft round falloff.
    Properties
    {
        [Header(Video)]
        [NoScaleOffset] _VideoAtlas ("Video Atlas", 2D) = "black" {}
        _AtlasGrid ("Atlas Grid (cols, rows)", Vector) = (3, 2, 0, 0)

        [Header(Room From Headset Camera)]
        _RoomFront ("Room In Front Petals", Range(0, 1)) = 1
        _RoomBack ("Room In Back Ring", Range(0, 1)) = 0
        _RoomFold ("Room Mirroring", Range(0, 1)) = 1
        _VideoReady ("Video Ready", Range(0, 1)) = 0
        _VideoGain ("Video Brightness", Range(0, 2)) = 1

        [Header(Petals)]
        _Segments ("Petal Count", Range(3, 12)) = 6
        _PetalInner ("Petal Inner Radius", Range(0, 0.6)) = 0.14
        _PetalOuter ("Petal Outer Radius", Range(0.4, 1.2)) = 0.95
        _PetalWidth ("Petal Width", Range(0.3, 1.2)) = 0.95
        _PetalBase ("Petal Base Radius", Range(0.01, 0.2)) = 0.07
        _Breathe ("Breathing", Range(0, 0.1)) = 0.025
        _RotationSpeed ("Rotation Speed", Range(-1, 1)) = 0.03

        [Header(Back Petals)]
        _BackOpacity ("Back Petal Opacity", Range(0, 1)) = 0.55
        _BackLength ("Back Petal Length", Range(0.8, 1.4)) = 1.08
        _BackWidth ("Back Petal Width", Range(0.3, 1.2)) = 0.8
        _BackDim ("Back Petal Brightness", Range(0, 1)) = 0.5

        [Header(Transitions)]
        _SecondsPerArt ("Seconds Per Clip", Range(2, 30)) = 7
        _TransitionTime ("Transition Seconds", Range(0.2, 4)) = 1.8
        _TransitionWave ("Ripple Delay Per Petal", Range(0, 0.4)) = 0.09
        _FoldSegments ("Fold Mirrors", Range(3, 12)) = 6
        _FoldAmount ("Fold Strength", Range(0, 1)) = 1
        _BloomClose ("Petal Close On Swap", Range(0, 0.3)) = 0.07
        _StepTurn ("Turn Per Swap (petals)", Range(0, 1)) = 1

        [Header(Lines and Background)]
        _LineColor ("Line Color", Color) = (0.86, 0.83, 0.77, 1)
        _LineOpacity ("Line Opacity", Range(0, 1)) = 0.6
        _LineWidth ("Line Width (px)", Range(0, 6)) = 1.5
        _BackgroundColor ("Background Color", Color) = (0.05, 0.05, 0.055, 1)
        _BackgroundOpacity ("Background Opacity", Range(0, 1)) = 0.9

        [Header(Framing)]
        _Opacity ("Opacity", Range(0, 1)) = 1
        _Alpha ("Runtime Alpha", Range(0, 1)) = 1
        _Feather ("Edge Feather", Range(0.01, 0.6)) = 0.18
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Include/IMH_Noise.hlsl"

            // On the headset the petals can look into the real room, through the
            // colour camera (PassthroughCameraFeed), folded by the same mirrors.
            #pragma shader_feature_local_fragment _PASSTHROUGH_CAMERA
            #if defined(_PASSTHROUGH_CAMERA)
                #include "Include/IMH_PassthroughCamera.hlsl"
            #endif

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
                half   aspect     : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            TEXTURE2D(_VideoAtlas); SAMPLER(sampler_VideoAtlas);

            CBUFFER_START(UnityPerMaterial)
                float4 _VideoAtlas_TexelSize;
                half4 _AtlasGrid;
                half4 _LineColor;
                half4 _BackgroundColor;
                half  _VideoReady;
                half  _VideoGain;
                half  _Segments;
                half  _PetalInner;
                half  _PetalOuter;
                half  _PetalWidth;
                half  _PetalBase;
                half  _Breathe;
                half  _RotationSpeed;
                half  _BackOpacity;
                half  _BackLength;
                half  _BackWidth;
                half  _BackDim;
                half  _SecondsPerArt;
                half  _TransitionTime;
                half  _TransitionWave;
                half  _FoldSegments;
                half  _FoldAmount;
                half  _BloomClose;
                half  _StepTurn;
                half  _LineOpacity;
                half  _LineWidth;
                half  _BackgroundOpacity;
                half  _Opacity;
                half  _Alpha;
                half  _Feather;
                half  _RoomFront;
                half  _RoomBack;
                half  _RoomFold;
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

                // Aspect from the quad's own world scale. Each eye has its own
                // viewport under Single Pass Instanced, so _ScreenParams would give
                // the two eyes different shapes.
                float sx = length(float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20));
                float sy = length(float3(unity_ObjectToWorld._m01, unity_ObjectToWorld._m11, unity_ObjectToWorld._m21));
                output.aspect = half(sx / max(sy, 1e-4));
                return output;
            }

            // 1 on a line of the given pixel width, 0 off it, antialiased.
            float LineMask(float distancePx, float widthPx)
            {
                return 1.0 - smoothstep(widthPx * 0.5 - 0.5, widthPx * 0.5 + 0.5, distancePx);
            }

            // Signed distance to a petal: a narrow round base (radius r1 at the
            // origin) widening to a broad round tip (radius r2, h along +y). Every
            // part of the outline is an arc or a tangent line, so there is no
            // corner anywhere to read as a point.
            float PetalDistance(float2 p, float r1, float r2, float h)
            {
                p.x = abs(p.x);
                float b = (r1 - r2) / h;
                float a = sqrt(saturate(1.0 - b * b));
                float k = dot(p, float2(-b, a));
                if (k < 0.0)
                    return length(p) - r1;
                if (k > a * h)
                    return length(p - float2(0.0, h)) - r2;
                return dot(p, float2(a, b)) - r1;
            }

            struct Petal
            {
                float distance;   // uv units, negative inside
                float index;      // 0 .. count-1
                float2 centre;    // midpoint of the petal, uv
                float halfLength; // half the petal's length, uv
            };

            // Nearest petal of a ring whose petal 0 points along angle `offset`.
            // Tests the pixel's own wedge and its near neighbour: a rounded base
            // can lean across the wedge line, and one wedge alone would cut it
            // straight.
            Petal NearestPetal(float2 uv, float count, float offset,
                               float innerR, float outerR, float width, float baseR)
            {
                float petalAngle = IMH_TAU / count;
                float rel = (atan2(uv.y, uv.x) - offset) / petalAngle;
                float wedge = floor(rel + 0.5);
                float neighbour = wedge + (rel - wedge >= 0.0 ? 1.0 : -1.0);

                // Tip as wide as the wedge allows at its radius, times width.
                float s = sin(petalAngle * 0.5) * width;
                float tipR = outerR * s / (1.0 + s);
                float r1 = min(baseR, tipR);
                float baseC = innerR + r1;
                float tipC = max(outerR - tipR, baseC + 1e-3);

                Petal best;
                best.distance = 1e5;
                best.index = 0.0;
                best.centre = 0.0;
                best.halfLength = 0.5 * (outerR - innerR);

                [unroll]
                for (int i = 0; i < 2; i++)
                {
                    float w = i == 0 ? wedge : neighbour;
                    float theta = offset + w * petalAngle;
                    float2 axis = float2(cos(theta), sin(theta));
                    float2 side = float2(-axis.y, axis.x);
                    float2 d = uv - axis * baseC;
                    float dist = PetalDistance(float2(dot(d, side), dot(d, axis)), r1, tipR, tipC - baseC);
                    if (dist < best.distance)
                    {
                        best.distance = dist;
                        best.index = w - count * floor(w / count);
                        best.centre = axis * (0.5 * (innerR + outerR));
                    }
                }
                return best;
            }

            // One tile of the atlas at a 0..1 position inside it, inset a couple of
            // texels so bilinear filtering never drags in the neighbouring clip.
            half3 SampleTile(float tile, float2 local)
            {
                float2 grid = max((float2)_AtlasGrid.xy, 1.0);
                float2 tileXy = float2(fmod(tile, grid.x), floor(tile / grid.x));
                float2 inset = 2.0 * grid * _VideoAtlas_TexelSize.xy;
                local = lerp(inset, 1.0 - inset, saturate(local));
                return SAMPLE_TEXTURE2D(_VideoAtlas, sampler_VideoAtlas, (tileXy + local) / grid).rgb;
            }

            // Mirror p into one wedge of an n-fold kaleidoscope, with the wedge
            // turned to point up so it reads the middle of the frame, where the
            // faces and hands are.
            float2 KaleidoFold(float2 p, float n, float turn)
            {
                float seg = IMH_TAU / n;
                float a = atan2(p.y, p.x) + turn;
                a = a - seg * floor(a / seg);
                a = abs(a - seg * 0.5) + IMH_PI * 0.5 - seg * 0.25;
                return float2(cos(a), sin(a)) * length(p);
            }

            // Footage for one petal: the clip it is on, folded into a kaleidoscope
            // and swapped to the next clip around each hand-over. `offset` is the
            // pixel relative to the petal centre in screen orientation, `scale`
            // the half-size of the square the tile maps onto.
            // The room through one petal: the petal-local point is mapped back to
            // where it sits on the overlay, then out along the eye ray into the
            // room. Unfolded, a petal is a clear window onto what is behind it;
            // folded, the same mirrors that fold the art fold the room.
            half3 PetalRoom(float2 q, float2 centre, float spin, float aspect, out bool valid)
            {
                valid = false;
                #if defined(_PASSTHROUGH_CAMERA)
                    // Inverse of frag's uv = Rotate(screen, -spin) and
                    // offset = Rotate(uv - centre, spin).
                    float2 screen = IMH_Rotate2D(centre + IMH_Rotate2D(q, -spin), spin);
                    float2 quadUv = float2(screen.x / max(aspect, 1e-4), screen.y) * 0.5 + 0.5;
                    float3 positionWS = TransformObjectToWorld(float3(quadUv - 0.5, 0.0));
                    float3 eye = GetCameraPositionWS();
                    return IMH_PtSampleRay(eye, normalize(positionWS - eye), valid);
                #else
                    return half3(0.0h, 0.0h, 0.0h);
                #endif
            }

            half3 PetalVideo(float petal, float tileShift, float2 offset, float scale,
                             float localTime, float period, float2 centre, float spin,
                             float aspect, half roomMix, out float fold)
            {
                float tileCount = max(_AtlasGrid.x, 1.0) * max(_AtlasGrid.y, 1.0);

                // Signed seconds from this petal's nearest hand-over.
                float cycle = localTime / period;
                float fromSwap = (cycle - round(cycle)) * period;
                float halfT = max(_TransitionTime * 0.5, 0.05);
                fold = (1.0 - smoothstep(0.0, halfT, abs(fromSwap))) * _FoldAmount;

                // Fold and zoom in together; the fold turns as it forms so the
                // mirrors visibly sweep rather than just appear.
                float2 folded = KaleidoFold(offset, max(round(_FoldSegments), 3.0), fromSwap * 1.4);
                float2 q = lerp(offset, folded * 0.7, fold);
                float2 local = q / (2.0 * scale) + 0.5;

                // The clip changes only inside a short window at the fold's peak,
                // where the footage is most abstract, so the cut never reads.
                float swapHalf = halfT * 0.3;
                float before = floor((localTime - swapHalf) / period);
                float after  = floor((localTime + swapHalf) / period);
                float mixNext = smoothstep(-swapHalf, swapHalf, fromSwap);

                // Wrapped with floor, not fmod: `before` is negative for the first
                // moments after start, and fmod keeps the sign.
                float tileA = petal + tileShift + before;
                float tileB = petal + tileShift + after;
                tileA -= tileCount * floor(tileA / tileCount);
                tileB -= tileCount * floor(tileB / tileCount);

                half3 video = SampleTile(tileA, local);
                if (after != before)
                    video = lerp(video, SampleTile(tileB, local), (half)mixNext);

                #if defined(_PASSTHROUGH_CAMERA)
                    half room = roomMix * (half)_PtReady;
                    if (room > 0.001h)
                    {
                        // No zoom on the room: unfolded it must line up with the
                        // passthrough around the petal.
                        float roomFold = max(fold, _RoomFold);
                        bool valid;
                        half3 roomColour = PetalRoom(lerp(offset, folded, roomFold), centre, spin, aspect, valid);
                        video = lerp(video, roomColour, valid ? room : 0.0h);
                    }
                #endif
                return video;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Uniform across the draw, so this skips a full screen of video
                // sampling while the effect is faded out.
                half fade = _Opacity * _Alpha;
                if (fade <= 0.002h)
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);

                float2 screenUv = input.uv * 2.0 - 1.0;
                screenUv.x *= input.aspect;

                float pxPerUv = 1.0 / max(max(fwidth(screenUv.x), fwidth(screenUv.y)), 1e-6);

                float time = _Time.y;
                float period = max(_SecondsPerArt, max(_TransitionTime, 0.2) + 0.5);
                float count = max(round(_Segments), 3.0);
                float petalAngle = IMH_TAU / count;

                // Whole-flower motion: slow drift, plus one eased petal-step per
                // hand-over, centred on the swap.
                float cycle = time / period;
                float fromSwap = (cycle - round(cycle)) * period;
                float halfT = max(_TransitionTime * 0.5, 0.05);
                float steps = round(cycle) - 1.0 + smoothstep(-halfT, halfT, fromSwap);
                float spin = time * _RotationSpeed + steps * petalAngle * _StepTurn;
                float2 uv = IMH_Rotate2D(screenUv, -spin);
                float radius = length(uv);

                // Slow breathing, out of phase with nothing in particular.
                float breathe = 1.0 + _Breathe * sin(time * 0.55);

                float innerR = _PetalInner;
                float outerR = max(_PetalOuter, innerR + 0.1) * breathe;

                // ---- back ring --------------------------------------------------
                Petal back = NearestPetal(uv, count, petalAngle * 0.5,
                                          innerR, outerR * _BackLength, _BackWidth, _PetalBase);
                float backDelay = (back.index + 0.5) * _TransitionWave;
                float backFold;
                half3 backVideo = PetalVideo(back.index, floor(count * 0.5),
                                             IMH_Rotate2D(uv - back.centre, spin),
                                             back.halfLength, time - backDelay, period,
                                             back.centre, spin, input.aspect, _RoomBack, backFold);
                half backLuma = dot(backVideo, half3(0.2126h, 0.7152h, 0.0722h));
                backVideo = lerp(half3(backLuma, backLuma, backLuma), backVideo, 0.7h) * _BackDim * _VideoGain;

                float backPx = back.distance * pxPerUv;
                half backShape = (half)(1.0 - smoothstep(-0.5, 0.5, backPx));

                // ---- front ring -------------------------------------------------
                // The petal closes a little as its footage folds, and re-opens as
                // it unfolds -- the bloom that carries each hand-over. Measured
                // in a first pass, then the petal is rebuilt at that size.
                Petal front = NearestPetal(uv, count, 0.0, innerR, outerR, _PetalWidth, _PetalBase);
                float frontDelay = front.index * _TransitionWave;
                float frontLocal = time - frontDelay;
                float frontCycle = frontLocal / period;
                float frontFromSwap = (frontCycle - round(frontCycle)) * period;
                float closing = (1.0 - smoothstep(0.0, halfT, abs(frontFromSwap))) * _BloomClose;

                float frontOuter = lerp(outerR, innerR + 0.1, closing);
                front = NearestPetal(uv, count, 0.0, innerR, frontOuter, _PetalWidth, _PetalBase);

                float frontFold;
                half3 frontVideo = PetalVideo(front.index, 0.0,
                                              IMH_Rotate2D(uv - front.centre, spin),
                                              front.halfLength, frontLocal, period,
                                              front.centre, spin, input.aspect, _RoomFront, frontFold)
                                 * _VideoGain;

                float frontPx = front.distance * pxPerUv;
                half frontShape = (half)(1.0 - smoothstep(-0.5, 0.5, frontPx));

                // ---- composite, back to front, premultiplied -----------------
                half ready = saturate(_VideoReady);

                half backdrop = _BackgroundOpacity
                              * (1.0h - (half)smoothstep(outerR, outerR + _Feather, radius));
                half3 color = _BackgroundColor.rgb * backdrop;
                half alpha = backdrop;

                half backA = backShape * ready * _BackOpacity;
                color = backVideo * backA + color * (1.0h - backA);
                alpha = backA + alpha * (1.0h - backA);

                half backLine = (half)(LineMask(abs(backPx), _LineWidth) * _LineOpacity * 0.5);
                color = _LineColor.rgb * backLine + color * (1.0h - backLine);
                alpha = backLine + alpha * (1.0h - backLine);

                half frontA = frontShape * ready;
                color = frontVideo * frontA + color * (1.0h - frontA);
                alpha = frontA + alpha * (1.0h - frontA);

                half frontLine = (half)(LineMask(abs(frontPx), _LineWidth) * _LineOpacity);
                color = _LineColor.rgb * frontLine + color * (1.0h - frontLine);
                alpha = frontLine + alpha * (1.0h - frontLine);

                alpha *= fade * input.color.a;
                color *= fade * input.color.a;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
