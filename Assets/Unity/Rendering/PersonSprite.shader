// S2G. A person, as a camera-facing cut-out drawn with GPU instancing.
// The sprite's red channel is how much of the activity colour shows (the
// clothes), green marks skin, and alpha is the silhouette. One draw call for
// a whole village.
Shader "Godless/PersonSprite"
{
    Properties
    {
        _MainTex ("Sprite", 2D) = "white" {}
        _Cutoff ("Cutoff", Range(0, 1)) = 0.5
        _Skin ("Skin", Color) = (0.86, 0.68, 0.52, 1)
        _Outline ("Outline", Color) = (0.07, 0.06, 0.05, 1)
    }
    SubShader
    {
        Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" "IgnoreProjector" = "True" }
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed _Cutoff;
            fixed4 _Skin;
            fixed4 _Outline;

            UNITY_INSTANCING_BUFFER_START(Props)
                UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                fixed4 t = tex2D(_MainTex, i.uv);
                clip(t.a - _Cutoff);
                fixed4 c = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
                fixed3 body = lerp(_Outline.rgb, c.rgb, t.r);
                return fixed4(lerp(body, _Skin.rgb, t.g), 1);
            }
            ENDCG
        }
    }
}
