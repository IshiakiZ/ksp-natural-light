using System;
using Keystone;
using UnityEngine;
using UnityEngine.Rendering;

namespace NaturalLight
{
    /// <summary>
    /// The weather in the air: rain, snow, fog and dust between the camera and the distance.
    ///
    /// A weather mod (Natural Weather) draws the drops and flakes near the camera, a few tens of metres round it; beyond that
    /// each is too small to see, but all of them together take the light from what is behind them and put their own in its
    /// place. In rain the far side of the pad greys out and the hills are gone; in snow everything whitens into the distance;
    /// in fog there is nothing past a hundred metres; in a dust storm on Duna everything sinks into an ochre murk. How far
    /// one can see is the weather's (Keystone's Weather: WeatherHere.Seeing, the distance at which a dark thing is lost
    /// against the sky, so that the air takes 3.912 over it: Koschmieder's rule), less what the air itself takes on a clear
    /// day, which the game and Scatterer draw already. It is as thick as that up to the weather's top (a rain's cloud base,
    /// a fog's top, a dust storm's) and thins out above, so that looking up through rain the cloud base is greyed by the rain
    /// below it and no more, and from above a fog bank lies on the ground. Its light is the light all round as the mod has it
    /// (the sky's and the ground's, Daylight.Around), in the dust's own colour for dust; under a deck of cloud, the deck's own
    /// light as the weather mod draws it and the ground's under it (Keystone's Weather.CloudLight); and brighter towards the
    /// sun where the sun comes through: drops, flakes and dust throw light forward.
    ///
    /// Drawn over each of the game's two flight cameras by a shader of the mod's own (see tools/shaderpack/veil.glsl and
    /// OwnShader): the far one (beyond 400 m) when it has drawn everything, the sky and the clouds included; the near one when it
    /// has drawn what is solid and before what is see-through, so that the drops by the camera, flames and smoke are not veiled
    /// as if they were as far off as the ground behind them. Where one camera draws everything (Direct3D 11, Windows), among
    /// the see-through things in the clouds' turn, just before them (see Lay). Packed for the game's OpenGL (Macs and Linux)
    /// and built for Direct3D (Windows) by tools/unityshaders/windows.py.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class WeatherVeil : MonoBehaviour
    {
        public static string Report = "";
        static Shader shader;
        static bool tried, broken;
        static string why = "";

        sealed class OnCamera
        {
            public Camera Cam;
            public CommandBuffer Commands;
            public Material Mat;
            public CameraEvent When;
            public bool AskedDepth;
            public bool Laid;           // drawn by the sheet laid on among the see-through things (Lay), not by Commands
            public bool Sky;            // whether where it drew nothing is the sky (_VeilUp.w)
            public float Forward;       // how strongly the air throws light forward (_VeilSun.w)
        }
        Vector3 upNow = Vector3.up, toSunNow = Vector3.up;    // up and towards the sun at the camera, in the world, as LateUpdate found them
        // (When: on the near camera once what is solid is drawn, and the clouds' shadows laid on it (Natural Weather's clouds do that
        // as soon as it is drawn), but before Scatterer copies the picture for its haze, which wipes whatever is drawn between its
        // copy and its haze; on the far camera after everything, the clouds included, which are behind the rain)
        readonly OnCamera near = new OnCamera { When = CameraEvent.BeforeImageEffectsOpaque };
        // (The far camera's: after its see-through things and the clouds laid on with them, before anything is done to its
        // finished picture. Until 2026-10-13 it was put after everything, and on this Mac, as the game stood that day,
        // nothing of it showed: fog ended in a ruled line where the near camera's range does, with the far ground, the
        // hills and the whole sky clear beyond it.)
        readonly OnCamera far = new OnCamera { When = CameraEvent.BeforeImageEffects };
        Mesh sheet;
        float shown;                    // how much of it there is, eased (0 to 1): it comes and goes with the weather, and is not switched

        // what the weather says, looked up a few times a second
        float sigma, top, soft, dust, snow, lookedAt = -10f;
        float coverOver, coverOverShown;    // how much of the sky over the camera the clouds cover (Look), and as eased
        float floor;                    // the weather's floor: the ground or the sea under the camera (m above the sea's level)
        bool lidLit;                    // whether what lies under its top is lit from above by the sun on it (fog, dust, haze; not rain)
        float backUp;                   // how much of the sunlight its top throws back up, seen from over it (0 to 1; see LateUpdate)
        Color dustColour = new Color(0.8f, 0.5f, 0.3f);

        static void Load()
        {
            tried = true;
            shader = OwnShader.Load("veil.bundle", "NaturalLight/Veil", "the weather's veil", out why);
            Kit.Log("Natural Light", shader != null ? "has the weather's veil" : "no weather's veil: " + why);
        }

#if DEV
        static bool off;
        /// <summary>(For the development build: the veil off and on again, for pictures of before and after.)</summary>
        public static string Switch() { off = !off; return off ? "the veil off" : "the veil on"; }
        /// <summary>(For the development build: what the veil is drawing with.)</summary>
        public static string State() => Report + " sigma " + (last != null ? last.sigma.ToString("F5") + " /m, top " + last.top.ToString("F0") + " m above the sea, thinning in " + last.soft.ToString("F0") + " m, dust " + last.dust.ToString("F2") + ", snow " + last.snow.ToString("F2") + ", shown " + last.shown.ToString("F2") + "; its own light " + last.ownWas.ToString("F3") + " (as it shows), the sun's towards the sun " + last.sunWas.ToString("F3") + "; under the deck " + last.deckedWas.ToString("F2") + " (the sky over the camera " + last.coverOverShown.ToString("F2") + " covered)" + (lightAsBefore ? " (its light held as before)" : "") : "?");
        static WeatherVeil last;
        Color ownWas, sunWas;
        float deckedWas;
        static bool lightAsBefore;
        /// <summary>(For the development build: the veil's own light as before 2026-10-25, the light from all round whatever the
        /// clouds over it, and back: for pictures of before and after.)</summary>
        public static string LightAsBefore() { lightAsBefore = !lightAsBefore; return lightAsBefore ? "the veil's light as before 2026-10-25" : "the veil's light from the deck over it"; }
        static bool turnedLate;
        public static string Late() { turnedLate = !turnedLate; return turnedLate ? "the veil told how the camera stands in its LateUpdate alone, as before 2026-11-05" : "the veil told how the camera stands as each camera sets out to draw"; }
        static int only;
        /// <summary>(For the development build: both cameras' passes, the near camera's alone, the far one's alone, in turn; and
        /// what each camera reaches, for a band left unveiled on OpenGL.)</summary>
        public static string Which()
        {
            only = (only + 1) % 3;
            string Reach(string name) { Camera c = Named(name); return c == null ? name + " none" : name + " " + c.nearClipPlane.ToString("F1") + " to " + c.farClipPlane.ToString("F0") + " m, depth " + c.depthTextureMode; }
            return (only == 0 ? "both passes" : only == 1 ? "the near camera's pass alone" : "the far camera's pass alone") + "; " + Reach("Camera 00") + "; " + Reach("Camera 01");
        }
#endif

        void Awake() { Daylight.Setup(); Camera.onPreCull += Turned; }

        /// <summary>
        /// The veil's view of the camera (which way is up, and the sun, in its own axes; how wide it sees) set again as each camera
        /// sets out to draw. Set in this LateUpdate alone, as until 2026-11-05, it was the camera's look before the game's own late
        /// step had turned it: every frame the view turned, the veil reckoned the sky from where the camera had looked the frame
        /// before. On the far camera, a pixel of sky it took to be under the horizon was veiled only as far as the line of sight
        /// meets the sea, near enough not at all, and the sky behind showed raw: a thin white line with black lengths in it (the
        /// sky's glow at the horizon, and where nothing is drawn) standing over a sea drawn to the horizon by as far as the view
        /// turned in a frame (found on the PC, 2026-11-05, in tools/dev/scratch-2026-11-04-pc/seapan.py's swing: 32 frames of
        /// 530, there with Natural Ocean's sea, the clouds and Far Ground each taken away; under Scatterer's sea, which stood in
        /// that strip, it had been hidden). TurnedLate (development build, WeatherVeil.Late): as before.
        /// </summary>
        void Turned(Camera cam)
        {
            if (cam == null) return;
#if DEV
            if (turnedLate) return;
#endif
            if (cam == near.Cam && near.Mat != null) Face(near, cam);
            if (cam == far.Cam && far.Mat != null) Face(far, cam);
            if (cam == layingFor && laying != null && laying.activeSelf) { Transform t = cam.transform; laying.transform.SetPositionAndRotation(t.position + t.forward * 100f, t.rotation); }
        }

        /// <summary>The veil's material told how the camera stands: how wide it sees, and up and the sun in its own axes.</summary>
        void Face(OnCamera on, Camera cam)
        {
            Transform t = cam.transform;
            Matrix4x4 p = cam.projectionMatrix;
            on.Mat.SetVector("_VeilView", new Vector4(1f / p[0, 0], 1f / p[1, 1], p[0, 2] / p[0, 0], p[1, 2] / p[1, 1]));
            on.Mat.SetVector("_VeilUp", new Vector4(Vector3.Dot(upNow, t.right), Vector3.Dot(upNow, t.up), Vector3.Dot(upNow, t.forward), on.Sky ? 1f : 0f));
            on.Mat.SetVector("_VeilSun", new Vector4(Vector3.Dot(toSunNow, t.right), Vector3.Dot(toSunNow, t.up), Vector3.Dot(toSunNow, t.forward), on.Forward));
        }

        void OnDestroy()
        {
            Camera.onPreCull -= Turned;
            Detach(near); Detach(far);
            foreach (OnCamera on in new[] { near, far })
            {
                if (on.Mat != null) Destroy(on.Mat);
                if (on.Commands != null) on.Commands.Release();
            }
            if (laying != null) Destroy(laying);
            if (layNothing != null) Destroy(layNothing);
            if (sheet != null) Destroy(sheet);
            Report = "";
        }

        void Detach(OnCamera on)
        {
            if (on.Cam != null && on.Commands != null)
            {
                if (!on.Laid) on.Cam.RemoveCommandBuffer(on.When, on.Commands);
                if (on.AskedDepth) on.Cam.depthTextureMode &= ~DepthTextureMode.Depth;
            }
            if (on.Laid) { layingFor = null; if (laying != null) laying.SetActive(false); }
            on.Cam = null; on.AskedDepth = false; on.Laid = false;
        }

        static Camera Named(string name)
        {
            Camera named = Keystone.Names.CameraNamed(name); if (named != null) return named;
            return null;
        }

        void LateUpdate()
        {
#if DEV
            last = this;
#endif
            bool wanted = Daylight.On && Daylight.Veil != null && Daylight.Veil && Daylight.Near && !MapView.MapIsEnabled && !broken
#if DEV
                && !off
#endif
                ;
            Camera main = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (wanted && main != null && body != null && body.atmosphere && Time.realtimeSinceStartup - lookedAt > 0.25f)
            {
                lookedAt = Time.realtimeSinceStartup;
                try { Look(body, main.transform.position); }
                catch (Exception ex) { broken = true; sigma = 0f; Kit.Log("Natural Light", "the weather's veil stopped: " + ex.Message); }
            }
            // (from far above the weather's top the clouds are what is seen, and they are another mod's: the veil is for being in it)
            bool inIt = main != null && body != null && body.GetAltitude((Vector3d)main.transform.position) < top + 8f * soft + 2000f;
            float want = wanted && inIt && body.atmosphere && sigma > 0.00002f ? 1f : 0f;
            shown = Mathf.MoveTowards(shown, want, 0.5f * Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            if (want > 0f && !tried) Load();
            if (shader == null || shown <= 0.001f || main == null || body == null)
            {
                Shader.SetGlobalVector(idHandAir, Vector4.zero);
                if (near.Cam != null || far.Cam != null) { Detach(near); Detach(far); }
                Report = wanted && want > 0f && shader == null && why.Length > 0 ? "No veil of weather: " + why + "." : "";
                return;
            }
            if (sheet == null) sheet = OwnShader.Sheet();
            Vector3d at = (Vector3d)main.transform.position;
            double alt = body.GetAltitude(at);
            Vector3 up = ((Vector3)(at - body.position)).normalized;
            Vector3 toSun = Planetarium.fetch != null && Planetarium.fetch.Sun != null ? ((Vector3)(Planetarium.fetch.Sun.position - at)).normalized : up;

            // ---- its light: the light from all round as the mod has it (as light), as it shows; in the dust's own colour for dust
            // (in dust the light all round is the dust's colour already: see Daylight.DustColour. Falling snow is whiter than rain:
            // a flake throws light back every way, where a drop sends most of it on forward, so a veil of snow is lit by the bright
            // sky overhead as much as by anything, and one of rain much less)
            Color around = Daylight.Around * (1.25f + 0.8f * snow);
            // ---- under a deck of cloud: lit by the deck over it and the ground under it, as they are drawn
            // (The air under a deck is lit from above by the deck's underside, from below by the ground the deck lights, and by
            // little else; a drop or a flake in it sends on the light from all round near enough alike (the sun is taken below,
            // as much as comes through). So its own light is the mean of the two halves of the sky: the deck's light as the
            // weather mod draws it (Keystone's Weather.CloudLight), and the ground's, which the deck lights as it would a white
            // wall, times the ground's own whiteness. About six tenths of the deck's light over grass, nine tenths over snow: the
            // far hills fade into a grey a little darker than the deck, as under a real overcast, where the horizon is darker than
            // overhead (the CIE's overcast sky has it a third). By as much of the sky in sight as the clouds hide, and as much of
            // the sun as they take: where the sun comes through, the air is lit by it and by the blue between the clouds, as the
            // light from all round has it (by the clouds' cover alone, a fog seen from a plane under fair heaps in the sun was a
            // flat grey for the heaps' shaded bases, 2026-10-25); with the sun down, by the cover alone. The sun taken by the
            // clouds over the camera as they close into one, by how much of the sky they cover over a few kilometres round
            // (coverOver): from half covered to all but a twentieth. Under scattered or broken cloud the light from all round has
            // what the clouds take already (Daylight's light under cloud), and a long line of sight runs through sun and shade.
            // (By the sun at the craft, as the light from all round has it, a fog seen from a camera high over it with the craft
            // standing in it was lit as from inside the fog, 2026-10-25, and under broken heaps the whole veil would have greyed
            // in each heap's shadow; by the cover over the camera as it is, the same fog under scattered altocumulus at 3 km,
            // four tenths covered, was greyed by a fifth.) Not in dust, whose colour
            // the light from all round has already. Taken as the light from all round, as until 2026-10-25, it was brighter than
            // the deck: rain and haze washed the deck out to a flat pale grey, and the far hills stood white against it.)
            coverOverShown = shown < 0.05f ? coverOver : Mathf.Lerp(coverOverShown, coverOver, 1f - Mathf.Exp(-Mathf.Min(Time.unscaledDeltaTime, 0.1f) / 2f));
            float closed = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 0.95f, coverOverShown));
            float sunIn = (1f - closed) * Mathf.Clamp01(Daylight.SunUp * 10f + 0.5f);
            float decked = Deck(out Color deckLight, out float deckCover) ? Mathf.Clamp01(deckCover) * (1f - sunIn) * (1f - Mathf.Clamp01(dust * 2f)) : 0f;
#if DEV
            if (lightAsBefore) decked = 0f;
            deckedWas = decked;
#endif
            if (decked > 0f)
            {
                Color ground = Daylight.GroundNow.linear;
                float white = Mathf.Clamp01((ground.r + ground.g + ground.b) / 3f);
                around = Color.Lerp(around, deckLight * (0.5f * (1f + white)), decked);
            }
            Color own = new Color(Mathf.LinearToGammaSpace(Mathf.Max(0f, around.r)), Mathf.LinearToGammaSpace(Mathf.Max(0f, around.g)), Mathf.LinearToGammaSpace(Mathf.Max(0f, around.b)));
            // ---- and the sun's, thrown forward: a third of the sunlight's own brightness looking straight at the sun (none where it is hidden)
            Light sunLight = Sun.Instance != null ? Sun.Instance.sunLight : null;
            Color sun = sunLight != null ? Daylight.Sunlight * (sunLight.intensity * 0.33f) : Color.black;
            if (dust > 0.01f) sun = Color.Lerp(sun, sun * dustColour, Mathf.Clamp01(dust * 1.5f));
            // (how forward: drops far more than flakes, and both more than the finer dust)
            float forward = Mathf.Lerp(Mathf.Lerp(0.8f, 0.6f, snow), 0.55f, Mathf.Clamp01(dust * 2f));
            // ---- and seen from over its top, the sunlight it throws back up. From above, a sea fog in the sun is a white sheet,
            // as the top of any cloud is; lit by the light from all round alone, it was a flat dark grey plane (the main chat,
            // 2026-10-18: from 1.7 km over a fog 123 m deep). By the rule the clouds' own shader lights a cloud by, what a layer
            // lets through is 1 / (1 + 0.75 (1 - g) tau) and what it does not comes back out of its top: tau its whole depth from
            // floor to top, g 0.85 for drops and 0.7 for dust and haze; by how high the sun stands. (Not rain's: it falls from
            // under a cloud, in the cloud's shade.)
            float tauV = sigma * Mathf.Max(top - floor, 0f), gv = dust > 0.01f || top - floor >= 1499f ? 0.7f : 0.85f;
            backUp = lidLit ? (1f - 1f / (1f + 0.75f * (1f - gv) * tauV)) * Mathf.Max(0f, Vector3.Dot(up, toSun)) : 0f;

#if DEV
            ownWas = own; sunWas = sun;
#endif
            // ---- the same numbers for any shader that draws after the veil is laid on and has to take its own (Natural
            // Ocean's sea, drawn after the near camera's veil: without it the sea within 400 m stood clear in rain and fog)
            Shader.SetGlobalVector(idHandAir, new Vector4(sigma * shown, top - (float)alt, soft, 0f));
            Shader.SetGlobalVector(idHandColour, new Vector4(own.r, own.g, own.b, 0f));
            Shader.SetGlobalVector(idHandSun, new Vector4(toSun.x, toSun.y, toSun.z, forward));
            Shader.SetGlobalVector(idHandSunColour, new Vector4(sun.r, sun.g, sun.b, backUp));
            // (a sea that says it takes its own veil: it writes the frame it last drew in; Sea.cs)
            int stamp = Mathf.RoundToInt(Shader.GetGlobalFloat(idSeaVeils));
            bool seaVeilsItself = stamp > 0 && Mathf.Abs(Time.frameCount % 1000000 - stamp) <= 3;
            // (over a sea the air ends at the water, not at the ground under it)
            seaHigh = body.ocean && alt > 0.0 ? (float)alt : 0f;
            seaRadius = (float)body.Radius;
            Camera first = Named("Camera 00"), second = Named("Camera 01");
            // (where one camera draws everything, Direct3D 11, its pass has the sky as well; and a sea that takes its own
            // veil is left alone by whichever pass comes before it is drawn: the near camera's, or the only one)
            Draw(near, first, second == null, second == null, seaVeilsItself, up, toSun, alt, own, sun, forward);
            Draw(far, second, true, false, false, up, toSun, alt, own, sun, forward);
            Report = "Weather in the air: seeing " + (sigma > 0f ? Far(3.912f / (sigma + 3.912f / 30000f)) : "far") + (shown < 0.99f ? " (" + Mathf.RoundToInt(100f * shown) + "%)" : "") + ".";
        }

        static string Far(float metres) => metres >= 2000f ? (metres / 1000f).ToString("F0") + " km" : (Mathf.Round(metres / 10f) * 10f).ToString("F0") + " m";

        float seaHigh, seaRadius = 600000f;
        static readonly int idHandAir = Shader.PropertyToID("_WeatherVeilAir"), idHandColour = Shader.PropertyToID("_WeatherVeilColour"), idHandSun = Shader.PropertyToID("_WeatherVeilSun"),
            idHandSunColour = Shader.PropertyToID("_WeatherVeilSunColour"), idSeaVeils = Shader.PropertyToID("_SeaVeilsItself");

        void Draw(OnCamera on, Camera cam, bool sky, bool laid, bool leaveSea, Vector3 up, Vector3 toSun, double alt, Color own, Color sun, float forward)
        {
            if (cam == null) { Detach(on); return; }
#if DEV
            if ((only == 1 && on == far) || (only == 2 && on == near)) { Detach(on); return; }
#endif
            if (on.Commands == null)
            {
                on.Commands = new CommandBuffer { name = "Natural Light: the weather's veil" };
                on.Mat = new Material(shader) { renderQueue = LaidQueue };
                on.Mat.SetVector("_VeilSheet", new Vector4(1f, 1f, 0.5f, 1f));
            }
            upNow = up; toSunNow = toSun; on.Sky = sky; on.Forward = forward;
            Face(on, cam);
            // (the sky is taken out as far as the weather can reach: past its top it thins out to nothing in a few "soft" heights)
            on.Mat.SetVector("_VeilAir", new Vector4(sigma * shown, top - (float)alt, soft, 60000f));
            on.Mat.SetVector("_VeilColour", new Vector4(own.r, own.g, own.b, SystemInfo.usesReversedZBuffer ? 1f : 0f));
            on.Mat.SetVector("_VeilSunColour", new Vector4(sun.r, sun.g, sun.b, backUp));
            CelestialBody world = FlightGlobals.currentMainBody;
            on.Mat.SetVector("_VeilSea", new Vector4(seaHigh, seaRadius, leaveSea ? 1f : 0f, world != null ? (float)world.Radius : 600000f));
            if (cam != on.Cam || laid != on.Laid)
            {
                Detach(on);
                if (laid) layingFor = cam;
                else
                {
                    on.Commands.Clear();
                    on.Commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                    on.Commands.DrawMesh(sheet, Matrix4x4.identity, on.Mat, 0, 0);
                    cam.AddCommandBuffer(on.When, on.Commands);
                }
                on.Cam = cam; on.Laid = laid;
            }
            if (laid) Lay(cam);
            // (the depth picture is needed: asked for while the veil is drawn, and only taken back if it was this that asked; looked at
            // every frame, since another mod may take it back)
            if ((cam.depthTextureMode & DepthTextureMode.Depth) == 0) { cam.depthTextureMode |= DepthTextureMode.Depth; on.AskedDepth = true; }
        }

        // ---- where one camera draws everything (Direct3D 11): the veil laid on among the see-through things, after Scatterer's
        // sky (2998) and its haze over the far ground, and before the clouds (2999, which take the veil over their own distance
        // themselves: Natural Weather's cloudsover.glsl), smoke and flames (3000 and on). (Laid on before all of them, at the time
        // the near camera's is, as until 2026-10-25, Scatterer's sky was drawn over it and a clear day's haze over the far ground:
        // in rain with the clouds held off the sky stood blue and the hills twenty kilometres off showed through, and under a deck,
        // wherever the hills stand in front of it, a bright pale band ran along the horizon.) Drawn by a sheet over the whole
        // screen in the clouds' own turn, set further off than theirs, which stands at the camera, so that it comes before them:
        // the see-through things of one turn are drawn from the furthest. Only for the camera it is for; for any other, nothing.
        const int LaidQueue = 2999;
        GameObject laying;
        Material layNothing;
        Camera layingFor;

        void Lay(Camera cam)
        {
            if (laying == null)
            {
                laying = new GameObject("Natural Light: the weather's veil, laid on");
                laying.AddComponent<MeshFilter>().sharedMesh = sheet;
                var r = laying.AddComponent<MeshRenderer>();
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                layNothing = new Material(shader) { name = "Natural Light: the veil laid on (nothing)", renderQueue = LaidQueue };
                layNothing.SetVector("_VeilSheet", new Vector4(0f, 0f, 0f, 1f));
                r.sharedMaterial = layNothing;
                laying.AddComponent<VeilLaid>().Owner = this;
            }
            if (!laying.activeSelf) laying.SetActive(true);
            // (on a layer the camera draws, the scenery's if it can)
            int layer = (cam.cullingMask & (1 << 15)) != 0 ? 15 : 0;
            if ((cam.cullingMask & (1 << layer)) == 0) for (int l = 0; l < 32; l++) if ((cam.cullingMask & (1 << l)) != 0) { layer = l; break; }
            laying.layer = layer;
            Transform t = cam.transform;
            laying.transform.SetPositionAndRotation(t.position + t.forward * 100f, t.rotation);
        }

        /// <summary>The material the laid-on sheet is drawn with for a camera just about to draw it (VeilLaid).</summary>
        internal Material LaidFor(Camera cam) => cam != null && cam == layingFor && near.Laid && near.Mat != null ? near.Mat : layNothing;

        // ---- what is newer in Keystone than the Keystone this mod asks for (0.7): the deck's light as the weather mod draws it
        // (Weather.CloudLight, after 0.10.0), kept in a method of its own and guarded, so that with an older Keystone the veil is
        // lit as before, not stopped.
        static bool deckMissing;

        /// <summary>The deck's light as the weather mod last said it (as light), and how much of the sky it covers: false where
        /// there is none to take (none said in the last ten seconds, or a Keystone older than it).</summary>
        static bool Deck(out Color light, out float cover)
        {
            light = Color.black; cover = 0f;
            if (deckMissing) return false;
            try { return AskDeck(out light, out cover); }
            catch (Exception) { deckMissing = true; return false; }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool AskDeck(out Color light, out float cover)
        {
            light = Keystone.Weather.CloudLight;
            cover = Keystone.Weather.CloudLightCover;
            return Time.unscaledTime - Keystone.Weather.CloudLightAt < 10f;
        }

        /// <summary>What the weather is where the camera is: how thick its veil, how high it reaches, and whether it is dust.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void Look(CelestialBody body, Vector3 camera)
        {
            sigma = 0f; snow = 0f; lidLit = true; coverOver = 0f;
            if (!Keystone.Weather.There) return;
            double lat = body.GetLatitude((Vector3d)camera), lon = body.GetLongitude((Vector3d)camera);
            WeatherHere h = Keystone.Weather.At(body, lat, lon, Planetarium.GetUniversalTime());
            // (how much of the sky over the camera the clouds cover, over a few kilometres round: the layers whose tops are over
            // it, one over another, for the light under the deck)
            float alt = (float)body.GetAltitude((Vector3d)camera);
            coverOver = 1f - Open(h.Low, alt) * Open(h.Middle, alt) * Open(h.High, alt);
            // (what the air takes on a clear day, thirty kilometres or so to lose a hill in, is drawn already by the game and Scatterer)
            sigma = Mathf.Max(0f, 3.912f / Mathf.Max(h.Seeing, 30f) - 3.912f / 30000f);
            double ground = body.TerrainAltitude(lat, lon, true);
            if (body.ocean && ground < 0.0) ground = 0.0;
            float g = (float)ground;
            dust = Mathf.Clamp01(h.Dust);
            // (fog as deep as its kind makes it: ground fog a few tens of metres, with the sky clear above it; steam on
            // water a few metres; sea fog and hill fog some hundreds)
            // (and its top is a top: a lid of warmer air lies on a fog, and within a few metres to a few tens of metres above
            // it the air is clear. Thinning out over a quarter of its depth, as it first did, a deep sea fog was still
            // fog a hundred metres above itself.)
            if (h.Fog > 0.02f && h.FogDeep > 1f) { top = g + Mathf.Max(4f, h.FogDeep); soft = Mathf.Clamp(0.12f * (top - g), 3f, 25f); }
            else if (h.Low.Kind == CloudKind.Fog && h.Low.Cover > 0.05f) { top = Mathf.Max(g + 20f, h.Low.Top); soft = Mathf.Max(15f, 0.25f * (top - g)); }
            else if (h.Low.Kind == CloudKind.Dust && h.Low.Cover > 0.05f) { top = Mathf.Max(g + 200f, h.Low.Top); soft = Mathf.Max(100f, 0.3f * (top - g)); }
            else if (h.Rain > 0.005f)
            {
                // (rain and snow fall from the cloud's base: as thick as they are up to it; a shower's base when there is no low layer)
                top = h.Low.Cover > 0.15f && h.Low.Base > g + 50f ? h.Low.Base : g + 1500f;
                lidLit = false;
                soft = 200f;
                snow = Mathf.Clamp01(h.Snow);
            }
            // (and haze, the weather's own on a clear day's: it fills the air under the lid that caps the lowest kilometre or two,
            // and is gone within a couple of hundred metres above it, so that from above it lies as a band along the horizon,
            // under the heaps that stand on its top. Thinning out over 800 m above, as it first did, it was thick still a
            // kilometre over its top: from about the clouds' height a level line of sight was greyed by 85 to 98 %, a grey bar
            // round the view with the clouds behind it (the owner, 2026-10-18).)
            else { top = g + 1500f; soft = 150f; }
            floor = g;
            // (the dust's colour, for the sunlight it throws forward: the ground's, which is what it is made of, paler and warmed; see
            // Daylight.DustColour. The light from all round is in that colour already.)
            if (dust > 0.01f) dustColour = Daylight.DustColour(Daylight.GroundNow);
        }

        /// <summary>How much of the sun a layer over the camera leaves, 0 to 1: the sky it does not cover, and of what it does a
        /// share, for the thin ones the sun shows through (cirrus, and altostratus, the ground glass), about a third taken. Fog and
        /// dust are the veil itself, not a deck over it: nothing.</summary>
        static float Open(CloudLayer l, float alt)
        {
            if (l.Cover <= 0.02f || l.Top <= alt || l.Kind == CloudKind.Fog || l.Kind == CloudKind.Dust) return 1f;
            float takes = l.Kind == CloudKind.Cirrus || l.Kind == CloudKind.Altostratus ? 0.35f : 1f;
            return 1f - Mathf.Clamp01(l.Cover) * takes;
        }
    }

    /// <summary>On the veil's laid-on sheet: just before a camera draws it, the material for that camera (WeatherVeil.LaidFor).</summary>
    sealed class VeilLaid : MonoBehaviour
    {
        internal WeatherVeil Owner;
        Renderer drawn;

        void OnWillRenderObject()
        {
            if (Owner == null) return;
            if (drawn == null) drawn = GetComponent<Renderer>();
            Material m = Owner.LaidFor(Camera.current);
            if (drawn != null && m != null && drawn.sharedMaterial != m) drawn.sharedMaterial = m;
        }
    }
}
