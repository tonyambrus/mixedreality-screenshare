// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

Shader "Video/ARGBFeedShader (unlit)"
{
    Properties
    {
        [HideInEditor][NoScaleOffset] _MainTex("Main Tex", 2D) = "black" {}
        //_MainTex("Main Tex", 2D) = "white" {}
        [Toggle(UVFLIPY)]_UVFlipY("Flip Y UV?", Int) = 1
        [Toggle(ALPHACLIP)]_Enable1Bit("Alpha Clip?", Int) = 0
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.05
        [Toggle(ULTRACLIP)]_EnableUltraClip("Ultra AlphaClip?", Int) = 0
        _UltraSpread("Ultra Clip Spread", Range(0.5, 10.0)) = 1

        [Space(20)]
		[Header(Clipping Settings)]
        [Toggle(PLANECLIP)]_EnableClipPlane("Enable CLIPPING Plane", Int) = 0
        _ClipPlane("Clip Plane", Vector) = (0.0, 1.0, 0.0, 0.0)
        _ClippingPlaneBorderWidth("Clipping Plane Border Width", Range(0.001, 1.0)) = 0.025
		_ClippingPlaneBorderColor("Clipping Plane Border Color", Color) = (1.0, 0.2, 0.0, 1.0)


        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Cull", Float) = 2

    }
        SubShader
    {
            Cull[_Cull]
        Pass
        {

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature ALPHACLIP
            #pragma shader_feature UVFLIPY
            #pragma shader_feature PLANECLIP
            #pragma shader_feature ULTRACLIP
            #pragma multi_compile_instancing
            #pragma multi_compile __ DISPLAY_MITIGATION_ON
            #pragma multi_compile __ APPLY_CHANNEL_MULTIPLIER_ON

            #include "UnityCG.cginc"
            #include "../../ChaosCore/Shaders/ColorCorrection.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float3 posWorld : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;
#ifdef ALPHACLIP
            half _Cutoff;
            half _UltraSpread;
#endif
#ifdef PLANECLIP
			float4 _ClipPlane;
			float4 _ClipPlaneRadiusLocation;
			half _ClippingRadius;
			fixed _ClippingPlaneBorderWidth;
			fixed3 _ClippingPlaneBorderColor;
#endif

			inline float PointVsPlane(float3 worldPosition, float4 plane)
			{
				float3 planePosition = plane.xyz * plane.w;
				return dot(worldPosition - planePosition, plane.xyz);
			}

            inline float toluma(float4 col)
            {
                return col.r * 0.3 + col.g * 0.59 + col.b * 0.11;
            }

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                
#ifdef UVFLIPY
                o.uv = TRANSFORM_TEX(float2(v.uv.x, 1 - v.uv.y), _MainTex);
#else
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
#endif
                o.posWorld = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 col = tex2D(_MainTex, i.uv);


#ifdef ALPHACLIP
                float lum = toluma(col);
                float cutoff = _Cutoff;
#ifdef ULTRACLIP
                lum += toluma(tex2D(_MainTex, i.uv + float2(-_MainTex_TexelSize.x, _MainTex_TexelSize.y) * _UltraSpread));
                lum += toluma(tex2D(_MainTex, i.uv + float2(0, _MainTex_TexelSize.y) * _UltraSpread));
                lum += toluma(tex2D(_MainTex, i.uv + float2(_MainTex_TexelSize.x, _MainTex_TexelSize.y) * _UltraSpread));

                lum += toluma(tex2D(_MainTex, i.uv + float2(-_MainTex_TexelSize.x, 0) * _UltraSpread));
                lum += toluma(tex2D(_MainTex, i.uv + float2(_MainTex_TexelSize.x, 0) * _UltraSpread));

                lum += toluma(tex2D(_MainTex, i.uv + float2(-_MainTex_TexelSize.x, -_MainTex_TexelSize.y) * _UltraSpread));
                lum += toluma(tex2D(_MainTex, i.uv + float2(0, -_MainTex_TexelSize.y) * _UltraSpread));
                lum += toluma(tex2D(_MainTex, i.uv + float2(_MainTex_TexelSize.x, -_MainTex_TexelSize.y) * _UltraSpread));

                cutoff *= 9;
#endif
                clip(lum - cutoff);
#endif

#ifdef PLANECLIP
				float planeDistance = PointVsPlane(i.posWorld.xyz, _ClipPlane);

				fixed3 planeBorderColor = lerp(_ClippingPlaneBorderColor, fixed3(0.0, 0.0, 0.0), planeDistance / _ClippingPlaneBorderWidth);

				col.rgb += planeBorderColor * ((planeDistance < _ClippingPlaneBorderWidth) ? 1.0 : 0.0);
                col *= (planeDistance > 0.0);

                clip(col.a - _Cutoff);
#endif

                return ApplyColorCorrection(i.vertex.xy, col);
            }
            ENDCG
        }
    }
}
