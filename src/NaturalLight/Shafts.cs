using Keystone;
using UnityEngine;
using UnityEngine.Rendering;

namespace NaturalLight
{
    /// <summary>
    /// Shafts of sunlight.
    ///
    /// Air with anything in it shows the sun's light as it passes, and where something stands in the sun's
    /// way the air behind it is dark: so with the sun behind a launch tower, a rocket or the edge of a cloud,
    /// the light comes round it in shafts. Asked for as lighting that is "super cinematic and godlike".
    ///
    /// They are made from the picture itself (see tools/shaderpack/shafts.glsl): what is bright and far
    /// round the sun, with whatever is near blacked out, drawn small and then drawn out away from the sun
    /// twice over, and added back. That is done before the film look, which then rolls them over and
    /// gives them their glow. Only where there is air: in space the sun's light passes through nothing.
    ///
    /// The shader is one of the mod's own (see OwnShader), packed for the game's OpenGL (Macs and Linux) and built
    /// for Direct3D (Windows) by tools/unityshaders/windows.py.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class SunShafts : MonoBehaviour
    {
        public static string Report = "";
        static Shader shader;
        static bool tried;
        static string why = "";

        CommandBuffer commands;
        Camera onCamera;
        Material far, drawnA, drawnB, added;
        RenderTexture bright, raysA, raysB;             // what is bright and far in the picture, at half its size; the shafts, at a quarter
        Mesh sheet;
        bool askedDepth;
        float strength;                 // how much they show, 0 to 1, by where the sun is in the picture and how much air there is

        static void Load()
        {
            tried = true;
            shader = OwnShader.Load("shafts.bundle", "NaturalLight/Shafts", "shafts of sunlight", out why);
            Kit.Log("Natural Light", shader != null ? "has shafts of sunlight" : "no shafts of sunlight: " + why);
        }

#if DEV
        static int upTo = 4;
        /// <summary>(For the development build: how much of the work is done, to find what costs what. 0 and 1: nothing; 2: what is bright found; 3: and drawn out; 4: and added.)</summary>
        public static string Less() { upTo = (upTo + 4) % 5; return upTo.ToString(); }
        /// <summary>(For the development build: TUFX's post-processing on the game's cameras switched off, and on again: to see what the shafts do in a game without it.)</summary>
        public static string Bare()
        {
            string said = "";
            foreach (Camera cam in Camera.allCameras)
                foreach (Behaviour b in cam.GetComponents<Behaviour>())
                    if (b != null && b.GetType().Name == "PostProcessLayer") { b.enabled = !b.enabled; said += cam.name + (b.enabled ? " on; " : " off; "); }
            Camera last = Last();
            return said + (last != null ? "Camera 00 keeps more than white: " + last.allowHDR + ", draws into " + (last.targetTexture != null ? last.targetTexture.name : "the screen") + ", depth picture " + last.depthTextureMode : "");
        }
        /// <summary>(For the development build: the camera's picture kept with more than white in it, or not: to see what the shafts do where there is no film.)</summary>
        public static string Plain()
        {
            Camera cam = Last();
            if (cam == null) return "no camera";
            cam.allowHDR = !cam.allowHDR;
            return cam.allowHDR ? "more than white kept" : "cut off at white";
        }
#endif

        void Awake() { Daylight.Setup(); }

        void OnDestroy()
        {
            Detach();
            Release();
            foreach (Material m in new[] { far, drawnA, drawnB, added }) if (m != null) Destroy(m);
            if (sheet != null) Destroy(sheet);
            if (commands != null) commands.Release();
            Report = "";
        }

        void Detach()
        {
            if (onCamera != null && commands != null)
            {
                onCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, commands);
                if (askedDepth) onCamera.depthTextureMode &= ~DepthTextureMode.Depth;
            }
            onCamera = null; askedDepth = false;
        }

        void Release()
        {
            foreach (RenderTexture t in new[] { bright, raysA, raysB }) if (t != null) { t.Release(); Destroy(t); }
            bright = raysA = raysB = null;
        }

        static Camera Last()
        {
            foreach (Camera cam in Camera.allCameras) if (cam != null && cam.name == "Camera 00" && cam.isActiveAndEnabled) return cam;
            return null;
        }

        void LateUpdate()
        {
            bool wanted = Daylight.On && Daylight.Shafts != null && Daylight.Shafts && Daylight.Near && !MapView.MapIsEnabled;
            if (wanted && !tried) Load();
            Camera cam = wanted && shader != null ? Last() : null;
            float want = 0f;
            Vector3 sunAt = Vector3.zero;
            if (cam != null && Planetarium.fetch != null && Planetarium.fetch.Sun != null)
            {
                Vector3 toSun = ((Vector3)(Planetarium.fetch.Sun.position - Daylight.Here)).normalized;
                sunAt = cam.WorldToViewportPoint(cam.transform.position + toSun * 1000f);
                // In front of the camera, and in the picture or not far outside it; where there is air to show the light; and the sun
                // up, or only just gone (the shafts of the first and last light are the best of them).
                float facing = Vector3.Dot(cam.transform.forward, toSun);
                float inView = sunAt.z > 0f ? Mathf.Clamp01(1.6f - Mathf.Max(Mathf.Abs(sunAt.x - 0.5f), Mathf.Abs(sunAt.y - 0.5f)) * 1.6f) : 0f;
                float up = Mathf.InverseLerp(-0.06f, 0.0f, Daylight.SunUp);
                want = Mathf.Clamp01(facing * 2f) * inView * up * Mathf.Sqrt(Mathf.Clamp01(Daylight.Air));
            }
            // (stronger with the sun low, when its light comes the long way through the air)
            float much = (0.5f + 1.3f * Mathf.Clamp01(1f - Daylight.SunUp / 0.5f)) * (Daylight.ShaftLight != null ? (float)Daylight.ShaftLight : 1f);
            if (much <= 0f) want = 0f;
            strength = Mathf.MoveTowards(strength, want, 2f * Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            if (cam == null || strength <= 0.003f)
            {
                if (onCamera != null) { Detach(); Release(); }
                Report = wanted && shader == null && why.Length > 0 ? "No shafts of sunlight: " + why + "." : "";
                return;
            }

            if (commands == null)
            {
                commands = new CommandBuffer { name = "Natural Light: shafts of sunlight" };
                far = new Material(shader); drawnA = new Material(shader); drawnB = new Material(shader); added = new Material(shader);
                foreach (Material one in new[] { far, drawnA, drawnB, added }) one.SetVector("_ShaftSheet", new Vector4(1f, 1f, 0.5f, 1f));
                sheet = OwnShader.Sheet();
            }
            int wide = cam.pixelWidth, high = cam.pixelHeight;
            if (wide < 32 || high < 32) return;
            RenderTextureFormat kind = cam.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
            if (bright == null || bright.width != wide / 2 || bright.height != high / 2 || bright.format != kind)
            {
                Release();
                bright = new RenderTexture(wide / 2, high / 2, 0, kind) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "Natural Light shafts: what is bright and far" };
                // (Drawn out at a quarter of the picture's size: shafts are soft things, and nothing is lost by it. What is
                // bright and far is found at half size first, so that a thin thing in front of the sun, a strut of a tower, is
                // not missed between two dots; each dot of the quarter is then four of those run together.)
                raysA = new RenderTexture(Mathf.Max(1, wide / 4), Mathf.Max(1, high / 4), 0, kind) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "Natural Light shafts: one" };
                raysB = new RenderTexture(Mathf.Max(1, wide / 4), Mathf.Max(1, high / 4), 0, kind) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "Natural Light shafts: two" };
            }
            float backwards = SystemInfo.usesReversedZBuffer ? 1f : 0f;
            var sun = new Vector4(sunAt.x, sunAt.y, wide / (float)high, 0f);
            // (the sky has to be brighter than this to count; within this many picture heights of the sun; so strong; so much lost a look)
            var look = new Vector4(0.42f, 1.15f, 0.95f * strength * much, 0.9f);
            // (their colour: the sunlight's own, as it reaches here)
            Color tint = Daylight.Sunlight;
            float most = Mathf.Max(tint.r, Mathf.Max(tint.g, tint.b));
            tint = most > 0.01f ? Color.Lerp(Color.white, tint / most, 0.8f) : Color.white;
            far.SetVector("_ShaftStage", new Vector4(1f, backwards, 0f, 0f));
            // (Each change from drawing one picture to drawing another costs more, in the game's OpenGL on a Mac, than the
            // drawing: a fifth of a thousandth of a second each, measured, whatever the size. So it is done in as few as will
            // do: twice drawn out, twelve looks each, where three times ten was no better to look at.)
            Material[] goes = { drawnA, drawnB };
            float[] reach = { 0.3f, 0.9f };
            for (int n = 0; n < 2; n++)
            {
                goes[n].SetVector("_ShaftStage", new Vector4(2f, backwards, 0f, 0f));
                goes[n].SetVector("_ShaftSun", new Vector4(sun.x, sun.y, sun.z, reach[n]));
                goes[n].SetVector("_ShaftLook", look);
                goes[n].SetTexture("_ShaftRays", n == 0 ? bright : raysA);
            }
            added.SetVector("_ShaftStage", new Vector4(3f, backwards, 0f, 0f));
            added.SetVector("_ShaftTint", new Vector4(tint.r, tint.g, tint.b, 0.12f));
            added.SetTexture("_ShaftRays", raysB);
            foreach (Material one in new[] { far, added }) { one.SetVector("_ShaftSun", sun); one.SetVector("_ShaftLook", look); }

            if (cam != onCamera) Detach();
            if ((cam.depthTextureMode & DepthTextureMode.Depth) == 0) { cam.depthTextureMode |= DepthTextureMode.Depth; askedDepth = true; }

            commands.Clear();
#if DEV
            if (upTo >= 2) {
#endif
            // (the picture so far is read as it stands, by the first go, and not copied first)
            commands.SetGlobalTexture("_ShaftScene", BuiltinRenderTextureType.CameraTarget);
            commands.SetRenderTarget(bright); commands.ClearRenderTarget(false, true, Color.black); commands.DrawMesh(sheet, Matrix4x4.identity, far, 0, 0);
#if DEV
            }
            if (upTo >= 3) {
#endif
            commands.SetRenderTarget(raysA); commands.ClearRenderTarget(false, true, Color.black); commands.DrawMesh(sheet, Matrix4x4.identity, drawnA, 0, 0);
            commands.SetRenderTarget(raysB); commands.ClearRenderTarget(false, true, Color.black); commands.DrawMesh(sheet, Matrix4x4.identity, drawnB, 0, 0);
#if DEV
            }
            if (upTo >= 2)
#endif
            commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
#if DEV
            if (upTo >= 4)
#endif
            commands.DrawMesh(sheet, Matrix4x4.identity, added, 0, 0);
            if (onCamera != cam)
            {
                cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, commands);
                onCamera = cam;
            }
            Report = "Shafts of sunlight " + Mathf.RoundToInt(100f * strength) + "%.";
        }
    }
}
