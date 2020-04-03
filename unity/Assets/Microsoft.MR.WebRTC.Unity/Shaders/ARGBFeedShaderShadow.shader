// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

Shader "Video/ARGBFeedShaderShadow"
{
    Properties
    {
        [HideInEditor][NoScaleOffset] _MainTex("Main Tex", 2D) = "white" {}
        //_MainTex("Main Tex", 2D) = "white" {}
        _Color("Shadow Color", Color) = (1,1,1,1)
        radius("Radius", Range(0,100)) = 15
        resolution("Resolution", float) = 800
        hstep("HorizontalStep", Range(0,1)) = 0.5
        vstep("VerticalStep", Range(0,1)) = 0.5
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.05
    }
        SubShader
    {
        Tags {"Queue" = "Transparent" "IgnoreProjector" = "true" "RenderType" = "Transparent"}
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile __ DISPLAY_MITIGATION_ON
            #pragma multi_compile __ APPLY_CHANNEL_MULTIPLIER_ON
            #pragma fragmentoption ARB_precision_hint_fastest

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
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            half4 _Color;
            float _Cutoff;
            float4 _MainTex_ST;
            float radius;
            float resolution;
            float hstep;
            float vstep;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(float2(v.uv.x, 1 - v.uv.y), _MainTex);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                //float2 uv = i.texcoord.xy;
                float4 sum = float4(0.0, 0.0, 0.0, 0.0);
                float2 tc = i.uv;

                //blur radius in pixels
                float blur = radius / resolution / 4;

                sum += tex2D(_MainTex, float2(tc.x - 4.0 * blur * hstep, tc.y - 4.0 * blur * vstep)) * 0.0162162162;
                sum += tex2D(_MainTex, float2(tc.x - 3.0 * blur * hstep, tc.y - 3.0 * blur * vstep)) * 0.0540540541;
                sum += tex2D(_MainTex, float2(tc.x - 2.0 * blur * hstep, tc.y - 2.0 * blur * vstep)) * 0.1216216216;
                sum += tex2D(_MainTex, float2(tc.x - 1.0 * blur * hstep, tc.y - 1.0 * blur * vstep)) * 0.1945945946;

                sum += tex2D(_MainTex, float2(tc.x, tc.y)) * 0.2270270270;

                sum += tex2D(_MainTex, float2(tc.x + 1.0 * blur * hstep, tc.y + 1.0 * blur * vstep)) * 0.1945945946;
                sum += tex2D(_MainTex, float2(tc.x + 2.0 * blur * hstep, tc.y + 2.0 * blur * vstep)) * 0.1216216216;
                sum += tex2D(_MainTex, float2(tc.x + 3.0 * blur * hstep, tc.y + 3.0 * blur * vstep)) * 0.0540540541;
                sum += tex2D(_MainTex, float2(tc.x + 4.0 * blur * hstep, tc.y + 4.0 * blur * vstep)) * 0.0162162162;
                //return float4(sum.rgb, 1);
                clip(sum - _Cutoff);
                float4 totalColor = saturate(sum * 8);
                totalColor.a *= totalColor.r;
                totalColor *= _Color;

                return ApplyColorCorrection(i.vertex.xy, totalColor);
            }
            ENDCG
        }
    }
}
