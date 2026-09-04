// Diggles ground renderer — 3-material weighted blend with random per-tile
// variants, URP lighting + main-light shadows (cast & receive).
//
// weight = vertex.color.rgb;
// base = (stoneArr[stoneSlice]*r + earthArr[stoneSlice]*g) / (r+g)
// moss = MODULATE2X(base, mossArr[mossSlice])
// rgb = lerp(base, moss, b)
// final = rgb * ambient + rgb * mainLightColor * NdotL * shadowAttenuation
Shader "Diggles/Ground"
{
    Properties
    {
        _StoneArr   ("Stone Array", 2DArray)        = "white" {}
        _EarthArr   ("Earth Array", 2DArray)        = "white" {}
        _MossArr    ("Moss  Array", 2DArray)        = "white" {}
        _Brightness ("Brightness",  Range(0.1, 3))  = 1.0
        _Ambient    ("Ambient",     Range(0.0, 1))  = 0.4
        // Per-material (= per-scape) detail controls. Defaults are exact no-ops so the
        // urw/forest material renders bit-identically to before.
        _DetailFloor    ("Detail Floor (min wMoss)",   Range(0, 1)) = 0.0
        _DetailSaturate ("Detail Re-saturate (0=off)", Range(0, 1)) = 0.0
        _DetailContrast ("Detail Contrast Gain",       Range(1, 4)) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 100

        // ---------- Forward pass with lighting + shadows ----------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            // URP shadow keywords (so receive-shadows works)
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv0        : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float2 uv          : TEXCOORD2;
                float2 slice       : TEXCOORD3;
                float4 color       : COLOR;
                float4 shadowCoord : TEXCOORD4;
            };

            TEXTURE2D_ARRAY(_StoneArr); SAMPLER(sampler_StoneArr);
            TEXTURE2D_ARRAY(_EarthArr); SAMPLER(sampler_EarthArr);
            TEXTURE2D_ARRAY(_MossArr);  SAMPLER(sampler_MossArr);

            // Diggles class_light sources — a STATIC custom light list (set globally each frame by
            // RuntimeWorldSpawner.UploadClassLights). Looped in software like the original engine,
            // bypassing URP additional-light clustering entirely (no Forward+ near-camera flicker).
            #define DIG_MAX_LIGHTS 128
            float4 _DigLights[DIG_MAX_LIGHTS];      // xyz = world pos, w = range
            float4 _DigLightCols[DIG_MAX_LIGHTS];   // rgb = colour * intensity
            int    _DigLightCount;
            // Accumulated class-light ceiling — the native diffuse saturation min(I,255) ⇒ 1.0
            // in port units (RENDERING_LIGHTING_RESEARCH §2a). User knob: diggles_config.json
            // "classLightCap" / RuntimeWorldSpawner.ClassLightCap (uploaded with the arrays).
            float  _DigLightCap;

            // Fog of war — global per-tile map set by FogOfWarPresenter (same global-upload
            // idiom as _DigLights). R = the native per-corner fogFactor, G = the black-corner
            // indicator (byte==0 → 0, else 1) — both DECODED on the CPU (FUN_004ca830:133-283
            // laws) so the bilinear sample below IS the native per-vertex corner interpolation
            // (FUN_004cc040). _FowParams = (mapW, mapH, fogActive, 0); fogActive is 0 during
            // cutscenes / show_fogofwar 0 / -nofow. _FowWorldToNative maps positionWS into the
            // terrain-local frame: x = native tileX, -z = native tileY, y = the native render
            // depth z (7 = deepest floor.. 14.5 = front plane) — RuntimeWorldSpawner
            //.NativeToLocal = (x, z*0.5+7, -y); tile (tx,ty) spans local x∈[tx,tx+1],
            // z∈[-(ty+1),-ty] with its fog byte at the MIN corner (BdbPmpWorldBuilder
            //.EmitVertexW:1088 + 004ca830 corner sampling), hence the (nat+0.5)/size mapping.
            TEXTURE2D(_FowTex); SAMPLER(sampler_FowTex);
            float4   _FowParams;
            float4x4 _FowWorldToNative;

            // Underground darkness baseline — the native per-tile BASELIGHT byte (tile+0x07,
            // FUN_005906a0: 20 under open sky, ~4 in unlit tunnels, →0 against up-left rock;
            // Docs/TUNNEL_TORCH_LIGHTING_RE.md §6). Set by BaseLightPresenter: R = byte/20 on
            // the same corner-texel lattice as _FowTex (bilinear = the native per-vertex
            // corner interpolation). _BaseLightParams = (mapW, mapH, active, minFactor);
            // minFactor = the Play-tuning floor lever (0 = native). It scales ambient + main
            // light ONLY — the _DigLights term adds ON TOP, which is exactly what makes
            // torches light the dark tunnels.
            TEXTURE2D(_BaseLightTex); SAMPLER(sampler_BaseLightTex);
            float4   _BaseLightParams;
            float4x4 _BaseLightToNative;

            // Horizon-zone rear fade — set by BackdropPresenter (FUN_004750b0 zone scan).
            // rgb = active zone colour (0..1), w = fog depth in render-z units (0 = no zone → off).
            // _HorizonWorldToNative maps positionWS into the root-local frame whose local y IS
            // the native render z (7 = deepest floor.. 14.5 = front plane).
            float4   _HorizonFog;
            float4x4 _HorizonWorldToNative;
            // Debug (console `horizon_tint 1`): >0.5 hot-pinks the fade REGION with a forced
            // depth of 32, active-zone or not — makes the region math visible on screen.
            float    _HorizonDebugTint;

            CBUFFER_START(UnityPerMaterial)
                float4 _StoneArr_ST;
                float4 _EarthArr_ST;
                float4 _MossArr_ST;
                float  _Brightness;
                float  _Ambient;
                float  _DetailFloor;
                float  _DetailSaturate;
                float  _DetailContrast;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posIn = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = posIn.positionCS;
                OUT.positionWS  = posIn.positionWS;
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv          = IN.uv0;
                OUT.slice       = IN.uv1;
                OUT.color       = IN.color;
                OUT.shadowCoord = TransformWorldToShadowCoord(posIn.positionWS);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float stoneSlice = floor(IN.slice.x + 0.5);
                float mossSlice  = floor(IN.slice.y + 0.5);
                half4 stone = SAMPLE_TEXTURE2D_ARRAY(_StoneArr, sampler_StoneArr, IN.uv, stoneSlice);
                half4 earth = SAMPLE_TEXTURE2D_ARRAY(_EarthArr, sampler_EarthArr, IN.uv, stoneSlice);
                half4 moss  = SAMPLE_TEXTURE2D_ARRAY(_MossArr,  sampler_MossArr,  IN.uv, mossSlice);

                half wStone = IN.color.r;
                half wEarth = IN.color.g;
                half wMoss  = IN.color.b;
                half baseSum = max(wStone + wEarth, 0.001);
                half3 baseRgb = (stone.rgb * wStone + earth.rgb * wEarth) / baseSum;
                // Sharpen the detail texel: contrast about the 0.5 MODULATE2X identity.
                // _DetailContrast==1 (forest) => d == moss.rgb exactly (saturate is a no-op on a [0,1] sample).
                half3 d = saturate((moss.rgb - 0.5) * _DetailContrast + 0.5);
                // MODULATE2X as before (identity when d==0.5). Forest uses d==moss.rgb.
                half3 modulated = saturate(baseRgb * d * 2.0);
                // Re-saturate colored scapes: blend the modulated result toward the raw
                // detail hue scaled to base luminance, so lava/sulfur/crystal keep their
                // color instead of being dragged to gray by the near-neutral base.
                // _DetailSaturate==0 (forest) => modulated is unchanged.
                half baseLum = dot(baseRgb, half3(0.299, 0.587, 0.114));
                half3 detailColored = saturate(d * (baseLum * 2.0));
                modulated = lerp(modulated, detailColored, saturate(_DetailSaturate));
                // Raise the blend gate floor per-scape (forest keeps _DetailFloor==0).
                half wMossEff = max(saturate(wMoss), _DetailFloor);
                // Texture ALPHA = the sparse inclusion mask: lava is ~60% holes → rare red veins, holes
                // show the base ground through. urw/swf/kris are opaque (a=1) so they are unaffected.
                half3 albedo = lerp(baseRgb, modulated, wMossEff * moss.a) * _Brightness;

                // Lighting: ambient + main directional light + additional point/spot lights.
                half3 normal = normalize(IN.normalWS);

                // --- Main directional light (with shadows) ---
                Light mainLight = GetMainLight(IN.shadowCoord);
                half NdotL = saturate(dot(normal, mainLight.direction));
                half3 lighting = mainLight.color * (NdotL * mainLight.shadowAttenuation);

                // --- Underground darkness baseline (see the declaration comment above) ---
                // base = 1 above ground (texel 1.0 under open sky), ~0.2 in unlit tunnels.
                // Applied to ambient + main light by folding into `lighting` and `_Ambient`
                // BEFORE the torch loop adds its term un-scaled.
                half baseF = 1.0;
                if (_BaseLightParams.z > 0.5)
                {
                    float3 bnat = mul(_BaseLightToNative, float4(IN.positionWS, 1.0)).xyz;
                    float2 blUv = float2((bnat.x + 0.5) / _BaseLightParams.x,
                                         (-bnat.z + 0.5) / _BaseLightParams.y);
                    baseF = max(SAMPLE_TEXTURE2D(_BaseLightTex, sampler_BaseLightTex, blUv).r,
                                _BaseLightParams.w);
                }
                lighting *= baseF;

                // --- Diggles class_light sources: a STATIC custom light list (NOT URP additional lights) ---
                // The original engine accumulated point lights in software (per vertex); we do the same per
                // pixel over a global buffer set by RuntimeWorldSpawner. This bypasses URP's Forward+
                // clustering entirely → no near-camera flicker, no per-tile/per-object light limits.
                // Faithful falloff exp(-2*(d/R)^2): peak == colour (range-independent → one flat intensity),
                // hard-cull at R, half-Lambert (soft, even glow). Loops 0 lights when none are set.
                //
                // NATIVE ACCUMULATION BOUND (FUN_004cc040:115-168; RENDERING_LIGHTING_RESEARCH.md §2a):
                // the original SUMS all in-range lights per vertex with no per-light cap, then the
                // TOTAL intensity saturates at 255/channel (diffuse) — the ≤64 excess goes to the DX7
                // specular add. One torch already saturates its pool, so overlapping torches render
                // bit-identical pixels — overlap is invisible BY THE CAP, not by tuning. Port units:
                // multiplier 1.0 == native diffuse 255 == full texture brightness ⇒ clamp the summed
                // torch term per channel at 1.0 AFTER the loop. A single torch peaks at 0.9 (< 1.0),
                // so the committed single-torch look is untouched; 3-4 overlapping torches plateau
                // instead of stacking to blinding. The ≤64/255 specular overspill is deliberately NOT
                // routed (would fabricate an overlap-only highlight — §2a "Port transplant").
                half3 digLight = half3(0.0, 0.0, 0.0);
                [loop]
                for (int dl = 0; dl < _DigLightCount; dl++)
                {
                    float4 lp = _DigLights[dl];                 // xyz = world pos, w = range
                    float3 toL = lp.xyz - IN.positionWS;
                    float d2 = dot(toL, toL);
                    float R = lp.w;
                    if (d2 >= R * R) continue;
                    float d = sqrt(d2);
                    float x = d / R;
                    float att = exp(-2.0 * x * x) * (1.0 - smoothstep(0.9, 1.0, x));
                    float3 Ldir = toL / max(d, 1e-4);
                    half ndl = saturate(dot(normal, Ldir) * 0.5 + 0.5);
                    digLight += _DigLightCols[dl].rgb * (ndl * att);
                }
                lighting += min(digLight, half3(_DigLightCap, _DigLightCap, _DigLightCap));

                half3 finalRgb = albedo * (_Ambient * baseF + lighting);

                // --- Cutout ALPHA TEST (research verdict, fable-rim-research.md):
                // the native terrain material scape_solid draws with ALPHABLEND OFF and
                // ALPHATEST GREATER ref=126 (FUN_00457b90:737-749); the tested alpha is the
                // VERTEX diffuse alpha alone (stage ALPHAOP=SELECTARG1(DIFFUSE) —
                // FUN_004564c0:140-143) = the bilinear of the per-corner cutout bytes
                // (0 at a state-3 node, 255 otherwise). The mesher bakes that bilinear into
                // COLOR.a; pixels survive only when a*255 > 126 — a hard STENCIL cut along
                // the 126.5 isoline through the mid-tile of the cutout ring. No fade, no
                // translucency, no luminance change: the earlier `finalRgb *= a`
                // read this byte as a light multiplier and painted the player's 
                //; the alpha BLEND painted a translucent band — neither
                // exists in the native pipeline.
                clip(IN.color.a * 255.0 - 126.5);

                // --- Horizon-zone rear fade (native FUN_00484cd0:82-104): vertices with render
                // z < 0 (seven metres BEHIND the deepest floor plane) lerp to the active horizon
                // zone's colour over `depth` units: f = clamp01(-z/depth), frame clear = the same
                // colour (FUN_006ad9a0:128-140). NOTE (measured): the BDB vertex
                // palette spans z ∈ [7, 14.5] exactly — TERRAIN never enters this fade, natively
                // or here; the law lives in this shader for fidelity + the debug tint, and its
                // real native targets are authored backdrop MODELS (DigglesModel twin).
                // Applied before the FoW veil, matching the native vertex-colour order.
                // horizon_tint 1 (debug): force the region visible in hot pink with the
                // canonical Info_Fog depth (32) even when no zone is active — separates
                // "region math wrong" from "zone chain dead" in one Play glance.
                float  hDepth = (_HorizonDebugTint > 0.5) ? max(_HorizonFog.w, 32.0) : _HorizonFog.w;
                float3 hCol   = (_HorizonDebugTint > 0.5) ? float3(1.0, 0.0, 0.8) : _HorizonFog.rgb;
                if (hDepth > 0.0)
                {
                    // Root-local y IS the native render z (terrain 7 = deepest dug floor..
                    // 14.5 = undug front plane; camera at-height const 14) — the same frame
                    // RV_VIEW consumes and FUN_00488980:131-148 feeds FUN_00484cd0 from. The
                    // law fades only z < 0 — seven metres BEHIND the deepest floor plane.
                    // (The "*2 - 14" remap moved the threshold to ly<7 and doubled the
                    // ramp rate — wrong frame; 14 being the fixed point of z*0.5+7 is what let
                    // it slip past the camera cross-check.)
                    float hz = mul(_HorizonWorldToNative, float4(IN.positionWS, 1.0)).y;
                    if (hz < 0.0)
                    {
                        float hf = saturate(-hz / hDepth);
                        finalRgb = lerp(finalRgb, hCol, hf);
                    }
                }

                // --- Fog of war (native per-vertex fog, FUN_004cc040:41-228; constants from
                //). Gated: fogActive==0 leaves rgb as-is.
                if (_FowParams.z > 0.5)
                {
                    float3 nat = mul(_FowWorldToNative, float4(IN.positionWS, 1.0)).xyz;
                    float2 fowUv = float2((nat.x + 0.5) / _FowParams.x,
                                          (-nat.z + 0.5) / _FowParams.y);
                    half2 fow = SAMPLE_TEXTURE2D(_FowTex, sampler_FowTex, fowUv).rg;
                    // GRAY fog, depth-graded: f = clamp01((1-(z-7)*0.125*1.02) * fogFactor)
                    // (DAT_00737520=7.0, DAT_0073703C=0.125, DAT_0073713C=1.02) — ~4% at the
                    // front plane 14.5, 100% at the deepest floor 7. The native scales diffuse
                    // by (1-f) and ADDS f*RGB(0xAA,0xAA,0x9B) through the texture-independent
                    // SPECULAR channel; at f=1 the pixel is exactly the flat warm gray — the
                    // lerp below reproduces that.
                    float f = saturate((1.0 - (nat.y - 7.0) * 0.125 * 1.02) * fow.r);
                    finalRgb = finalRgb * (1.0 - f) + f * float3(170.0, 170.0, 155.0) / 255.0;
                    // BLACK edge LAST: the native vertex multiplier (bias/256 + 1) — 0 at an
                    // unexplored (byte 0) corner, bilinear across the tile (004cc040:190-192).
                    finalRgb *= fow.g;
                }
                return half4(finalRgb, 1.0);
            }
            ENDHLSL
        }

        // ---------- ShadowCaster pass so meshes can CAST shadows ----------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex shadowVert
            #pragma fragment shadowFrag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttr
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            float4 GetShadowPositionHClip(ShadowAttr IN)
            {
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            float4 shadowVert(ShadowAttr IN) : SV_POSITION { return GetShadowPositionHClip(IN); }
            half4  shadowFrag()              : SV_Target   { return 0; }
            ENDHLSL
        }

        // ---------- DepthOnly pass (URP needs it) ----------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex depthVert
            #pragma fragment depthFrag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthAttr { float4 positionOS : POSITION; };
            struct DepthVary { float4 positionCS : SV_POSITION; };

            DepthVary depthVert(DepthAttr IN)
            {
                DepthVary OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }
            half4 depthFrag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
