using System;
using System.Collections.Generic;
using Keystone;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// (Since 0.13.0.) The other worlds, and craft far off, as the points of light they are.
    ///
    /// The game draws every world as a ball, and one smaller than a dot of the screen as nothing (or as a dot that comes and goes
    /// as it moves). So from Kerbin by night Jool, Duna and Eve are not in the sky at all, though worlds like them are the
    /// brightest things in a real night sky after the moon; and a craft in orbit, a few metres across and a hundred kilometres up,
    /// is never seen crossing it. Here each world or craft too small to show is drawn as a point of light, as bright as it really
    /// is, worked out as an astronomer works out a planet's brightness: from its size, from how much of the sun's light it throws
    /// back (a world: the mean colour of its own picture, as for the light of other worlds; a craft: a grey of a third), from how
    /// much of its lit side is turned this way, and from how far it is from the sun and from here.
    ///
    /// That is set against the game's own sky of stars, measured as it is drawn (DEV Census): its fourth brightest is taken as of
    /// magnitude 0, as the fourth of ours is (Arcturus). The game's sky has fewer faint stars and more middling ones than a real
    /// one (its brightest is only twice its fifteenth, where Sirius is ten times ours), so it is its brightest that the worlds
    /// are set against, as an eye looking for a planet sets it against the brightest stars near it. A point is drawn as a
    /// camera sees one: a soft dot about a pixel across with a faint glow round it, and, where it is brighter than the picture can
    /// hold, burnt out white in the middle and wider, which is why bright stars look bigger in every photograph.
    ///
    /// Drawn into the sky of stars by the camera that draws it, first of all, so that whatever is in front hides it: a world, the
    /// ground, a cloud. Dimmed with the stars, as their light is (by the sky's light at dusk and by day: see Twilight; with the sun
    /// in sight; with something bright in view, by this mod's eye: see FilmLook), so the brightest come out at dusk before most
    /// stars do, as the evening star does; dimmed and reddened by the air towards the horizon. A world in its parent's shadow is dark, and a craft goes out as it passes into the
    /// shadow of the world it goes round: after sunset at the space centre, craft in a low orbit catch the sun overhead and fade
    /// out partway across the sky. Once a world or craft is two or three dots of the screen across, the game's own drawing of it
    /// takes over.
    ///
    /// The idea is Distant Object Enhancement's (TheDarkBadger, MOARdV and others; CC BY 4.0, as this mod is): far worlds and craft
    /// as points of light. Worked out anew here, with the brightness from the physics in place of its tuned curves.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    [DefaultExecutionOrder(150)]
    public sealed class FarLights : MonoBehaviour
    {
        // Each point is drawn as two squares, each with a picture of a dot of a given light on it (Picture): the one below its light
        // and the one above, weighed between them. (Drawn first as rings of coloured corners, as many as the dot's shape needed, a
        // point came out 15 to 40% brighter on Direct3D than on OpenGL, with the game's anti-aliasing: there a pixel only partly
        // covered by one of the small triangles at a point's middle takes the colour worked out at the pixel's own middle, which can
        // lie beyond the triangle's tip, where the colours run on past the brightest. Found on the PC, 2026-10-24. A picture read at
        // any place gives what is there, and nothing past its brightest.)
        const int Levels = 14;                                  // pictures of a dot, each of twice the light of the one before
        const float Least = 0.25f;                              // the light of the first, in white dots of the screen
        const int Cell = 128;                                   // each picture, so many of its own dots across
        const float Half = 24f;                                 // each square, half across, in dots of the screen (the glow of the brightest)
        const int Each = 8;                                     // corners of one point: two squares
        const int Most = 48;                                    // points drawn at most: the brightest
        // (the dot: as narrow as it can be and still add up to the same light wherever it falls between pixels)
        const float Sigma = 0.65f;
        // (the glow round it, as a lens and an eye throw it: its share of the light, how wide its middle is, how fast it falls away)
        const float Halo = 0.06f, HaloWide = 1.6f, HaloFall = 1.6f;
        // (a dot of the screen at the game's own field of view, 60 degrees across 1080 dots, in radians)
        const float Dot = 1.0692e-3f;
        // The light of a point of magnitude 0, as so many white dots of the screen at that size (as light, not as shown): the game's
        // own sky as drawn, its fourth brightest star (DEV Census, 2026-10-24: 8.7; its brightest 11, its fifteenth 5.6, its 1,602nd
        // 0.55; the same with the deep sky and without, whose added stars are fainter than the game's brightest).
        static float zero = 8.7f;
        // (nothing brighter than this as a point: anything brighter in a real sky is a disc, the moon; and a point of light this
        // bright is already a dazzle as wide as a few dots)
        const float Brightest = -5f;

        struct Point { public string Name; public Vector3 Way; public Color Light; public float Mag, Flux, Across, Fade; public bool Craft; }

        readonly List<Point> points = new List<Point>(64);
        GameObject shown;
        Mesh mesh;
        Material ink;
        Vector3[] spots = new Vector3[0];
        Color32[] tints = new Color32[0];
        Vector2[] places = new Vector2[0];
        Texture2D dots;
        int room, usedWas;
        static readonly Color32 Clear = new Color32(0, 0, 0, 0);
        Camera skyCam;
        GalaxyCubeControl skyWas;
        Renderer face;
        float brightest, reach, scale, stars, fine;
        int reported = -1;
        static FarLights running;
        static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        public static string Report = "";

        void Awake() => running = this;

        void OnDestroy()
        {
            if (running == this) running = null;
            if (shown != null) Destroy(shown);
            if (mesh != null) Destroy(mesh);
            if (ink != null) Destroy(ink);
            if (dots != null) Destroy(dots);
        }

        void Show(bool on) { if (shown != null && shown.activeSelf != on) shown.SetActive(on); }

        void Update()
        {
            // (the colour of each world, read off its picture at the start of a frame, one a frame: each is a picture copied back
            // from the card, which holds the game up while it is, and sixteen at once would be a long frame)
            if (!Daylight.On || Daylight.FarLit == null || !Daylight.FarLit || FlightGlobals.Bodies == null) return;
            foreach (CelestialBody b in FlightGlobals.Bodies)
                if (b != null && !b.isStar && !Daylight.FaceKnown(b)) { Daylight.FaceOf(b); break; }
        }

        void LateUpdate()
        {
#if DEV
            timer.Restart();
            Work();
            timer.Stop();
            float ms = (float)timer.Elapsed.TotalMilliseconds;
            meanMs = Mathf.Lerp(meanMs, ms, 0.02f); worstMs = Mathf.Max(worstMs * 0.999f, ms);
#else
            Work();
#endif
        }
#if DEV
        static readonly System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();
        static float meanMs, worstMs;
        /// <summary>(Development build: what this costs a frame, its mean and its worst lately.)</summary>
        public static string Cost() => meanMs.ToString("F3") + " ms a frame, the worst lately " + worstMs.ToString("F2") + " (before the worlds " + parts[0].ToString("F3") + ", the worlds " + parts[1].ToString("F3") + ", craft " + parts[2].ToString("F3") + ", the rings " + parts[3].ToString("F3") + ", the mesh " + parts[4].ToString("F3") + ")";
        static readonly float[] parts = new float[5];
#endif

        void Work()
        {
            points.Clear();
            GalaxyCubeControl sky = GalaxyCubeControl.Instance;
            bool wanted = Daylight.On && Daylight.FarLit != null && Daylight.FarLit && !MapView.MapIsEnabled && FlightGlobals.Bodies != null && Planetarium.fetch != null && Planetarium.fetch.Sun != null;
            if (!wanted || sky == null) { Show(false); Report = ""; reported = -1; return; }
            if (skyCam == null) skyCam = Names.CameraNamed("GalaxyCamera");
            Camera eye = HighLogic.LoadedSceneIsFlight && FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Names.CameraNamed("Camera 00") ?? Camera.main;
            if (skyCam == null || eye == null) { Show(false); Report = ""; reported = -1; return; }

            // ---- the game's sky: how far off its faces are, and how bright its stars are just now (as Natural Light's deep sky reads it)
            if (sky != skyWas)
            {
                skyWas = sky; face = null; brightest = 0f; reach = 0f;
                foreach (Renderer r in sky.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r.name.StartsWith("Natural Light")) continue;
                    if (face == null) face = r;
                    float far = Vector3.Distance(r.bounds.center, skyCam.transform.position);
                    if (far > 1f && (reach <= 0f || far < reach)) reach = far;
                }
                if (reach <= 0f) reach = 500f;
            }
            float most = Mathf.Max(sky.maxGalaxyColor.r, Mathf.Max(sky.maxGalaxyColor.g, sky.maxGalaxyColor.b));
            brightest = Mathf.Max(brightest, most);
            Color now = sky.maxGalaxyColor;
            if (face != null) { face.GetPropertyBlock(block); if (!block.isEmpty) now = block.GetColor("_Color"); }
            // (the faces are coloured as shown, so their light goes as that to the power 2.2)
            stars = brightest > 1e-4f ? Mathf.Pow(Mathf.Clamp01(Mathf.Max(now.r, Mathf.Max(now.g, now.b)) / brightest), 2.2f) : 1f;
            float perDot = 2f * Mathf.Tan(skyCam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, skyCam.pixelHeight);
            if (stars < 0.002f || perDot <= 0f) { Show(false); Report = "Far lights: the stars are out (the sky too bright)."; reported = -1; return; }

            Vector3d here = eye.transform.position;
            CelestialBody sun = Planetarium.fetch.Sun;
            CelestialBody home = HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.mainBody : FlightGlobals.GetHomeBody();
            CelestialBody homeWorld = FlightGlobals.GetHomeBody();
            double year = homeWorld != null && homeWorld.orbit != null ? homeWorld.orbit.semiMajorAxis : 13599840256.0;
            // (the air above: how much there is to see through, and which way is up)
            Vector3 up = Vector3.up; double dip = -1.0; float column = 0f;
            if (home != null)
            {
                Vector3d fromMiddle = here - home.position;
                double r = fromMiddle.magnitude, height = r - home.Radius;
                up = (Vector3)(fromMiddle / r);
                dip = Math.Sqrt(Math.Max(0.0, 1.0 - Math.Pow(home.Radius / Math.Max(r, home.Radius), 2.0)));
                if (home.atmosphere && height < home.atmosphereDepth)
                    column = (float)(home.GetPressure(Math.Max(0.0, height)) / 101.325 / Math.Max(0.01, home.GeeASL));
            }
            // (a point's light in more dots for a finer screen and for a narrower view, as the game's stars' is: their picture is
            // spread over more dots then. So a point keeps its brightness against them however the view is zoomed)
            fine = Mathf.Max(0.25f, skyCam.pixelHeight / 1080f);
            float zoom = Dot / (fine * perDot);
            scale = fine * fine * zoom * zoom;

#if DEV
            long t0 = timer.ElapsedTicks;
#endif
            // ---- the worlds
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (b == null || b == sun || b.isStar) continue;
                Vector3d to = b.position - here;
                double far = to.magnitude;
                if (far <= b.Radius * 1.01) continue;
                float across = (float)(2.0 * Math.Asin(Math.Min(1.0, b.Radius / far)) / perDot);
                if (across > 3f || !Daylight.FaceKnown(b)) continue;
                Add(b.bodyName, (Vector3)(to / far), b.position, far, b.Radius, Daylight.FaceOf(b), b, sun, up, dip, column, across, year, false, true);
            }

#if DEV
            long t1 = timer.ElapsedTicks;
#endif
            // ---- craft
            if (Daylight.FarCraft != null && Daylight.FarCraft && FlightGlobals.Vessels != null)
            {
                foreach (Vessel v in FlightGlobals.Vessels)
                {
                    if (v == null || v.vesselType == VesselType.Flag || v.vesselType == VesselType.EVA || v.vesselType == VesselType.SpaceObject || v.mainBody == null) continue;
                    Vector3d at = v.GetWorldPos3D();
                    Vector3d to = at - here;
                    double far = to.magnitude;
                    if (far < 200.0) continue;
                    double size = Math.Sqrt(Area(v) / Math.PI);
                    float across = (float)(2.0 * size / far / perDot);
                    // (a craft near enough to be loaded is drawn by the game; one further off, not at all)
                    if (across > 3f && v.loaded) continue;
                    Add(v.vesselName, (Vector3)(to / far), at, far, size, Grey, v.mainBody, sun, up, dip, column, across, year, true, v.loaded);
                }
            }

#if DEV
            if (ladder)
                for (int i = 0; i < 8; i++)
                {
                    float m = i - 3;
                    Vector3 way = Quaternion.AngleAxis((i - 3.5f) * 2.5f, eye.transform.up) * eye.transform.forward;
                    points.Add(new Point { Name = "magnitude " + m, Way = way, Light = Color.white, Mag = m, Flux = FromMagnitude(m), Across = 0f, Fade = 1f });
                }
#endif
#if DEV
            long t2 = timer.ElapsedTicks;
#endif
            // ---- drawn: the brightest of them, each as two squares with a picture of a dot on each
            if (points.Count > Most) { points.Sort((a, b) => a.Mag.CompareTo(b.Mag)); points.RemoveRange(Most, points.Count - Most); }
            Build(points.Count);
            if (shown == null) return;
            shown.transform.position = skyCam.transform.position;
            shown.transform.rotation = Quaternion.identity;
            float far2 = reach * 0.8f;
            for (int p = 0; p < room; p++)
            {
                int at = p * Each;
                if (p >= points.Count)
                {
                    // (room not wanted this frame: emptied once, when it stops being used)
                    if (p < usedWas) for (int i = 0; i < Each; i++) { spots[at + i] = Vector3.zero; tints[at + i] = Clear; }
                    continue;
                }
                Point pt = points[p];
                float light = pt.Flux * pt.Fade;
                Vector3 w = pt.Way;
                Vector3 a = Vector3.Cross(w, Mathf.Abs(w.y) < 0.9f ? Vector3.up : Vector3.right).normalized, c = Vector3.Cross(w, a);
                float step = perDot * far2 * Half;
                Vector3 middle = w * far2, across = a * step, up2 = c * step;
                // (its colour as the picture shows it, its brightest channel at one: so a bright coloured point keeps its hue where it
                // burns out, as an eye sees a bright planet in its colour)
                float r = AsShown(Mathf.Clamp01(pt.Light.r * 0.5f)), g = AsShown(Mathf.Clamp01(pt.Light.g * 0.5f)), bl = AsShown(Mathf.Clamp01(pt.Light.b * 0.5f));
                float top = Mathf.Max(r, Mathf.Max(g, bl));
                if (top > 0f) { r /= top; g /= top; bl /= top; }
                // (which pictures, and how much of each: by the light's place between them, counted in doublings)
                float q = Mathf.Log(Mathf.Max(light, 1e-9f) / Least, 2f);
                int low; float upper, dim = 1f;
                if (q <= 0f) { low = 0; upper = 0f; dim = Mathf.Pow(Mathf.Max(light, 0f) / Least, 1f / 2.2f); }
                else if (q >= Levels - 1) { low = Levels - 1; upper = 0f; }
                else { low = (int)q; upper = q - low; }
                Square(at, middle, across, up2, low, (1f - upper) * dim, r, g, bl);
                Square(at + 4, middle, across, up2, Mathf.Min(low + 1, Levels - 1), upper * dim, r, g, bl);
            }
            usedWas = points.Count;
#if DEV
            long t3 = timer.ElapsedTicks;
#endif
            mesh.vertices = spots;
            mesh.colors32 = tints;
            mesh.uv = places;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (reach * 3f));
#if DEV
            long t4 = timer.ElapsedTicks;
            double perMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            parts[0] = Mathf.Lerp(parts[0], (float)(t0 / perMs), 0.02f); parts[1] = Mathf.Lerp(parts[1], (float)((t1 - t0) / perMs), 0.02f);
            parts[2] = Mathf.Lerp(parts[2], (float)((t2 - t1) / perMs), 0.02f); parts[3] = Mathf.Lerp(parts[3], (float)((t3 - t2) / perMs), 0.02f);
            parts[4] = Mathf.Lerp(parts[4], (float)((t4 - t3) / perMs), 0.02f);
#endif
            Show(points.Count > 0);
            // (its words made only when the count changes: made every frame they would be garbage)
            if (points.Count != reported) { reported = points.Count; Report = points.Count == 0 ? "" : "Far lights: " + points.Count + (points.Count == 1 ? " point" : " points") + " of light in the sky."; }
        }

        /// <summary>A point's light, as so many white dots of the screen, from its magnitude: no brighter than Brightest, and dimmed with the stars.</summary>
        float FromMagnitude(float mag) => Mathf.Min(zero * Mathf.Pow(10f, -0.4f * mag) * scale, zero * Mathf.Pow(10f, -0.4f * Brightest) * fine * fine) * stars;

        /// <summary>One point, if it is lit and above the horizon: its brightness worked out as for a ball lit by the sun.</summary>
        void Add(string name, Vector3 way, Vector3d at, double far, double radius, Color colour, CelestialBody parent, CelestialBody sun, Vector3 up, double dip, float column, float across, double year, bool craft, bool drawnByGame)
        {
            // (behind the world the camera is at)
            float rising = 1f;
            if (dip >= 0.0)
            {
                double under = -Vector3.Dot(way, up);
                if (under > dip + 0.004) return;
                rising = Mathf.Clamp01((float)((dip + 0.004 - under) / 0.008));
            }
            // How much of its lit side is turned this way: all of it with the sun behind the camera, none with the sun behind it.
            Vector3d itsSun = sun.position - at;
            double toSun = itsSun.magnitude;
            double cos = Vector3d.Dot(itsSun / toSun, -(Vector3d)way);
            double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos)));
            double shows = ((Math.PI - angle) * Math.Cos(angle) + Math.Sin(angle)) / Math.PI;
            if (shows < 1e-5) return;
            // (and how much of the sun reaches it past the world it goes round, and that one's own: a moon in its planet's shadow,
            // a craft in the shadow of the world below it)
            float lit = 1f;
            for (CelestialBody by = craft ? parent : parent.referenceBody; by != null && by != sun && lit > 0f; by = by.referenceBody == by ? null : by.referenceBody)
            {
                Vector3d toBy = by.position - at;
                double db = toBy.magnitude;
                if (db <= by.Radius) { lit = 0f; break; }
                double rs = Math.Asin(Math.Min(1.0, sun.Radius / toSun)), rb = Math.Asin(Math.Min(1.0, by.Radius / db));
                double apart = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(toBy / db, itsSun / toSun))));
                lit *= 1f - Eclipse.Shared(rs, rb, apart);
                if (!craft) break;
            }
            if (lit <= 0.001f) return;
            float seen = 0.2126f * colour.r + 0.7152f * colour.g + 0.0722f * colour.b;
            if (seen <= 1e-5f) return;
            // The light it throws this way, as a share of the sun's at the home world: two thirds of its colour for a ball, by its
            // size and distance, and by its own distance from the sun. In magnitudes, the sun from the home world being -26.74.
            double share = 0.667 * seen * shows * (radius / far) * (radius / far) * (year / toSun) * (year / toSun) * lit;
            float mag = (float)(-26.74 - 2.5 * Math.Log10(Math.Max(share, 1e-40)));
            if (mag > 9f) return;
            // As it grows past a dot or so, its light eases into what the game's own drawing of it shows: a disc of its lit share,
            // as bright as sunlit ground is by day. (A point's light is the stars' measure, and sunlit ground is thousands of times
            // brighter than that: a photograph taken for the stars burns the moon out white. The game draws its worlds by day's
            // measure whatever the hour, so a point as bright as the stars' measure makes it, laid over a world two dots across,
            // was a dazzle many times its size: Minmus from Kerbin by night.)
            float fromMag = FromMagnitude(mag);
            float asDisc = 0.785f * across * across * (float)((1.0 + cos) * 0.5) * seen * 0.5f * lit;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.6f, 1.6f, across));
            float flux = Mathf.Exp(Mathf.Lerp(Mathf.Log(Mathf.Max(fromMag, 1e-12f)), Mathf.Log(Mathf.Max(asDisc, 1e-12f)), t));
            Color light = colour / seen;
            light.a = 1f;
            // (the air in the way: by how much more of it there is towards that height in the sky than overhead, and more for blue than
            // for red; what it takes overhead the stars lose too, and that is in their share already: see Twilight)
            if (column > 0f)
            {
                float high = Mathf.Max(0f, Mathf.Asin(Mathf.Clamp(Vector3.Dot(way, up), -1f, 1f)) * Mathf.Rad2Deg);
                float through = (1f / (Mathf.Sin(high * Mathf.Deg2Rad) + 0.50572f * Mathf.Pow(high + 6.07995f, -1.6364f)) - 1f) * column;     // (Kasten and Young's air mass, less the one overhead)
                light.r *= Mathf.Pow(10f, -0.4f * 0.17f * through);
                light.g *= Mathf.Pow(10f, -0.4f * 0.24f * through);
                light.b *= Mathf.Pow(10f, -0.4f * 0.36f * through);
            }
            // (and handed over to the game's own drawing of it, where the game draws it, by three dots across)
            float fade = rising * (drawnByGame ? 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.6f, 3f, across)) : 1f);
            if (fade <= 0f || flux * fade < 1e-6f) return;
            points.Add(new Point { Name = name, Way = way, Light = light, Mag = mag, Flux = flux, Across = across, Fade = fade, Craft = craft });
        }

        // (a picture's values for light from nought to one, worked out once)
        static readonly float[] gamma = MakeGamma();

        static float[] MakeGamma()
        {
            var o = new float[1025];
            for (int i = 0; i <= 1024; i++) o[i] = Mathf.Pow(i / 1024f, 1f / 2.2f);
            return o;
        }

        /// <summary>One square of a point: its four corners, the picture of the given light on it, weighed so much, in its hue.</summary>
        void Square(int at, Vector3 middle, Vector3 across, Vector3 up, int level, float weight, float r, float g, float b)
        {
            spots[at] = middle - across - up; spots[at + 1] = middle + across - up; spots[at + 2] = middle + across + up; spots[at + 3] = middle - across + up;
            var t = new Color32((byte)(Mathf.Clamp01(r * weight) * 255f + 0.5f), (byte)(Mathf.Clamp01(g * weight) * 255f + 0.5f), (byte)(Mathf.Clamp01(b * weight) * 255f + 0.5f), 255);
            tints[at] = t; tints[at + 1] = t; tints[at + 2] = t; tints[at + 3] = t;
            float u0 = (level % 4) * 0.25f, v0 = (level / 4) * 0.25f, d = 0.25f;
            places[at] = new Vector2(u0, v0); places[at + 1] = new Vector2(u0 + d, v0); places[at + 2] = new Vector2(u0 + d, v0 + d); places[at + 3] = new Vector2(u0, v0 + d);
        }

        /// <summary>
        /// The pictures of a dot, four by four in one: each as the screen shows a point of that light, a soft dot and its glow
        /// (Spread), burnt out where it is brighter than white, and so wider; each of twice the light of the one before. Its own
        /// dots come at 2.7 to a dot of the screen, so that the dot's narrow middle is drawn smooth.
        /// </summary>
        static Texture2D Picture()
        {
            var t = new Texture2D(Cell * 4, Cell * 4, TextureFormat.RGBA32, false) { name = "Natural Light far lights", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[Cell * Cell * 16];
            float perTexel = 2f * Half / Cell;
            for (int level = 0; level < 16; level++)
            {
                int ox = (level % 4) * Cell, oy = (level / 4) * Cell;
                float light = Least * Mathf.Pow(2f, Mathf.Min(level, Levels - 1));
                for (int y = 0; y < Cell; y++)
                    for (int x = 0; x < Cell; x++)
                    {
                        float dx = (x + 0.5f - Cell * 0.5f) * perTexel, dy = (y + 0.5f - Cell * 0.5f) * perTexel, rr = Mathf.Sqrt(dx * dx + dy * dy);
                        // (nought at the square's edge, so that no square shows)
                        float edge = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(Half * 0.8f, Half, rr));
                        byte v = (byte)(AsShown(Mathf.Clamp01(light * Spread(rr))) * edge * 255f + 0.5f);
                        px[ox + x + (oy + y) * Cell * 4] = new Color32(v, v, v, 255);
                    }
            }
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        /// <summary>Light from nought to one as a picture shows it (the table, between its steps; finer than a picture's own 256).</summary>
        static float AsShown(float light)
        {
            if (light <= 0f) return 0f;
            if (light >= 1f) return 1f;
            float at = light * 1024f;
            int i = (int)at;
            return gamma[i] + (gamma[i + 1] - gamma[i]) * (at - i);
        }

        /// <summary>How much of a point's light falls on a dot this far (in dots) from its middle: a soft dot and a glow round it.</summary>
        static float Spread(float r)
        {
            float r2 = r * r;
            return (1f - Halo) * Mathf.Exp(-r2 / (2f * Sigma * Sigma)) / (2f * Mathf.PI * Sigma * Sigma)
                 + Halo * (HaloFall - 1f) / (Mathf.PI * HaloWide * HaloWide) * Mathf.Pow(1f + r2 / (HaloWide * HaloWide), -HaloFall);
        }

        /// <summary>Room for so many points in the mesh, made when more are wanted than there is room for.</summary>
        void Build(int wanted)
        {
            if (wanted <= room && shown != null) return;
            if (ink == null)
            {
                Shader shader = Shader.Find("KSP/Particles/Additive") ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive");
                if (shader == null) { Kit.Log("Natural Light", "no shader to draw far lights with"); enabled = false; return; }
                if (dots == null) dots = Picture();
                ink = new Material(shader) { mainTexture = dots };
                ink.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            }
            if (shown == null)
            {
                shown = new GameObject("Natural Light far lights") { layer = face != null ? face.gameObject.layer : 18 };
                mesh = new Mesh { name = "Natural Light far lights" };
                mesh.MarkDynamic();
                shown.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer r = shown.AddComponent<MeshRenderer>();
                r.sharedMaterial = ink;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            room = Mathf.Max(8, Mathf.Min(Most, (wanted + 7) / 8 * 8));
            spots = new Vector3[room * Each];
            tints = new Color32[room * Each];
            places = new Vector2[room * Each];
            usedWas = 0;
            var corners = new int[room * 12];
            for (int q = 0; q < room * 2; q++)
            {
                int at = q * 4, n = q * 6;
                corners[n] = at; corners[n + 1] = at + 2; corners[n + 2] = at + 1;
                corners[n + 3] = at; corners[n + 4] = at + 3; corners[n + 5] = at + 2;
            }
            mesh.Clear();
            mesh.vertices = spots;
            mesh.colors32 = tints;
            mesh.uv = places;
            mesh.triangles = corners;
        }

        // ---------------------------------------------------------------- craft

        static readonly Color Grey = new Color(0.33f, 0.33f, 0.33f);
        sealed class Size { public int Parts; public double Area; }
        static readonly Dictionary<Guid, Size> sizes = new Dictionary<Guid, Size>();

        /// <summary>
        /// How much a craft shows from the side, in square metres: each part as wide as its widest joint and as long as its joints
        /// are apart (a fuel tank side on), a solar panel two square metres more. Worked out again when its parts change.
        /// </summary>
        static double Area(Vessel v)
        {
            int parts = v.loaded ? v.parts.Count : v.protoVessel != null ? v.protoVessel.protoPartSnapshots.Count : 0;
            if (sizes.TryGetValue(v.id, out Size known) && known.Parts == parts) return known.Area;
            double area = 0.0;
            if (v.loaded) foreach (Part p in v.parts) area += Side(p, p.FindModuleImplementing<ModuleDeployableSolarPanel>() != null);
            else if (v.protoVessel != null)
                foreach (ProtoPartSnapshot s in v.protoVessel.protoPartSnapshots)
                {
                    bool panel = false;
                    foreach (ProtoPartModuleSnapshot m in s.modules) if (m.moduleName == "ModuleDeployableSolarPanel") { panel = true; break; }
                    area += s.partInfo != null && s.partInfo.partPrefab != null ? Side(s.partInfo.partPrefab, panel) : 1.0;
                }
            area = Math.Max(0.5, Math.Min(2000.0, area));
            if (sizes.Count > 4096) sizes.Clear();
            sizes[v.id] = new Size { Parts = parts, Area = area };
            return area;
        }

        static double Side(Part p, bool panel)
        {
            float wide = 0f, low = float.MaxValue, high = float.MinValue;
            if (p.attachNodes != null)
                foreach (AttachNode n in p.attachNodes)
                {
                    wide = Mathf.Max(wide, n.size <= 0 ? 0.625f : 1.25f * n.size);
                    low = Mathf.Min(low, n.position.y); high = Mathf.Max(high, n.position.y);
                }
            if (wide <= 0f) wide = 0.625f;
            float tall = high > low ? Mathf.Max(high - low, wide * 0.5f) : wide;
            return wide * tall + (panel ? 2.0 : 0.0);
        }

#if DEV
        // ---------------------------------------------------------------- for the development build

        /// <summary>(Development build: each point drawn: its magnitude, how wide its world or craft is in dots, how faded; and the stars' share.)</summary>
        public static string Says()
        {
            if (running == null) return "not running";
            var o = new System.Text.StringBuilder("magnitude 0 = " + zero.ToString("F3") + " dots of white light; " + running.points.Count + " points; the sky's faces " + running.reach.ToString("F0") + " off; the sky camera " + (running.skyCam != null ? running.skyCam.fieldOfView.ToString("F1") + " degrees, " + running.skyCam.pixelHeight + " dots high, drawing from " + running.skyCam.nearClipPlane.ToString("G3") + " to " + running.skyCam.farClipPlane.ToString("G3") + " at " + running.skyCam.transform.position.ToString("F1") : "none") + "\n");
            GalaxyCubeControl sky = GalaxyCubeControl.Instance;
            Color faceNow = Color.clear;
            if (running.face != null) { running.face.GetPropertyBlock(block); if (!block.isEmpty) faceNow = block.GetColor("_Color"); }
            if (sky != null)
                foreach (string f in new[] { "atmosFade", "dayTimeFade", "glareFade", "totalFade", "sunSrfAngle", "sunCamAngle", "lineOfSightToSun" })
                {
                    var field = typeof(GalaxyCubeControl).GetField(f, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    o.Append(f + " " + (field != null ? field.GetValue(sky) : "?") + "; ");
                }
            o.Append("the stars at " + (100f * running.stars).ToString("F0") + "% (the game's sky: brightest " + (sky != null ? sky.maxGalaxyColor.ToString("F2") : "?") + ", a face now " + faceNow.ToString("F2") + ", kept " + (FilmLook.starsKept ? FilmLook.starsWere.ToString("F2") : "none") + ", the brightest seen " + running.brightest.ToString("F2") + "), a point's light times " + running.scale.ToString("F2") + " for this view\n");
            Camera eye = HighLogic.LoadedSceneIsFlight && FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Names.CameraNamed("Camera 00") ?? Camera.main;
            CelestialBody home = HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.mainBody : FlightGlobals.GetHomeBody();
            Vector3 up = eye != null && home != null ? ((Vector3)(eye.transform.position - home.position)).normalized : Vector3.up;
            Vector3 north = home != null ? Vector3.ProjectOnPlane((Vector3)home.transform.up, up).normalized : Vector3.forward, east = Vector3.Cross(up, north);
            foreach (Point p in running.points) o.Append(p.Name + " (" + Mathf.RoundToInt(Mathf.Asin(Mathf.Clamp(Vector3.Dot(p.Way, up), -1f, 1f)) * Mathf.Rad2Deg) + " degrees up, bearing " + Mathf.RoundToInt(Mathf.Repeat(Mathf.Atan2(Vector3.Dot(p.Way, east), Vector3.Dot(p.Way, north)) * Mathf.Rad2Deg, 360f)) + "): magnitude " + p.Mag.ToString("F2") + ", drawn as " + p.Flux.ToString("G3") + " dots of light, " + p.Across.ToString("F2") + " dots across, faded to " + p.Fade.ToString("F2") + ", colour " + p.Light.ToString("F2") + (p.Craft ? " (craft)" : "") + "\n");
            return o.ToString();
        }

        /// <summary>(Development build: the light of magnitude 0, in white dots: "0.5".)</summary>
        public static string Zero(string v) { zero = float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); return "magnitude 0 = " + zero + " dots of white light"; }

        /// <summary>
        /// (Development build: the game's sky of stars measured as it is drawn. Its camera made to draw the whole sky, a face of a cube
        /// at a time, 90 degrees and 1800 dots across each (a dot about as wide as one at the game's own view, 60 degrees across
        /// 1080), with these points left out; every star found in it (a dot brighter than all round it), and its light added up
        /// over the dots round it, less the sky behind it, as dots of white light at the game's own view. Then the brightest, in
        /// order: the fifteenth of a real sky is of magnitude 1, the forty-eighth 2, the 171st 3, the 513th 4, the 1,602nd 5.
        /// "keep" sets magnitude 0 from the fifteenth and the forty-eighth.)
        /// </summary>
        public static string Census(string arg)
        {
            Camera c = Names.CameraNamed("GalaxyCamera");
            if (c == null) return "no sky camera";
            const int size = 1800;
            RenderTexture rt = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            RenderTexture keepTarget = c.targetTexture;
            float keepFov = c.fieldOfView, keepAspect = c.aspect;
            Quaternion keepTurn = c.transform.rotation;
            bool hid = running != null && running.shown != null && running.shown.activeSelf;
            if (hid) running.shown.SetActive(false);
            var found = new List<float>();
            var read = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var asLight = new float[256];
            for (int i = 0; i < 256; i++) asLight[i] = Mathf.Pow(i / 255f, 2.2f);
            var lum = new float[size * size];
            Vector3[] ways = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right, Vector3.up, Vector3.down };
            Vector3[] ups = { Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.back, Vector3.forward };
            // (the sky as bright as it is with nothing to dim it: the game's faces at their full colour, the deep sky's at its own)
            var kept = new List<KeyValuePair<Renderer, MaterialPropertyBlock>>();
            GalaxyCubeControl sky = GalaxyCubeControl.Instance;
            Color full = FilmLook.starsKept ? FilmLook.starsWere : sky != null ? sky.maxGalaxyColor : Color.white;
            float deep = 0.5f * 0.8f * (Daylight.SkyGlow != null ? (float)Daylight.SkyGlow : 1f);
            try
            {
                if (sky != null)
                    foreach (Renderer r in sky.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || (running != null && r.gameObject == running.shown)) continue;
                        var was = new MaterialPropertyBlock();
                        r.GetPropertyBlock(was);
                        kept.Add(new KeyValuePair<Renderer, MaterialPropertyBlock>(r, was));
                        var bright = new MaterialPropertyBlock();
                        r.GetPropertyBlock(bright);
                        if (r.name.StartsWith("Natural Light")) bright.SetColor("_TintColor", new Color(deep, deep, deep, 0.5f));
                        else bright.SetColor("_Color", full);
                        r.SetPropertyBlock(bright);
                    }
                c.targetTexture = rt; c.fieldOfView = 90f; c.aspect = 1f;
                for (int v = 0; v < 6; v++)
                {
                    c.transform.rotation = Quaternion.LookRotation(ways[v], ups[v]);
                    c.Render();
                    RenderTexture before = RenderTexture.active;
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                    read.Apply(false);
                    RenderTexture.active = before;
                    Color32[] px = read.GetPixels32();
                    for (int i = 0; i < px.Length; i++) lum[i] = 0.2126f * asLight[px[i].r] + 0.7152f * asLight[px[i].g] + 0.0722f * asLight[px[i].b];
                    // (a dot of this view against one at the game's own: smaller towards the corners, by the cube of the cosine)
                    float centre = 2f / size;
                    for (int y = 3; y < size - 3; y++)
                        for (int x = 3; x < size - 3; x++)
                        {
                            float here = lum[x + y * size];
                            if (here < 0.0015f) continue;
                            bool top = true;
                            for (int dy = -2; dy <= 2 && top; dy++)
                                for (int dx = -2; dx <= 2; dx++)
                                {
                                    if (dx == 0 && dy == 0) continue;
                                    float there = lum[x + dx + (y + dy) * size];
                                    if (there > here || (there == here && (dy < 0 || (dy == 0 && dx < 0)))) { top = false; break; }
                                }
                            if (!top) continue;
                            float all = 0f, edge = 0f;
                            for (int dy = -3; dy <= 3; dy++)
                                for (int dx = -3; dx <= 3; dx++)
                                {
                                    float there = lum[x + dx + (y + dy) * size];
                                    all += there;
                                    if (dx == -3 || dx == 3 || dy == -3 || dy == 3) edge += there;
                                }
                            float behind = edge / 24f;
                            float light = all - 49f * behind;
                            if (light <= 0f || here < 2f * behind) continue;
                            float u = (x + 0.5f) / size * 2f - 1f, w = (y + 0.5f) / size * 2f - 1f;
                            float cos = 1f / Mathf.Sqrt(1f + u * u + w * w);
                            float dot = centre * cos * cos * cos;
                            found.Add(light * (dot * centre / (Dot * Dot)));
                        }
                }
            }
            finally
            {
                foreach (KeyValuePair<Renderer, MaterialPropertyBlock> k in kept) if (k.Key != null) k.Key.SetPropertyBlock(k.Value.isEmpty ? null : k.Value);
                c.targetTexture = keepTarget; c.fieldOfView = keepFov; c.aspect = keepAspect; c.transform.rotation = keepTurn;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(read);
                if (hid) running.shown.SetActive(true);
            }
            found.Sort((a, b) => b.CompareTo(a));
            int[] ranks = { 1, 2, 4, 8, 15, 30, 48, 100, 171, 300, 513, 1000, 1602, 3000, 4800 };
            float[] mags = { float.NaN, float.NaN, 0f, float.NaN, 1f, float.NaN, 2f, float.NaN, 3f, float.NaN, 4f, float.NaN, 5f, float.NaN, 6f };
            var o = new System.Text.StringBuilder(found.Count + " stars found, the sky at its full brightness (" + full.ToString("F2") + ")");
            foreach (int k in ranks)
            {
                if (k > found.Count) break;
                int idx = Array.IndexOf(ranks, k);
                float f = found[k - 1];
                o.Append("\n  " + k + ": " + f.ToString("G3") + (float.IsNaN(mags[idx]) ? "" : " (a real sky's magnitude " + mags[idx] + " here: magnitude 0 = " + (f * Mathf.Pow(10f, 0.4f * mags[idx])).ToString("G3") + ")"));
            }
            if (arg == "keep" && found.Count >= 48)
            {
                zero = Mathf.Sqrt(found[14] * Mathf.Pow(10f, 0.4f) * found[47] * Mathf.Pow(10f, 0.8f));
                o.Append("\nmagnitude 0 set to " + zero.ToString("G3"));
            }
            return o.ToString();
        }

        /// <summary>(Development build: the flight camera turned to look towards a point, by its name, with the point so many degrees
        /// to the right of the middle so that the craft is not in front of it: "Jool|12". The camera stays as far from the craft as it was.)</summary>
        public static string Aim(string how)
        {
            if (running == null || FlightCamera.fetch == null) return "no camera";
            string[] w = (how ?? "").Split('|');
            float off = w.Length > 1 ? float.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture) : 12f;
            foreach (Point p in running.points)
                if (p.Name == w[0])
                {
                    FlightCamera cam = FlightCamera.fetch;
                    Vector3 side = Vector3.Cross(p.Way, cam.transform.up).normalized;
                    Vector3 look = Quaternion.AngleAxis(off, Vector3.Cross(side, p.Way)) * p.Way;
                    cam.SetCamCoordsFromPosition(cam.GetPivot().position - look * cam.Distance);
                    return "looking " + off + " degrees beside " + w[0];
                }
            return "no point called " + w[0];
        }

        /// <summary>(Development build: when a craft, by its name, next passes over the camera's craft as a point of light: above
        /// ten degrees, in the sun, and the sun six degrees or more below the horizon there; searched twenty seconds of game time at a
        /// time over two of Kerbin's days. "Docking Test Probe".)</summary>
        public static string NextPass(string name)
        {
            Vessel me = FlightGlobals.ActiveVessel, v = null;
            foreach (Vessel x in FlightGlobals.Vessels) if (x != null && x.vesselName == name) { v = x; break; }
            if (v == null || me == null || Planetarium.fetch == null) return "no craft called " + name;
            CelestialBody body = me.mainBody, sun = Planetarium.fetch.Sun;
            if (v.orbit == null || v.orbit.referenceBody != body) return name + " goes round another world";
            double now = Planetarium.GetUniversalTime();
            Vector3d sunWay = (sun.position - body.position).normalized;
            var o = new System.Text.StringBuilder();
            double from = -1.0, highest = -90.0, bearing = 0.0;
            int found = 0;
            // (the craft's place now, turned with the world about its pole: the orbit's places at any time are given in the frame as it
            // stands now, and in that frame the ground turns. Checked against the game: the sun -62 degrees up 10,338 s before 55 up.)
            Vector3 place = (Vector3)((Vector3d)me.GetWorldPos3D() - body.position), pole = (Vector3)body.transform.up;
            for (double t = now; t < now + 43200.0 && found < 8; t += 10.0)
            {
                Vector3d here = Quaternion.AngleAxis((float)(-360.0 * (t - now) / body.rotationPeriod), pole) * place;
                Vector3d there = v.orbit.getRelativePositionAtUT(t).xzy;
                Vector3d up = here.normalized, to = there - here;
                double far = to.magnitude, high = Math.Asin(Vector3d.Dot(to / far, up)) * 180.0 / Math.PI;
                double sunHigh = Math.Asin(Vector3d.Dot(up, sunWay)) * 180.0 / Math.PI;
                double along = Vector3d.Dot(there, sunWay);
                bool lit = along > 0.0 || (there - sunWay * along).magnitude > body.Radius;
                bool seen = sunHigh < -6.0 && high > 10.0 && lit;
                if (seen && from < 0.0) { from = t; highest = -90.0; }
                if (seen && high > highest)
                {
                    highest = high;
                    Vector3d north = Vector3d.Exclude(up, body.transform.up).normalized, east = Vector3d.Cross(up, north);
                    bearing = (Math.Atan2(Vector3d.Dot(to, east), Vector3d.Dot(to, north)) * 180.0 / Math.PI + 360.0) % 360.0;
                }
                if (!seen && from >= 0.0)
                {
                    o.Append("\n  in " + (from - now).ToString("F0") + " s for " + (t - from).ToString("F0") + " s, highest " + highest.ToString("F0") + " degrees up at bearing " + bearing.ToString("F0"));
                    from = -1.0; found++;
                }
            }
            return found == 0 ? "no pass of " + name + " in the next two days" : name + " in sight:" + o;
        }

        /// <summary>(Development build: the flight camera turned steadily round, so many degrees a second for so many seconds:
        /// "2|5". To see whether the points hold their brightness as they cross the screen's dots.)</summary>
        public static string Pan(string how)
        {
            if (running == null || FlightCamera.fetch == null) return "no camera";
            string[] w = (how ?? "").Split('|');
            float rate = float.Parse(w[0], System.Globalization.CultureInfo.InvariantCulture), seconds = w.Length > 1 ? float.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture) : 5f;
            running.StartCoroutine(Panning(rate, seconds));
            return "turning " + rate + " degrees a second for " + seconds + " s";
        }

        static System.Collections.IEnumerator Panning(float rate, float seconds)
        {
            float left = seconds;
            while (left > 0f && FlightCamera.fetch != null)
            {
                FlightCamera.fetch.camHdg += rate * Mathf.Deg2Rad * Time.unscaledDeltaTime;
                left -= Time.unscaledDeltaTime;
                yield return null;
            }
        }

        /// <summary>(Development build: the game's anti-aliasing set for a moment, "0", "2", "4"; "back" puts the game's own setting back.)</summary>
        public static string Msaa(string v)
        {
            if (v == "back") { QualitySettings.antiAliasing = GameSettings.ANTI_ALIASING; return "anti-aliasing as the game has it, " + QualitySettings.antiAliasing; }
            QualitySettings.antiAliasing = int.Parse(v);
            return "anti-aliasing " + QualitySettings.antiAliasing;
        }

        /// <summary>
        /// (Development build: what the points' drawing rests on that could differ from one graphics system to another: the system,
        /// the screen, each of the game's cameras (its size, whether it may draw with more than white's range and with anti-aliasing,
        /// what it draws into), and the numbers the points' light is reckoned by. For the test row being brighter on Direct3D than on
        /// OpenGL, 2026-10-24, the rest of the picture as bright on both.)
        /// </summary>
        public static string Pipeline()
        {
            var o = new System.Text.StringBuilder(SystemInfo.graphicsDeviceType + " (" + SystemInfo.graphicsDeviceVersion + "), the screen " + Screen.width + "x" + Screen.height + ", anti-aliasing " + QualitySettings.antiAliasing + ", " + QualitySettings.activeColorSpace + " colour\n");
            foreach (string n in new[] { "GalaxyCamera", "Camera ScaledSpace", "Camera 01", "Camera 00" })
            {
                Camera c = Names.CameraNamed(n);
                if (c == null) { o.Append(n + ": none\n"); continue; }
                o.Append(n + ": " + c.pixelWidth + "x" + c.pixelHeight + ", depth " + c.depth + ", HDR allowed " + c.allowHDR + ", anti-aliasing allowed " + c.allowMSAA + ", " + c.actualRenderingPath + ", into " + (c.targetTexture != null ? c.targetTexture.name + " (" + c.targetTexture.format + ", " + c.targetTexture.antiAliasing + "x)" : "the screen") + ", clears " + c.clearFlags + ", fov " + c.fieldOfView.ToString("F2") + "\n");
            }
            if (running != null) o.Append("the points: fine " + running.fine.ToString("F3") + ", scale " + running.scale.ToString("F2") + ", stars " + running.stars.ToString("F3") + ", brightest " + running.brightest.ToString("F3") + ", reach " + running.reach.ToString("F1") + (running.ink != null ? ", drawn with " + running.ink.shader.name : ""));
            return o.ToString();
        }

        static bool? hdrWas;
        /// <summary>(Development build: whether the sky's camera may draw with more than white's range: "off", "on"; "back" as it was.)</summary>
        public static string Hdr(string v)
        {
            Camera c = Names.CameraNamed("GalaxyCamera");
            if (c == null) return "no sky camera";
            if (hdrWas == null) hdrWas = c.allowHDR;
            c.allowHDR = v == "back" ? hdrWas.Value : v == "on";
            return "the sky camera's HDR " + (c.allowHDR ? "allowed" : "not allowed");
        }

        /// <summary>(Development build: a row of test points of magnitude -3 to 4 across the middle of the view, to set against the stars by eye. "off" takes them away.)</summary>
        public static string Ladder(string v) { ladder = v != "off"; return ladder ? "a row of points of magnitude -3 to 4 across the view" : "no row"; }
        static bool ladder;
#endif
    }
}
