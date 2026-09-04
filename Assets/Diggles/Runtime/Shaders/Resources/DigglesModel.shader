// Diggles/Model — emulates DX7 fixed-function rendering for.3db models.
//
// Solves the URP/Lit Z-fighting flicker issue: URP/Lit refuses to honor
// ZWrite=On in Transparent surface mode, which causes per-frame sort flicker
// for any mesh using alpha-blend. This shader pins ZWrite=On unconditionally
// (the DX7 default) while still using alpha-blend for transparent texels.
//
// Properties:
// * ZWrite always On (the fix)
// * Single forward pass with Lambertian (NdotL) + ambient SH
// * Alpha test cutoff = 0.01 (clips fully-transparent pixels so Z buffer
// isn't polluted by them)
// * Alpha blend = SrcAlpha / OneMinusSrcAlpha (DX7 default)
// * Shadow + DepthOnly passes for URP shadow/depth-prepass compatibility
//
// All Diggles model materials should use this shader. HasRealTransparency
// in TgaLoader.cs decides which textures have meaningful alpha content;
// opaque materials still go through this shader (alpha=1 everywhere makes
// the blend a no-op visually).

Shader "Diggles/Model"
{
    Properties
    {
        [MainTexture] _BaseMap ("Albedo (RGBA)", 2D) = "white" {}
        [MainColor]   _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.05

        // Per-material render state — set by MarkerModelResolver based on the
        // texture's classified TgaAlphaKind (Opaque/Cutout/Transparent).
        // Defaults below match Cutout; Resolver overrides for the other two.
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1   // One
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 0   // Zero
        [Enum(Off, 0, On, 1)] _ZWrite ("ZWrite", Float) = 1
        [Toggle] _AlphaToMask ("Alpha To Mask (A2C)", Float) = 1
        // Backface culling. Default Off (double-sided) preserves the original
        // world/campaign look; callers that want single-sided (e.g. the anim
        // tester, so a mushroom cap's underside doesn't show through) set Back=2.
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0

        // GPU vertex-morph (interpolation). When _UseVat>0.5 the vertex position
        // comes from the VAT (lerp of rows _F0,_F1 by _Blend) instead of the mesh,
        // turning 10 FPS keyframes into smooth motion. Driven per-renderer by
        // DigglesAnimator via a MaterialPropertyBlock. Default off = use the mesh.
        [Toggle] _UseVat ("Use VAT morph", Float) = 0
        _Vat ("Vertex Anim Texture", 2D) = "black" {}
        // NOTE: the morph phase _F0/_F1/_Blend is intentionally NOT declared here. Being a
        // Properties-block "material property" outside UnityPerMaterial breaks SRP-Batcher
        // compatibility ("...found in another cbuffer than UnityPerMaterial"); being INSIDE it
        // forbids the per-instance override needed for GPU instancing. So they are declared only as
        // per-instance shader uniforms (UNITY_DEFINE_INSTANCED_PROP in DigglesMorph, below).
        // DigglesAnimator feeds them by property ID on its MaterialPropertyBlock — SetFloat works on
        // any named uniform whether or not it appears in Properties.
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite [_ZWrite]
            ZTest LEqual
            Cull [_Cull]
            Blend [_SrcBlend] [_DstBlend]
            AlphaToMask [_AlphaToMask]
            // Per-material render state. Resolver picks one of three setups
            // based on TGA alpha classification:
            // Opaque → Blend One Zero, ZWrite On, A2C Off, no clip
            // Cutout → Blend One Zero, ZWrite On, A2C On, clip
            // (A2C uses MSAA for smooth foliage edges
            // WITHOUT alpha-blending against sky → no blue
            // fringe halo)
            // Transparent → Blend SrcAlpha InvSrcAlpha, ZWrite Off, A2C Off
            // (true gradient transparency for FX/lights)

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Diggles class_light sources — STATIC custom light list (set globally by RuntimeWorldSpawner).
            // Looped in software (bypasses URP additional-light clustering → no Forward+ flicker/limits).
            #define DIG_MAX_LIGHTS 128
            float4 _DigLights[DIG_MAX_LIGHTS];      // xyz = world pos, w = range
            float4 _DigLightCols[DIG_MAX_LIGHTS];   // rgb = colour * intensity
            int    _DigLightCount;
            // Accumulated class-light ceiling — native diffuse saturation min(I,255) ⇒ 1.0 in
            // port units; user knob "classLightCap" (see DigglesGround.shader).
            float  _DigLightCap;

            // Fog of war — same globals as DigglesGround (set by FogOfWarPresenter). The
            // native tints objects with a TWIN of the terrain fog shader (FUN_00485370 caches
            // the tile's corner fog values, FUN_004856e0 bilerps them at the object position
            // and applies the identical black multiply + depth-graded gray) — sampling the
            // shared fog texture at the fragment's own position is that same bilinear field.
            TEXTURE2D(_FowTex); SAMPLER(sampler_FowTex);
            float4   _FowParams;
            float4x4 _FowWorldToNative;

            // Horizon-zone rear fade — same globals as DigglesGround (set by BackdropPresenter).
            // The native model vertex path applies the identical z<0 fade (FUN_004856e0:162-199).
            float4   _HorizonFog;
            float4x4 _HorizonWorldToNative;
            // Debug (console `horizon_tint 1`): >0.5 hot-pinks the fade REGION with a forced
            // depth of 32, active-zone or not — makes the region math visible on screen.
            float    _HorizonDebugTint;

            // Underground darkness baseline — same globals as DigglesGround (set by
            // BaseLightPresenter; Docs/TUNNEL_TORCH_LIGHTING_RE.md §6/§8.6). Models take the
            // terrain's per-tile base factor so characters/props sitting in an unlit tunnel
            // darken with it instead of glowing at surface brightness — RE-confirmed faithful
            // the native model path samples the SAME baselight+class-light field
            // per vertex (FUN_004856e0; RENDERING_LIGHTING_RESEARCH.md §2a). _DigLights stay
            // UN-scaled: torches light models in the dark.
            TEXTURE2D(_BaseLightTex); SAMPLER(sampler_BaseLightTex);
            float4   _BaseLightParams;
            float4x4 _BaseLightToNative;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _Cutoff;
                float _UseVat;
            CBUFFER_END

            // Per-frame morph phase (f0->f1 by blend). Declared via the instancing macros
            // UNCONDITIONALLY and kept OUT of UnityPerMaterial so that cbuffer's size is identical
            // across every pass and variant — a hard SRP-Batcher requirement. (A conditional member
            // tripped "UnityPerMaterial CBuffer inconsistent size", because UNITY_INSTANCING_ENABLED
            // does not resolve the same way in ForwardLit vs ShadowCaster.)
            // * DigglesAnimator.UseInstancing OFF (default): these expand to plain uniforms fed
            // per-renderer by DigglesAnimator's MaterialPropertyBlock — visuals byte-identical to
            // the pre-instancing shader.
            // * ON (prototype): they become a per-instance buffer so same-clip morph characters draw
            // together. UNITY_ACCESS_INSTANCED_PROP resolves to the plain uniform when off, so the
            // read sites are identical either way.
            UNITY_INSTANCING_BUFFER_START(DigglesMorph)
                UNITY_DEFINE_INSTANCED_PROP(float, _F0)
                UNITY_DEFINE_INSTANCED_PROP(float, _F1)
                UNITY_DEFINE_INSTANCED_PROP(float, _Blend)
            UNITY_INSTANCING_BUFFER_END(DigglesMorph)

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_Vat);
            SAMPLER(sampler_Vat);

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                float3 normalOS  : NORMAL;
                float2 uv        : TEXCOORD0;
                float2 uv1       : TEXCOORD1;   // x = this vertex's VAT column
            };

            // Optional GPU vertex morph: lerp VAT rows _F0,_F1 by _Blend.
            float3 ApplyVat(float3 posOS, float2 uv1)
            {
                if (_UseVat < 0.5) return posOS;
                float u = uv1.x;
                float f0 = UNITY_ACCESS_INSTANCED_PROP(DigglesMorph, _F0);
                float f1 = UNITY_ACCESS_INSTANCED_PROP(DigglesMorph, _F1);
                float bl = UNITY_ACCESS_INSTANCED_PROP(DigglesMorph, _Blend);
                float3 p0 = SAMPLE_TEXTURE2D_LOD(_Vat, sampler_Vat, float2(u, f0), 0).xyz;
                float3 p1 = SAMPLE_TEXTURE2D_LOD(_Vat, sampler_Vat, float2(u, f1), 0).xyz;
                return lerp(p0, p1, bl);
            }

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
                float2 uv          : TEXCOORD1;
                float3 positionWS  : TEXCOORD2;
                float  fogFactor   : TEXCOORD3;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                float3 posOS = ApplyVat(IN.positionOS.xyz, IN.uv1);
                VertexPositionInputs vi = GetVertexPositionInputs(posOS);
                OUT.positionHCS = vi.positionCS;
                OUT.positionWS  = vi.positionWS;
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv          = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogFactor   = ComputeFogFactor(vi.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half4 col = tex * _BaseColor;

                // Alpha-test: discard fully-transparent texels so they don't
                // write to the Z buffer (lets things behind them be visible).
                clip(col.a - _Cutoff);

                half3 n = normalize(IN.normalWS);

                #if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                #else
                Light mainLight = GetMainLight();
                #endif

                half NdotL = saturate(dot(n, mainLight.direction));
                half shadow = mainLight.shadowAttenuation;
                half3 lightColor = mainLight.color * (NdotL * shadow);

                // Underground darkness baseline (declaration comment above): scale the main
                // light NOW so the torch loop below adds un-scaled; ambient is scaled at use.
                half baseF = 1.0;
                if (_BaseLightParams.z > 0.5)
                {
                    float3 bnat = mul(_BaseLightToNative, float4(IN.positionWS, 1.0)).xyz;
                    float2 blUv = float2((bnat.x + 0.5) / _BaseLightParams.x,
                                         (-bnat.z + 0.5) / _BaseLightParams.y);
                    baseF = max(SAMPLE_TEXTURE2D(_BaseLightTex, sampler_BaseLightTex, blUv).r,
                                _BaseLightParams.w);
                }
                lightColor *= baseF;

                // Diggles class_light sources: STATIC custom light list (NOT URP additional lights) — same
                // software loop as DigglesGround. Bypasses URP Forward+ clustering (no flicker/limits).
                // Lighting models by class lights is FAITHFUL: the native model renderer runs the same
                // per-vertex law as terrain (FUN_004856e0 from "CModelRenderer Vertexprocessing").
                //
                // NATIVE ACCUMULATION BOUND (FUN_004856e0:10 = FUN_004cc040 twin; RENDERING_
                // LIGHTING_RESEARCH.md §2a): lights SUM uncapped, then the total saturates at
                // 255/channel (+ ≤64 overspill→DX7 specular, not routed — §2a). Port: clamp the summed
                // term at 1.0/channel — single torch (peak 0.9) unchanged, overlaps plateau.
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
                    half ndl = saturate(dot(n, Ldir) * 0.5 + 0.5);
                    digLight += _DigLightCols[dl].rgb * (ndl * att);
                }
                lightColor += min(digLight, half3(_DigLightCap, _DigLightCap, _DigLightCap));

                half3 ambient = SampleSH(n);

                // Floor ambient so dark side isn't pitch black.
                ambient = max(ambient, half3(0.25, 0.25, 0.25));

                half3 finalRgb = col.rgb * (ambient * baseF + lightColor);

                // --- Horizon-zone rear fade (native FUN_004856e0:162-199, twin of the terrain's
                // FUN_00484cd0:82-104): geometry with render z < 0 (seven metres behind the
                // deepest floor plane) lerps to the active zone colour over `depth` units. THIS
                // twin is the law's live native home: terrain never reaches z<0 (BDB palette
                // z ∈ [7,14.5], measured), but authored backdrop MODELS placed behind the slab
                // (hinterlegung/flusslandschaft-class deco) do, and they melt into the zone
                // clear colour with depth. Off when no zone is active (_HorizonFog.w == 0).
                // horizon_tint 1 (debug): force the region hot pink at depth 32, zone or not.
                float  hDepth = (_HorizonDebugTint > 0.5) ? max(_HorizonFog.w, 32.0) : _HorizonFog.w;
                float3 hCol   = (_HorizonDebugTint > 0.5) ? float3(1.0, 0.0, 0.8) : _HorizonFog.rgb;
                if (hDepth > 0.0)
                {
                    // Root-local y IS the native render z (terrain 7..14.5) — same frame and
                    // law as DigglesGround: fade only z < 0, seven metres behind the deepest
                    // floor plane. (The "*2 - 14" remap was the wrong frame.)
                    float hz = mul(_HorizonWorldToNative, float4(IN.positionWS, 1.0)).y;
                    if (hz < 0.0)
                    {
                        float hf = saturate(-hz / hDepth);
                        finalRgb = lerp(finalRgb, hCol, hf);
                    }
                }

                // --- Fog of war (the native object tint twin FUN_004856e0:160-190, same laws
                // as the terrain's FUN_004cc040): depth-graded gray from the entity's own
                // render z, then the black-corner multiply LAST. Gated on fogActive.
                if (_FowParams.z > 0.5)
                {
                    float3 nat = mul(_FowWorldToNative, float4(IN.positionWS, 1.0)).xyz;
                    float2 fowUv = float2((nat.x + 0.5) / _FowParams.x,
                                          (-nat.z + 0.5) / _FowParams.y);
                    half2 fow = SAMPLE_TEXTURE2D(_FowTex, sampler_FowTex, fowUv).rg;
                    float f = saturate((1.0 - (nat.y - 7.0) * 0.125 * 1.02) * fow.r);
                    finalRgb = finalRgb * (1.0 - f) + f * float3(170.0, 170.0, 155.0) / 255.0;
                    finalRgb *= fow.g;
                }

                finalRgb = MixFog(finalRgb, IN.fogFactor);

                // A2C uses output alpha as MSAA sub-sample coverage fraction.
                return half4(finalRgb, col.a);
            }
            ENDHLSL
        }

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
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _Cutoff;
                float _UseVat;
            CBUFFER_END

            // Per-frame morph phase (f0->f1 by blend). Declared via the instancing macros
            // UNCONDITIONALLY and kept OUT of UnityPerMaterial so that cbuffer's size is identical
            // across every pass and variant — a hard SRP-Batcher requirement. (A conditional member
            // tripped "UnityPerMaterial CBuffer inconsistent size", because UNITY_INSTANCING_ENABLED
            // does not resolve the same way in ForwardLit vs ShadowCaster.)
            // * DigglesAnimator.UseInstancing OFF (default): these expand to plain uniforms fed
            // per-renderer by DigglesAnimator's MaterialPropertyBlock — visuals byte-identical to
            // the pre-instancing shader.
            // * ON (prototype): they become a per-instance buffer so same-clip morph characters draw
            // together. UNITY_ACCESS_INSTANCED_PROP resolves to the plain uniform when off, so the
            // read sites are identical either way.
            UNITY_INSTANCING_BUFFER_START(DigglesMorph)
                UNITY_DEFINE_INSTANCED_PROP(float, _F0)
                UNITY_DEFINE_INSTANCED_PROP(float, _F1)
                UNITY_DEFINE_INSTANCED_PROP(float, _Blend)
            UNITY_INSTANCING_BUFFER_END(DigglesMorph)

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_Vat);
            SAMPLER(sampler_Vat);

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                float3 normalOS  : NORMAL;
                float2 uv        : TEXCOORD0;
                float2 uv1       : TEXCOORD1;   // x = this vertex's VAT column
            };

            struct Varyings
            {
                float2 uv         : TEXCOORD0;
                float4 positionCS : SV_POSITION;
            };

            // Same GPU vertex morph as ForwardLit/DepthOnly — without it the shadow
            // is cast from the static frame-0 mesh (shadow froze on the first frame).
            float3 ApplyVatS(float3 posOS, float2 uv1)
            {
                if (_UseVat < 0.5) return posOS;
                float u = uv1.x;
                float f0 = UNITY_ACCESS_INSTANCED_PROP(DigglesMorph, _F0);
                float f1 = UNITY_ACCESS_INSTANCED_PROP(DigglesMorph, _F1);
                float bl = UNITY_ACCESS_INSTANCED_PROP(DigglesMorph, _Blend);
                float3 p0 = SAMPLE_TEXTURE2D_LOD(_Vat, sampler_Vat, float2(u, f0), 0).xyz;
                float3 p1 = SAMPLE_TEXTURE2D_LOD(_Vat, sampler_Vat, float2(u, f1), 0).xyz;
                return lerp(p0, p1, bl);
            }

            Varyings shadowVert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                float3 posOS = ApplyVatS(IN.positionOS.xyz, IN.uv1);
                float3 positionWS = TransformObjectToWorld(posOS);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                OUT.positionCS = positionCS;
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 shadowFrag(Varyings IN) : SV_Target
            {
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            // Respect per-material ZWrite — Transparent materials should not
            // write depth (would block things behind from rendering).
            ZWrite [_ZWrite]
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex depthVert
            #pragma fragment depthFrag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _Cutoff;
                float _UseVat;
            CBUFFER_END

            // Per-frame morph phase (f0->f1 by blend). Declared via the instancing macros
            // UNCONDITIONALLY and kept OUT of UnityPerMaterial so that cbuffer's size is identical
            // across every pass and variant — a hard SRP-Batcher requirement. (A conditional member
            // tripped "UnityPerMaterial CBuffer inconsistent size", because UNITY_INSTANCING_ENABLED
            // does not resolve the same way in ForwardLit vs ShadowCaster.)
            // * DigglesAnimator.UseInstancing OFF (default): these expand to plain uniforms fed
            // per-renderer by DigglesAnimator's MaterialPropertyBlock — visuals byte-identical to
            // the pre-instancing shader.
            // * ON (prototype): they become a per-instance buffer so same-clip morph characters draw
            // together. UNITY_ACCESS_INSTANCED_PROP resolves to the plain uniform when off, so the
            // read sites are identical either way.
            UNITY_INSTANCING_BUFFER_START(DigglesMorph)
                UNITY_DEFINE_INSTANCED_PROP(float, _F0)
                UNITY_DEFINE_INSTANCED_PROP(float, _F1)
                UNITY_DEFINE_INSTANCED_PROP(float, _Blend)
            UNITY_INSTANCING_BUFFER_END(DigglesMorph)

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_Vat);
            SAMPLER(sampler_Vat);

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                float2 uv         : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
            };

            struct Varyings
            {
                float2 uv         : TEXCOORD0;
                float4 positionCS : SV_POSITION;
            };

            float3 ApplyVatD(float3 posOS, float2 uv1)
            {
                if (_UseVat < 0.5) return posOS;
                float u = uv1.x;
                float f0 = UNITY_ACCESS_INSTANCED_PROP(DigglesMorph, _F0);
                float f1 = UNITY_ACCESS_INSTANCED_PROP(DigglesMorph, _F1);
                float bl = UNITY_ACCESS_INSTANCED_PROP(DigglesMorph, _Blend);
                float3 p0 = SAMPLE_TEXTURE2D_LOD(_Vat, sampler_Vat, float2(u, f0), 0).xyz;
                float3 p1 = SAMPLE_TEXTURE2D_LOD(_Vat, sampler_Vat, float2(u, f1), 0).xyz;
                return lerp(p0, p1, bl);
            }

            Varyings depthVert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                float3 posOS = ApplyVatD(IN.positionOS.xyz, IN.uv1);
                OUT.positionCS = TransformObjectToHClip(posOS);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 depthFrag(Varyings IN) : SV_Target
            {
                half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).a * _BaseColor.a;
                clip(alpha - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
