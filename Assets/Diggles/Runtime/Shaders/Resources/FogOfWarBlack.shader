// Fog-of-war black volume material — the native fog quads' material 17 (untextured, opaque:
// front covers FUN_004cca20 / side walls FUN_004c6b20 carry zeroed color dwords and must cover,
// so it cannot be alpha-blended). Unlike the alpha DigMark plane this writes depth (queue
// Geometry), so entities/particles inside the black volume are occluded, not just tinted.
// _Color = the native set_brightness cover offset (DAT_0076A32C/330/334, default 0 ⇒ pure
// black); exposed for a future set_brightness port. See Docs/FOG_OF_WAR_RE.md.
Shader "Diggles/FowBlack"
{
    Properties
    {
        _Color ("Color", Color) = (0, 0, 0, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "FowBlack"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float  depthY      : TEXCOORD0;   // object-space y = the native render depth z
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.depthY = IN.positionOS.y;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // Flat black, ungraded: the native cover color dwords are zeroed, so never-seen
                // volume carries no depth-gray or specular term.
                return half4(_Color.rgb, 1.0);
            }
            ENDHLSL
        }

        // DepthOnly so the URP depth prepass sees the covers too (they occlude in depth-based
        // effects the same way they occlude in color).
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthAttr { float4 positionOS : POSITION; };
            struct DepthVary { float4 positionCS : SV_POSITION; };

            DepthVary depthVert (DepthAttr IN)
            {
                DepthVary OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 depthFrag () : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
