// ARKit's people stencil into the passthrough texture's alpha (iPhone 11 port). PassthroughController
// draws it after the camera image, with the same widened display transform, so each texel's alpha
// says whether a person (the user's own hand and arm) is there. PassthroughHand then draws those
// texels again over everything virtual. In Resources so Shader.Find has it in a build.
Shader "Hidden/CardboardXR/PassthroughPeople"
{
    Properties
    {
        _HumanStencil ("Human Stencil", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "PeopleToAlpha"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask A

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_HumanStencil);
            SAMPLER(sampler_HumanStencil);
            // The same widened transform the camera image was drawn with: screen UV to camera UV.
            float4x4 _UnityDisplayTransform;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = mul(float4(input.uv, 1.0, 1.0), _UnityDisplayTransform).xy;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return half4(0.0, 0.0, 0.0, SAMPLE_TEXTURE2D(_HumanStencil, sampler_HumanStencil, input.uv).r);
            }
            ENDHLSL
        }
    }
}
