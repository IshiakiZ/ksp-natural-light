#ifdef VERTEX
#version 150
uniform vec4 _ShaftSheet;    // xy: 1 (the sheet as large as the screen), z: its depth, w: 1
in vec3 in_POSITION0;
out vec2 vs_TEXCOORD0;
void main()
{
    // One triangle that covers the whole screen, given in the screen's own coordinates (-1 to 1 each way).
    gl_Position = vec4(in_POSITION0.xy * _ShaftSheet.xy, _ShaftSheet.z, _ShaftSheet.w);
    vs_TEXCOORD0 = in_POSITION0.xy * 0.5 + 0.5;
}
#endif
#ifdef FRAGMENT
#version 150
#extension GL_ARB_shader_bit_encoding : enable
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
uniform vec4 _ZBufferParams;
uniform vec4 _ShaftStage;    // x: 1, 2 or 3 for the three goes; y: 1 if the depths run backwards (nearest is 1)
uniform vec4 _ShaftSun;      // xy: where the sun is in the picture (0 to 1 each way; it may be outside), z: the picture's width over its height, w: how far this go reaches towards the sun (a part of the way)
uniform vec4 _ShaftLook;     // x: how bright the sky has to be to count, y: how far from the sun (in picture heights) it still counts, z: how strong the shafts are, w: how much is lost with each look
uniform vec4 _ShaftTint;     // rgb: their colour, a: how much of them shows over what is right in front of the camera
uniform sampler2D _ShaftRays;   // the last go's work
uniform sampler2D _ShaftScene;  // the picture as the game has drawn it so far (handed over as it is, not kept by the material)
uniform sampler2D _CameraDepthTexture;
in vec2 vs_TEXCOORD0;
out vec4 SV_Target0;

void main()
{
    vec2 uv = vs_TEXCOORD0;
    if (_ShaftStage.x < 1.5)
    {
        // ---- what is bright and far
        float depth = textureLod(_CameraDepthTexture, uv, 0.0).r;
        // (nothing near at all is at the very end of the depths: 1, or 0 where they run backwards)
        float open = _ShaftStage.y > 0.5 ? step(depth, 0.000001) : step(0.999999, depth);
        vec3 colour = textureLod(_ShaftScene, uv, 0.0).rgb;
        float light = dot(colour, vec3(0.2126, 0.7152, 0.0722));
        // (a few of the game's own pixels are not numbers at all: those are no light. What the number is made of is looked
        // at, not what it compares as, which a compiler may take out as never true.)
        if ((floatBitsToInt(light) & 0x7F800000) == 0x7F800000) { colour = vec3(0.0); light = 0.0; }
        float bright = max(0.0, light - _ShaftLook.x) / max(light, 0.001);
        // (and only round the sun: by how far off it is in the picture, in picture heights)
        vec2 off = (uv - _ShaftSun.xy) * vec2(_ShaftSun.z, 1.0);
        float close = clamp(1.0 - length(off) / _ShaftLook.y, 0.0, 1.0);
        // (most of it from close round the sun itself, so that what stands in front of the sun throws a clear streak;
        // and a little from the wider sky, which is what lights the air evenly)
        float close4 = close * close * close * close;
        float from = 0.35 * close * close + 2.2 * close4 * close4 * close4;
        SV_Target0 = vec4(min(colour, vec3(4.0)) * bright * open * from, 1.0);
        return;
    }
    if (_ShaftStage.x < 2.5)
    {
        // ---- drawn out from the sun
        vec2 step2 = (_ShaftSun.xy - uv) * _ShaftSun.w / 12.0;
        vec3 sum = vec3(0.0);
        float keep = 1.0, all = 0.0;
        vec2 at = uv;
        for (int n = 0; n < 12; n++)
        {
            sum += textureLod(_ShaftRays, at, 0.0).rgb * keep;
            all += keep;
            keep *= _ShaftLook.w;
            at += step2;
        }
        SV_Target0 = vec4(sum / all, 1.0);
        return;
    }
    // ---- added to the picture (the sheet is drawn with what it gives added to what is there)
    // The lit air is all the way from the camera to the sky. What is near has little of it in front, so little is added over
    // it: the light comes round a rocket and does not lie on it like a veil. (How far off, 0 at the camera to 1 at the far end.)
    float depth = textureLod(_CameraDepthTexture, uv, 0.0).r;
    float away = clamp(1.0 / (_ZBufferParams.x * depth + _ZBufferParams.y), 0.0, 1.0);
    float over = mix(_ShaftTint.a, 1.0, away);
    SV_Target0 = vec4(textureLod(_ShaftRays, uv, 0.0).rgb * _ShaftTint.rgb * (_ShaftLook.z * over), 0.0);
}
#endif
