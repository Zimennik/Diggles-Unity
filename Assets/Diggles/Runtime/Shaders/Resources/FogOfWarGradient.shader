// Fog-of-war gradient patch material — the port's stand-in for the native mat-21→22
// cave-backdrop alpha fade. Fully-open tiles emit no terrain in the port (load-time block 59 /
// oob / beyond the render bbox), so nothing carries the terrain shader's black-corner gradient
// there and the black boundary would end in a hard rectangle. FogOfWarOverlay lays a quad over
// each such tile at its fill depth and this shader paints it black with alpha = 1 − fow.g — the
// same decoded black-corner indicator and bilinear sample the terrain shaders multiply with
// (DigglesGround/DigglesModel "finalRgb *= fow.g"), so the fade matches across the
// meshless/meshed seam: opaque on the black lattice lines (where the opaque covers/side walls
// stand), transparent once the tile's four corners are lit. The native alpha law is the same
// value (mat-22 vertex alpha = bias + 0x100 over 256 ≡ the decoded indicator); the native fades
// its rock-textured backdrop out over the black clear, the port fades black in over its void.
// See Docs/FOG_OF_WAR_RE.md.
Shader "Diggles/FowGradient"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "FowGradient"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Same fog globals as DigglesGround (set by FogOfWarPresenter each frame).
            TEXTURE2D(_FowTex); SAMPLER(sampler_FowTex);
            float4   _FowParams;          // (mapW, mapH, fogActive, 0)
            float4x4 _FowWorldToNative;   // world → terrain-local (native render frame)

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posIn = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = posIn.positionCS;
                OUT.positionWS  = posIn.positionWS;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                if (_FowParams.z < 0.5) return half4(0, 0, 0, 0);   // fog off → invisible
                float3 nat = mul(_FowWorldToNative, float4(IN.positionWS, 1.0)).xyz;
                float2 fowUv = float2((nat.x + 0.5) / _FowParams.x,
                                      (-nat.z + 0.5) / _FowParams.y);
                half g = SAMPLE_TEXTURE2D(_FowTex, sampler_FowTex, fowUv).g;
                return half4(0, 0, 0, 1.0 - g);
            }
            ENDHLSL
        }
    }
}
