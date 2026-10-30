Shader "Leaf/FaceReveal" {
 Properties { _MainTex("Portrait", 2D) = "white" {} _Color("Tint", Color) = (1,1,1,1) }
 SubShader {
  Tags { "Queue"="Transparent" "RenderType"="Transparent" }
  Blend SrcAlpha OneMinusSrcAlpha
  ZWrite Off
  Cull Off
  Pass {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
   struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
   sampler2D _MainTex; fixed4 _Color;
   v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
   fixed4 frag(v2f i):SV_Target { return tex2D(_MainTex,i.uv)*_Color; }
   ENDCG
  }
 }
}
