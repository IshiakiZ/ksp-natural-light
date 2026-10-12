#ifdef VERTEX
#version 150
uniform vec4 _CleanSheet;    // xy: 1 (the sheet as large as the screen), z: its depth, w: 1
in vec3 in_POSITION0;
void main()
{
    // One triangle that covers the whole screen, given in the screen's own coordinates (-1 to 1 each way).
    gl_Position = vec4(in_POSITION0.xy * _CleanSheet.xy, _CleanSheet.z, _CleanSheet.w);
}
#endif
#ifdef FRAGMENT
#version 150
#extension GL_ARB_shader_bit_encoding : enable
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
uniform vec4 _CleanMark;        // rgb: what to put there (black; a colour, to see where they are)
uniform sampler2D _CleanScene;  // the picture at half its size
out vec4 SV_Target0;

bool broken(float x) { return (floatBitsToInt(x) & 0x7F800000) == 0x7F800000; }

void main()
{
    ivec2 last = textureSize(_CleanScene, 0) - ivec2(1);
    vec4 c = texelFetch(_CleanScene, min(ivec2(gl_FragCoord.xy) / 2, last), 0);
    if (!(broken(c.r) || broken(c.g) || broken(c.b))) discard;
    SV_Target0 = vec4(_CleanMark.rgb, 1.0);
}
#endif
