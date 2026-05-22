Shader "Custom/UIBlur"
{
    Properties
    {
        _BlurSize   ("Blur Size",   Range(0.5, 10)) = 4
        _Iterations ("Iterations",  Range(1, 16))   = 8
        _Tint       ("Tint Color",  Color)           = (0, 0, 0, 0.35)
    }

    SubShader
    {
        Tags
        {
            "Queue"           = "Transparent"
            "RenderType"      = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Cull Off
        ZWrite Off
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "UIBlur"

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 screenPos   : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float  _BlurSize;
                int    _Iterations;
                float4 _Tint;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.screenPos   = ComputeScreenPos(OUT.positionHCS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.screenPos.xy / IN.screenPos.w;

                // Gaussian blur — accumulates samples around the pixel in screen space.
                float2 texelSize = _BlurSize / _ScreenParams.xy;

                half4 color = 0;
                float weight  = 0;
                int   half_n  = _Iterations / 2;

                for (int x = -half_n; x <= half_n; x++)
                {
                    for (int y = -half_n; y <= half_n; y++)
                    {
                        // Simple triangular weight: centre is heavier.
                        float w = (half_n + 1 - abs(x)) * (half_n + 1 - abs(y));
                        half3 sampleColor = SampleSceneColor(uv + float2(x, y) * texelSize);
                        color += half4(sampleColor, 1.0h) * w;
                        weight += w;
                    }
                }

                color /= weight;

                // Tint overlay blended on top of the blurred scene.
                color.rgb = lerp(color.rgb, _Tint.rgb, _Tint.a);
                color.a   = 1.0;
                return color;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
