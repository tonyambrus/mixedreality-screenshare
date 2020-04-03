// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

Shader "Video/YUVFeedShader (unlit)"
{
    Properties
    {
        [Toggle(MIRROR)] _Mirror("Horizontal Mirror", Float) = 0
        [Toggle(DO_Y_FLIP)] _DoYFlip("Do Y Flip", Float) = 0
        [Toggle(SATURATE)] _Saturate("Saturate", Float) = 1
        [HideInEditor][NoScaleOffset] _YPlane("Y plane", 2D) = "white" {}
        [HideInEditor][NoScaleOffset] _UPlane("U plane", 2D) = "white" {}
        [HideInEditor][NoScaleOffset] _VPlane("V plane", 2D) = "white" {}
    }
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile __ MIRROR
            #pragma shader_feature SATURATE
            #pragma multi_compile __ DO_Y_FLIP

            #pragma multi_compile __ DISPLAY_MITIGATION_ON
            #pragma multi_compile __ APPLY_CHANNEL_MULTIPLIER_ON

            #include "UnityCG.cginc"

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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
#if UNITY_UV_STARTS_AT_TOP && DO_Y_FLIP
                o.uv.y = 1 - v.uv.y;
#endif
#ifdef MIRROR
                o.uv.x = 1 - v.uv.x;
#endif
                return o;
            }

            sampler2D _YPlane;
            sampler2D _UPlane;
            sampler2D _VPlane;

            half3 yuv2rgb(half3 yuv)
            {
                // The YUV to RBA conversion, please refer to: http://en.wikipedia.org/wiki/YUV
                // Y'UV420p (I420) to RGB888 conversion section.
                half y_value = yuv[0];
                half u_value = yuv[1];
                half v_value = yuv[2];
#if SATURATE
                float r = saturate(1.164 * (y_value - (16.0 / 255.0)) + 1.793 * (v_value - 0.5));
                float g = saturate(1.164 * (y_value - (16.0 / 255.0)) - 0.534 * (v_value - 0.5) - 0.213 * (u_value - 0.5));
                float b = saturate(1.164 * (y_value - (16.0 / 255.0)) + 2.115 * (u_value - 0.5));
#else
                half r = y_value + 1.370705 * (v_value - 0.5);
                half g = y_value - 0.698001 * (v_value - 0.5) - (0.337633 * (u_value - 0.5));
                half b = y_value + 1.732446 * (u_value - 0.5);
#endif
                return half3(r, g, b);
            }

            fixed3 frag (v2f i) : SV_Target
            {
                half3 yuv;
                yuv.x = tex2D(_YPlane, i.uv).r;
                yuv.y = tex2D(_UPlane, i.uv).r;
                yuv.z = tex2D(_VPlane, i.uv).r;
                return half4(yuv2rgb(yuv), 1);
            }
            ENDCG
        }
    }
}
