// NaturalLight/Shafts: made from tools/shaderpack/shafts.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "NaturalLight/Shafts"
{
    Properties
    {
        _ShaftRays ("ShaftRays", 2D) = "" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One One
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
            #define SampleGradient(S, uv, dx, dy) S.SampleGrad(sampler##S, uv, dx, dy)
            #define Mod(x, y) ((x) - (y) * floor((x) / (y)))
            #define GreaterThan(a, b) ((a) > (b))
            #define GreaterThanEqual(a, b) ((a) >= (b))
            #define LessThan(a, b) ((a) < (b))
            #define LessThanEqual(a, b) ((a) <= (b))
            float4 TexelFetch(Texture2D t, int2 p, int lod) { return t.Load(int3(p, lod)); }
            float4 TexelFetch(Texture3D t, int3 p, int lod) { return t.Load(int4(p, lod)); }
            int2 TextureSize(Texture2D t, int lod) { uint w, h; t.GetDimensions(w, h); return int2(w, h); }
            bool AllZero(float4 v) { return !any(v); }
            // (one of a vector's numbers, chosen by a variable, read and given a new value: see chosen in tools/unityshaders/port.py)
            float Pick(float2 v, int n) { return n <= 0 ? v.x : v.y; }
            float Pick(float3 v, int n) { return n <= 0 ? v.x : n == 1 ? v.y : v.z; }
            float Pick(float4 v, int n) { return n <= 0 ? v.x : n == 1 ? v.y : n == 2 ? v.z : v.w; }
            float2 Put(float2 v, int n, float x) { return float2(n == 0 ? x : v.x, n == 1 ? x : v.y); }
            float3 Put(float3 v, int n, float x) { return float3(n == 0 ? x : v.x, n == 1 ? x : v.y, n == 2 ? x : v.z); }
            float4 Put(float4 v, int n, float x) { return float4(n == 0 ? x : v.x, n == 1 ? x : v.y, n == 2 ? x : v.z, n == 3 ? x : v.w); }
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

            float4 _ShaftSheet;     // xy: 1 (the sheet as large as the screen), z: its depth, w: 1
            float4 _ShaftStage;     // x: 1, 2 or 3 for the three goes; y: 1 if the depths run backwards (nearest is 1)
            float4 _ShaftSun;     // xy: where the sun is in the picture (0 to 1 each way; it may be outside), z: the picture's width over its height, w: how far this go reaches towards the sun (a part of the way)
            float4 _ShaftLook;     // x: how bright the sky has to be to count, y: how far from the sun (in picture heights) it still counts, z: how strong the shafts are, w: how much is lost with each look
            float4 _ShaftTint;     // rgb: their colour, a: how much of them shows over what is right in front of the camera
            Texture2D _ShaftRays; SamplerState sampler_ShaftRays;     // the last go's work
            Texture2D _ShaftScene; SamplerState sampler_ShaftScene;     // the picture as the game has drawn it so far (handed over as it is, not kept by the material)
            Texture2D _CameraDepthTexture; SamplerState sampler_CameraDepthTexture;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 vs_TEXCOORD0 : TEXCOORD0;
            };

            v2f vert(float3 in_POSITION0 : POSITION)
            {
                v2f OUT = (v2f)0;
                // One triangle that covers the whole screen, given in the screen's own coordinates (-1 to 1 each way).
                OUT.pos = float4(in_POSITION0.xy * _ShaftSheet.xy, _ShaftSheet.z, _ShaftSheet.w);
                // (the first two goes are drawn into pictures of the shafts' own, which lie as every picture does on this system; the
                // third into whatever the camera is drawing into, which may lie either way: as for the lens, above)
                float way = 1.0;
#if UNITY_UV_STARTS_AT_TOP
                way = _ShaftStage.x < 2.5 ? -1.0 : _ProjectionParams.x;
#endif
                OUT.vs_TEXCOORD0 = float2(in_POSITION0.x * 0.5 + 0.5, in_POSITION0.y * way * 0.5 + 0.5);
                return OUT;
            }

            // Shafts of sunlight (Natural Light's Shafts.cs sets it going, before anything else is done to the picture).
            // Air with anything in it, haze, dust, spray, shows the sun's light as it passes: and where something stands
            // in the sun's way, a tower, a rocket, the edge of a cloud, the air behind it is dark, so the light comes
            // through the gaps in shafts. They are made from the picture itself, in three goes:
            //
            // 1. What is bright and far. The sky round the sun, as bright as it is in the picture, and black wherever
            //    something near stands in front of it (the game says how far off each pixel is). Drawn small.
            // 2. Drawn out. Each pixel gathers what lies between it and the sun, less the further off: so every bright
            //    patch is smeared outward from the sun, and every dark thing in front of the sun throws a dark streak
            //    the same way. Done twice over, the second time reaching further.
            // 3. Added to the picture.

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                float2 uv = IN.vs_TEXCOORD0;
                if (_ShaftStage.x < 1.5)
                {
                    // ---- what is bright and far
                    float depth = SampleLod(_CameraDepthTexture, uv, 0.0).r;
                    // (nothing near at all is at the very end of the depths: 1, or 0 where they run backwards)
                    float open = _ShaftStage.y > 0.5 ? step(depth, 0.000001) : step(0.999999, depth);
                    float3 colour = SampleLod(_ShaftScene, uv, 0.0).rgb;
                    float light = dot(colour, float3(0.2126, 0.7152, 0.0722));
                    // (a few of the game's own pixels are not numbers at all: those are no light. What the number is made of is looked
                    // at, not what it compares as, which a compiler may take out as never true.)
                    if ((asint(light) & 0x7F800000) == 0x7F800000) { colour = ((float3)(0.0)); light = 0.0; }
                    float bright = max(0.0, light - _ShaftLook.x) / max(light, 0.001);
                    // (and only round the sun: by how far off it is in the picture, in picture heights)
                    float2 off = (uv - _ShaftSun.xy) * float2(_ShaftSun.z, 1.0);
                    float close = clamp(1.0 - length(off) / _ShaftLook.y, 0.0, 1.0);
                    // (most of it from close round the sun itself, so that what stands in front of the sun throws a clear streak;
                    // and a little from the wider sky, which is what lights the air evenly)
                    float close4 = close * close * close * close;
                    float from = 0.35 * close * close + 2.2 * close4 * close4 * close4;
                    result = float4(min(colour, ((float3)(4.0))) * bright * open * from, 1.0);
                    return result;
                }
                if (_ShaftStage.x < 2.5)
                {
                    // ---- drawn out from the sun
                    float2 step2 = (_ShaftSun.xy - uv) * _ShaftSun.w / 12.0;
                    float3 sum = ((float3)(0.0));
                    float keep = 1.0, all_ = 0.0;
                    float2 at = uv;
                    [loop] for (int n = 0; n < 12; n++)
                    {
                        sum += SampleLod(_ShaftRays, at, 0.0).rgb * keep;
                        all_ += keep;
                        keep *= _ShaftLook.w;
                        at += step2;
                    }
                    result = float4(sum / all_, 1.0);
                    return result;
                }
                // ---- added to the picture (the sheet is drawn with what it gives added to what is there)
                // The lit air is all the way from the camera to the sky. What is near has little of it in front, so little is added over
                // it: the light comes round a rocket and does not lie on it like a veil. (How far off, 0 at the camera to 1 at the far end.)
                float depth = SampleLod(_CameraDepthTexture, uv, 0.0).r;
                float away = clamp(1.0 / (_ZBufferParams.x * depth + _ZBufferParams.y), 0.0, 1.0);
                float over = lerp(_ShaftTint.a, 1.0, away);
                result = float4(SampleLod(_ShaftRays, uv, 0.0).rgb * _ShaftTint.rgb * (_ShaftLook.z * over), 0.0);
                return result;
            }
            ENDCG
        }
    }
}
