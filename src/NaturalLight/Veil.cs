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
    /// (the sky's and the ground's, Daylight.Around), in the dust's own colour for dust, and brighter towards the sun where
    /// the sun is out: drops, flakes and dust throw light forward.
    ///
    /// Drawn over each of the game's two flight cameras by a shader of the mod's own (see tools/shaderpack/veil.glsl and
    /// OwnShader): the far one (beyond 400 m) when it has drawn everything, the sky and the clouds included; the near one when it
    /// has drawn what is solid and before what is see-through, so that the drops by the camera, flames and smoke are not veiled
    /// as if they were as far off as the ground behind them. Packed for the game's OpenGL (Macs and Linux) and built for
    /// Direct3D (Windows) by tools/unityshaders/windows.py.
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
        }
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
        public static string State() => Report + " sigma " + (last != null ? last.sigma.ToString("F5") + " /m, top " + last.top.ToString("F0") + " m above the camera, thinning in " + last.soft.ToString("F0") + " m, dust " + last.dust.ToString("F2") + ", snow " + last.snow.ToString("F2") + ", shown " + last.shown.ToString("F2") + "; its own light " + last.ownWas.ToString("F3") + " (as it shows), the sun's towards the sun " + last.sunWas.ToString("F3") : "?");
        static WeatherVeil last;
        Color ownWas, sunWas;
#endif

        void Awake() { Daylight.Setup(); }

        void OnDestroy()
        {
            Detach(near); Detach(far);
            foreach (OnCamera on in new[] { near, far })
            {
                if (on.Mat != null) Destroy(on.Mat);
                if (on.Commands != null) on.Commands.Release();
            }
            if (sheet != null) Destroy(sheet);
            Report = "";
        }

        static void Detach(OnCamera on)
        {
            if (on.Cam != null && on.Commands != null)
            {
                on.Cam.RemoveCommandBuffer(on.When, on.Commands);
                if (on.AskedDepth) on.Cam.depthTextureMode &= ~DepthTextureMode.Depth;
            }
            on.Cam = null; on.AskedDepth = false;
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
            Draw(near, first, second == null, seaVeilsItself, up, toSun, alt, own, sun, forward);
            Draw(far, second, true, false, up, toSun, alt, own, sun, forward);
            Report = "Weather in the air: seeing " + (sigma > 0f ? Far(3.912f / (sigma + 3.912f / 30000f)) : "far") + (shown < 0.99f ? " (" + Mathf.RoundToInt(100f * shown) + "%)" : "") + ".";
        }

        static string Far(float metres) => metres >= 2000f ? (metres / 1000f).ToString("F0") + " km" : (Mathf.Round(metres / 10f) * 10f).ToString("F0") + " m";

        float seaHigh, seaRadius = 600000f;
        static readonly int idHandAir = Shader.PropertyToID("_WeatherVeilAir"), idHandColour = Shader.PropertyToID("_WeatherVeilColour"), idHandSun = Shader.PropertyToID("_WeatherVeilSun"),
            idHandSunColour = Shader.PropertyToID("_WeatherVeilSunColour"), idSeaVeils = Shader.PropertyToID("_SeaVeilsItself");

        void Draw(OnCamera on, Camera cam, bool sky, bool leaveSea, Vector3 up, Vector3 toSun, double alt, Color own, Color sun, float forward)
        {
            if (cam == null) { Detach(on); return; }
            if (on.Commands == null)
            {
                on.Commands = new CommandBuffer { name = "Natural Light: the weather's veil" };
                on.Mat = new Material(shader);
                on.Mat.SetVector("_VeilSheet", new Vector4(1f, 1f, 0.5f, 1f));
            }
            Transform t = cam.transform;
            Matrix4x4 p = cam.projectionMatrix;
            on.Mat.SetVector("_VeilView", new Vector4(1f / p[0, 0], 1f / p[1, 1], p[0, 2] / p[0, 0], p[1, 2] / p[1, 1]));
            on.Mat.SetVector("_VeilUp", new Vector4(Vector3.Dot(up, t.right), Vector3.Dot(up, t.up), Vector3.Dot(up, t.forward), sky ? 1f : 0f));
            // (the sky is taken out as far as the weather can reach: past its top it thins out to nothing in a few "soft" heights)
            on.Mat.SetVector("_VeilAir", new Vector4(sigma * shown, top - (float)alt, soft, 60000f));
            on.Mat.SetVector("_VeilColour", new Vector4(own.r, own.g, own.b, SystemInfo.usesReversedZBuffer ? 1f : 0f));
            on.Mat.SetVector("_VeilSun", new Vector4(Vector3.Dot(toSun, t.right), Vector3.Dot(toSun, t.up), Vector3.Dot(toSun, t.forward), forward));
            on.Mat.SetVector("_VeilSunColour", new Vector4(sun.r, sun.g, sun.b, backUp));
            CelestialBody world = FlightGlobals.currentMainBody;
            on.Mat.SetVector("_VeilSea", new Vector4(seaHigh, seaRadius, leaveSea ? 1f : 0f, world != null ? (float)world.Radius : 600000f));
            if (cam != on.Cam)
            {
                Detach(on);
                on.Commands.Clear();
                on.Commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                on.Commands.DrawMesh(sheet, Matrix4x4.identity, on.Mat, 0, 0);
                cam.AddCommandBuffer(on.When, on.Commands);
                on.Cam = cam;
            }
            // (the depth picture is needed: asked for while the veil is drawn, and only taken back if it was this that asked; looked at
            // every frame, since another mod may take it back)
            if ((cam.depthTextureMode & DepthTextureMode.Depth) == 0) { cam.depthTextureMode |= DepthTextureMode.Depth; on.AskedDepth = true; }
        }

        /// <summary>What the weather is where the camera is: how thick its veil, how high it reaches, and whether it is dust.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        void Look(CelestialBody body, Vector3 camera)
        {
            sigma = 0f; snow = 0f; lidLit = true;
            if (!Keystone.Weather.There) return;
            double lat = body.GetLatitude((Vector3d)camera), lon = body.GetLongitude((Vector3d)camera);
            WeatherHere h = Keystone.Weather.At(body, lat, lon, Planetarium.GetUniversalTime());
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
    }
}
