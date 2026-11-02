Shader "Desktop Pet/Soft Anime"
{
    Properties
    {
        [MainTexture] _BaseMap("Painted Texture", 2D) = "white" {}
        [MainColor] _BaseColor("Texture Tint", Color) = (1, 1, 1, 1)
        _ShadeColor("Soft Shadow Tint", Color) = (0.76, 0.71, 0.82, 1)
        _ShadeThreshold("Shadow Threshold", Range(0, 1)) = 0.45
        _ShadeSoftness("Shadow Softness", Range(0.01, 0.5)) = 0.2
        _Brightness("Texture Brightness", Range(0.5, 1)) = 0.96
        _Saturation("Painted Color Saturation", Range(0.5, 1.5)) = 1
        _MidtoneContrast("Painted Midtone Contrast", Range(0.8, 1.3)) = 1
        _LightInfluence("Light Intensity Influence", Range(0, 1)) = 0.2
        _LightColorInfluence("Light Color Influence", Range(0, 1)) = 0.08
        _SpecularStrength("Soft Highlight Strength", Range(0, 0.1)) = 0.015
        _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.2
        [Toggle] _AlphaClip("Alpha Clipping", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 0
        [HideInInspector] _Surface("Surface", Float) = 0
        [HideInInspector] _SrcBlend("Source Blend", Float) = 1
        [HideInInspector] _DstBlend("Destination Blend", Float) = 0
        [HideInInspector] _ZWrite("Depth Write", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
        LOD 150
        Cull [_Cull]

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _ShadeColor;
            half _ShadeThreshold;
            half _ShadeSoftness;
            half _Brightness;
            half _Saturation;
            half _MidtoneContrast;
            half _LightInfluence;
            half _LightColorInfluence;
            half _SpecularStrength;
            half _Cutoff;
            half _AlphaClip;
            half _Surface;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            half3 normalWS : TEXCOORD1;
            float3 positionWS : TEXCOORD2;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings PetVertex(Attributes input)
        {
            Varyings output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
            output.positionCS = position.positionCS;
            output.positionWS = position.positionWS;
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
            return output;
        }

        half4 PetTexture(float2 uv)
        {
            half4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv) * _BaseColor;
            if (_AlphaClip > 0.5h) clip(color.a - _Cutoff);
            return color;
        }
        ENDHLSL

        Pass
        {
            Name "SoftAnimeForward"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Blend [_SrcBlend] [_DstBlend], One OneMinusSrcAlpha
            ZWrite [_ZWrite]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex PetVertex
            #pragma fragment PetFragment
            #pragma multi_compile_instancing

            half4 PetFragment(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 textureColor = PetTexture(input.uv);
                half3 normal = NormalizeNormalPerPixel(input.normalWS);
                normal *= IS_FRONT_VFACE(frontFace, 1.0h, -1.0h);
                Light mainLight = GetMainLight();
                half wrappedLight = dot(normal, mainLight.direction) * 0.5h + 0.5h;
                half ramp = smoothstep(_ShadeThreshold - _ShadeSoftness,
                    _ShadeThreshold + _ShadeSoftness, wrappedLight);
                half lightPeak = max(mainLight.color.r, max(mainLight.color.g, mainLight.color.b));
                half3 lightTint = mainLight.color / max(lightPeak, 0.001h);
                lightTint = lerp(half3(1, 1, 1), lightTint, _LightColorInfluence * saturate(lightPeak));
                // Painted MMD textures already contain much of their lighting. Bound
                // the light contribution instead of adding HDR diffuse/ambient energy.
                half brightness = _Brightness * lerp(1.0h, saturate(lightPeak), _LightInfluence);
                // These controls default to identity. A model-specific material can
                // recover separation in pastel albedo without exposure or a global LUT.
                half luminance = dot(textureColor.rgb, half3(0.2126h, 0.7152h, 0.0722h));
                half3 painted = max(half3(0, 0, 0), lerp(luminance.xxx, textureColor.rgb, _Saturation));
                painted = pow(painted, _MidtoneContrast);
                half3 color = painted * lerp(_ShadeColor.rgb, half3(1, 1, 1), ramp) * brightness * lightTint;
                half3 viewDirection = GetWorldSpaceNormalizeViewDir(input.positionWS);
                half3 halfDirection = SafeNormalize(mainLight.direction + viewDirection);
                half highlight = pow(saturate(dot(normal, halfDirection)), 28.0h) * _SpecularStrength * ramp * saturate(lightPeak);
                color = lerp(color, painted, saturate(highlight));
                return half4(saturate(color), _Surface > 0.5h ? textureColor.a : 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex PetVertex
            #pragma fragment PetDepthFragment
            #pragma multi_compile_instancing
            half PetDepthFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                PetTexture(input.uv);
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
