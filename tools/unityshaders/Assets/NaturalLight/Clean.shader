// NaturalLight/Clean: made from tools/shaderpack/clean.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "NaturalLight/Clean"
{
    Properties
    {

    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One Zero
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma only_renderers d3d11 glcore metal vulkan
            #include "UnityCG.cginc"

            // (the GLSL has each of the game's matrices as its four columns; HLSL has them as rows)
            #define hlslcc_mtx4x4unity_ObjectToWorld transpose(unity_ObjectToWorld)
            #define hlslcc_mtx4x4unity_WorldToObject transpose(unity_WorldToObject)
            #define hlslcc_mtx4x4unity_MatrixVP transpose(UNITY_MATRIX_VP)
            #define hlslcc_mtx4x4unity_MatrixV transpose(UNITY_MATRIX_V)
            #define SampleLod(S, uv, lod) S.SampleLevel(sampler##S, uv, lod)
            #define SampleFlat(S, uv) S.SampleLevel(sampler##S, uv, 0.0)
            #define Mod(x, y) ((x) - (y) * floor((x) / (y)))
            #define GreaterThan(a, b) ((a) > (b))
            #define GreaterThanEqual(a, b) ((a) >= (b))
            #define LessThan(a, b) ((a) < (b))
            #define LessThanEqual(a, b) ((a) <= (b))
            float4 TexelFetch(Texture2D t, int2 p, int lod) { return t.Load(int3(p, lod)); }
            float4 TexelFetch(Texture3D t, int3 p, int lod) { return t.Load(int4(p, lod)); }
            int2 TextureSize(Texture2D t, int lod) { uint w, h; t.GetDimensions(w, h); return int2(w, h); }
            bool AllZero(float4 v) { return !any(v); }
            // gl_FragCoord: which pixel is being drawn, its rows counted from the bottom, as every picture's are read here. Direct3D counts
            // the rows of what it draws into from the top, and the game draws into its pictures upside down to make up for it (saying so with
            // _ProjectionParams.x at -1): so where it is drawing straight to the screen, the row is turned over, or each picture read by pixel
            // (the camera's depth, the smoke drawn small) would be read upside down.
            float4 FragCoord(float4 pos)
            {
            #if UNITY_UV_STARTS_AT_TOP
                if (_ProjectionParams.x > 0.0) pos.y = _ScreenParams.y - pos.y;
            #endif
                return pos;
            }

            float4 _CleanSheet;     // xy: 1 (the sheet as large as the screen), z: its depth, w: 1
            float4 _CleanMark;     // rgb: what to put there (black; a colour, to see where they are)
            Texture2D _CleanScene; SamplerState sampler_CleanScene;     // the picture at half its size

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                // One triangle that covers the whole screen, given in the screen's own coordinates (-1 to 1 each way).
                OUT.pos = float4(in_POSITION0.xy * _CleanSheet.xy, _CleanSheet.z, _CleanSheet.w);
                return OUT;
            }

            // Pixels that are not numbers, blacked out (Natural Light's Film.cs sets it going, before anything else is done
            // to the picture).
            //
            // Some of the game's own shaders give a pixel here and there that is not a number at all: a highlight that
            // comes out as nothing divided by nothing (the tower by the launch pad against a low sun has a line of them).
            // In the game as it comes they are stored as black and never seen. Kept in a picture that holds more than
            // white, which a film look needs, they are still there when the picture is developed: each comes out as a
            // bright yellow or green dot, the smoothing of edges over time keeps it where it was on the screen when the
            // camera turns, and a glow spreads it into a square. Unity's own cure for it does not work in the game's
            // OpenGL, where the test it uses is one that the card's compiler takes out as never true.
            //
            // So: the picture has just been shrunk to half its size (to measure how bright the view is), and in the
            // shrinking one such pixel spoils the whole dot it falls in. This draws black over the two-by-two pixels of
            // every spoilt dot, and leaves every other pixel as it is. What a number is made of is looked at, not what it
            // compares as, so that no compiler can take the test out.

            bool broken(float x) { return (asint(x) & 0x7F800000) == 0x7F800000; }

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                int2 last = TextureSize(_CleanScene, 0) - ((int2)(1));
                float4 c = TexelFetch(_CleanScene, min(((int2)(FragCoord(IN.pos).xy)) / 2, last), 0);
                if (!(broken(c.r) || broken(c.g) || broken(c.b))) discard;
                result = float4(_CleanMark.rgb, 1.0);
                return result;
            }
            ENDCG
        }
    }
}
