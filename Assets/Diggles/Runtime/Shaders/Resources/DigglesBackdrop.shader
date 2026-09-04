// Diggles panorama backdrop layer — the port of the native "GroundRenderer"/"GroundRenderer2"
// pre-pass nodes (FUN_0047f440 / FUN_00480720): plain alpha-blended texture quads drawn before
// the terrain with no depth test/write (native TLVERTs at z=0.02 in a sort-key 1..7 background
// pass). The opaque terrain draws later (Geometry queue) and paints over them — same final image.
//
// Two vertex paths, selected per material by _ScreenAnchored:
// * 1 (the 6 panorama layers): positionOS.xy = viewport coords 0..1 — emitted straight to clip
// space, no camera transform. This is the native TLVERT law: the quads are glued to the
// screen and all apparent motion lives in the UVs (FUN_004802c0 counter-pan).
// * 0 (the ground carpet): ordinary world-space geometry (the native carpet is world-space,
// FUN_00480720) through the regular MVP transform.
// Queue is set per material from BackdropPresenter (1000 + layer, carpet last).
Shader "Diggles/Backdrop"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Tint    ("Tint", Color) = (1,1,1,1)
        _ScreenAnchored ("Screen anchored (1 = viewport-space verts)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Background" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Backdrop"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always   // native pre-pass has no z-test; queue < Geometry so terrain covers it
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _Tint;
            float  _ScreenAnchored;

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                if (_ScreenAnchored > 0.5)
                {
                    // Viewport (0..1, y up) → clip space at the far plane. _ProjectionParams.x
                    // carries the render-target y-flip the projection matrix would have baked in.
                    float2 ndc = IN.positionOS.xy * 2.0 - 1.0;
                    ndc.y *= _ProjectionParams.x;
                    OUT.positionCS = float4(ndc, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                }
                else
                {
                    OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                }
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * _Tint;
                return c;
            }
            ENDHLSL
        }
    }
}
