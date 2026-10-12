#ifdef VERTEX
#version 150
uniform vec4 _VeilSheet;     // xy: 1 (the sheet as large as the screen), z: its depth, w: 1
in vec3 in_POSITION0;
out vec2 vs_TEXCOORD0;
void main()
{
    // One triangle that covers the whole screen, given in the screen's own coordinates (-1 to 1 each way).
    gl_Position = vec4(in_POSITION0.xy * _VeilSheet.xy, _VeilSheet.z, _VeilSheet.w);
    vs_TEXCOORD0 = in_POSITION0.xy * 0.5 + 0.5;
}
#endif
#ifdef FRAGMENT
#version 150
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
uniform vec4 _ZBufferParams;
uniform vec4 _VeilView;      // xy: how far the picture reaches to its right edge and its top at one metre ahead; zw: how far its middle is off the line straight ahead
uniform vec4 _VeilUp;        // xyz: up, in the camera's own axes (right, up, ahead); w: 1 for the far camera (where it drew nothing is the sky), 0 for the near one (where it drew nothing, the far camera's picture is, veiled already)
uniform vec4 _VeilAir;       // x: how thick (the light lost in each metre); y: its top, above the camera (metres; below zero, the camera is above it); z: how soon it thins out above its top (metres); w: how far to take the sky (metres)
uniform vec4 _VeilColour;    // rgb: its own light, lit from all round (as it shows); a: 1 if the depths run backwards (nearest is 1)
uniform vec4 _VeilSun;       // xyz: towards the sun, in the camera's axes; w: how strongly what is in the air throws light forward (0 evenly, towards 1 all straight on)
uniform vec4 _VeilSunColour; // rgb: what the sun adds to it looking straight towards the sun (as it shows)
uniform sampler2D _CameraDepthTexture;
in vec2 vs_TEXCOORD0;
out vec4 SV_Target0;

// How much weather lies along so many metres of a line that climbs mu a metre, where the weather thins out by e every
// "soft" metres of height, counted in metres of it as thick as it is at the line's start.
float thinning(float len, float mu, float soft)
{
    float k = len * mu / soft;
    return abs(k) < 0.001 ? len * (1.0 - 0.5 * k) : soft / mu * (1.0 - exp(-k));
}

void main()
{
    vec2 uv = vs_TEXCOORD0;
    float depth = textureLod(_CameraDepthTexture, uv, 0.0).r;
    // (nothing drawn there at all: at the very end of the depths, 1, or 0 where they run backwards)
    float nothing = _VeilColour.a > 0.5 ? step(depth, 0.000001) : step(0.999999, depth);
    if (nothing > 0.5 && _VeilUp.w < 0.5) discard;
    vec3 ray = vec3((uv.x * 2.0 - 1.0) * _VeilView.x + _VeilView.z, (uv.y * 2.0 - 1.0) * _VeilView.y + _VeilView.w, 1.0);
    float along = length(ray);
    vec3 dir = ray / along;
    // (the depth picture keeps how far ahead each pixel is, as the camera counts it: turned into metres along the line)
    float ahead = 1.0 / (_ZBufferParams.z * depth + _ZBufferParams.w);
    float far = nothing > 0.5 ? _VeilAir.w : ahead * along;
    float mu = dot(dir, _VeilUp.xyz);
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
        float here = exp(top / soft);
        float toTop = mu < -0.00001 ? top / mu : 1.0e9;
        if (far <= toTop) tau = here * thinning(far, mu, soft);
        else tau = here * thinning(toTop, mu, soft) + (far - toTop);
    }
    float veiled = 1.0 - exp(-_VeilAir.x * tau);
    // (its light: from all round, and towards the sun more, by a Henyey-Greenstein lobe made 1 straight towards the sun)
    float g = clamp(_VeilSun.w, 0.0, 0.95);
    float c = dot(dir, _VeilSun.xyz);
    float lobe = (1.0 - g) * (1.0 - g) / pow(max(1.0 + g * g - 2.0 * g * c, 0.0001), 1.5) * (1.0 - g * g) / (1.0 + g);
    vec3 colour = _VeilColour.rgb + _VeilSunColour.rgb * lobe;
    // (drawn over what is there: the weather's light added by as much as there is of it, and what is behind kept by as much as shows)
    SV_Target0 = vec4(colour * veiled, veiled);
}
#endif
