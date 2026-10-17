// NaturalLight/Veil: made from tools/shaderpack/veil.glsl by tools/unityshaders/port.py. Do not edit: edit the GLSL and run that again.
Shader "NaturalLight/Veil"
{
    Properties
    {

    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Blend One OneMinusSrcAlpha
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

            float4 _VeilSheet;     // xy: 1 (the sheet as large as the screen), z: its depth, w: 1
            float4 _VeilView;     // xy: how far the picture reaches to its right edge and its top at one metre ahead; zw: how far its middle is off the line straight ahead
            float4 _VeilUp;     // xyz: up, in the camera's own axes (right, up, ahead); w: 1 for the far camera (where it drew nothing is the sky), 0 for the near one (where it drew nothing, the far camera's picture is, veiled already)
            float4 _VeilAir;     // x: how thick (the light lost in each metre); y: its top, above the camera (metres; below zero, the camera is above it); z: how soon it thins out above its top (metres); w: how far to take the sky (metres)
            float4 _VeilColour;     // rgb: its own light, lit from all round (as it shows); a: 1 if the depths run backwards (nearest is 1)
            float4 _VeilSun;     // xyz: towards the sun, in the camera's axes; w: how strongly what is in the air throws light forward (0 evenly, towards 1 all straight on)
            float4 _VeilSunColour;     // rgb: what the sun adds to it looking straight towards the sun (as it shows)
            float4 _VeilSea;     // x: how high the camera is above the sea's level, m (0: no sea, or the camera is under it); y: the sea's radius, m; z: 1 if this camera's sea takes its veil in its own shader, and the pass leaves it alone
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
                OUT.pos = float4(in_POSITION0.xy * _VeilSheet.xy, _VeilSheet.z, _VeilSheet.w);
                // (drawn into whatever the camera is drawing into, which may lie either way: as for the lens's second go, above; Natural
                // Light's veil and the sea's lens in the water the same)
                float way = 1.0;
#if UNITY_UV_STARTS_AT_TOP
                way = _ProjectionParams.x;
#endif
                OUT.vs_TEXCOORD0 = float2(in_POSITION0.x * 0.5 + 0.5, in_POSITION0.y * way * 0.5 + 0.5);
                return OUT;
            }

            // The weather in the air between the camera and what it sees: rain, snow, fog, dust (Natural Light's Veil.cs sets it going,
            // from what a weather mod says through Keystone's Weather). Beyond a few tens of metres a raindrop or a flake is too small to
            // be seen as one, but all of them together take the light from what is behind them and put their own in its place: the far
            // side of the pad greys out in rain, the hills go white in snow, everything sinks into an ochre murk in a dust storm.
            //
            // Per pixel: how far the camera looks (the game's depth picture says, and where nothing was drawn it is the sky, as far as
            // the weather goes up); how much of the weather lies along that line, which is all of it below the weather's top (a rain's
            // cloud base, a fog's top) and less and less above it, worked out exactly rather than stepped; and from that how much of what
            // is behind shows and how much of the weather's own light. Its light is the light all round (the sky's and the ground's, as
            // the mod has it), and more towards the sun where the sun is out, since drops, flakes and dust all throw light forward.

            // How much weather lies along so many metres of a line that climbs mu a metre, where the weather thins out by e every
            // "soft" metres of height, counted in metres of it as thick as it is at the line's start.
            float thinning(float len, float mu, float soft)
            {
                float k = len * mu / soft;
                return abs(k) < 0.001 ? len * (1.0 - 0.5 * k) : soft / mu * (1.0 - exp(-k));
            }

            // The same along a line that starts "top" metres under the weather's top, from a camera above it (top below nothing),
            // and stays above it: counted in metres of the weather as thick as it is under its top. (Once written as how thick the
            // weather is where the camera is, times thinning(): from a few hundred metres above a shallow fog the first is too small
            // for the card and comes out nothing, the second too large and comes out infinite, and nothing times infinite is not a
            // number. The ground and the sea under the camera, and everything drawn in front of them, came out black. Here each
            // power of e is of a height above the top, and is at most 1.)
            float thinningAbove(float top, float len, float mu, float soft)
            {
                float k = len * mu / soft;
                float here = exp(top / soft);
                return abs(k) < 0.001 ? here * len * (1.0 - 0.5 * k) : soft / mu * (here - exp((top - len * mu) / soft));
            }

            float4 frag(v2f IN) : SV_Target
            {
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                float2 uv = IN.vs_TEXCOORD0;
                float depth = SampleLod(_CameraDepthTexture, uv, 0.0).r;
                // (nothing drawn there at all: at the very end of the depths, 1, or 0 where they run backwards. Exactly so, not nearly:
                // the last millionth of the near camera's depths on OpenGL is the ground from 399.2 m to 400 m, which then had no veil
                // from this camera's pass and none from the far one's, whose ground it covers: a clear line across the murk.)
                float nothing = _VeilColour.a > 0.5 ? step(depth, 0.0) : step(1.0, depth);
                if (nothing > 0.5 && _VeilUp.w < 0.5) discard;
                float3 ray = float3((uv.x * 2.0 - 1.0) * _VeilView.x + _VeilView.z, (uv.y * 2.0 - 1.0) * _VeilView.y + _VeilView.w, 1.0);
                float along = length(ray);
                float3 dir = ray / along;
                // (the depth picture keeps how far ahead each pixel is, as the camera counts it: turned into metres along the line)
                float ahead = 1.0 / (_ZBufferParams.z * depth + _ZBufferParams.w);
                float far = nothing > 0.5 ? _VeilAir.w : ahead * along;
                float mu = dot(dir, _VeilUp.xyz);
                // (Over a sea the air ends at the water. A sea drawn after the depth picture is made is not in it: what is there is the
                // ground under the water, further off, and deep water was veiled as if it were as far off as its bottom. Where the
                // line of sight meets the sea's level first, that is how far the air goes: the level falls away from the camera's own
                // by the square of the distance over twice the planet's radius. And a sea that takes its veil in its own shader
                // (Natural Ocean's, where this pass comes before it is drawn) is left alone here, the ground under it too.)
                if (_VeilSea.x > 0.0 && mu < 0.0)
                {
                    float under = mu * mu - 2.0 * _VeilSea.x / _VeilSea.y;
                    if (under > 0.0)
                    {
                        float seaAt = 2.0 * _VeilSea.x / (-mu + sqrt(under));
                        if (seaAt < far)
                        {
                            if (_VeilSea.z > 0.5) discard;
                            far = seaAt;
                        }
                    }
                }
                float top = _VeilAir.y;
                float soft = max(_VeilAir.z, 1.0);
                float tau;
                if (top > 0.0)
                {
                    // ---- the camera in the weather: all of it up to the top, then thinning out
                    float toTop = mu > 0.00001 ? top / mu : 1.0e9;
                    if (far <= toTop) tau = far;
                    else tau = toTop + thinning(far - toTop, mu, soft);
                }
                else
                {
                    // ---- the camera above its top: thinner where it is, by how far above, and as thick as it gets once back under the top
                    float toTop = mu < -0.00001 ? top / mu : 1.0e9;
                    if (far <= toTop) tau = thinningAbove(top, far, mu, soft);
                    else tau = thinningAbove(top, toTop, mu, soft) + (far - toTop);
                }
                float veiled = 1.0 - exp(-_VeilAir.x * tau);
                // (its light: from all round, and towards the sun more, by a Henyey-Greenstein lobe made 1 straight towards the sun)
                float g = clamp(_VeilSun.w, 0.0, 0.95);
                float c = dot(dir, _VeilSun.xyz);
                float lobe = (1.0 - g) * (1.0 - g) / pow(max(1.0 + g * g - 2.0 * g * c, 0.0001), 1.5) * (1.0 - g * g) / (1.0 + g);
                float3 colour = _VeilColour.rgb + _VeilSunColour.rgb * lobe;
                // (drawn over what is there: the weather's light added by as much as there is of it, and what is behind kept by as much as shows)
                result = float4(colour * veiled, veiled);
                return result;
            }
            ENDCG
        }
    }
}
