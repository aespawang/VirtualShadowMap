Shader "VSM/VisPhyTexture"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Range ("Range", Vector) = (0, 1, 0, 0)
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalRenderPipeline"
            "RenderType"="Opaque"
        }

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #pragma shader_feature ENABLE_PHY_PAGE_STATUS_DEBUG_BUFFER

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            
            float4 _Range;
            
            float4 _VSMPageGridParams;
            
            struct PhyPageStatus
            {
                int VirPageIdx;
                int Status;
            };
            
            #if ENABLE_PHY_PAGE_STATUS_DEBUG_BUFFER
            StructuredBuffer<PhyPageStatus> _VSMPhyPageStatusDebugBuffer;
            #endif

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 positionWS = TransformObjectToWorld(IN.positionOS);
                OUT.positionHCS = TransformWorldToHClip(positionWS);
                OUT.uv = IN.uv;
                return OUT;
            }

            float4 frag(Varyings IN) : SV_Target
            {
                float depth = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).r;
                depth = (depth - _Range.x) / (_Range.y - _Range.x + 0.0001);
                
                int2 phyPageLoc = (int2)(IN.uv * _VSMPageGridParams.zw);
                #if ENABLE_PHY_PAGE_STATUS_DEBUG_BUFFER
                int phyPageIdx = (_VSMPageGridParams.w - 1 - phyPageLoc.y) * _VSMPageGridParams.z + phyPageLoc.x;
                if (_VSMPhyPageStatusDebugBuffer[phyPageIdx].Status == 0) return float4(0, 0, 0, 1);
                #endif
                float4 color = float4(depth, depth, depth, 1);
                color *= (phyPageLoc.x + phyPageLoc.y) % 2 == 0 ? float4(1, 0, 0, 1) : float4(1, 1, 0, 1);
                return color;
            }
            ENDHLSL
        }
    }
}