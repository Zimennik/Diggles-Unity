// Dig-mark plane renderer — the original DigMark draw job (Wiggles\Map\DigMark.cpp):
// an alpha-blended, vertex-colored, unlit triangle mesh on the fixed front plane, with
// • position wobble x += A·sin(y + 0.25·frame), y += A·sin(x + 0.25·frame)
// (FUN_0056af90:725-752, amp _DAT_00736ff8 = 0.03; marquee FUN_0056d0b0:512-539,
// amp _DAT_00737018 = 0.06 — per-material _WobbleAmp), and
// • brightness pulse rgb ×= 1 + ratio·sin(3x + 3y + 0.25·frame)
// (FUN_0056af90:612 — phase scales DAT_00737428 = 3.0, DAT_00737058 = 0.25/frame;
// the per-palette ratio 7/offset is baked into TEXCOORD0.x by DigMarkOverlay).
// 0.25·frame at the original's ~30 fps ≈ 7.5 rad/s of _Time.y.
// Mesh positions are in terrain-local space (x = native tile x, z = −native tile y,
// y = the native render plane z = 15.0 — FUN_0056af90:757 / FUN_0056d0b0:544).
// ZWrite off + LEqual: terrain between the camera and the plane still occludes the marks,
// but the marks never project down into the depth layers.
Shader "Diggles/DigMark"
{
    Properties
    {
        _WobbleAmp ("Wobble Amplitude (tiles)", Float) = 0.03
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        LOD 100

        Pass
        {
            Name "DigMarkUnlit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;
                float2 uv0        : TEXCOORD0;   // x = pulse amplitude ratio (7/135 or 7/45)
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half4  color       : COLOR;
            };

            CBUFFER_START(UnityPerMaterial)
                float _WobbleAmp;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                // Terrain-local → native tile frame (BdbPmpWorldBuilder.EmitVertexW: x = native x,
                // z = −native y). Wobble + pulse phases use the pre-wobble coords, like the native
                // post-pass which reads the build buffer and writes the render buffer.
                float nx = IN.positionOS.x;
                float ny = -IN.positionOS.z;
                float ft = 7.5 * _Time.y;              // 0.25 · frame @ ~30 fps
                float3 p = IN.positionOS.xyz;
                p.x += _WobbleAmp * sin(ny + ft);      // native x' = x + A·sin(y + 0.25f)
                p.z -= _WobbleAmp * sin(nx + ft);      // native y' = y + A·sin(x + 0.25f) (y = −z)
                OUT.positionHCS = TransformObjectToHClip(p);
                half pulse = sin(3.0 * (nx + ny) + ft);
                OUT.color = half4(saturate(IN.color.rgb * (1.0 + IN.uv0.x * pulse)), IN.color.a);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                return IN.color;
            }
            ENDHLSL
        }
    }
}
