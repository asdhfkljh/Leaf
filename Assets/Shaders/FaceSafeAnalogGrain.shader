Shader "Leaf/Face Safe Analog Grain"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _FaceMask ("Face Mask", 2D) = "black" {}
        _Intensity ("Grain Intensity", Range(0, 1)) = 0.52
        _GrainSize ("Grain Size", Range(0.25, 4)) = 1.2
        _ChromaGrain ("Coloured Grain", Range(0, 1)) = 0.36
        _ChromaticAberration ("RGB Split", Range(0, 3)) = 0.55
        _ShadowLift ("Shadow Grain Boost", Range(0, 2)) = 0.8
        _Vignette ("Vignette", Range(0, 1)) = 0.18
        _FaceFeather ("Face Edge Feather", Range(0, 1)) = 0.2
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex, _FaceMask;
            float4 _MainTex_TexelSize;
            float _Intensity, _GrainSize, _ChromaGrain, _ChromaticAberration;
            float _ShadowLift, _Vignette, _FaceFeather;

            float Hash12(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                float t = floor(_Time.y * 24.0);
                float2 grainUv = floor(uv / (_MainTex_TexelSize.xy * _GrainSize));
                float lumaNoise = Hash12(grainUv + t) * 2.0 - 1.0;
                float3 chromaNoise = float3(
                    Hash12(grainUv + t * 1.71),
                    Hash12(grainUv + t * 2.37 + 19.1),
                    Hash12(grainUv + t * 3.11 + 71.7)) * 2.0 - 1.0;

                float2 split = _MainTex_TexelSize.xy * _ChromaticAberration;
                float3 source;
                source.r = tex2D(_MainTex, uv + float2(split.x, 0)).r;
                source.g = tex2D(_MainTex, uv).g;
                source.b = tex2D(_MainTex, uv - float2(split.x, 0)).b;
                float luminance = dot(source, float3(0.2126, 0.7152, 0.0722));
                float shadowWeight = 1.0 + (1.0 - luminance) * _ShadowLift;
                float3 grain = lerp(lumaNoise.xxx, chromaNoise, _ChromaGrain);
                float3 noisy = saturate(source + grain * (_Intensity * 0.17 * shadowWeight));

                float2 fromCentre = uv * 2.0 - 1.0;
                float vignette = 1.0 - dot(fromCentre, fromCentre) * _Vignette;
                noisy *= saturate(vignette);

                // White in the mask means the face remains completely clean.
                float face = tex2D(_FaceMask, uv).r;
                face = smoothstep(0.02, max(0.03, _FaceFeather), face);
                return float4(lerp(noisy, tex2D(_MainTex, uv).rgb, face), 1);
            }
            ENDCG
        }
    }
}
