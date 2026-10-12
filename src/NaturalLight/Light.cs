using System;
using System.Collections.Generic;
using Keystone;
using UnityEngine;
using UnityEngine.Rendering;

#if !DEV
[assembly: KSPAssembly("NaturalLight", 0, 10)]
[assembly: KSPAssemblyDependency("Keystone", 0, 7)]
#endif

namespace NaturalLight
{
    /// <summary>
    /// The light that is not the sun, and the sun as it is by the time it arrives.
    ///
    /// The game lights a craft with one sun and, for everything the sun does not reach, one flat grey that is
    /// the same from every side: a quarter of full brightness in air, a sixteenth in a vacuum, whether the
    /// craft stands on grass at noon or hangs over the night side of a planet. So the shaded side of a rocket
    /// is the same grey underneath as on top, in orbit as on the pad.
    ///
    /// In truth what the sun does not light is lit by everything else: the sky, from above and from all
    /// round, in the sky's own colour and only where there is air and daylight; the ground, from below, in
    /// the ground's own colour and only as brightly as the sun is lighting that ground; and every other world
    /// in the sky, from its own side. Over an ocean the underside of a craft is blue; over Duna, rust; over
    /// the night side, nothing but the Mun. In orbit the same light comes up from the planet, weaker with
    /// height as the planet takes up less of what the craft sees.
    ///
    /// This class works that out every frame for where the craft is and gives it to the game as its ambient
    /// light, which the game's own shaders already know how to use from every direction at once (it is handed
    /// over as what Unity calls an ambient probe). It also sees to the sun itself: its colour through the air
    /// where nothing else does that, its strength as an eye would have it when the air has dimmed it, and how
    /// dark its shadows are. None of it needs a shader of its own.
    ///
    /// It is the light for the scene the craft is in. The planets seen from afar, the insides of cockpits and
    /// the crew's portraits are drawn by other cameras, and are given the game's own light as before.
    ///
    /// The lamps (engines, and the floodlights of launch sites) are in files of their own.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class LightAtStart : MonoBehaviour
    {
        void Awake() { Daylight.Setup(); Destroy(gameObject); }
    }

    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class Daylight : MonoBehaviour
    {
        public const string Version = "0.10.4";

        static Mod mod;
        /// <summary>The mod's page in Keystone's window: for the parts of the mod that have something to say on it.</summary>
        internal static Mod Page => mod;
        static Toggle on;
        static Slider sky, ground, dark, worlds, eyes, golden, shadows;
        static Choice through;
        static Toggle fine, inside, rays;
        static Slider thrown;
        public static Toggle Engines, Floods, Filmed, Windows, Deep, Shafts, Smoke, Crew, Runway, Veil;

        /// <summary>The light from all round, the sky's and the ground's and other worlds', taken together (as light): for the other parts of the mod.</summary>
        public static Color Around = Color.black;
        static readonly Vector3[] sixWays = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
        static readonly Color[] sixFound = new Color[6];
        public static Slider EngineLight, FloodLight, Beams, Punch, Glow, Eye, Creases, Marks, Grain, WindowLight, SkyGlow, SkySeed, ShaftLight, RunwayLight;

        /// <summary>The colour and strength of sunlight as it reaches the craft: for the other parts of the mod.</summary>
        public static Color Sunlight = Color.white;

        /// <summary>The colour of the ground round about (as it shows): for the other parts of the mod (the colour of dust raised from it).</summary>
        public static Color GroundNow = new Color(0.3f, 0.3f, 0.3f);

        /// <summary>
        /// How much less light there is on level ground for the weather, in stops (0 on a clear day; about 2 under an overcast,
        /// 3 or more under a raincloud): for the camera's eye, which opens for it as a camera's meter would (see FilmLook).
        /// </summary>
        public static float Clouded;

        /// <summary>Whether the mod is on at all (its first setting puts out every part of it, the lamps included).</summary>
        public static bool On => on != null && on;

        /// <summary>Whether there is a place being lit just now (a craft in flight, or the space centre), where it is, and on what world: for the other parts of the mod.</summary>
        public static bool Near;
        public static Vector3d Here;
        public static CelestialBody Body;

        /// <summary>
        /// Where the light is worked out for: the craft being flown, or, at the space centre, the space centre itself.
        /// </summary>
        static bool Where(out CelestialBody body, out Vector3d place, out double height, out double above, out double latitude, out double longitude, out bool overWater)
        {
            body = null; place = Vector3d.zero; height = above = latitude = longitude = 0.0; overWater = false;
            if (HighLogic.LoadedSceneIsFlight)
            {
                Vessel v = FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
                if (v == null || v.mainBody == null) return false;
                body = v.mainBody; place = v.GetWorldPos3D();
                height = Math.Max(0.0, v.altitude); above = Math.Max(0.0, v.radarAltitude);
                latitude = v.latitude; longitude = v.longitude;
                overWater = body.ocean && v.terrainAltitude < 0.0;
                return true;
            }
            if (HighLogic.LoadedScene == GameScenes.SPACECENTER && SpaceCenter.Instance != null && SpaceCenter.Instance.SpaceCenterTransform != null)
            {
                body = SpaceCenter.Instance.cb != null ? SpaceCenter.Instance.cb : FlightGlobals.GetHomeBody();
                if (body == null) return false;
                place = SpaceCenter.Instance.SpaceCenterTransform.position;
                body.GetLatLonAlt(place, out latitude, out longitude, out height);
                height = Math.Max(0.0, height); above = 0.0;
                return true;
            }
            return false;
        }

        /// <summary>How much air the craft is in: 1 at Kerbin's sea level and anything thicker, 0 in a vacuum.</summary>
        public static float Air;

        /// <summary>The sine of how high the sun stands where the craft is (1 overhead, 0 on the horizon): for the other parts of the mod.</summary>
        public static float SunUp = 1f;

        /// <summary>What the mod's own lamps may light: what the game's sun does (the scene the craft is in, and not the planets drawn from afar or the insides of cockpits, which share the same space).</summary>
        public static int Lit => Sun.Instance != null && Sun.Instance.sunLight != null ? Sun.Instance.sunLight.cullingMask : ~((1 << 5) | (1 << 9) | (1 << 10) | (1 << 16) | (1 << 20));

        public static void Setup()
        {
            if (mod == null || Kit.Find("Natural Light") != mod)
            {
                mod = Kit.Register("Natural Light", Version, "Light as it really falls: from the sky and the ground in their own colours, from the other worlds in the sky, from a low sun that stays golden, from the flame of an engine and from floodlights at the launch sites by night, instead of from the sun and one flat grey alone.");
                on = mod.Toggle("on", "Natural light", true, "Off: the game's own light again, all of it: one flat grey for whatever the sun does not reach, and no light from engines or at launch sites.");
                sky = mod.Slider("sky", "Sky light", 1f, 0f, 2.5f, "", 2, "How strongly the sky lights what faces it. Only where there is air, and by day.");
                ground = mod.Slider("ground", "Ground light", 1f, 0f, 2.5f, "", 2, "How strongly the ground, or the planet below, lights what faces it: in the ground's own colour, and only where the sun is on that ground.");
                dark = mod.Slider("dark", "Darkest", 0.045f, 0f, 0.25f, "", 3, "The least light there ever is, so that the night side is dark but not black. The game's own flat light is 0.06 in space and 0.24 in air, by night as by day: raise this towards that if nights are too dark to fly in.");
                rays = mod.Toggle("rays", "Light found by rays", true, "Rays are cast from the craft in every direction, to find what is round it: a hangar wall, a crater's side, an overhang. The sky then lights the craft only from where it can be seen, and whatever the rays find lights it from there, in its own colour, as brightly as the sun and the sky are on it (a second ray, towards the sun, says whether it is in shadow: the craft's own shadow included). Near the ground only.");
                thrown = mod.Slider("thrown", "Light thrown back", 1f, 0f, 2.5f, "", 2, "How strongly what the rays find lights the craft.");
                inside = mod.Toggle("inside", "Inside the craft too", true, "The cockpit, and the crew's portraits, lit by a share of the light outside and by the cabin's own glow, instead of by the game's one flat grey: in a planet's shadow the crew sit in a dim cabin, as the craft they are in sits in the dark.");
                mod.Heading("The sun");
                through = mod.Choice("through", "Sun through air", 0, new[] { "if nothing else does", "always", "never" }, "A low sun shines through far more air than a high one, and comes out dimmer and redder (or, through Duna's dust, bluer). Scatterer does this already where it is installed and set to: 'if nothing else does' leaves it to Scatterer then.");
                Shafts = mod.Toggle("shafts", "Shafts of sunlight", true, "With the sun in view and something in front of it, a tower, a rocket, the edge of a cloud, its light comes round in shafts through the air. Only where there is air. (A shader of the mod's own, packed for the game's OpenGL on a Mac or Linux and built for Direct3D on Windows.)");
                ShaftLight = mod.Slider("shaftlight", "How strong", 1f, 0f, 3f, "", 2, "How strongly the shafts show.");
                Veil = mod.Toggle("veil", "Rain, snow, fog and dust in the air", true, "With a weather mod installed (Natural Weather), what is in the air veils the distance as it does: the far side of the pad greys out in rain, the hills whiten in snow, fog swallows everything past a hundred metres, a dust storm on Duna is an ochre murk. As far as the weather says one can see, lit as the sky and the ground light it, and brighter towards the sun where the sun is out. (A shader of the mod's own, packed for the game's OpenGL on a Mac or Linux and built for Direct3D on Windows.)");
                golden = mod.Slider("golden", "Low sun", 0.7f, 0f, 1f, "", 2, "A sinking sun is dimmed by the air, and the eye (like any camera) opens up to keep pace: so the last hour of sunlight is still strong, in its evening colour, and throws long clear shadows, instead of fading into grey. 0: no keeping pace, the sun simply dims.");
                shadows = mod.Slider("shadows", "Depth of shadows", 1f, 0f, 1f, "", 2, "The game lets a quarter of the sunlight through every shadow, to keep its flat grey light from looking dead. With the sky and the ground lighting what is in shadow that is not needed: at 1 no sunlight comes through, and shadows are as dark (and, under a blue sky, as blue) as they are. 0: as the game has them.");
                fine = mod.Toggle("fine", "Finer shadows up close", true, "The game keeps its finest shadows for whatever is near the camera, and with the camera drawn in close moves that fineness in with it: but only on Windows' Direct3D. This does the same on a Mac and on Linux, so that one part of a craft throws a clean shadow on another. (Left alone where Scatterer is set to handle shadows itself.)");
                mod.Heading("Other worlds");
                worlds = mod.Slider("worlds", "Light of other worlds", 1f, 0f, 3f, "", 2, "Every world in the sky throws back sunlight: the Mun over Kerbin by night, Kerbin over the Mun, Jool over its moons. Each lights the craft from its own side, in its own colour, by how much of it is lit and how large it stands.");
                eyes = mod.Slider("eyes", "Night eyes", 1f, 0f, 2f, "", 2, "In the dark the eye opens up, and moonlight that is a hundred-thousandth of daylight is enough to see by. How far it does so here: 0 shows other worlds' light as weak as it truly is (next to nothing), 1 makes a full Mun enough to fly by.");
                mod.Heading("Engines");
                Engines = mod.Toggle("engines", "Engine light", true, "A running engine lights what is round it: the craft, the pad, the ground it is landing on.");
                EngineLight = mod.Slider("enginelight", "How bright", 1f, 0.2f, 3f, "", 2, "How strongly engines light their surroundings.");
                Smoke = mod.Toggle("smoke", "Smoke takes the light", true, "The game draws its smoke as bright at midnight as at noon. With this, an engine's trail and the cloud at a launch are as bright as the light on them: gold at sunrise, dark at night but for the glow of the flame and the launch site's floodlights. By day as the game has it.");
                mod.Heading("Launch sites");
                Floods = mod.Toggle("floods", "Floodlights by night", true, "Launch pads and runways are lit by night, as real ones are: a ring of floodlights on the ground round where a craft is rolled out, which come on as the sun goes down.");
                FloodLight = mod.Slider("floodlight", "How bright", 1f, 0.2f, 2.5f, "", 2, "How strongly the floodlights light the craft and the ground.");
                Beams = mod.Slider("beams", "Beams in the air", 1f, 0f, 3f, "", 2, "The floodlights' beams seen in the night air, slanting up over the site. 0: none.");
                Runway = mod.Toggle("runway", "Runway lamps light the ground", true, "The rows of lamps along the runway, and its big lamps at the corners, throw their light on the concrete and the grass round each one, and on whatever stands or rolls past them. They come on with the game's own lamps at dusk.");
                RunwayLight = mod.Slider("runwaylight", "How bright", 1f, 0.2f, 3f, "", 2, "How strongly the runway's lamps light the ground round them.");
                Windows = mod.Toggle("windows", "Light from windows", true, "The lit windows of the space centre's buildings throw their light on the ground in front of them: as wide as the windows, further out the higher they are, brighter the more of the wall is glass. Where the windows are is found on the buildings themselves.");
                WindowLight = mod.Slider("windowlight", "How bright", 1f, 0.2f, 3f, "", 2, "How strongly the windows light the ground.");
                mod.Heading("In the buildings");
                Crew = mod.Toggle("crew", "The ground crew stand in the light they are in", true, "In the assembly building and the hangar the game draws the ground crew and their trucks as bright in the darkest corner as under a floodlight, and the rocket's shadow passes over them without touching them. With this each is as bright as the floor it is on, and darkens in the rocket's shadow as the floor does.");
                mod.Heading("Deep sky");
                Deep = mod.Toggle("deep", "Nebulae and a deeper sky", true, "Nebulae, a faint glow along the band of the galaxy, a few far galaxies and some hundreds of brighter coloured stars, laid over the game's own sky of stars. Space stays mostly black. It is all worked out from noise when the game starts (about a second, on the side), and sits on the band of whatever sky is installed.");
                SkyGlow = mod.Slider("skyglow", "How bright", 1f, 0f, 3f, "", 2, "How strongly the deep sky shows.");
                SkySeed = mod.Slider("skyseed", "Which sky", 7f, 1f, 60f, "", 0, "Another number, another sky: different nebulae in different places.");
                mod.Heading("Film");
                Filmed = mod.Toggle("film", "Film look", true, "The picture developed as a film camera's would be. The game cuts off flat whatever is brighter than white; film lets it roll over into a glow. Contrast is stronger, with the colours left as the light makes them; creases and the places where things meet are darker, and the camera's eye opens and closes with how bright the view is. It is done by the TUFX mod's post-processing, and does nothing where TUFX is not installed.");
                Punch = mod.Slider("punch", "Contrast and colour", 1f, 0f, 2f, "", 2, "How strong the film's contrast is. At 1 the colours are left as the light makes them; above 1 colour is added as well, for a stronger look. 0: the film curve alone.");
                Glow = mod.Slider("glow", "Glow", 1f, 0f, 2f, "", 2, "The glow round whatever is very bright: a flame, a lamp, sunlight on white paint.");
                Eye = mod.Slider("eye", "The camera's eye", 1f, 0f, 2f, "", 2, "The exposure follows the view, as an eye or a camera does. In the dark it opens, slowly, and what could not be seen can be (up to three stops, nine times the light). In the light it closes. And it opens no further than the brightest thing in view can bear: with a floodlit rocket or a sunlit planet in sight, everything dark stays dark, stars included. Under cloud (with a weather mod) it opens for the light the clouds take, two or three stops, as a camera's meter does however bright the sky. 0: one exposure always.");
                Grain = mod.Slider("grain", "Grain in the dark", 1f, 0f, 4f, "", 2, "A picture pushed to see in the dark is a little grainy, as film pushed that far is. It is faint as installed. 0: none.");
                Creases = mod.Slider("creases", "Creases", 1f, 0f, 2f, "", 2, "The darkening of creases and of where one thing meets another, which makes a craft look solid. 0: none of this mod's (the TUFX profile's own, if it has any, stays).");
                Marks = mod.Slider("marks", "Lens marks", 1f, 0f, 2f, "", 2, "Slightly darker corners and a trace of colour fringing towards them, as every real lens has.");
            }
        }

        /// <summary>(Text a development build is handed to try things with.)</summary>
        internal static string Kept(string key) => mod != null ? mod.Kept(key) : null;

        static Daylight running;
        string report = "";
        float nextReport;

        // what was worked out for this frame
        SphericalHarmonicsL2 probe;
        bool have;

        // the game's own ambient light, kept so that every other camera can be given it back
        AmbientMode itsMode;
        bool changed;

        void Awake()
        {
            Setup();
            running = this;
            mod.Panel = Readout;
            Camera.onPreCull += Before;
#if DEV
            Camera.onPreRender += SeenBefore; Camera.onPostRender += SeenAfter;
#endif
        }

#if DEV
        static readonly Dictionary<string, string> seenAt = new Dictionary<string, string>();
        static void SeenBefore(Camera cam) { if (cam != null && Sun.Instance != null && Sun.Instance.sunLight != null) seenAt[cam.name + " before drawing"] = Sun.Instance.sunLight.color.ToString() + " x " + Sun.Instance.sunLight.intensity.ToString("F2"); }
        static void SeenAfter(Camera cam) { if (cam != null && Sun.Instance != null && Sun.Instance.sunLight != null) seenAt[cam.name + " after drawing"] = Sun.Instance.sunLight.color.ToString() + " x " + Sun.Instance.sunLight.intensity.ToString("F2"); }
        /// <summary>(For the development build: the sun's colour as each camera finds it, before and after it draws.)</summary>
        public static string SunSeen()
        {
            var o = new System.Text.StringBuilder("set to " + (running != null ? running.sunSet.ToString() : "?") + "; ");
            foreach (KeyValuePair<string, string> kv in seenAt) o.Append(kv.Key + ": " + kv.Value + "; ");
            return o.ToString();
        }
#endif

        void OnDestroy()
        {
            Light sunLight = Sun.Instance != null ? Sun.Instance.sunLight : null;
            if (sunLight != null)
            {
                if (tinting) sunLight.color = Color.white;
                if (!float.IsNaN(itsShadows)) sunLight.shadowStrength = itsShadows;
            }
            tinting = false; itsShadows = float.NaN;
            CabinBack();
            if (haveSplit) { QualitySettings.shadowCascade4Split = itsSplit; haveSplit = false; }
            Camera.onPreCull -= Before;
#if DEV
            Camera.onPreRender -= SeenBefore; Camera.onPostRender -= SeenAfter;
#endif
            if (changed) RenderSettings.ambientMode = itsMode;
            changed = false;
            if (running == this) running = null;
            if (mod != null && mod.Panel == Readout) mod.Panel = null;
        }

        void Readout()
        {
            GUILayout.Label(report + (EngineLamps.Report.Length > 0 ? "\n" + EngineLamps.Report : "") + (Floodlights.Report.Length > 0 ? "\n" + Floodlights.Report : "") + (RunwayLamps.Report.Length > 0 ? "\n" + RunwayLamps.Report : "") + (Surroundings.Report.Length > 0 ? "\n" + Surroundings.Report : "") + (SunShafts.Report.Length > 0 ? "\n" + SunShafts.Report : "") + (WeatherVeil.Report.Length > 0 ? "\n" + WeatherVeil.Report : "") + (LitSmoke.Report.Length > 0 ? "\n" + LitSmoke.Report : "") + (FilmLook.Report.Length > 0 ? "\n" + FilmLook.Report : ""), Host.Small);
        }

        // ---------------------------------------------------------------- the colour of the ground

        sealed class Globe
        {
            public Color32[] Map;
            public int Wide, High;
        }

        static readonly Dictionary<string, Globe> globes = new Dictionary<string, Globe>();

        /// <summary>
        /// A small copy of the picture the game draws a planet with from afar, to read the ground's colour off. (The
        /// picture itself cannot be read from where it is kept, so it is drawn once into something that can.)
        /// </summary>
        static Globe GlobeOf(CelestialBody body)
        {
            if (globes.TryGetValue(body.bodyName, out Globe known)) return known;
            Globe made = null;
            try
            {
                Renderer face = body.scaledBody != null ? body.scaledBody.GetComponent<Renderer>() : null;
                Texture picture = face != null && face.sharedMaterial != null && face.sharedMaterial.HasProperty("_MainTex") ? face.sharedMaterial.GetTexture("_MainTex") : null;
                if (picture != null)
                {
                    const int W = 512, H = 256;
                    RenderTexture small = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    RenderTexture before = RenderTexture.active;
                    Graphics.Blit(picture, small);
                    RenderTexture.active = small;
                    var copy = new Texture2D(W, H, TextureFormat.RGBA32, false);
                    copy.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                    copy.Apply();
                    RenderTexture.active = before;
                    RenderTexture.ReleaseTemporary(small);
                    made = new Globe { Map = copy.GetPixels32(), Wide = W, High = H };
                    Destroy(copy);
                }
            }
            catch (Exception ex) { Kit.Log("Natural Light", "could not read the colour of " + body.bodyName + "'s ground (" + ex.Message + "): taking it as grey"); }
            globes[body.bodyName] = made;
            return made;
        }

        /// <summary>
        /// The colour of the ground round a place, averaged over so many degrees of arc each way. Grey if the planet's
        /// picture cannot be read. Close to the ground it is the ground of the kind the craft is over that counts, land or
        /// water: a craft on a beach is lit from below by the beach, not by the sea beside it, and the picture is far too
        /// coarse to tell the two apart at such a place (a dot of it is seven kilometres across on Kerbin).
        /// </summary>
        static Color GroundColour(CelestialBody body, double latitude, double longitude, double reach, bool close, bool overWater)
        {
            Globe globe = GlobeOf(body);
            if (globe == null) return new Color(0.3f, 0.3f, 0.3f);
            reach = Math.Max(0.4, Math.Min(60.0, reach));
            float r = 0f, g = 0f, b = 0f, weight = 0f;
            bool sorted = close && body.ocean && body.pqsController != null;
            for (int j = -2; j <= 2; j++)
                for (int i = -2; i <= 2; i++)
                {
                    double lat = latitude + reach * j / 2.0, lon = longitude + reach * i / 2.0 / Math.Max(0.2, Math.Cos(latitude * Math.PI / 180.0));
                    if (lat > 89.5 || lat < -89.5) continue;
                    float w = 1f / (1f + 0.6f * (i * i + j * j));
                    if (sorted)
                    {
                        bool wet = false;
                        try { wet = body.TerrainAltitude(lat, lon, true) < 0.0; } catch (Exception) { }
                        if (wet != overWater) w *= 0.02f;
                    }
                    // (in this copy of the picture longitude runs from right to left, with 90 degrees east at its left edge: found by
                    // writing the map out and trying every way round against eleven known places on Kerbin, the desert's airfield, the
                    // land west of the launch site and the sea east of it among them; ten of the eleven agree with this and no other)
                    double u = (90.0 - lon) / 360.0; u -= Math.Floor(u);
                    double v = (lat + 90.0) / 180.0;
                    int x = Math.Min(globe.Wide - 1, (int)(u * globe.Wide)), y = Math.Min(globe.High - 1, (int)(v * globe.High));
                    Color32 c = globe.Map[x + y * globe.Wide];
                    r += c.r * w; g += c.g * w; b += c.b * w; weight += w;
                }
            if (weight <= 0f) return new Color(0.3f, 0.3f, 0.3f);
            return new Color(r / weight / 255f, g / weight / 255f, b / weight / 255f);
        }

        Color groundNow = new Color(0.3f, 0.3f, 0.3f);
        float groundAt = -10f;

        // ---------------------------------------------------------------- the colour of the sky

        static readonly Dictionary<string, Color> skies = new Dictionary<string, Color>();

        /// <summary>
        /// The colour a world's sky lights things in (as it shows, its strongest part as 1). The game says what colour it
        /// wants a planet's air to light things with, but for Kerbin that is as good as white, and a clear sky is not: it is
        /// blue because air scatters short waves more than long ones (by the fourth power of the wavelength), and what it
        /// scatters is what lights the shade. The game draws each planet's sky from three wavelengths of its own, so the
        /// colour is worked out from those: blue for Kerbin and Laythe, pale rust for Duna, violet for Eve. Half-way to
        /// white from there, since haze, cloud and light scattered twice over are all nearer white.
        /// </summary>
        static Color SkyOf(CelestialBody body)
        {
            if (skies.TryGetValue(body.bodyName, out Color known)) return known;
            Color colour = body.atmosphericAmbientColor;
            float most = Mathf.Max(colour.r, Mathf.Max(colour.g, colour.b));
            colour = most > 0.01f ? colour / most : new Color(0.7f, 0.8f, 1f);
            try
            {
                AtmosphereFromGround air = body.afg;
                if (air != null && air.waveLength.r > 0.1f && air.waveLength.g > 0.1f && air.waveLength.b > 0.1f)
                {
                    Color w = air.waveLength;
                    var scattered = new Color(1f / (w.r * w.r * w.r * w.r), 1f / (w.g * w.g * w.g * w.g), 1f / (w.b * w.b * w.b * w.b));
                    scattered /= Mathf.Max(scattered.r, Mathf.Max(scattered.g, scattered.b));
                    colour = Color.Lerp(scattered.gamma, Color.white, 0.5f);
                }
            }
            catch (Exception) { }
            colour.a = 1f;
            skies[body.bodyName] = colour;
            return colour;
        }

        /// <summary>
        /// Light from all round at one height: level with the craft (tilt 0), or from a little above or below level. It is given
        /// as four lights a quarter-turn apart, which in the way the game keeps such light (to the second order of spherical
        /// harmonics) add up to an even ring, the same from whichever side.
        /// </summary>
        static void Band(ref SphericalHarmonicsL2 made, Vector3 up, float tilt, Color colour, float strength)
        {
            if (strength <= 0f) return;
            Vector3 one = Vector3.Cross(up, Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right).normalized, two = Vector3.Cross(up, one);
            made.AddDirectionalLight((one + up * tilt).normalized, colour, strength);
            made.AddDirectionalLight((-one + up * tilt).normalized, colour, strength);
            made.AddDirectionalLight((two + up * tilt).normalized, colour, strength);
            made.AddDirectionalLight((-two + up * tilt).normalized, colour, strength);
        }

        readonly float[] openSides = { 1f, 1f, 1f, 1f };

        /// <summary>As Band, but each of the four sides (north, east, south, west) by its own share: for a sky that is hidden on one side.</summary>
        static void Sides(ref SphericalHarmonicsL2 made, Vector3 up, Vector3 north, float tilt, Color colour, float strength, float[] share)
        {
            if (strength <= 0f) return;
            Vector3 east = Vector3.Cross(up, north);
            made.AddDirectionalLight((north + up * tilt).normalized, colour, strength * share[0]);
            made.AddDirectionalLight((east + up * tilt).normalized, colour, strength * share[1]);
            made.AddDirectionalLight((-north + up * tilt).normalized, colour, strength * share[2]);
            made.AddDirectionalLight((-east + up * tilt).normalized, colour, strength * share[3]);
        }

        /// <summary>A colour as light, made as bright to the eye as white of that strength would be (a blue sky lights the shade as well as a grey one).</summary>
        /// <summary>How much of its own colour the sky's light keeps, and the ground's (1 all of it, 0 none: grey of the same brightness).</summary>
        static float skyKeep = 1f, groundKeep = 1f;
#if DEV
        /// <summary>(For the development build: "sky ground", how much of their own colours the sky's and the ground's light keep.)</summary>
        public static string Tints(string arg)
        {
            string[] w = (arg ?? "").Split(' ');
            if (w.Length > 0 && w[0].Length > 0) skyKeep = float.Parse(w[0], System.Globalization.CultureInfo.InvariantCulture);
            if (w.Length > 1) groundKeep = float.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture);
            return "the sky's light keeps " + skyKeep + " of its colour, the ground's " + groundKeep;
        }
#endif

        static Color AsBright(Color shown)
        {
            Color light = shown.linear;
            float seen = 0.2126f * light.r + 0.7152f * light.g + 0.0722f * light.b;
            return seen > 0.02f ? light * (0.9f / seen) : light;
        }

        // ---------------------------------------------------------------- working it out

        void LateUpdate()
        {
            have = false;
            FineShadows();
            if (!on || !Where(out CelestialBody body, out Vector3d place, out double height, out double above, out double latitude, out double longitude, out bool overWater))
            {
                Near = false;
                if (cabinWas.Count > 0) CabinBack();
                SunThrough(Color.white, false);
                return;
            }
            Near = true; Here = place; Body = body;
            CelestialBody sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (body == null || sun == null) return;

            Vector3 up = (place - body.position).normalized;
            Vector3 toSun = (sun.position - place).normalized;
            double radius = body.Radius;
            float sunUp = Vector3.Dot(up, toSun);                           // the sine of how high the sun stands
            SunUp = sunUp;
            Air = body.atmosphere && height < body.atmosphereDepth ? Mathf.Clamp01(1f - Mathf.Exp(-3f * (float)(body.GetPressure(height) / 101.325))) : 0f;
            // How much of what is below is planet: all of it on the ground, less with height (as a flat thing facing the planet sees it).
            float fills = (float)((radius / (radius + height)) * (radius / (radius + height)));
            // How far round the planet can be seen from here, in degrees.
            double reach = Math.Acos(radius / (radius + height)) * 180.0 / Math.PI;

            // Everything below is added up as light is (twice the light is twice the number), which is how the game wants a probe: it
            // turns the sum into what is shown itself. Measured: a probe of 0.24 from all round shows twice as bright as the game's own
            // flat 0.24, which is a shown value; the same in light is 0.043. So what the settings and the colours say in shown values is
            // turned into light first (Color.linear).
            var made = new SphericalHarmonicsL2();
            // (twice as much at the space centre, whose buildings have to be found in the dark to be clicked on)
            float darkest = dark * (HighLogic.LoadedSceneIsFlight ? 1f : 2f);
            // (by night it is cold too, for the same reason moonlight is: see the light of other worlds)
            float night = body.atmosphere ? Mathf.InverseLerp(0.0f, -0.2f, sunUp) : 0f;
            made.AddAmbientLight(Color.Lerp(new Color(darkest, darkest, darkest), new Color(0.72f * darkest, 0.9f * darkest, 1.18f * darkest), night).linear);

            // ---- what is round about, by rays: near the ground, in flight
            float close = rays != null && rays && HighLogic.LoadedSceneIsFlight ? Mathf.Clamp01(1f - (float)above / 1200f) : 0f;
            float openAbove = 1f, foundBelow = 0f;
            Vector3 north = Vector3.ProjectOnPlane((Vector3)body.transform.up, up);
            north = north.sqrMagnitude > 1e-6f ? north.normalized : Vector3.Cross(up, Vector3.right).normalized;
            if (close > 0f)
            {
                Surroundings.Cast((Vector3)place, up, north, toSun, sunUp, 8, groundNow.linear);
                Surroundings.Open(out openAbove, openSides, out foundBelow);
                openAbove = Mathf.Lerp(1f, openAbove, close); foundBelow *= close;
                for (int q = 0; q < 4; q++) openSides[q] = Mathf.Lerp(1f, openSides[q], close);
            }
            else
            {
                if (Surroundings.Report.Length > 0) Surroundings.Clear();
                for (int q = 0; q < 4; q++) openSides[q] = 1f;
            }

            // ---- the sky: where there is air, by day, from above, in the air's own colour
            float skyLight = 0f, airBelow = 0f;
            Color skyFalls = Color.black;
            Color skyColour = Color.black;
            // ---- the clouds overhead, where a weather mod says there are any (Keystone's Weather): they take the sun's direct light,
            // spread it over the whole sky, and grey the sky's light (see Clouds)
            float cloudSun = 1f, cloudSky = 1f, cloudGrey = 0f, dust = 0f;
            sunDimmedElsewhere = false;
            Clouds(body, place, toSun, ref cloudSun, ref cloudSky, ref cloudGrey, ref dust);
            cloudSunNow = cloudSun;
            if (body.atmosphere)
            {
                skyColour = Color.Lerp(SkyOf(body), new Color(0.80f, 0.82f, 0.85f), cloudGrey);
                // (in a dust storm the light that comes down is the dust's: sunlight scattered over and over through it, which takes
                // its colour more deeply each time, as the light under a Martian dust storm is butterscotch. The dust is the ground's,
                // raised: so its colour is the ground's colour, deepened.)
                if (dust > 0.01f) skyColour = Color.Lerp(skyColour, DustColour(groundNow), Mathf.Clamp01(dust * 1.5f));
                // (how much of the planet's air is underneath: none of it on the ground, all of it from orbit)
                float all = 1f - Mathf.Exp(-3f * (float)(body.GetPressure(0.0) / 101.325));
                float here = height < body.atmosphereDepth ? 1f - Mathf.Exp(-3f * (float)(body.GetPressure(height) / 101.325)) : 0f;
                airBelow = Mathf.Clamp01(all - here);
            }
            // What the air does to sunlight that comes through it: takes out most what the sky is the colour of (that is where the
            // sky's colour comes from), and more the lower the sun, which then shines through many times as much air.
            Color passes = Color.white;
            float glow = 0f;
            if (body.atmosphere && height < body.atmosphereDepth)
            {
                float air = (float)(body.GetPressure(height) / 101.325);                        // in Kerbin's sea-level air
                float thick = 1f - Mathf.Exp(-3f * air);
                float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.22f, 0.18f, sunUp));  // twilight lasts until the sun is well under
                // (how many times as much air as straight up: Kasten and Young's rule, which holds down to the horizon)
                float degrees = Mathf.Asin(Mathf.Clamp(sunUp, -1f, 1f)) * Mathf.Rad2Deg;
                float airs = 1f / (Mathf.Max(sunUp, 0f) + 0.50572f * Mathf.Pow(Mathf.Max(degrees, -1f) + 6.07995f, -1.6364f));
                Color taken = new Color(0.04f + 0.26f * skyColour.r * skyColour.r * skyColour.r, 0.04f + 0.26f * skyColour.g * skyColour.g * skyColour.g, 0.04f + 0.26f * skyColour.b * skyColour.b * skyColour.b);
                float depth = airs * air;
                passes = new Color(Mathf.Exp(-taken.r * depth), Mathf.Exp(-taken.g * depth), Mathf.Exp(-taken.b * depth));
                // (as bright, on what faces straight up at noon at sea level, as the game's own flat light is on everything: 0.056 of
                // light, which shows as 0.27; dimmer on what faces sideways or down, which is the point; and dimmer with the sun lower,
                // by about as much as a real sky is: half at sixteen degrees, a quarter with the sun on the horizon. That last matters
                // more than it sounds. A low sun puts little light on level ground, and a sky left at its noon strength drowns the
                // shadows there, when the long shadows of morning and evening are the best of the day.)
                // (and what the eye does for a low sun, below, it does for the sky as well)
                skyLight = 0.040f * sky * thick * day * (0.28f + 0.72f * Mathf.Clamp01(sunUp / 0.8f)) * gain;
                // (held down so far to what the game's flat light was, because anything more ran into white and was cut off. With a
                // film curve to roll the bright end over, that no longer holds, and the sky can fill the shade as a real one does: a
                // good deal more brightly than the game ever let it. See FilmLook.)
                skyLight *= 1f + 0.5f * FilmLook.Working;
                // (under cloud, dust or fog: see UnderCloud)
#if DEV
                if (oldClouds)
                {
                    skyLight *= cloudSky;
                    skyLight += 0.05f * sky * day * (0.28f + 0.72f * Mathf.Clamp01(sunUp / 0.8f)) * gain * (1f - cloudSun) * Mathf.Min(1f, cloudSky) * (1f + 0.5f * FilmLook.Working);
                    Clouded = 0f; cloudShare = 1f;
                }
                else
#endif
                skyLight = UnderCloud(skyLight, sunUp, cloudSun, cloudSky);
                // A sky is all round, not only overhead: what stands upright is lit by the half of it in front, which is three fifths
                // of what the ground gets (the sky is brighter towards the horizon than overhead). So the light is given as coming
                // partly from above and partly from all round the horizon, in the shares that make it so, and little of it reaches
                // what faces down.
                Color skyIs = AsBright(Color.Lerp(Color.white, skyColour, skyKeep));
                // (and only from where it can be seen: see Surroundings)
                made.AddDirectionalLight(up, skyIs, skyLight * 1.13f * openAbove);
                Sides(ref made, up, north, 0f, skyIs, skyLight * 0.565f, openSides);
                skyFalls = skyIs * (skyLight * 1.41f);
                // The sky is not the same all round. Towards the sun it is brighter, and with the sun low that side of it is lit by
                // sunlight that has come a long way through the air and lost what the sky is the colour of: the warm side of a sunset
                // (on Kerbin; a cold blue one through Duna's dust), which goes on glowing for a while after the sun has gone.
                Vector3 level = toSun - up * sunUp;
                if (level.sqrMagnitude > 1e-6f)
                {
                    Vector3 side = (level.normalized + up * 0.2f).normalized;
                    Color far = new Color(Mathf.Exp(-taken.r * 14f * air), Mathf.Exp(-taken.g * 14f * air), Mathf.Exp(-taken.b * 14f * air));
                    float most = Mathf.Max(far.r, Mathf.Max(far.g, far.b));
                    if (most > 1e-4f) far /= most;
                    float low = Mathf.Exp(-(degrees - 1f) * (degrees - 1f) / 110f) * Mathf.InverseLerp(-14f, -5f, degrees);    // most with the sun at the horizon, gone by fourteen degrees under
                    glow = 0.034f * sky * thick * low * (1f - cloudGrey);
                    made.AddDirectionalLight(side, far.linear, glow);
                    // (and by day the sky round the sun is the brightest part of it, and nearer white)
                    made.AddDirectionalLight(toSun, Color.Lerp(skyColour, Color.white, 0.6f).linear, 0.012f * sky * thick * day * cloudSun);
                }
            }
            SunThrough(passes, true);
            if (!body.atmosphere || height >= body.atmosphereDepth) Clouded = 0f;

            // ---- every other world in the sky: from its own side, in its own colour, by how much of it is lit and how large it stands
            string shining = Worlds(ref made, body, sun, place, up, sunUp, height, skyLight);

            // ---- the ground, or the planet below: from underneath, in its own colour, as brightly as the sun is on it
            // (close to, it is the ground round about, lit as the sun stands here; from high up, the lit part of the planet that can be seen)
            float lit = Mathf.Lerp(Mathf.Max(0f, sunUp), Mathf.Clamp01(0.5f + 0.5f * sunUp), 1f - fills);
            // (and under cloud, close to the ground, the ground has only the light the clouds let through to throw back)
#if DEV
            if (!oldClouds)
#endif
            lit *= Mathf.Lerp(1f, cloudShare, fills);
            lit = Mathf.Max(lit, skyLight * 1.5f);                                               // (shaded ground under a bright sky is not black)
            // (half of the light from the ground comes from within the craft's own height of the point beneath it, nine tenths from within
            // three times that: the colour is taken from so much of the ground, and not from all that can be seen)
            if (Time.realtimeSinceStartup - groundAt > 0.5f)
            {
                groundAt = Time.realtimeSinceStartup;
                double spread = Math.Min(reach, 2.5 * above / radius * 180.0 / Math.PI);
                groundNow = GroundColour(body, latitude, longitude, spread, above < 15000.0, overWater);
                GroundNow = groundNow;
            }
            Color groundColour = Color.Lerp(Color.white * (0.2126f * groundNow.r + 0.7152f * groundNow.g + 0.0722f * groundNow.b), groundNow, groundKeep);
            // (ground in full sun throws back its own colour's share of the sun's light: half is taken, since no ground is a mirror of it)
            float groundLight = 0.5f * ground * lit * fills * (1f + 0.7f * FilmLook.Working);
            // (and in the colour the sunlight has by the time it is down there, which near sunset is the colour of the sunset)
            // (and as strong: close to the ground it is the sun as it is here that lights it; from orbit the ground below has a sun of its own)
            // (the sun's colour without the clouds: under cloud the ground is lit by the sky, in the colour the sunlight had, and by
            // as much as the clouds let through, which lit says; with the clouds' share in the colour as well, it was black, and the
            // ground under an overcast threw back nothing at all)
            Color sunTint = Color.Lerp(Color.white, reachingClear, fills * fills);
            // (a planet with air is brighter from above than its ground is: the air under the craft, and the clouds in it, throw back
            // about a sixth of the sunlight on their own account, in the air's colour and near white)
            Color below = (groundColour * sunTint).linear + Color.Lerp(skyColour, Color.white, 0.5f).linear * (0.16f * airBelow);
            // (less of it, by as much of what is below as the rays have found for themselves)
            float worked = 1f - foundBelow;
            made.AddDirectionalLight(-up, below, groundLight * worked);
            // (and what stands upright sees half of the ground: that much of its light, from all round and a little below level)
            Band(ref made, up, -0.35f, below, groundLight * 0.3f * fills * worked);
            made.AddAmbientLight(below * (groundLight * 0.05f * worked));
            // ---- and what the rays found: each surface from where it is, as the sun and the sky are on it
            // (the sun's light as it falls on what faces it: half, as for the worked-out ground, of what it is)
            if (close > 0f)
                Surroundings.Bounce(ref made, up, north, reaching.linear * (0.5f * (1f + 0.7f * FilmLook.Working)), skyFalls, close * ground * thrown);

            probe = made; have = true;
            // (what comes from all round, taken together: the mean of what something facing each of six ways would get)
            made.Evaluate(sixWays, sixFound);
            Color together = Color.black;
            foreach (Color one in sixFound) together += one;
            Around = new Color(Mathf.Max(0f, together.r / 6f), Mathf.Max(0f, together.g / 6f), Mathf.Max(0f, together.b / 6f));
            if (inside != null && inside) InsideLight(); else { haveInside = false; if (cabinWas.Count > 0) CabinBack(); }

            if (Time.realtimeSinceStartup > nextReport)
            {
                nextReport = Time.realtimeSinceStartup + 0.5f;
                // (said as it shows, not as light: what faces straight up gets this from the sky, what faces straight down this from the ground)
                float fromSky = Mathf.LinearToGammaSpace(skyLight * 1.41f);
                Color under = below * (groundLight * 1.24f);
                float fromGround = Mathf.LinearToGammaSpace(Mathf.Max(under.r, Mathf.Max(under.g, under.b)));
                report = "Over " + body.bodyName + ", the sun " + Mathf.RoundToInt(Mathf.Asin(Mathf.Clamp(sunUp, -1f, 1f)) * Mathf.Rad2Deg) + "° up.  Sky " + fromSky.ToString("F2")
                    + (skyLight > 0.0005f ? " (" + Hex(skyColour) + ")" : "") + " from above, ground " + fromGround.ToString("F2") + " (" + Hex(groundColour) + ") from below, least " + darkest.ToString("F3") + "."
                    + (glow > 0.0005f ? "  The sky's warm side " + Mathf.LinearToGammaSpace(glow * 1.06f).ToString("F2") + "." : "")
                    + (sunUp > -0.1f ? "  Sunlight " + Hex(reaching) + (gain > 1.02f ? ", the eye " + gain.ToString("F2") + " times open" : "") + "." : "") + shining;
            }
        }

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        // ---------------------------------------------------------------- the sun through the air

        bool tinting;
        static bool? othersDo;
        float cloudSunNow = 1f;
        static bool cloudsBroken;
        static bool sunDimmedElsewhere;     // the weather mod dims the sun itself just now (its own clouds drawn): not to be dimmed twice

        /// <summary>
        /// The clouds between the craft and the sun and over it, from Keystone's Weather where a weather mod answers (since
        /// Natural Light 0.9.0, Keystone 0.7): how much of the sun's direct light gets through, the sky's light against a
        /// clear sky's, and how grey it is. Kept in a method of its own and guarded, so that with an older Keystone (no
        /// Weather) the light is worked out as before.
        /// </summary>
        static void Clouds(CelestialBody body, Vector3d place, Vector3 toSun, ref float sun, ref float sky, ref float grey, ref float dust)
        {
            if (cloudsBroken) return;
            try { AskClouds(body, place, toSun, ref sun, ref sky, ref grey, ref dust); }
            catch (Exception) { cloudsBroken = true; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void AskClouds(CelestialBody body, Vector3d place, Vector3 toSun, ref float sun, ref float sky, ref float grey, ref float dust)
        {
            if (!Keystone.Weather.There) return;
            double ut = Planetarium.GetUniversalTime();
            WeatherShade shade = Keystone.Weather.Shade(body, place, (Vector3d)toSun, ut);
            sun = Mathf.Clamp01(shade.Sun); sky = Mathf.Clamp(shade.Sky, 0f, 4f); grey = Mathf.Clamp01(shade.Grey);
            dust = Mathf.Clamp01(Keystone.Weather.At(body, place).Dust);
            sunDimmedElsewhere = shade.SunDimmed;
        }

        /// <summary>
        /// The colour of dust raised from ground of this colour, and of the light that comes down through it (its strongest part as
        /// 1): the ground's hue, paler (fine dust is paler than the ground it came from), and warmed, since dust takes blue light
        /// before the rest: Duna's rust-red ground gives an ochre, as the light under a Martian dust storm is butterscotch. (Tried
        /// first as the ground's colour deepened, to the power 1.5: a salmon-red murk, far redder than the game's own Duna sky.)
        /// </summary>
        internal static Color DustColour(Color ground)
        {
            float most = Mathf.Max(ground.r, Mathf.Max(ground.g, ground.b));
            Color c = most > 0.02f ? ground / most : new Color(1f, 0.7f, 0.45f);
            c = new Color(Mathf.Sqrt(c.r), Mathf.Sqrt(c.g) * 0.9f, Mathf.Sqrt(c.b) * 0.55f);
            most = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            c /= Mathf.Max(most, 0.01f);
            c.a = 1f;
            return c;
        }

        /// <summary>
        /// The sky's light under cloud, dust or fog. What a cloud takes from the sun is not lost: most of it comes down through the
        /// cloud, spread over the whole sky, and the ground is lit from all of it and from no one side. Measured on real days, an
        /// overcast lets through a tenth to a quarter of the light of a clear day, sun and sky together; a raincloud a twentieth to a
        /// tenth; a thunderstorm's base less again. The weather says how much of the sun's direct light gets through (cloudSun) and
        /// the sky's light against a clear sky's (cloudSky: the share of a clear day's light the cloud lets through, spread over the
        /// sky; see Keystone's WeatherShade). So: a clear day's light on level ground is worked out as it shows, sun and sky added
        /// as the game adds them, turned into light; the cloud lets its share of that through; and where the sun is hidden, all of
        /// that comes from the sky. (Before 0.10.0 the sun's share was a guess a good deal too small: an overcast came out at a
        /// fourteenth of a clear day, a rain at a thirty-fifth, which is night-dark.) The stops lost are kept for the eye.
        /// </summary>
        float UnderCloud(float clearSky, float sunUp, float cloudSun, float cloudSky)
        {
            float level = Mathf.Max(0f, sunUp);
            cloudSun = Mathf.Clamp01(cloudSun);
            // a clear day's light on level ground, as light: the sky's, and the sun's added to it as the game adds them (in shown values)
            float skyAlone = clearSky * 1.13f;
            float sunShown = sunClear * level;
            float clearLight = Mathf.Pow(Mathf.LinearToGammaSpace(skyAlone) + sunShown, 2.2f);
            float sunAlone = Mathf.Max(0f, clearLight - skyAlone);
            // the share of a clear day's light the clouds let through (the weather's Sky is that, spread over the whole sky), and so the light
            // there is now: the sky's own through the gaps and the cloud, what the clouds take from the sun let through as light from the
            // sky, and what still comes straight from the sun
            float lets = Mathf.Clamp01(cloudSky / (1f + 3f * level));
            float now = skyAlone * Mathf.Min(1f, cloudSky) + sunAlone * (1f - cloudSun) * lets + sunAlone * cloudSun;
            // (the sky made as bright as leaves that much on level ground with the sun as it is: shown values add, light does not)
            float fromSky = Mathf.Max(0f, Mathf.Pow(now, 1f / 2.2f) - sunShown * cloudSun);
            float skyNow = Mathf.Pow(fromSky, 2.2f) / 1.13f;
            cloudShare = clearLight > 1e-6f ? Mathf.Clamp01(now / clearLight) : 1f;
            clearLightWas = clearLight; lightWas = now;
            // (and how much less light that is than a clear day's, in stops: by day only, for the eye)
            Clouded = clearLight > 1e-5f && now > 1e-6f ? Mathf.Clamp(Mathf.Log(clearLight / now, 2f), 0f, 5f) * Mathf.Clamp01(level / 0.08f) : 0f;
            return skyNow;
        }

        float cloudShare = 1f;          // the share of a clear day's light on level ground there is under the weather now
        float clearLightWas, lightWas;  // (a clear day's light on level ground, and the light there now, as light: for the development build's readout)

#if DEV
        static bool oldClouds;
        /// <summary>(For the development build: the light under cloud as it was worked out in 0.9.0, and back: for pictures of before and after.)</summary>
        public static string OldClouds() { oldClouds = !oldClouds; return oldClouds ? "the light under cloud as in 0.9.0" : "the light under cloud as now"; }
        /// <summary>(For the development build: the light under cloud as it is worked out this moment.)</summary>
        public static string CloudLight() => running == null ? "not running" : "the sun on white facing it " + running.sunClear.ToString("F3") + " (shown, before cloud), the sun " + Mathf.RoundToInt(Mathf.Asin(Mathf.Clamp(SunUp, -1f, 1f)) * Mathf.Rad2Deg) + " degrees up; on level ground a clear day's light " + running.clearLightWas.ToString("F3") + ", now " + running.lightWas.ToString("F3") + " (as light): " + (100f * running.cloudShare).ToString("F1") + "% of it; the eye opens for " + Clouded.ToString("F2") + " stops lost to the weather" + (oldClouds ? " (0.9.0's light)" : "");
#endif

        /// <summary>Whether something else installed already dims and reddens the low sun (Scatterer does, where it is set to).</summary>
        static bool OthersDo()
        {
            if (othersDo == null)
            {
                othersDo = false;
                try
                {
                    string config = KSPUtil.ApplicationRootPath + "GameData/Scatterer/config/config.cfg";
                    if (System.IO.File.Exists(config) && System.Text.RegularExpressions.Regex.IsMatch(System.IO.File.ReadAllText(config), @"sunlightExtinction\s*=\s*True")) othersDo = true;
                }
                catch (Exception) { }
            }
            return othersDo.Value;
        }

        // what the sun is set to, what the cameras then find it to be (something else may have dimmed it on the way), and so what
        // reaches the craft of it
        Color sunSet = Color.white, others = Color.white, reaching = Color.white, reachingClear = Color.white;
        float gain = 1f, itsShadows = float.NaN;
        float sunClear = 1f;            // how brightly the sun shows on white that faces it, before the clouds (shown, as the game adds light)
        int sunSetAt = -1;

        /// <summary>
        /// The game's own sun, which is white and of one strength whatever it shines through, given the colour sunlight has
        /// when it reaches the craft; turned up as the eye turns itself up when the air has dimmed it; and its shadows made as
        /// dark as wanted.
        ///
        /// The colour is this mod's own working out where nothing else does it. Where Scatterer does, the sun is left white
        /// here and Scatterer dims and reddens it between this and the drawing: the cameras are asked what they found (see
        /// Before), and the eye keeps pace with that instead.
        /// </summary>
        void SunThrough(Color passes, bool working)
        {
            Light sunLight = Sun.Instance != null ? Sun.Instance.sunLight : null;
            if (sunLight == null) { tinting = false; return; }
            bool mine = working && on && (through.Value == 1 || (through.Value == 0 && !OthersDo()));
            Color own = mine ? passes : Color.white;
            if (working && on)
            {
                Color left = own * others;
                // (how bright what is left looks, and its strongest colour: the eye goes by the first, and is held back by the second,
                // so that a red evening sun is turned up no further than its red can go)
                float seen = Mathf.Clamp(0.2126f * left.r + 0.7152f * left.g + 0.0722f * left.b, 0.04f, 1f);
                float most = Mathf.Clamp(Mathf.Max(left.r, Mathf.Max(left.g, left.b)), 0.04f, 1f);
                float wanted = Mathf.Min(1f / Mathf.Lerp(1f, seen, golden), 1.2f / most);
                // (below the horizon there is no sun to keep up with: night is another matter, and has its own eye)
                wanted = Mathf.Lerp(1f, wanted, Mathf.InverseLerp(-0.1f, 0.02f, SunUp));
                // (where the film look is at work the camera has an eye of its own, and the two would add up: this one does less)
                wanted = Mathf.Lerp(wanted, 1f + (wanted - 1f) * 0.6f, FilmLook.Working);
                gain = Mathf.Lerp(gain, wanted, 1f - Mathf.Exp(-4f * Mathf.Min(Time.unscaledDeltaTime, 0.1f)));
                // (and with a film curve the sun itself can be as much brighter than the shade as it is)
                // (and what the clouds leave of it: after the eye has been set, which keeps pace with the air and not with clouds)
                sunSet = own * (gain * (1f + 0.22f * FilmLook.Working)); sunSet.a = 1f;
#if DEV
                if (oldClouds) { sunSet *= cloudSunNow; sunSet.a = 1f; }
#endif
                // (and how brightly it shows on white that faces it, before the clouds: for the light under cloud)
                Color clear = left * (gain * (1f + 0.22f * FilmLook.Working));
                sunClear = sunLight.intensity * (0.2126f * clear.r + 0.7152f * clear.g + 0.0722f * clear.b);
                sunLight.color = sunSet; sunSetAt = Time.frameCount; tinting = true;
                reaching = left * (gain * (1f + 0.22f * FilmLook.Working) * cloudSunNow); reaching.a = 1f;
                reachingClear = left * (gain * (1f + 0.22f * FilmLook.Working)); reachingClear.a = 1f;
#if DEV
                if (oldClouds) reachingClear = reaching;
#endif
                Sunlight = reaching;
                if (float.IsNaN(itsShadows)) itsShadows = sunLight.shadowStrength;
                sunLight.shadowStrength = Mathf.Lerp(itsShadows, 1f, shadows);
            }
            else
            {
                if (tinting) { sunLight.color = Color.white; tinting = false; }
                sunClear = sunLight.intensity;
                if (!float.IsNaN(itsShadows)) { sunLight.shadowStrength = itsShadows; itsShadows = float.NaN; }
                gain = 1f; others = Color.white; reaching = Color.white; reachingClear = Color.white; sunSetAt = -1;
            }
        }

        // ---------------------------------------------------------------- shadows near the camera

        Vector3 itsSplit;
        bool haveSplit;
        static bool? scattererShadows;

        /// <summary>
        /// The game draws shadows in four bands of fineness, by distance from the camera, out to five kilometres: the nearest
        /// band, the finest, is the first 250 metres. On Direct3D it draws that band in to 50 metres when the camera is close
        /// to the craft (and to 100 from further off), which is what lets a booster throw a clean shadow on the tank beside
        /// it. Its own code says "if Direct3D 11" and does nothing otherwise, so a Mac or a Linux machine never gets it.
        /// This is the same rule, for them. (Tried on a Mac with the game's own numbers: the shadow of the core on a
        /// booster's nose, which the wide band loses altogether, is there and clean.)
        /// </summary>
        void FineShadows()
        {
            if (scattererShadows == null)
            {
                scattererShadows = false;
                try
                {
                    string config = KSPUtil.ApplicationRootPath + "GameData/Scatterer/config/config.cfg";
                    if (System.IO.File.Exists(config) && System.Text.RegularExpressions.Regex.IsMatch(System.IO.File.ReadAllText(config), @"terrainShadows\s*=\s*True")) scattererShadows = true;
                }
                catch (Exception) { }
            }
            bool wanted = on && fine && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 && !scattererShadows.Value
                && QualitySettings.shadowCascades == 4 && FlightCamera.fetch != null && FlightGlobals.ready && !MapView.MapIsEnabled;
            if (!wanted)
            {
                if (haveSplit) { QualitySettings.shadowCascade4Split = itsSplit; haveSplit = false; }
                return;
            }
            if (!haveSplit) { itsSplit = QualitySettings.shadowCascade4Split; haveSplit = true; }
            float off = FlightCamera.fetch.Distance;
            Vector3 split = off < 20f ? new Vector3(0.01f, 0.02f, itsSplit.z) : off < 500f ? new Vector3(0.02f, 0.05f, itsSplit.z) : itsSplit;
            // (never coarser than it was)
            split.x = Mathf.Min(split.x, itsSplit.x); split.y = Mathf.Min(split.y, itsSplit.y);
            QualitySettings.shadowCascade4Split = split;
        }

        // ---------------------------------------------------------------- the light of other worlds

        static readonly Dictionary<string, Color> faces = new Dictionary<string, Color>();

        /// <summary>The colour of a world as a whole (the mean of its picture), as light.</summary>
        static Color FaceOf(CelestialBody body)
        {
            if (faces.TryGetValue(body.bodyName, out Color known)) return known;
            Color face = new Color(0.1f, 0.1f, 0.1f);
            Globe globe = GlobeOf(body);
            if (globe != null)
            {
                double r = 0, g = 0, b = 0; int n = 0;
                for (int i = 0; i < globe.Map.Length; i += 7) { Color c = ((Color)globe.Map[i]).linear; r += c.r; g += c.g; b += c.b; n++; }
                if (n > 0) face = new Color((float)(r / n), (float)(g / n), (float)(b / n));
            }
            // (air and cloud throw back about a sixth of the light on their own account, as from above: see the ground)
            if (body.atmosphere)
            {
                Color air = body.atmosphericAmbientColor;
                float most = Mathf.Max(air.r, Mathf.Max(air.g, air.b));
                if (most > 0.01f) face += Color.Lerp(air / most, Color.white, 0.5f).linear * 0.16f;
            }
            faces[body.bodyName] = face;
            return face;
        }

        /// <summary>
        /// A world lit by the sun lights whatever can see it: by its colour, by how much of its lit side is turned this way,
        /// and by how much of the sky it takes up. From Kerbin the full Mun gives a fifty-thousandth of what the sun does,
        /// which shows as nothing by day and is all there is by night; so in the dark its light is turned up, as the eye
        /// turns itself up (see "Night eyes"). Jool over Laythe needs no such help.
        /// </summary>
        string Worlds(ref SphericalHarmonicsL2 made, CelestialBody home, CelestialBody sun, Vector3d place, Vector3 up, float sunUp, double height, float skyLight)
        {
            float much = worlds;
            if (much <= 0f || FlightGlobals.Bodies == null) return "";
            // Whether the sun itself reaches the craft (it is above the edge of the world the craft is at, seen from this height).
            double dip = Math.Sqrt(Math.Max(0.0, 1.0 - Math.Pow(home.Radius / (home.Radius + height), 2.0)));
            bool sunlit = sunUp > -dip;
            float darkness = sunlit ? 0f : 1f - Mathf.Clamp01(skyLight / 0.01f);
            // (a full Mun over Kerbin is then about as bright, on what faces it, as the game's own flat light is on everything)
            float open = 1f + (1500f * eyes - 1f) * darkness;
            // (where the film look's camera has an eye of its own, most of the opening is left to that: it does it for the whole
            // picture, and knows when there is something bright in view that it must stay shut for)
            if (Eye != null) open = Mathf.Lerp(open, 1f + (open - 1f) * 0.22f, FilmLook.Working * Mathf.Clamp01((float)Eye));
            double toSunHere = (sun.position - place).magnitude;
            string said = "";
            float best = 0f;
            foreach (CelestialBody other in FlightGlobals.Bodies)
            {
                if (other == null || other == home || other == sun || other.isStar) continue;
                Vector3d away = other.position - place;
                double far = away.magnitude;
                if (far <= other.Radius) continue;
                double takes = (other.Radius / far) * (other.Radius / far);                // how much of the sky it fills, as a flat thing facing it sees it
                if (takes < 2e-8) continue;
                Vector3 towards = (Vector3)(away / far);
                // (hidden behind the world the craft is at?)
                double under = -Vector3.Dot(towards, up);
                if (under > dip + 0.01) continue;
                float rising = Mathf.Clamp01((float)((dip + 0.01 - under) / 0.02));
                // How much of its lit side is turned this way: the whole of it when the sun is behind the craft, none when it is behind the world.
                Vector3d itsSun = (sun.position - other.position), itsUs = -away;
                double cos = Vector3d.Dot(itsSun.normalized, itsUs.normalized);
                double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos)));
                double shows = ((Math.PI - angle) * Math.Cos(angle) + Math.Sin(angle)) / Math.PI;
                double sunThere = (toSunHere / itsSun.magnitude) * (toSunHere / itsSun.magnitude);
                // (0.79: the game's sun, as light; two thirds: what a ball throws back straight at the sun, of what a flat sheet would)
                float light = (float)(0.79 * 0.667 * takes * shows * sunThere) * much * open * rising;
                Color face = FaceOf(other);
                float most = Mathf.Max(face.r, Mathf.Max(face.g, face.b));
                if (most <= 1e-5f) continue;
                light *= most; face /= most;
                light = Mathf.Min(light, 0.06f * much);                                     // (never brighter than a bright dusk)
                // (to an eye opened up for the dark, dim light is blue: it is the rods that see it, and they see blue-green best. So
                // moonlight, which is sunlight off grey rock and if anything warm, looks cold and silver.)
                face = Color.Lerp(face, new Color(0.6f * face.g, 0.84f * face.g, 1.14f * face.g), 0.8f * darkness * Mathf.Clamp01(eyes));
                if (light < 2e-5f) continue;
                made.AddDirectionalLight(towards, face, light);
                made.AddAmbientLight(face * (light * 0.1f));
                if (light > best) { best = light; said = "  " + other.bodyName + "'s light " + Mathf.LinearToGammaSpace(light * 1.06f).ToString("F2") + " (" + Hex(face.gamma) + ")."; }
            }
            return said;
        }

        // ---------------------------------------------------------------- giving it to the cameras it is for

        /// <summary>Whether a camera draws the scene the craft is in (the game has two for that, a near one and a far one).</summary>
        float strengthWas = 1f;
        int strengthAt = -1;
        bool strengthSet;

        void GiveStrengthBack()
        {
            if (!strengthSet) return;
            Light sl = Sun.Instance != null ? Sun.Instance.sunLight : null;
            if (sl != null) sl.intensity = strengthWas;
            strengthSet = false;
        }

        static bool Local(Camera cam)
        {
            string name = cam.name;
            return name == "Camera 00" || name == "Camera 01" || name == "FXCamera";
        }

        static float Share(float found, float set) => set > 1e-4f ? Mathf.Clamp(found / set, 0f, 2f) : 1f;

        /// <summary>Whether a camera draws the inside of a cockpit: the view from a seat, and the crew's portraits (which are small pictures of the same insides).</summary>
        static bool Inside(Camera cam) => (cam.cullingMask & ((1 << 16) | (1 << 20))) != 0 && (cam.cullingMask & (1 << 15)) == 0;

        SphericalHarmonicsL2 insideProbe;
        bool haveInside;
        static readonly Vector3[] six = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
        static readonly Color[] sixLights = new Color[6];

        /// <summary>
        /// The light inside the craft. The game gives a cockpit, and the crew's portraits, its one flat grey whatever is
        /// outside: so the crew sit evenly lit in their corner of the screen while the craft they are in is in the black of
        /// a planet's shadow. Inside there is what comes in at the windows of the light outside (a good half of it, from no
        /// side in particular, since the cockpit turns it about) and the glow of the cabin's own lamps and instruments,
        /// which is all there is at night.
        /// </summary>
        void InsideLight()
        {
            probe.Evaluate(six, sixLights);
            Color outside = Color.black;
            foreach (Color c in sixLights) outside += c / 6f;
            var made = new SphericalHarmonicsL2();
            made.AddAmbientLight(outside * 0.6f + new Color(0.115f, 0.105f, 0.095f).linear);
            insideProbe = made; haveInside = true;
            // The cabin's own lamps: a crew flying by night turn theirs down, to see out. By how bright it is outside: all the
            // way up by day, down to four tenths in the dark (and the film's eye, where there is one, makes up for nothing in
            // here: the portraits are drawn apart from the picture it works on).
            float seen = 0.2126f * outside.r + 0.7152f * outside.g + 0.0722f * outside.b;
            CabinLamps(Mathf.Lerp(0.4f, 1f, Mathf.Clamp01(seen / 0.02f)));
        }

        readonly Dictionary<Light, float> cabinWas = new Dictionary<Light, float>();
        readonly Dictionary<Light, float> cabinSet = new Dictionary<Light, float>();
        readonly List<Light> cabin = new List<Light>();
        float cabinLooked = -10f, cabinLevel = 1f;

        /// <summary>The lamps inside cockpits, turned to so much of what they are. (Looked for every two seconds: a cockpit's insides come and go with the craft.)</summary>
        void CabinLamps(float level)
        {
            cabinLevel = Mathf.MoveTowards(cabinLevel, level, 0.5f * Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            if (Time.realtimeSinceStartup - cabinLooked > 2f)
            {
                cabinLooked = Time.realtimeSinceStartup;
                cabin.Clear();
                foreach (Light l in FindObjectsOfType<Light>())
                    if (l != null && l.type != LightType.Directional && (l.cullingMask & ((1 << 16) | (1 << 20))) != 0 && (l.cullingMask & (1 << 15)) == 0 && !l.name.StartsWith("Natural Light")) cabin.Add(l);
            }
            foreach (Light l in cabin)
            {
                if (l == null) continue;
                // (if something else has changed a lamp since it was last set here, that is its new strength)
                if (!cabinWas.ContainsKey(l) || !cabinSet.TryGetValue(l, out float set) || Mathf.Abs(l.intensity - set) > 1e-4f) cabinWas[l] = l.intensity;
                float now = cabinWas[l] * cabinLevel;
                l.intensity = now; cabinSet[l] = now;
            }
        }

        void CabinBack()
        {
            foreach (KeyValuePair<Light, float> kv in cabinWas)
                if (kv.Key != null && cabinSet.TryGetValue(kv.Key, out float set) && Mathf.Abs(kv.Key.intensity - set) < 1e-4f) kv.Key.intensity = kv.Value;
            cabinWas.Clear(); cabinSet.Clear(); cabin.Clear(); cabinLevel = 1f;
        }

        void Before(Camera cam)
        {
            if (cam == null) return;
            if (have && on && Local(cam))
            {
                // What the clouds leave of the sun: given as its strength, at the moment of drawing, to the cameras of the scene
                // only, and given back for every other camera. Not as its colour: Scatterer, where it dims and reddens the sun
                // itself, puts a colour of its own in just before these cameras draw, kept from when the scene began, and with it
                // the sun shone at full strength through any overcast (measured: set to black, found at (1.15, 0.86, 0.46)). The
                // game sets the strength afresh each frame, so it is taken as the game left it, once a frame. (Left alone where
                // nothing is in the way: where a weather mod's own clouds are drawn, they dim the sun this same way themselves, and
                // the weather then says the sun is clear of cloud.)
                Light sl = Sun.Instance != null ? Sun.Instance.sunLight : null;
                if (sl != null && ((cloudSunNow < 0.999f && !sunDimmedElsewhere) || strengthSet))
                {
                    if (strengthAt != Time.frameCount) { strengthWas = sl.intensity; strengthAt = Time.frameCount; }
                    sl.intensity = strengthWas * (sunDimmedElsewhere ? 1f : cloudSunNow);
#if DEV
                    if (oldClouds) sl.intensity = strengthWas;
#endif
                    strengthSet = true;
                }
                // What the sun's colour is now, at the moment of drawing, against what it was set to this frame: the difference is
                // someone else's doing (Scatterer's, where it dims the sun through the air), and is taken into account next frame.
                // (Only a dimming is believed. Anything else is somebody setting the sun outright, which cannot be told apart.)
                Light sunLight = Sun.Instance != null ? Sun.Instance.sunLight : null;
                if (sunLight != null && sunSetAt == Time.frameCount && cam.name == "Camera 00")
                {
                    Color found = sunLight.color;
                    var share = new Color(Share(found.r, sunSet.r), Share(found.g, sunSet.g), Share(found.b, sunSet.b));
                    others = share.r <= 1.02f && share.g <= 1.02f && share.b <= 1.02f ? share : Color.white;
                }
                if (!changed) { itsMode = RenderSettings.ambientMode; changed = true; }
                RenderSettings.ambientMode = AmbientMode.Custom;
                RenderSettings.ambientProbe = probe;
            }
            else if (have && on && inside != null && inside && haveInside && Inside(cam))
            {
                GiveStrengthBack();
                if (!changed) { itsMode = RenderSettings.ambientMode; changed = true; }
                RenderSettings.ambientMode = AmbientMode.Custom;
                RenderSettings.ambientProbe = insideProbe;
            }
            else if (changed || strengthSet)
            {
                GiveStrengthBack();
                // (every other camera, and every camera once this is off, has the game's own light)
                RenderSettings.ambientMode = itsMode;
                if (!have || !on) changed = false;
            }
        }

#if DEV
        /// <summary>(For the development build: the map the ground's colour is read off, written out as a picture four times its size, with a red mark where the craft is taken to be.)</summary>
        public static string Dump()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "no craft";
            Globe globe = GlobeOf(v.mainBody);
            if (globe == null) return "no map";
            const int K = 4;
            var big = new Texture2D(globe.Wide * K, globe.High * K, TextureFormat.RGB24, false);
            for (int y = 0; y < globe.High * K; y++)
                for (int x = 0; x < globe.Wide * K; x++) big.SetPixel(x, y, globe.Map[x / K + (y / K) * globe.Wide]);
            double u = (90.0 - v.longitude) / 360.0; u -= Math.Floor(u);
            int cx = (int)(u * globe.Wide * K), cy = (int)((v.latitude + 90.0) / 180.0 * globe.High * K);
            for (int d = -6; d <= 6; d++) { big.SetPixel(Mathf.Clamp(cx + d, 0, globe.Wide * K - 1), cy, Color.red); big.SetPixel(cx, Mathf.Clamp(cy + d, 0, globe.High * K - 1), Color.red); }
            big.Apply();
            string path = System.IO.Path.Combine(Application.temporaryCachePath, "naturallight_map.png");
            System.IO.File.WriteAllBytes(path, big.EncodeToPNG());
            Destroy(big);
            return path;
        }

        /// <summary>
        /// (For the development build: how the game's shadows are set, and, from the text kept as "tryshadows", a setting of them to
        /// try: "split0 split1 split2 bias normalBias distance", any of them "-" to leave as it is.)
        /// </summary>
        public static string Shadows()
        {
            Light sunLight = Sun.Instance != null ? Sun.Instance.sunLight : null;
            string text = Kept("tryshadows");
            if (!string.IsNullOrEmpty(text) && text.Trim() != "-")
            {
                string[] w = text.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                Func<int, float, float> num = (n, otherwise) => n < w.Length && float.TryParse(w[n], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float got) ? got : otherwise;
                Vector3 split = QualitySettings.shadowCascade4Split;
                QualitySettings.shadowCascade4Split = new Vector3(num(0, split.x), num(1, split.y), num(2, split.z));
                if (sunLight != null) { sunLight.shadowBias = num(3, sunLight.shadowBias); sunLight.shadowNormalBias = num(4, sunLight.shadowNormalBias); }
                QualitySettings.shadowDistance = num(5, QualitySettings.shadowDistance);
            }
            Vector3 now = QualitySettings.shadowCascade4Split;
            return "graphics " + SystemInfo.graphicsDeviceType + "; shadows " + QualitySettings.shadows + ", " + QualitySettings.shadowResolution + ", " + QualitySettings.shadowProjection + ", to " + QualitySettings.shadowDistance + " m in " + QualitySettings.shadowCascades
                + " parts split at " + now.x.ToString("F4") + " " + now.y.ToString("F4") + " " + now.z.ToString("F4") + (sunLight != null ? "; the sun's " + sunLight.shadows + " strength " + sunLight.shadowStrength + " bias " + sunLight.shadowBias + " normal bias " + sunLight.shadowNormalBias + " near plane " + sunLight.shadowNearPlane + " resolution " + sunLight.shadowResolution + " custom " + sunLight.shadowCustomResolution : "")
                + "; camera " + (FlightCamera.fetch != null ? FlightCamera.fetch.Distance.ToString("F1") : "?") + " m off"
                + (Sun.Instance != null && sunLight != null ? "; the sun fades from " + Sun.Instance.fadeStart + " to " + Sun.Instance.fadeEnd + " (sine of its height), now " + sunLight.intensity + " at " + SunUp.ToString("F3") + ", colour " + sunLight.color + ", others take " + (running != null ? running.others.ToString() : "?") : "");
        }

        /// <summary>(For the development build: who throws shadows and who takes them, among the craft's parts and what it stands on; and, with "shadowsoff" kept, the sun's shadows put out or back.)</summary>
        public static string Casters()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "no craft";
            var o = new System.Text.StringBuilder();
            Light sunLight = Sun.Instance != null ? Sun.Instance.sunLight : null;
            string off = Kept("shadowsoff");
            if (sunLight != null && !string.IsNullOrEmpty(off)) sunLight.shadows = off == "1" ? LightShadows.None : LightShadows.Soft;
            var counts = new Dictionary<string, int>();
            foreach (Part p in v.parts)
                foreach (Renderer r in p.GetComponentsInChildren<Renderer>(false))
                {
                    if (r is ParticleSystemRenderer) continue;
                    string k = "part: layer " + r.gameObject.layer + " " + r.GetType().Name + " casts " + r.shadowCastingMode + " takes " + r.receiveShadows + " probes " + r.lightProbeUsage + " shader " + (r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?")
                        + (r.sharedMaterial != null && r.sharedMaterial.HasProperty("_EmissiveColor") ? " emissive " + r.sharedMaterial.GetColor("_EmissiveColor") : "") + (r.sharedMaterial != null && r.sharedMaterial.HasProperty("_RimColor") ? " rim " + r.sharedMaterial.GetColor("_RimColor") + (r.sharedMaterial.HasProperty("_RimFalloff") ? "/" + r.sharedMaterial.GetFloat("_RimFalloff") : "") : "");
                    counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
                }
            Vector3 from = v.CoM + (Vector3)FlightGlobals.getUpAxis(v.CoM) * 2f;
            foreach (RaycastHit hit in Physics.RaycastAll(from, -(Vector3)FlightGlobals.getUpAxis(v.CoM), 60f, ~0))
            {
                Renderer r = hit.collider.GetComponentInParent<Renderer>() ?? hit.collider.GetComponentInChildren<Renderer>();
                if (hit.collider.GetComponentInParent<Part>() != null) continue;
                string k = "below: " + hit.collider.name + " layer " + hit.collider.gameObject.layer + (r != null ? " casts " + r.shadowCastingMode + " takes " + r.receiveShadows + " shader " + (r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?") : " (no renderer on it)");
                counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
            }
            foreach (KeyValuePair<string, int> kv in counts) o.Append(kv.Value + " x " + kv.Key + "\n");
            foreach (Camera cam in Camera.allCameras) o.Append("camera " + cam.name + " near " + cam.nearClipPlane + " far " + cam.farClipPlane + " mask " + cam.cullingMask + " path " + cam.actualRenderingPath + " depth " + cam.depth + "\n");
            o.Append("baked light probes: " + (LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count.ToString() : "none") + "\n");
            foreach (Light l in FindObjectsOfType<Light>()) if (l.enabled && l.intensity > 0f && (l.cullingMask & (1 << 17)) != 0 && (l.transform.position - v.transform.position).sqrMagnitude < l.range * l.range * 4f)
                    o.Append("light on kerbals' layer: " + l.name + " " + l.type + " " + l.renderMode + " intensity " + l.intensity + " range " + l.range + " " + (l.transform.position - v.transform.position).magnitude.ToString("F1") + " m off, colour " + l.color + "\n");
            if (sunLight != null) o.Append("sun: shadows " + sunLight.shadows + " mask " + sunLight.cullingMask + " intensity " + sunLight.intensity + " mode " + sunLight.renderMode);
            return o.ToString();
        }

        /// <summary>(For the development build: the colour each world's sky lights things in, and what it was worked out from.)</summary>
        public static string Skies()
        {
            var o = new System.Text.StringBuilder();
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null || !body.atmosphere) continue;
                AtmosphereFromGround air = body.afg;
                o.Append(body.bodyName + ": sky " + Hex(SkyOf(body)) + ", the game's own " + Hex(body.atmosphericAmbientColor) + (air != null ? ", wavelengths " + air.waveLength.r.ToString("F3") + " " + air.waveLength.g.ToString("F3") + " " + air.waveLength.b.ToString("F3") : ", no sky of the game's") + ", face " + Hex(FaceOf(body).gamma) + "; ");
            }
            return o.ToString();
        }

        /// <summary>(For the development build: the glow of the kerbal being flown: each of its glowing materials, what picture says where it glows, and whether its lamp is lit.)</summary>
        public static string SuitGlow()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "no craft";
            var o = new System.Text.StringBuilder();
            KerbalEVA eva = v.GetComponent<KerbalEVA>() ?? v.GetComponentInChildren<KerbalEVA>();
            o.Append(eva != null ? "kerbal: lamp " + eva.lampOn + "\n" : "not a kerbal\n");
            var seen = new HashSet<string>();
            foreach (Renderer r in v.GetComponentsInChildren<Renderer>(true))
            {
                Material m = r.sharedMaterial;
                if (m == null || !m.HasProperty("_EmissiveColor")) continue;
                Texture mask = m.HasProperty("_Emissive") ? m.GetTexture("_Emissive") : null;
                var block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block);
                string k = r.name + ": material " + m.name + ", glow " + m.GetColor("_EmissiveColor") + ", where: " + (mask != null ? mask.name + " " + mask.width + "x" + mask.height : "everywhere (no picture)") + (block.isEmpty ? "" : ", has a block; its glow " + block.GetColor("_EmissiveColor")) + (r.enabled && r.gameObject.activeInHierarchy ? "" : " (not drawn)");
                if (seen.Add(m.name + (mask != null ? mask.name : ""))) o.Append(k + "\n");
            }
            return o.ToString();
        }

        /// <summary>(For the development build: every lamp that lights the insides of cockpits, and every camera that draws them.)</summary>
        public static string Cabin()
        {
            var o = new System.Text.StringBuilder();
            foreach (Light l in FindObjectsOfType<Light>())
                if ((l.cullingMask & ((1 << 16) | (1 << 20))) != 0)
                    o.Append("light " + l.name + " (under " + (l.transform.parent != null ? l.transform.parent.name : "nothing") + ") " + l.type + " " + (l.enabled ? "on" : "off") + " intensity " + l.intensity + " range " + l.range + " colour " + l.color + " mask " + l.cullingMask + " shadows " + l.shadows + "\n");
            foreach (Camera cam in Camera.allCameras)
                if ((cam.cullingMask & ((1 << 16) | (1 << 20))) != 0) o.Append("camera " + cam.name + " mask " + cam.cullingMask + " hdr " + cam.allowHDR + " target " + (cam.targetTexture != null ? cam.targetTexture.width + "x" + cam.targetTexture.height : "the screen") + (Inside(cam) ? " (taken as inside)" : "") + "\n");
            o.Append("ambient now: " + RenderSettings.ambientMode + " " + RenderSettings.ambientLight);
            return o.ToString();
        }

        /// <summary>(For the development build: what it is doing, as the page says it.)</summary>
        public static string Report() => running != null ? running.report + (EngineLamps.Report.Length > 0 ? "  " + EngineLamps.Report : "") + (Floodlights.Report.Length > 0 ? "  " + Floodlights.Report : "") + (RunwayLamps.Report.Length > 0 ? "  " + RunwayLamps.Report : "") + (Surroundings.Report.Length > 0 ? "  " + Surroundings.Report : "") + (SunShafts.Report.Length > 0 ? "  " + SunShafts.Report : "") + (WeatherVeil.Report.Length > 0 ? "  " + WeatherVeil.Report : "") + (LitSmoke.Report.Length > 0 ? "  " + LitSmoke.Report : "") + (FilmLook.Report.Length > 0 ? "  " + FilmLook.Report : "") : "not running";

        /// <summary>(For the development build: the ground's colour at a few known places on the planet the craft is at, to check the map is read the right way round.)</summary>
        public static string Places()
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return "no craft";
            CelestialBody body = v.mainBody;
            var o = new System.Text.StringBuilder(body.bodyName + ": here " + Hex(GroundColour(body, v.latitude, v.longitude, 0.4, true, body.ocean && v.terrainAltitude < 0.0)));
            foreach (double[] at in new[] { new[] { 85.0, 0.0 }, new[] { -85.0, 0.0 }, new[] { 0.0, -60.0 }, new[] { 0.0, -74.5 }, new[] { 0.0, -90.0 }, new[] { 0.0, 0.0 }, new[] { 0.0, 90.0 }, new[] { 0.0, 180.0 }, new[] { -6.5, -144.0 } })
                o.Append("; " + at[0] + "," + at[1] + " " + Hex(GroundColour(body, at[0], at[1], 2, false, false)));
            return o.ToString();
        }
#endif
    }
}
