// The world's only material, for now: colour comes from the mesh, lighting
// from the scene. Ambient occlusion is already baked into the vertex colours
// by the mesher (S06), so this shader only has to light them.
//
// The project renders in linear colour space and vertex colours arrive as
// sRGB bytes, which Unity does not convert. Without GammaToLinearSpace every
// material renders pale and the S0A value separation — the contrast rule —
// is quietly flattened on screen.
Shader "Godless/VoxelVertexColor"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Lambert vertex:vert fullforwardshadows
        #pragma target 3.0

        fixed4 _Tint;

        struct Input
        {
            float4 vcol;
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.vcol = v.color;
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            float3 c = IN.vcol.rgb * _Tint.rgb;
            #ifndef UNITY_COLORSPACE_GAMMA
            c = GammaToLinearSpace(c);
            #endif
            o.Albedo = c;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
