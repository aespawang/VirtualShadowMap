Shader "VSM/CastShadow"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            
            float4x4 _VSMLightMatrix;

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float4 positionWS = mul(GetObjectToWorldMatrix(), float4(IN.positionOS, 1.0));
                OUT.positionCS = mul(_VSMLightMatrix, positionWS);
                return OUT;
            }

            float frag(Varyings IN) : SV_Depth
            {
                return IN.positionCS.z / IN.positionCS.w;
            }
            
            ENDHLSL
        }
    }
}
