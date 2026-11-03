// The user's own hand over everything virtual (iPhone 11 port). A second quad exactly over the
// passthrough screen draws the camera image again, last, but only where the people stencil in its
// alpha (PassthroughPeople) says a person is: so a real hand reaching in front of a panel hides the
// panel, instead of the panel covering the hand. The stencil is coarse (256 x 192), so its edge is
// softened rather than cut. In Resources so Shader.Find has it in a build.
Shader "Hidden/CardboardXR/PassthroughHand"
{
    Properties
    {
        [MainTexture] _BaseMap ("Camera Image", 2D) = "black" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Overlay+10"
        }

        Pass
        {
            Name "PassthroughHand"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
            CBUFFER_END

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
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 camera = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                return half4(camera.rgb, smoothstep(0.3, 0.7, camera.a));
            }
            ENDHLSL
        }
    }
}
