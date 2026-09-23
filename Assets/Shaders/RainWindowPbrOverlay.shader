Shader "IMETINHUMAN/VFX/Rain Window PBR Overlay"
{
    // Water on glass, traced rather than faked.
    //
    // Each drop is a spherical cap and the view ray is put through a real
    // refract() at the air-water interface. At a 74 degree contact angle the
    // normal tilts by nearly that much at the contact line, so a drop images a
    // wide solid angle, inverted -- which is why drops over dark ground carry
    // bright sky and drops over bright sky go dark.
    //
    // No Jacobian gain is applied to that sample. Radiance is invariant along a
    // ray, so the background radiance in the refracted direction is already the
    // answer. An inverse-determinant "caustic" factor belongs to irradiance
    // landing on a receiving surface, not to a view ray, and applying one here
    // drives the whole drop to black.
    Properties
    {
        [Header(Water Physics)]
        _IOR ("Index of Refraction", Range(1.0, 1.8)) = 1.333
        _ContactAngle ("Contact Angle (deg)", Range(10, 110)) = 74
        _Smoothness ("Surface Smoothness", Range(0, 1)) = 0.97
        _Thickness ("Column Thickness Scale", Range(0, 4)) = 1.0

        [Header(Static Beads)]
        _DropDensity ("Bead Density", Range(4, 60)) = 36
        _SizeBias ("Small Drop Bias", Range(0.25, 6)) = 2.0
        _EdgeSoftness ("Edge Antialias", Range(0.001, 0.3)) = 0.05
        _NormalStrength ("Normal Strength", Range(0, 4)) = 1.0
        [Toggle(_DROPS_LOW)] _DropsLow ("Reduced Layers (2 not 3)", Float) = 0

        [Header(Runners)]
        _RunnerColumns ("Runner Columns", Range(1, 40)) = 11
        _RunnerSpeed ("Runner Speed", Range(0, 2)) = 0.11
        _RunnerSize ("Runner Start Size", Range(0.002, 0.08)) = 0.012
        _Accretion ("Growth While Falling", Range(0, 6)) = 2.2
        _TrailStrength ("Wet Track Strength", Range(0, 2)) = 0.6

        [Header(Refraction)]
        [Toggle(_REFRACTION)] _RefractionEnabled ("Refract Scene", Float) = 1
        _RefractionScale ("Refraction Scale", Range(0, 3)) = 1.0
        _MaxTravel ("Max Background Distance (m)", Range(0.05, 20)) = 4.0
        _MaxDeflection ("Max Screen Deflection", Range(0.01, 1)) = 0.35
        [Toggle(_DISPERSION)] _DispersionEnabled ("Chromatic Dispersion (3 taps)", Float) = 1
        _Dispersion ("Dispersion Amount", Range(0, 4)) = 1.0

        [Header(Condensation)]
        _FogCoverage ("Fogged Fraction Of Pane", Range(0, 1)) = 0
        _FogOpacity ("Fog Opacity", Range(0, 1)) = 0.92
        _FogBlur ("Fog Blur (screen uv)", Range(0, 0.05)) = 0.014
        _FogRoomBlur ("Fog Blur On Headset (panorama mips)", Range(0, 8)) = 3.5
        _FogMilk ("Fog Milkiness", Range(0, 1)) = 0.38
        _FogColor ("Fog Colour", Color) = (0.86, 0.89, 0.92, 1)
        _FogEdge ("Fog Front Softness", Range(0.002, 0.2)) = 0.035
        _FogClearByDrops ("Drops Clear Fog", Range(0, 1)) = 1

        [Header(Airplane Window)]
        [Toggle(_WINDOW_FRAME)] _WindowFrameEnabled ("Window Aperture", Float) = 0
        _FrameColor ("Frame Colour", Color) = (0.34, 0.34, 0.36, 1)
        _WindowWidth ("Aperture Width", Range(0.2, 1.2)) = 0.82
        _WindowHeight ("Aperture Height", Range(0.2, 1.2)) = 0.95
        _WindowRound ("Corner Roundness", Range(2, 8)) = 2.6
        _FrameSoftness ("Frame Edge Softness", Range(0.001, 0.2)) = 0.012

        [Header(Stylisation)]
        _BaseTint ("Water Tint", Color) = (0.72, 0.88, 1.0, 1.0)
        _Stylize ("Tint Amount", Range(0, 1)) = 0.1
        _EnvReflection ("Environment Reflection", Range(0, 2)) = 1.0
        _AmbientScatter ("Ambient Scatter at Contact Line", Range(0, 2)) = 0.35
        _Opacity ("Opacity", Range(0, 1)) = 1.0
        _EdgeFade ("Pane Edge Fade (pane heights)", Range(0, 0.3)) = 0

        [Header(Surface)]
        _PaneSize ("Pane Size In Metres (0 = from the quad's scale)", Vector) = (0, 0, 0, 0)
        _CellAspect ("Drop Grid Width Scale", Float) = 1
        _TopFade ("Top Edge Fade (uv)", Range(0, 0.5)) = 0
        _Horizontal ("Horizontal Glass (no runners, round beads)", Range(0, 1)) = 0

        [Header(The Pane Itself)]
        _GlassPresence ("Glass", Range(0, 1)) = 1
        _GlassTint ("Glass Tint", Color) = (0.78, 0.89, 0.92, 1)
        _GlassReflect ("Glass Reflection", Range(0, 2)) = 1
        _GlassSmudge ("Smudges And Dust", Range(0, 1)) = 0.35
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("Depth Test", Float) = 8
        _Alpha ("Runtime Alpha", Range(0, 1)) = 1
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
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend One OneMinusSrcAlpha    // premultiplied
            ZWrite Off
            // Always for a pane pinned to the eye; LessEqual for the booth, so real
            // things nearer than the glass are not painted over.
            ZTest [_ZTest]
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #pragma multi_compile_instancing

            // The pane is lit by the scene, so it opts into the same light and
            // probe paths a Lit surface uses. PC_Renderer runs Forward+
            // (m_RenderingMode: 2), where the additional-light loop is clustered --
            // without _CLUSTER_LIGHT_LOOP the loop iterates nothing and every point
            // and spot light in the scene is silently ignored.
            //
            // No shadow variants: the pane is pinned to the eye, so a shadow on it
            // would only be the room's shadow map smeared across the view. Its
            // renderer does not receive shadows either.
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"

            #pragma shader_feature_local_fragment _REFRACTION
            #pragma shader_feature_local_fragment _DISPERSION
            #pragma shader_feature_local_fragment _WINDOW_FRAME
            // On the headset the room is only reachable through the colour camera
            // (PassthroughCameraFeed); drops refract and fog blurs that instead of
            // the opaque texture, which holds none of the room there.
            //
            // multi_compile, not shader_feature, for the two the headset uses: the
            // material is made in code, and a build keeps a shader_feature variant
            // only if some material asset uses it -- so these were stripped, and
            // the headset only ever ran the flat fallback.
            #pragma multi_compile_local_fragment _ _PASSTHROUGH_CAMERA
            #pragma multi_compile_local_fragment _ _DROPS_LOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #if defined(_REFRACTION)
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #endif
            #include "Include/IMH_WaterDrops.hlsl"
            #if defined(_PASSTHROUGH_CAMERA)
                #include "Include/IMH_PassthroughCamera.hlsl"
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3  normalWS   : TEXCOORD2;
                half4  tangentWS  : TEXCOORD3;
                float4 screenPos  : TEXCOORD4;
                float2 quadScale  : TEXCOORD5;   // world metres per uv unit
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseTint;
                half4 _FrameColor;
                half  _IOR;
                half  _ContactAngle;
                half  _Smoothness;
                half  _Thickness;
                half  _DropDensity;
                half  _SizeBias;
                half  _EdgeSoftness;
                half  _NormalStrength;
                half  _DropsLow;
                half  _RunnerColumns;
                half  _RunnerSpeed;
                half  _RunnerSize;
                half  _Accretion;
                half  _TrailStrength;
                half  _RefractionEnabled;
                half  _RefractionScale;
                half  _MaxTravel;
                half  _MaxDeflection;
                half  _DispersionEnabled;
                half  _Dispersion;
                half  _WindowFrameEnabled;
                half  _WindowWidth;
                half  _WindowHeight;
                half  _WindowRound;
                half  _FrameSoftness;
                half  _Stylize;
                half  _EnvReflection;
                half  _AmbientScatter;
                half  _Opacity;
                half  _EdgeFade;
                float4 _PaneSize;
                float _CellAspect;
                half  _TopFade;
                half  _Horizontal;
                half  _GlassPresence;
                half4 _GlassTint;
                half  _GlassReflect;
                half  _GlassSmudge;
                half  _ZTest;
                half  _Alpha;
                half4 _FogColor;
                half  _FogCoverage;
                half  _FogOpacity;
                half  _FogBlur;
                half  _FogRoomBlur;
                half  _FogMilk;
                half  _FogEdge;
                half  _FogClearByDrops;
            CBUFFER_END

            // Runner heads, one per lane, written each frame by
            // RainWindowOverlayController (see RainRunnerHeads.cs): (headY, radius,
            // releaseIndex, life). Index = column - floor(phase offset) + 1.
            #define RUNNER_SLOTS 48
            float4 _RunnerHeadsA[RUNNER_SLOTS];
            float4 _RunnerHeadsB[RUNNER_SLOTS];

            // Whole cells, at least one. floor(x + 0.5) rather than round(), whose
            // halfway case is left to the compiler -- RainRunnerHeads.cs must get
            // exactly the same counts.
            float CellCount(float x)
            {
                return max(floor(x + 0.5), 1.0);
            }

            void AddRunnerSet(float2 uv, float columns, float phaseOffset, float baseRadius,
                              float minRadius, bool setB, inout IMH_WaterField field, inout float swept)
            {
                float col = floor(uv.x * columns + phaseOffset);
                float firstCol = floor(phaseOffset) - 1.0;

                // The lane under the pixel and both neighbours: merging grows a
                // runner past its own lane's width.
                UNITY_UNROLL
                for (int i = -1; i <= 1; i++)
                {
                    int slot = clamp((int)(col + i - firstCol), 0, RUNNER_SLOTS - 1);
                    float4 head = setB ? _RunnerHeadsB[slot] : _RunnerHeadsA[slot];
                    IMH_AddRunnerFromHead(uv, col + i, columns, phaseOffset, head, baseRadius,
                                          _ContactAngle, _EdgeSoftness, minRadius, _TrailStrength,
                                          field, swept);
                }
            }

            // ---- condensation ---------------------------------------------------
            // When each point fogs; low values fog first. The fog travels an L:
            // in from the bottom-left corner, along the bottom to the right, then
            // up the right edge, spreading inward from that path as it goes, with
            // the centre pulled early so it is fogged by mid-growth. The top-left
            // is the last to go. Noise breaks the front up, as breath on glass
            // is. The field is fixed; only the threshold moves, so fog grows
            // outward from where it started and never jumps around.
            float FogField(float2 uv, float aspect)
            {
                // Arrival time via the bottom edge (slow creep upward) or via the
                // right edge (fast climb, then creep left); whichever is sooner.
                float alongBottom = 1.5 * uv.x + uv.y / 0.3;
                float upRight = 1.5 + 0.45 * uv.y + (1.0 - uv.x) / 0.45;
                float arrival = min(alongBottom, upRight);

                float centreDist = length(float2((uv.x - 0.5) * aspect, uv.y - 0.5));
                arrival -= 0.9 * saturate(1.0 - centreDist / 0.42);

                float2 p = float2(uv.x * 3.2 * aspect + 11.7, uv.y * 3.2 + 4.3);
                float n = 0.0;
                float amp = 0.5;
                UNITY_UNROLL
                for (int i = 0; i < 4; i++)
                {
                    n += IMH_Noise21(p) * amp;
                    p *= 2.03;
                    amp *= 0.5;
                }
                return arrival / 4.0 + 0.25 * n;
            }

            // Field value below which the given fraction of the pane lies. Measured
            // offline from FogField over quad aspects 1.0 to 2.2 (deciles, within
            // about 4% of each other), so _FogCoverage is a true area fraction.
            // Re-measure these whenever FogField changes.
            static const float kFogDeciles[11] = { 0.1249, 0.3692, 0.4662, 0.5296, 0.5790, 0.6137,
                                                   0.6489, 0.6911, 0.7357, 0.8208, 0.9919 };

            float FogThreshold(float coverage)
            {
                float c = saturate(coverage) * 10.0;
                int i = min((int)floor(c), 9);
                return lerp(kFogDeciles[i], kFogDeciles[i + 1], c - i);
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS   = half3(normalInputs.normalWS);
                output.tangentWS  = half4(normalInputs.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.uv         = input.uv;
                output.screenPos  = ComputeScreenPos(output.positionCS);

                // The field is built in uv, but every optical quantity below is in
                // metres. Carrying the quad's world size per uv unit is what lets a
                // non-square pane keep round drops and correct deviation.
                output.quadScale = float2(
                    length(float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20)),
                    length(float3(unity_ObjectToWorld._m01, unity_ObjectToWorld._m11, unity_ObjectToWorld._m21)));
                // A curved surface (the booth) has no scale to read its size from.
                if (_PaneSize.x > 0.0)
                    output.quadScale = _PaneSize.xy;
                return output;
            }

            half SmithVisibility(half ndl, half ndv, half a2)
            {
                half lambdaV = ndl * sqrt(ndv * ndv * (1.0h - a2) + a2);
                half lambdaL = ndv * sqrt(ndl * ndl * (1.0h - a2) + a2);
                return 0.5h / max(lambdaV + lambdaL, 1e-4h);
            }

            half DistributionGGX(half ndh, half a2)
            {
                half denom = ndh * ndh * (a2 - 1.0h) + 1.0h;
                return a2 / max(PI * denom * denom, 1e-5h);
            }

            half3 FresnelSchlick(half cosTheta, half3 f0)
            {
                half f = pow(1.0h - saturate(cosTheta), 5.0h);
                return f0 + (1.0h - f0) * f;
            }

            #if defined(_REFRACTION)
            // Traces the view ray through the air-water interface and reports where
            // it lands on the background, in screen uv.
            //
            // `valid` is false for rays that exit too close to parallel with the
            // view axis. Those are looking at something the screen never rendered.
            float2 RefractedScreenUv(float3 positionWS, float3 incident, float3 normalWS,
                                     float eta, float travel, float3 camForward,
                                     float2 screenUv, float maxDeflection, out bool valid)
            {
                float3 t = refract(incident, normalWS, eta);

                float forward = dot(t, camForward);
                valid = dot(t, t) > 1e-6 && forward > 0.15;
                if (!valid)
                    return screenUv;

                float3 hitWS = positionWS + t * (travel / forward);

                // Match ComputeScreenPos exactly, including the render-target y flip
                // it folds in via _ProjectionParams.x.
                float4 hitCS = mul(GetWorldToHClipMatrix(), float4(hitWS, 1.0));
                float2 hitNdc = hitCS.xy / max(hitCS.w, 1e-5);
                float2 hitUv = float2(hitNdc.x, hitNdc.y * _ProjectionParams.x) * 0.5 + 0.5;

                // A drop near its contact line images a far wider angle than the
                // screen holds. Clamping keeps the excess from becoming a streak of
                // edge texels.
                float2 delta = hitUv - screenUv;
                float len = length(delta);
                if (len > maxDeflection)
                    delta *= maxDeflection / max(len, 1e-5);

                return clamp(screenUv + delta, 0.001, 0.999);
            }
            #endif

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Global fade, driven by CameraLockedOverlay.
                //
                // This gates everything, not just alpha. Under premultiplied blending
                // the destination keeps whatever lands in rgb regardless of alpha, so
                // any term added after the fade stays fully visible at _Alpha = 0.
                // The branch is uniform across the draw, so it is free.
                half fade = _Opacity * _Alpha;

                // A pane standing in the room shows its border when the head turns
                // to it; fade the water out towards it rather than cut it off.
                if (_EdgeFade > 0.0h)
                {
                    float2 toEdge = min(input.uv, 1.0 - input.uv);
                    toEdge.x *= input.quadScale.x / max(input.quadScale.y, 1e-4);
                    fade *= (half)smoothstep(0.0, _EdgeFade, min(toEdge.x, toEdge.y));
                }
                if (_TopFade > 0.0h)
                    fade *= (half)(1.0 - smoothstep(1.0 - _TopFade, 1.0, input.uv.y));
                if (fade <= 0.002h)
                    return half4(0.0h, 0.0h, 0.0h, 0.0h);

                // ---- airplane window aperture --------------------------------
                #if defined(_WINDOW_FRAME)
                    float2 framePos = (input.uv * 2.0 - 1.0) / float2(max(_WindowWidth, 0.05), max(_WindowHeight, 0.05));
                    // Superellipse: round enough to read as an aircraft window
                    // without the pinched corners a plain ellipse gives.
                    float frameDist = pow(abs(framePos.x), _WindowRound) + pow(abs(framePos.y), _WindowRound);
                    half aperture = 1.0h - (half)smoothstep(1.0 - _FrameSoftness, 1.0 + _FrameSoftness, frameDist);

                    if (aperture <= 0.001h)
                        return half4(_FrameColor.rgb * fade, fade);
                #endif

                // One texel's footprint in uv. Beads below this size alias into
                // black speckle instead of resolving, so they get faded out.
                float minRadius = max(fwidth(input.uv.x), fwidth(input.uv.y));

                // Drop shapes are built in the pane's true proportions, so they
                // come out taller than wide on a wide pane, as real drops are.
                IMH_Aspect = max(input.quadScale.x, 1e-4) / max(input.quadScale.y, 1e-4);
                IMH_Gravity = 1.0 - saturate(_Horizontal);

                IMH_WaterField field = IMH_InitWaterField();
                float swept = 0.0;

                // ---- runners first -------------------------------------------
                // They decide which beads have already been absorbed, so they have
                // to run before the bead layers are placed. They merge with the
                // large beads in their path -- the same beads the last layer below
                // draws -- growing and speeding up at each one.
                float density = _DropDensity;
                // Cells across are whole numbers, scaled with the surface's width:
                // beads never straddle a cell, so on the booth, which closes on
                // itself, the last column meets the first without a seam.
                // RainRunnerHeads.cs uses the same counts.
                float cellAspect = max(_CellAspect, 0.05);
                float2 mergeCells = float2(CellCount(density * 0.85 * cellAspect), density * 0.7);

                // Two sets of lanes; RainRunnerHeads.cs defines both the same way.
                // None on horizontal glass: water there has no downhill to run.
                if (_Horizontal < 0.5)
                {
                    AddRunnerSet(input.uv, CellCount(_RunnerColumns * cellAspect), 0.0, _RunnerSize,
                                 minRadius, false, field, swept);
                    AddRunnerSet(input.uv, CellCount(_RunnerColumns * cellAspect * 0.6), 3.7, _RunnerSize * 1.5,
                                 minRadius, true, field, swept);
                }

                // ---- static bead field ---------------------------------------

                #if !defined(_DROPS_LOW)
                    IMH_AddBeadLayer(input.uv, float2(CellCount(density * 2.6 * cellAspect), density * 2.2),
                                     float2(7.13, 2.71), 0.85, _SizeBias * 1.5,
                                     _ContactAngle, _EdgeSoftness, minRadius, swept, field);
                #endif

                IMH_AddBeadLayer(input.uv, float2(CellCount(density * 1.5 * cellAspect), density * 1.25),
                                 float2(3.31, 8.07), 1.0, _SizeBias * 1.2,
                                 _ContactAngle, _EdgeSoftness, minRadius, swept, field);

                IMH_AddBeadLayer(input.uv, mergeCells,
                                 float2(0.0, 0.0), 1.0, _SizeBias,
                                 _ContactAngle, _EdgeSoftness, minRadius, swept, field);

                // ---- uv space -> metres --------------------------------------
                float sx = max(input.quadScale.x, 1e-4);
                float sy = max(input.quadScale.y, 1e-4);

                // Rescale both axes onto metres so slopes are true slopes.
                float2 gradM = float2(field.gradient.x * sy / sx, field.gradient.y) * _Thickness;

                half3 normalTS = normalize(half3(-gradM * _NormalStrength, 1.0));

                half  sgn = input.tangentWS.w;
                half3 bitangentWS = sgn * cross(input.normalWS, input.tangentWS.xyz);
                half3x3 tangentToWorld = half3x3(input.tangentWS.xyz, bitangentWS, input.normalWS);
                half3 normalWS = SafeNormalize(mul(normalTS, tangentToWorld));

                half3 viewDirWS = half3(GetWorldSpaceNormalizeViewDir(input.positionWS));

                // ---- lighting -------------------------------------------------
                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE) || defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    Light mainLight = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                #else
                    Light mainLight = GetMainLight();
                #endif

                half3 lightDir = mainLight.direction;
                half3 halfDir  = SafeNormalize(lightDir + viewDirWS);

                half ndl = saturate(dot(normalWS, lightDir));
                half ndv = saturate(dot(normalWS, viewDirWS)) + 1e-4h;
                half ndh = saturate(dot(normalWS, halfDir));
                half vdh = saturate(dot(viewDirWS, halfDir));

                half perceptualRoughness = max(1.0h - _Smoothness, 0.02h);
                half roughness = perceptualRoughness * perceptualRoughness;
                half a2 = max(roughness * roughness, 1e-4h);

                half f0Scalar = (_IOR - 1.0h) / (_IOR + 1.0h);
                f0Scalar *= f0Scalar;
                half3 f0 = half3(f0Scalar, f0Scalar, f0Scalar);

                half3 directSpec = DistributionGGX(ndh, a2) * SmithVisibility(ndl, ndv, a2)
                                 * FresnelSchlick(vdh, f0)
                                 * ndl * mainLight.color * mainLight.shadowAttenuation;

                float2 screenUv = input.screenPos.xy / max(input.screenPos.w, 1e-5);

                // ---- additional lights ---------------------------------------
                // Every point and spot light in the scene glints on the water. This
                // is what makes the pane belong to the room rather than sit on top
                // of it.
                #if defined(_ADDITIONAL_LIGHTS)
                {
                    InputData inputData = (InputData)0;
                    inputData.positionWS = input.positionWS;
                    inputData.normalWS = normalWS;
                    inputData.viewDirectionWS = viewDirWS;
                    inputData.normalizedScreenSpaceUV = screenUv;

                    uint lightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(lightCount)
                        Light addLight = GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1));

                        half3 addDir  = addLight.direction;
                        half3 addHalf = SafeNormalize(addDir + viewDirWS);
                        half addNdl = saturate(dot(normalWS, addDir));
                        half addNdh = saturate(dot(normalWS, addHalf));
                        half addVdh = saturate(dot(viewDirWS, addHalf));

                        directSpec += DistributionGGX(addNdh, a2)
                                    * SmithVisibility(addNdl, ndv, a2)
                                    * FresnelSchlick(addVdh, f0)
                                    * addNdl * addLight.color
                                    * (addLight.distanceAttenuation * addLight.shadowAttenuation);
                    LIGHT_LOOP_END
                }
                #endif

                half3 reflectVector = reflect(-viewDirWS, normalWS);
                half3 envSpec = GlossyEnvironmentReflection(reflectVector, input.positionWS,
                                                            perceptualRoughness, 1.0h, screenUv);

                #if defined(_PASSTHROUGH_CAMERA)
                    // Over passthrough the reflection probe is empty -- a drop's rim,
                    // nearly all reflection, came out black. Reflect the real room
                    // instead, in the true mirror direction, from the panorama the
                    // headset has built as the viewer looked around. Rougher water
                    // reads a blurrier mip, as a probe would.
                    half3 roomReflection = IMH_RoomEnv(reflectVector, perceptualRoughness * 6.0);
                    envSpec = lerp(envSpec, roomReflection, (half)_PtRoomEnvReady);
                #endif

                // ---- refraction ----------------------------------------------
                // Default to the straight-through view. A drop that cannot resolve a
                // refracted sample should still show the background, never black.
                half3 scene = half3(1.0h, 1.0h, 1.0h);
                float2 ptUv = 0.5;   // camera uv straight through the pane, for the fog

                #if defined(_REFRACTION)
                {
                    half3 straight = half3(SampleSceneColor(screenUv));
                    scene = straight;

                    // Distance from the pane to whatever is actually behind it. A
                    // drop in front of a far wall must bend far more than one in
                    // front of a near hand.
                    float glassEye = input.screenPos.w;
                    float sceneEye = LinearEyeDepth(SampleSceneDepth(screenUv), _ZBufferParams);
                    float travel = clamp(sceneEye - glassEye, 0.0, _MaxTravel);

                    float3 camForward = GetViewForwardDir();
                    float3 incident = -float3(viewDirWS);
                    float3 nWS = float3(normalWS);
                    float3 posWS = input.positionWS;

                    float scaledTravel = travel * _RefractionScale;
                    float dn = 0.0063 * _Dispersion;

                    bool validG;
                    float2 uvG = RefractedScreenUv(posWS, incident, nWS, 1.0 / _IOR,
                                                   scaledTravel, camForward, screenUv,
                                                   _MaxDeflection, validG);

                    // Reject samples that landed on something nearer than the pane:
                    // that geometry is in front of the water.
                    float hitEye = LinearEyeDepth(SampleSceneDepth(uvG), _ZBufferParams);

                    if (validG && hitEye >= glassEye)
                    {
                        #if defined(_DISPERSION)
                            // n(lambda) for water runs about 1.3435 at 400nm to
                            // 1.3309 at 700nm, so red bends least. Small by design --
                            // an obvious rainbow fringe is the giveaway.
                            bool validR, validB;
                            float2 uvR = RefractedScreenUv(posWS, incident, nWS, 1.0 / (_IOR - dn),
                                                           scaledTravel, camForward, screenUv,
                                                           _MaxDeflection, validR);
                            float2 uvB = RefractedScreenUv(posWS, incident, nWS, 1.0 / (_IOR + dn),
                                                           scaledTravel, camForward, screenUv,
                                                           _MaxDeflection, validB);
                            scene.r = half(SampleSceneColor(validR ? uvR : uvG).r);
                            scene.g = half(SampleSceneColor(uvG).g);
                            scene.b = half(SampleSceneColor(validB ? uvB : uvG).b);
                        #else
                            scene = half3(SampleSceneColor(uvG));
                        #endif
                    }
                    else
                    {
                        // Grazing exit or an occluder in front: blend towards the
                        // environment, which is what such a ray genuinely sees.
                        scene = lerp(straight, envSpec, 0.5h * saturate(field.coverage));
                    }
                }
                #elif defined(_PASSTHROUGH_CAMERA)
                {
                    // The same trace, but the background is the room from the
                    // headset camera, taken out to the assumed room distance.
                    float3 camForward = GetViewForwardDir();
                    float3 incident = -float3(viewDirWS);
                    float travel = max(_PtDepth - input.screenPos.w, 0.05);

                    // Where the camera cannot see -- the edges of the view are wider
                    // than its lens -- the panorama stands in: the same room, as
                    // last seen from that direction.
                    bool validS;
                    float3 straightHit = input.positionWS
                                       + incident * (travel / max(dot(incident, camForward), 0.15));
                    ptUv = IMH_PtWorldToUv(straightHit, validS);
                    half3 straight = validS ? IMH_PtSample(ptUv) : IMH_RoomEnv(incident, 0.0);
                    scene = straight;

                    float3 t = refract(incident, float3(normalWS), 1.0 / _IOR);
                    float forward = dot(t, camForward);
                    if (dot(t, t) > 1e-6 && forward > 0.15)
                    {
                        bool validR;
                        float3 hit = input.positionWS + t * (travel * _RefractionScale / forward);
                        float2 uvR = IMH_PtWorldToUv(hit, validR);

                        // A drop images a wide, inverted view of the room behind it.
                        // The live camera has it sharp where the bent ray lands in
                        // its image; past that, the panorama has the rest.
                        if (validR && validS)
                            scene = IMH_PtSample(uvR);
                        else
                            scene = IMH_RoomEnv(t, 0.5);

                        // Virtual things behind the glass -- the Humobox -- that the
                        // headset camera cannot see, imaged by the drop like the rest.
                        half4 virtualHit = IMH_VirtualAt(hit);
                        scene = lerp(scene, virtualHit.rgb, virtualHit.a);
                    }
                    else
                    {
                        // Bent too far to leave the drop forward: it sees the room
                        // off to the side, which only the panorama holds.
                        scene = IMH_RoomEnv(reflect(incident, float3(normalWS)), 1.0);
                    }
                }
                #endif

                // ---- composite -------------------------------------------------
                // Fresnel splits the energy: what reflects cannot also transmit.
                half3 fresnelView = FresnelSchlick(ndv, f0);
                half3 reflected = envSpec * fresnelView * _EnvReflection;
                half3 transmission = 1.0h - fresnelView;

                // Real water absorbs about 0.45 / 0.07 / 0.02 per metre in RGB. Over
                // a drop a millimetre thick that is a fraction of a percent, so the
                // blue cast everyone expects is a stylisation, not physics, and is
                // exposed as one.
                half3 absorption = exp(-half3(0.45h, 0.074h, 0.021h) * field.height * sy * _Thickness);
                half3 tint = lerp(half3(1.0h, 1.0h, 1.0h), _BaseTint.rgb, _Stylize);

                half coverage = saturate(field.coverage);

                #if !defined(_REFRACTION)
                    // Nothing to substitute behind the drop, so keep the body clear
                    // and let the contact line and specular carry the read. With
                    // the camera, that holds only until its first frames arrive.
                    half rimOnly = saturate(field.rim * 0.6h + field.trail * 0.4h);
                    #if defined(_PASSTHROUGH_CAMERA)
                        coverage = lerp(rimOnly, coverage, (half)_PtReady);
                    #else
                        coverage = rimOnly;
                    #endif
                #endif

                // The contact line and the thin trail film are the only parts of a
                // drop rough enough to scatter, so ambient enters there and nowhere
                // else -- water has no diffuse albedo.
                half3 ambient = SampleSHPixel(half3(0.0h, 0.0h, 0.0h), normalWS);
                half3 scatter = ambient * _AmbientScatter * saturate(field.rim + field.trail * 0.5h);

                half3 refracted = scene * absorption * tint * transmission;

                half3 emissive = directSpec + reflected * coverage + scatter;

                half3 premul = refracted * coverage + emissive;
                half surfaceAlpha = coverage;

                // ---- condensation ---------------------------------------------
                // A fogged film on the pane, under the drops. Fog is millions of
                // micro-droplets scattering light: the view through it blurs and
                // lifts towards a milky, room-lit grey. Every drop is a clear lens
                // through it and a running drop wipes a clear track, so the rain
                // keeps playing on top however much of the pane has fogged.
                // Not on the floor and ceiling: breath fogs the glass you stand
                // against, and a fogged cap reads as a lid over the booth --
                // worse, the panorama holds nothing for straight down, so the
                // film there is flat grey with no room behind it.
                half fogHere = _FogCoverage * (1.0h - saturate(_Horizontal));
                if (fogHere > 0.001h)
                {
                    float aspect = sx / sy;
                    float threshold = FogThreshold(fogHere);
                    float g = FogField(input.uv, aspect);
                    half fog = (half)(1.0 - smoothstep(threshold - _FogEdge, threshold + _FogEdge, g));

                    // Micro-droplet grain: density varies at a scale just above a
                    // texel, which is what makes it read as fog rather than frost.
                    float grain = IMH_Noise21(input.uv * float2(aspect, 1.0) * 220.0);
                    fog *= (half)lerp(0.8, 1.0, grain);

                    // Drops and the tracks runners leave are clear glass.
                    // Clear where there is water, and along the exact path each
                    // runner took, as wide as it was when it passed.
                    half cleared = saturate(max(coverage, (half)field.cleared));
                    fog *= 1.0h - cleared * _FogClearByDrops;

                    half fogAlpha = fog * _FogOpacity;
                    if (fogAlpha > 0.001h)
                    {
                        half3 blurred = _FogColor.rgb * 0.55h;
                        #if defined(_REFRACTION)
                        {
                            // Twelve taps on a golden-angle spiral, turned per pixel
                            // so the kernel's pattern becomes fine noise instead of
                            // visible rings.
                            float spinAngle = IMH_Hash21(input.uv * 911.0) * IMH_TAU;
                            half3 sum = 0.0h;
                            UNITY_UNROLL
                            for (int k = 0; k < 12; k++)
                            {
                                float r = sqrt((k + 0.5) / 12.0) * _FogBlur;
                                float a = k * 2.39996323 + spinAngle;
                                float2 tap = clamp(screenUv + float2(cos(a), sin(a)) * r, 0.001, 0.999);
                                sum += half3(SampleSceneColor(tap));
                            }
                            blurred = sum / 12.0h;
                        }
                        #elif defined(_PASSTHROUGH_CAMERA)
                        {
                            // Fog is the room's light scattered by micro-droplets: the
                            // view straight through, blurred. One read of a small mip
                            // of the panorama is exactly that, where a kernel on the
                            // camera image cost eight.
                            half3 room = IMH_RoomEnv(-float3(viewDirWS), _FogRoomBlur);
                            blurred = lerp(blurred, room, (half)_PtRoomEnvReady);
                        }
                        #endif

                        half luma = dot(blurred, half3(0.2126h, 0.7152h, 0.0722h));
                        half3 milky = _FogColor.rgb * (0.35h + 0.65h * luma) + ambient * 0.15h;
                        half3 fogColor = lerp(blurred, milky, _FogMilk);

                        // Fog sits under the drops: drop layer over fog layer, both
                        // premultiplied.
                        premul += fogColor * fogAlpha * (1.0h - surfaceAlpha);
                        surfaceAlpha += fogAlpha * (1.0h - surfaceAlpha);
                    }
                }

                // ---- the pane itself -------------------------------------------
                // Under the water and the fog: the glass the booth is made of.
                // Clear glass is invisible head-on and a mirror at a glancing
                // angle -- that Fresnel sheen is what tells the eye there is a
                // surface there at all -- and no pane is ever perfectly clean,
                // so wiped streaks and dust catch the light across it.
                if (_GlassPresence > 0.001h)
                {
                    half3 flatN = SafeNormalize(input.normalWS);
                    half ndvFlat = saturate(dot(flatN, viewDirWS)) + 1e-4h;
                    half sheen = 0.04h + 0.96h * pow(1.0h - ndvFlat, 5.0h);

                    // Metres across the pane: streaks run down it, dust is fine
                    // and even, so both keep their size however big the booth is.
                    float2 metres = input.uv * float2(sx, sy);
                    float wipes = IMH_Noise21(metres * float2(0.9, 4.5));
                    float dust = IMH_Noise21(metres * 27.0);
                    half smudge = (half)saturate(wipes * 0.8 + dust * 0.4 - 0.5) * _GlassSmudge;

                    #if defined(_PASSTHROUGH_CAMERA)
                        half3 paneEnv = lerp(half3(0.5h, 0.55h, 0.6h),
                                             IMH_RoomEnv(reflect(-viewDirWS, flatN), 1.0),
                                             (half)_PtRoomEnvReady);
                    #else
                        half3 paneEnv = envSpec;
                    #endif

                    // Underfoot and overhead the glass is quieter: smudges read
                    // as dirt on a lid from there, and the room they would catch
                    // is not in the panorama anyway.
                    half quiet = lerp(1.0h, 0.35h, saturate(_Horizontal));
                    half3 glassColor = (paneEnv * sheen * _GlassReflect
                                        + (paneEnv * 0.3h + ambient * 0.5h) * smudge) * _GlassTint.rgb * quiet;
                    half glassAlpha = saturate((sheen * _GlassReflect * 0.8h + smudge) * _GlassPresence * quiet);

                    // Glass is the bottom layer: water and fog sit on it.
                    premul += glassColor * glassAlpha * (1.0h - surfaceAlpha);
                    surfaceAlpha += glassAlpha * (1.0h - surfaceAlpha);
                }

                #if defined(_WINDOW_FRAME)
                    premul = lerp(_FrameColor.rgb, premul, aperture);
                    surfaceAlpha = lerp(1.0h, surfaceAlpha, aperture);
                #endif

                // Fade scales premultiplied colour and alpha together, the only way
                // a premultiplied surface interpolates to "not there".
                return half4(premul * fade, surfaceAlpha * fade);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
