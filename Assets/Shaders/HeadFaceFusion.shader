Shader "Leaf/Head Face Fusion"
{
    Properties { _MainTex ("Feature Atlas", 2D) = "white" {} _Blend ("Fusion", Range(0,1)) = 0 _Saturation ("Saturation", Range(0,1)) = .35 }
    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" }
        ZWrite Off
        Offset -1, -1
        CGPROGRAM
        #pragma surface surf Standard alpha:fade vertex:vert
        #pragma target 3.0
        sampler2D _MainTex;
        float4x4 _WorldToFace;
        float4 _LeftEye, _RightEye, _Mouth;
        float3 _Front;
        float _Blend, _Saturation;
        struct Input { float3 worldPos; float3 worldNormal; };
        void vert(inout appdata_full v) { }
        float4 patch(float2 face, float4 region, float slot)
        {
            float2 uv = (face - region.xy) / max(region.zw, .001) + .5;
            float2 edge = abs(uv - .5) * 2;
            float mask = 1 - smoothstep(.55, 1, length(edge));
            float3 color = tex2D(_MainTex, float2((saturate(uv.x) + slot) / 3, saturate(uv.y))).rgb;
            return float4(color, mask);
        }
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float2 p = mul(_WorldToFace, float4(IN.worldPos,1)).xy;
            float4 a = patch(p, _LeftEye, 0), b = patch(p, _RightEye, 1), c = patch(p, _Mouth, 2);
            float weight = a.a + b.a + c.a;
            float3 color = (a.rgb*a.a + b.rgb*b.a + c.rgb*c.a) / max(.0001, weight);
            color = lerp(dot(color, float3(.299,.587,.114)).xxx, color, _Saturation);
            o.Albedo = color; o.Metallic = 0; o.Smoothness = .25;
            o.Alpha = saturate(weight) * _Blend * smoothstep(.25,.7,dot(normalize(IN.worldNormal),_Front));
        }
        ENDCG
    }
    FallBack Off
}
