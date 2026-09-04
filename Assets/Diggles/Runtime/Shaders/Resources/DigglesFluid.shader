// CFluid volume material. Geometry supplies the executable's per-cell opacity through vertex color.
Shader "Diggles/Fluid"
{
    Properties
    {
        _MainTex ("Fluid Texture", 2D) = "white" {}
        _AlphaTex ("Native Alpha Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,0.75)
        _FlowSpeed ("Flow Speed", Vector) = (0.025,-0.012,0,0)
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+20" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "FluidUnlit"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_AlphaTex);
            SAMPLER(sampler_AlphaTex);
            TEXTURE2D(_FowTex);
            SAMPLER(sampler_FowTex);
            float4 _FowParams;
            float4x4 _FowWorldToNative;
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
                float4 _FlowSpeed;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                float3 positionWS : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex) + _FlowSpeed.xy * _Time.y;
                output.color = input.color;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 sample = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                // The shipped 1x1 alpha.tga stores the native mask in RGB; its TGA alpha byte is zero.
                half alphaMask = SAMPLE_TEXTURE2D(_AlphaTex, sampler_AlphaTex, input.uv).r;
                half3 finalRgb = sample.rgb * _Color.rgb * input.color.rgb;
                half finalAlpha = alphaMask * _Color.a * input.color.a;
                if (_FowParams.z > 0.5)
                {
                    // Same bilinear field and depth-gray law as DigglesGround. Native fluid
                    // additionally rejects cells whose four fog corners are all black.
                    float3 nat = mul(_FowWorldToNative, float4(input.positionWS, 1.0)).xyz;
                    float2 fowUv = float2((nat.x + 0.5) / _FowParams.x,
                                          (-nat.z + 0.5) / _FowParams.y);
                    half2 fow = SAMPLE_TEXTURE2D(_FowTex, sampler_FowTex, fowUv).rg;
                    float grayFactor = saturate((1.0 - (nat.y - 7.0) * 0.125 * 1.02) * fow.r);
                    finalRgb = finalRgb * (1.0 - grayFactor) +
                        grayFactor * float3(170.0, 170.0, 155.0) / 255.0;
                    finalRgb *= fow.g;
                    finalAlpha *= fow.g;
                }
                return half4(finalRgb, finalAlpha);
            }
            ENDHLSL
        }
    }
}
