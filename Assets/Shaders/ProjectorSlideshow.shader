Shader "IMETINHUMAN/VFX/Projector Slideshow"
{
    // The six art clips shown one after another, like slides in a projector.
    //
    // A rounded-square gate plays one clip, under a cream frame like a slide's
    // mount. Its light spills past the frame as a soft tint in the picture's own
    // colours -- taken from the video's mip chain, so the whole glow costs two
    // texture reads. Every few seconds the cartridge changes: the slide lifts up
    // out of the gate with the mount's dark edge sweeping across it, the empty
    // gate flashes with bare lamp light, and the next slide drops in from the
    // top and settles with a small bounce while the lamp flickers.
    //
    // Clips come from the same atlas the kaleidoscope used: one VideoPlayer, a
    // grid of looping tiles, one tile per slide.
    Properties
    {
        [Header(Video)]
        [NoScaleOffset] _VideoAtlas ("Video Atlas", 2D) = "black" {}
        _AtlasGrid ("Atlas Grid (cols, rows)", Vector) = (3, 2, 0, 0)
        _VideoReady ("Video Ready", Range(0, 1)) = 0
        _VideoGain ("Video Brightness", Range(0, 2)) = 1
        _ShowTime ("Seconds Since Start (runtime)", Float) = 0

        [Header(Slides)]
        _SecondsPerSlide ("Seconds Per Slide", Range(1, 30)) = 5
        _SwapSeconds ("Cartridge Swap Seconds", Range(0.2, 3)) = 0.9
        _GateSize ("Gate Half Size (quad heights)", Range(0.1, 1)) = 0.68
        _CornerRadius ("Corner Radius (of gate)", Range(0, 0.5)) = 0.1

        [Header(Frame)]
        _FrameColor ("Frame Colour", Color) = (0.9, 0.87, 0.8, 1)
        _FrameWidth ("Frame Width (of gate)", Range(0, 0.3)) = 0.07
        _FrameOverlap ("Frame Over Picture (of gate)", Range(0, 0.1)) = 0.02
        _MountShadow ("Slide Edge Shadow", Range(0, 1)) = 0.85

        [Header(Light)]
        _LampColor ("Lamp Colour", Color) = (1, 0.93, 0.8, 1)
        _LampBrightness ("Empty Gate Brightness", Range(0, 2)) = 0.9
        _GlowSize ("Spill Size (of gate)", Range(0.02, 1)) = 0.3
        _GlowStrength ("Spill Strength", Range(0, 2)) = 0.9
        _GlowOpacity ("Spill Opacity", Range(0, 1)) = 0.45
        _GlowEdgeColour ("Spill From Nearest Edge", Range(0, 1)) = 0.6
        _Flicker ("Lamp Flicker", Range(0, 0.2)) = 0.025
        _SwapFlicker ("Flicker During Swap", Range(0, 0.6)) = 0.2
        _Hotspot ("Centre Hotspot", Range(0, 0.5)) = 0.08

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
            #include "Include/IMH_Noise.hlsl"

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
                half4  _FrameColor;
                half4  _LampColor;
                half4  _BackgroundColor;
                half   _VideoReady;
                half   _VideoGain;
                float  _ShowTime;
                half   _SecondsPerSlide;
                half   _SwapSeconds;
                half   _GateSize;
                half   _CornerRadius;
                half   _FrameWidth;
                half   _FrameOverlap;
                half   _MountShadow;
                half   _LampBrightness;
                half   _GlowSize;
                half   _GlowStrength;
                half   _GlowOpacity;
                half   _GlowEdgeColour;
                half   _Flicker;
                half   _SwapFlicker;
                half   _Hotspot;
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

                // From the quad's own scale: under Single Pass Instanced each eye
                // has its own viewport, so _ScreenParams would disagree per eye.
                float sx = length(float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20));
                float sy = length(float3(unity_ObjectToWorld._m01, unity_ObjectToWorld._m11, unity_ObjectToWorld._m21));
                output.aspect = sx / max(sy, 1e-4);
                return output;
            }

            // Signed distance to a rounded square of half size h and corner r.
            float RoundedSquare(float2 p, float h, float r)
            {
                float2 q = abs(p) - (h - r);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            // One tile of the atlas at a 0..1 position, at a given mip level. Inset
            // so bilinear never pulls in the neighbouring clip.
            half3 SampleTile(float tile, float2 local, float lod)
            {
                float2 grid = max((float2)_AtlasGrid.xy, 1.0);
                float2 cell = float2(fmod(tile, grid.x), floor(tile / grid.x));
                // Atlas row 0 is the top of the video; uv runs bottom-up.
                cell.y = grid.y - 1.0 - cell.y;
                float2 inset = _VideoAtlas_TexelSize.xy * 2.0 * exp2(lod) * grid;
                local = clamp(local, inset, 1.0 - inset);
                return SAMPLE_TEXTURE2D_LOD(_VideoAtlas, sampler_VideoAtlas, (cell + local) / grid, lod).rgb;
            }

            // One slide in the gate, raised by `offsetY`: its picture where it
            // covers the gate, and just past its picture the mount blocking the
            // lamp -- the dark band that sweeps across during a swap.
            void SlideAt(float tile, float offsetY, float2 gateLocal, float h, half3 lamp,
                         half ready, inout half3 gateColour)
            {
                float2 local = gateLocal - float2(0.0, offsetY / (2.0 * h));
                float2 edge = min(local, 1.0 - local);
                float inside = min(edge.x, edge.y);
                if (inside > 0.0)
                    gateColour = lerp(lamp, SampleTile(tile, local, 0.0) * _VideoGain, ready);
                else if (inside > -0.06 && local.x > -0.06 && local.x < 1.06)
                    gateColour *= 1.0h - _MountShadow;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half fade = _Opacity * _Alpha;
                if (fade <= 0.002h)
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);

                // Quad space in half-heights: y runs -1..1, x as wide as the quad is.
                float2 p = input.uv * 2.0 - 1.0;
                p.x *= input.aspect;

                float h = _GateSize;
                float gateD = RoundedSquare(p, h, _CornerRadius * h);
                float aa = max(fwidth(gateD), 1e-4);

                // ---- which slide, and where it is in its swap --------------------
                float period = max(_SecondsPerSlide, _SwapSeconds + 0.1);
                float t = max(_ShowTime, 0.0);
                float slide = floor(t / period);
                float intoSlide = t - slide * period;
                float swap = saturate(intoSlide / max(_SwapSeconds, 0.01));   // 1 once settled
                float swapping = 1.0 - step(1.0, swap);

                float tileCount = max(_AtlasGrid.x, 1.0) * max(_AtlasGrid.y, 1.0);
                float tileNow = fmod(slide, tileCount);
                float tileOut = fmod(slide + tileCount - 1.0, tileCount);

                // Swap in three beats: the old slide lifts out (0 - 0.35), the gate
                // stands empty in lamp light (0.35 - 0.5), the new one drops in and
                // bounces (0.5 - 1). The very first slide has nothing to lift.
                float travel = 2.0 * h * 1.08;        // clear of the gate, mount included
                float liftT = saturate(swap / 0.35);
                float outY = liftT * liftT * travel;  // eases up and away
                float dropT = saturate((swap - 0.5) / 0.5);
                float inY = dropT < 0.55
                          ? (1.0 - (dropT / 0.55) * (dropT / 0.55)) * travel
                          : sin((dropT - 0.55) / 0.45 * 3.14159 * 2.0) * (1.0 - dropT) * 0.05 * h;
                bool firstSlide = slide < 0.5;

                // ---- the gate: picture, mount edges, empty lamp light --------------
                half3 lamp = _LampColor.rgb * _LampBrightness;
                half3 gateColour = lamp;
                float2 gateLocal = p / (2.0 * h) + 0.5;
                half ready = saturate(_VideoReady);

                if (swapping < 0.5)
                {
                    SlideAt(tileNow, 0.0, gateLocal, h, lamp, ready, gateColour);
                }
                else
                {
                    if (!firstSlide && swap < 0.35)
                        SlideAt(tileOut, outY, gateLocal, h, lamp, ready, gateColour);
                    if (swap >= 0.5)
                        SlideAt(tileNow, inY, gateLocal, h, lamp, ready, gateColour);
                }

                // The lamp is not perfectly steady, and jolts as the tray moves.
                float flickerNoise = IMH_Noise21(float2(t * 23.0, 3.7)) - 0.5;
                float click = swapping * (1.0 - abs(swap * 2.0 - 1.0));
                half flicker = (half)(1.0 + flickerNoise * 2.0 * (_Flicker + _SwapFlicker * click));
                // A projector is brighter at the centre of its throw.
                half hotspot = (half)(1.0 + _Hotspot * (1.0 - saturate(length(p) / (h * 1.4))) - _Hotspot * 0.5);
                gateColour *= flicker * hotspot;

                // ---- spill: the picture's light past the frame --------------------
                // Its colours from the mip chain: the whole slide's average, and the
                // nearest stretch of its edge, so the spill takes on what is at that
                // side of the picture. Empty gate: bare lamp.
                float mipMean = log2(max(_VideoAtlas_TexelSize.w / max(_AtlasGrid.y, 1.0), 1.0));
                half3 meanColour = SampleTile(tileNow, 0.5, mipMean) * _VideoGain;
                half3 edgeColour = SampleTile(tileNow, clamp(gateLocal, 0.0, 1.0), max(mipMean - 3.0, 0.0)) * _VideoGain;
                half3 slideLight = lerp(meanColour, edgeColour, _GlowEdgeColour);
                half lampShare = (half)(swapping * smoothstep(0.2, 0.45, swap) * (1.0 - smoothstep(0.6, 0.9, swap)));
                half3 spillColour = lerp(lerp(lamp, slideLight, ready), lamp, lampShare) * flicker;

                float frameOuter = _FrameWidth * h;
                float beyond = max(gateD - frameOuter, 0.0);
                half spill = (half)(exp(-beyond / max(_GlowSize * h, 1e-3)) * step(0.0, gateD - frameOuter + aa));

                // The quad itself must never show an edge.
                float2 toQuadEdge = min(input.uv, 1.0 - input.uv);
                spill *= (half)smoothstep(0.0, _QuadFeather, min(toQuadEdge.x, toQuadEdge.y));

                // ---- composite, back to front, premultiplied ----------------------
                half3 colour = _BackgroundColor.rgb * _BackgroundOpacity;
                half alpha = _BackgroundOpacity;
                colour *= (half)smoothstep(0.0, _QuadFeather, min(toQuadEdge.x, toQuadEdge.y));
                alpha *= (half)smoothstep(0.0, _QuadFeather, min(toQuadEdge.x, toQuadEdge.y));

                // Light adds; its opacity only tints what is behind a little.
                half spillA = spill * _GlowOpacity;
                colour = spillColour * spill * _GlowStrength + colour * (1.0h - spillA);
                alpha = spillA + alpha * (1.0h - spillA);

                // Picture.
                half gateCover = (half)(1.0 - smoothstep(-aa, aa, gateD));
                colour = lerp(colour, gateColour, gateCover);
                alpha = lerp(alpha, 1.0h, gateCover);

                // Frame over the picture's edge, lit from the gate side so it reads
                // as a raised mount rather than a flat line.
                float frameInner = -_FrameOverlap * h;
                half frameCover = (half)((1.0 - smoothstep(frameOuter - aa, frameOuter + aa, gateD))
                                       * smoothstep(frameInner - aa, frameInner + aa, gateD));
                half bevel = (half)saturate((gateD - frameInner) / max(frameOuter - frameInner, 1e-4));
                half3 frameColour = _FrameColor.rgb * lerp(1.08h, 0.82h, bevel) * lerp(1.0h, flicker, 0.5h);
                colour = lerp(colour, frameColour, frameCover);
                alpha = lerp(alpha, 1.0h, frameCover);

                return half4(colour * fade, alpha * fade);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
