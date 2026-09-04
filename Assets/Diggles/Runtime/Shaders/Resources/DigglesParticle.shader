// Diggles particle billboard — additive, premultiplied. The atlas cell UV is baked per-vertex by the
// CPU emitter (CpuParticleEmitter.Render), so this shader is deliberately minimal: sample the atlas,
// take colour from RGB and coverage from alpha (tex.rgb * tex.a), tint by the per-particle vertex
// COLOR (the type's age-driven diffuse/specular), scale by _Intensity (the source atlases are dim by
// design), and add additively (Blend One One) like the original D3D TLVERTEX path.
Shader "Diggles/Particle"
{
    Properties
    {
        _BaseMap ("Atlas", 2D) = "white" {}
        _Intensity ("Intensity", Float) = 3.0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src", Float) = 1   // One
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst", Float) = 1   // One (additive)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        LOD 100

        Pass
        {
            Name "Particle"
            Tags { "LightMode" = "UniversalForward" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;    // particle centre (world/object space)
                float2 uv         : TEXCOORD0;
                float3 spec       : TEXCOORD1;   // per-particle specular rgb (added after texture)
                float2 corner     : TEXCOORD2;   // 2D corner offset, added in view space (camera-facing)
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 spec        : TEXCOORD1;
                float4 color       : COLOR;
            };

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Intensity;
                float _SrcBlend;
                float _DstBlend;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                // View-space billboard: transform the particle centre to view space, add the 2D corner
                // offset there (so the quad always faces the rendering camera), then project.
                float3 wp = TransformObjectToWorld(IN.positionOS.xyz);
                float3 vp = mul(UNITY_MATRIX_V, float4(wp, 1.0)).xyz;
                vp.xy += IN.corner;
                OUT.positionHCS = mul(UNITY_MATRIX_P, float4(vp, 1.0));
                OUT.uv = IN.uv;
                OUT.spec = IN.spec;
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                // Original D3D path: texture × diffuse + specular, shaped by the texture alpha.
                // (spec is 0 for legacy types, so they render exactly as before.)
                half3 rgb = (tex.rgb * IN.color.rgb + IN.spec) * tex.a * IN.color.a * _Intensity;
                return half4(rgb, tex.a * IN.color.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
