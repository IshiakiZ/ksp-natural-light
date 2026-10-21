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
uniform vec4 _VeilSea;       // x: how high the camera is above the sea's level, m (0: no sea, or the camera is under it); y: the sea's radius, m; z: 1 if this camera's sea takes its veil in its own shader, and the pass leaves it alone; w: the world's radius, m
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

// The weather along a stretch of line that lies above the weather's top, from a to b metres out: the line's height over the
// camera after s metres is mu s + s^2 / 2R on a round world, so the stretch is taken in four, each with its own height over
// the top and its own climb, and counted in metres of the weather as thick as it is under its top.
float above(float a, float b, float mu, float top, float soft, float R)
{
    float sum = 0.0, len = max(b - a, 0.0) * 0.25;
    for (int i = 0; i < 4; i++)
    {
        float s = a + len * float(i);
        float over = mu * s + s * s / (2.0 * R) - top;
        sum += exp(-max(over, 0.0) / soft) * thinning(len, mu + s / R, soft);
    }
    return sum;
}

void main()
{
    vec2 uv = vs_TEXCOORD0;
    float depth = textureLod(_CameraDepthTexture, uv, 0.0).r;
    // (nothing drawn there at all: at the very end of the depths, 1, or 0 where they run backwards. Exactly so, not nearly:
    // the last millionth of the near camera's depths on OpenGL is the ground from 399.2 m to 400 m, which then had no veil
    // from this camera's pass and none from the far one's, whose ground it covers: a clear line across the murk.)
    float nothing = _VeilColour.a > 0.5 ? step(depth, 0.0) : step(1.0, depth);
    if (nothing > 0.5 && _VeilUp.w < 0.5) discard;
    vec3 ray = vec3((uv.x * 2.0 - 1.0) * _VeilView.x + _VeilView.z, (uv.y * 2.0 - 1.0) * _VeilView.y + _VeilView.w, 1.0);
    float along = length(ray);
    vec3 dir = ray / along;
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
    float R = max(_VeilSea.w, 1000.0);
    // (On a round world the weather's top curves away under the line of sight: the line's height over the camera after s
    // metres is mu s + s^2 / 2R, and it crosses the top's height where s = R (-mu -+ sqrt(mu^2 + 2 top / R)). Until
    // 2026-10-18 the weather was taken as flat: from above it, a line a degree or two below level, which on the planet never
    // comes down to the weather, ran into it tens of kilometres off and was greyed, a grey band round the horizon from about
    // the clouds' height, the clouds behind it (the owner, 2026-10-18: "At about cloud height, there's this gray bar around").)
    float cross = mu * mu + 2.0 * top / R;
    float tau, inLayer = 0.0;
    if (top > 0.0)
    {
        // ---- the camera in the weather: all of it until the line rises out through the top, then thinning out
        float out1 = R * (-mu + sqrt(max(cross, 0.0)));
        if (far <= out1) tau = far;
        else tau = out1 + above(out1, far, mu, top, soft, R);
    }
    else if (mu >= 0.0 || cross <= 0.0)
    {
        // ---- the camera above its top, and the line never comes down to it: thinner where it is, by how far above
        tau = above(0.0, far, mu, top, soft, R);
    }
    else
    {
        // ---- the camera above its top, the line down into the weather and (if it goes on far enough) out of it again
        float in1 = R * (-mu - sqrt(cross)), out1 = R * (-mu + sqrt(cross));
        if (far <= in1) tau = above(0.0, far, mu, top, soft, R);
        else { inLayer = min(far, out1) - in1; tau = above(0.0, in1, mu, top, soft, R) + inLayer + (far > out1 ? above(out1, far, mu, top, soft, R) : 0.0); }
    }
    float veiled = 1.0 - exp(-_VeilAir.x * tau);
    // (its light: from all round, and towards the sun more, by a Henyey-Greenstein lobe made 1 straight towards the sun)
    float g = clamp(_VeilSun.w, 0.0, 0.95);
    float c = dot(dir, _VeilSun.xyz);
    float lobe = (1.0 - g) * (1.0 - g) / pow(max(1.0 + g * g - 2.0 * g * c, 0.0001), 1.5) * (1.0 - g * g) / (1.0 + g);
    vec3 colour = _VeilColour.rgb + _VeilSunColour.rgb * lobe;
    // (and over the top, from above: the sunlight the weather throws back up, by as much of what is veiled as lies under its
    // top. _VeilSunColour is a third of the sunlight's own brightness; its a, how much of the sunlight the top throws back up,
    // by how high the sun stands: Veil.cs.)
    colour += _VeilSunColour.rgb * (3.0 * _VeilSunColour.a * inLayer / max(tau, 1e-3));
    // (drawn over what is there: the weather's light added by as much as there is of it, and what is behind kept by as much as shows)
    SV_Target0 = vec4(colour * veiled, veiled);
}
#endif
