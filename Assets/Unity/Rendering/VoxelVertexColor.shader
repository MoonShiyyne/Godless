// The world's only material, for now: colour comes from the mesh, lighting
// from the scene. Ambient occlusion is already baked into the vertex colours
// by the mesher (S06), so this shader only has to light them.
//
// The project renders in linear colour space and vertex colours arrive as
// sRGB bytes, which Unity does not convert. Without GammaToLinearSpace every
// material renders pale and the S0A value separation — the contrast rule —
// is quietly flattened on screen.
//
// S2V. The cutaway: while it is on, a column-by-column height map says where
// each building is opened, and anything cuttable above it is not drawn — nor
// does it cast a shadow, so a bed under a removed roof is lit. People draw
// with this shader too, uncuttable and instanced.
//
// Cutting leaves holes: a face between two solid voxels was never meshed, so
// the ground under a wall that is no longer drawn has no top to see. While the
// cutaway is on the view draws back faces as well, and a back face is shaded
// as a dark cross-section of whatever it belongs to — a cut solid reads solid.
Shader "Godless/VoxelVertexColor"
{
    Properties
    {
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _Cuttable ("Cuttable", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 200
        Cull [_Cull]

        CGPROGRAM
        #pragma surface surf Lambert vertex:vert fullforwardshadows addshadow
        #pragma multi_compile_instancing
        #pragma target 3.0

        fixed4 _Tint;
        float _Cuttable;

        // Set globally by CutawayView.
        sampler2D _GodlessCutMap;
        float4 _GodlessCutSize;
        float _GodlessCutOn;

        struct Input
        {
            float4 vcol;
            float3 worldPos;
            float3 worldNormal;
            float face : VFACE;
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.vcol = v.color;
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            if (_GodlessCutOn > 0.5 && _Cuttable > 0.5)
            {
                // A hair inside the voxel this face belongs to, so a wall's
                // outer face and a floor's top answer for their own column.
                float3 p = IN.worldPos - IN.worldNormal * 0.01;
                float2 uv = (floor(p.xz) + 0.5) * _GodlessCutSize.zw;
                float cut = tex2Dlod(_GodlessCutMap, float4(uv, 0, 0)).r;
                clip(cut - p.y);
            }

            float3 c = IN.vcol.rgb * _Tint.rgb;
            #ifndef UNITY_COLORSPACE_GAMMA
            c = GammaToLinearSpace(c);
            #endif
            if (IN.face < 0)
            {
                o.Albedo = 0;
                o.Emission = c * 0.22;
            }
            else o.Albedo = c;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
