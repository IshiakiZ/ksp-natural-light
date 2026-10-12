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
        readonly OnCamera far = new OnCamera { When = CameraEvent.AfterEverything };
        Mesh sheet;
        float shown;                    // how much of it there is, eased (0 to 1): it comes and goes with the weather, and is not switched

        // what the weather says, looked up a few times a second
        float sigma, top, soft, dust, snow, lookedAt = -10f;
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
            foreach (Camera cam in Camera.allCameras) if (cam != null && cam.name == name && cam.isActiveAndEnabled) return cam;
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

#if DEV
            ownWas = own; sunWas = sun;
#endif
            Draw(near, Named("Camera 00"), false, up, toSun, alt, own, sun, forward);
            Draw(far, Named("Camera 01"), true, up, toSun, alt, own, sun, forward);
            Report = "Weather in the air: seeing " + (sigma > 0f ? Far(3.912f / (sigma + 3.912f / 30000f)) : "far") + (shown < 0.99f ? " (" + Mathf.RoundToInt(100f * shown) + "%)" : "") + ".";
        }

        static string Far(float metres) => metres >= 2000f ? (metres / 1000f).ToString("F0") + " km" : (Mathf.Round(metres / 10f) * 10f).ToString("F0") + " m";

        void Draw(OnCamera on, Camera cam, bool sky, Vector3 up, Vector3 toSun, double alt, Color own, Color sun, float forward)
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
            on.Mat.SetVector("_VeilSunColour", new Vector4(sun.r, sun.g, sun.b, 0f));
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
            sigma = 0f; snow = 0f;
            if (!Keystone.Weather.There) return;
            double lat = body.GetLatitude((Vector3d)camera), lon = body.GetLongitude((Vector3d)camera);
            WeatherHere h = Keystone.Weather.At(body, lat, lon, Planetarium.GetUniversalTime());
            // (what the air takes on a clear day, thirty kilometres or so to lose a hill in, is drawn already by the game and Scatterer)
            sigma = Mathf.Max(0f, 3.912f / Mathf.Max(h.Seeing, 30f) - 3.912f / 30000f);
            double ground = body.TerrainAltitude(lat, lon, true);
            if (body.ocean && ground < 0.0) ground = 0.0;
            float g = (float)ground;
            dust = Mathf.Clamp01(h.Dust);
            if (h.Low.Kind == CloudKind.Fog && h.Low.Cover > 0.05f) { top = Mathf.Max(g + 20f, h.Low.Top); soft = Mathf.Max(15f, 0.25f * (top - g)); }
            else if (h.Low.Kind == CloudKind.Dust && h.Low.Cover > 0.05f) { top = Mathf.Max(g + 200f, h.Low.Top); soft = Mathf.Max(100f, 0.3f * (top - g)); }
            else if (h.Rain > 0.005f)
            {
                // (rain and snow fall from the cloud's base: as thick as they are up to it; a shower's base when there is no low layer)
                top = h.Low.Cover > 0.15f && h.Low.Base > g + 50f ? h.Low.Base : g + 1500f;
                soft = 200f;
                snow = Mathf.Clamp01(h.Snow);
            }
            else { top = g + 1500f; soft = 800f; }
            // (the dust's colour, for the sunlight it throws forward: the ground's, which is what it is made of, paler and warmed; see
            // Daylight.DustColour. The light from all round is in that colour already.)
            if (dust > 0.01f) dustColour = Daylight.DustColour(Daylight.GroundNow);
        }
    }
}
