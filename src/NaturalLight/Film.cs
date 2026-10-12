using System;
using Keystone;
using UnityEngine;
using UnityEngine.Rendering;

namespace NaturalLight
{
    /// <summary>
    /// The picture developed as a film camera's would be.
    ///
    /// Light is one half of how a scene looks; the other is what the camera makes of it. The game makes
    /// nothing of it: whatever comes out brighter than white is cut off flat, so a flame, a floodlit rocket
    /// and a sunlit cloud are the same blank white, and no light can be made stronger without burning a hole
    /// in the picture. A camera, and the films and games admired for how they look, do a few things after the
    /// picture is drawn, and they are most of the look:
    ///
    ///   * a film curve: the bright end rolls over a shoulder instead of being cut off, the dark end has a
    ///     toe, and there is more contrast between;
    ///   * a glow round whatever is very bright, as a lens and the air give it;
    ///   * the colours left as the light makes them (since 0.9.0: see Look);
    ///   * creases, and the places where things meet, darkened;
    ///   * an exposure that follows the brightness of the view, as an eye does;
    ///   * the marks of a lens: slightly darker corners, a trace of colour fringing.
    ///
    /// None of that needs anything new drawn; it is arithmetic on the finished picture, and Unity's own
    /// post-processing does every part of it. The game does not ship with that, but the TUFX mod brings it,
    /// for every system the game runs on. So this asks Keystone's film for a grade every frame (see
    /// Keystone.Film), which hands it to TUFX where TUFX is installed, as a layer over whatever TUFX profile
    /// is in use. Where it is not, nothing happens, and the page says so.
    ///
    /// The exposure. Unity's own eye needs a kind of shader the game's OpenGL on a Mac has not got, so the
    /// brightness of the view is measured here instead: the picture is shrunk, in steps of a half, to a few
    /// hundred dots before anything else is done to it, and those are read back a few times a second. The
    /// middle of the picture counts for more than its edges, and black sky for nothing, or a craft in space
    /// would be burnt out to make up for the dark round it.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class FilmLook : MonoBehaviour
    {
        public static string Report = "";

        /// <summary>
        /// How far the film is at work on a picture that keeps more than white (0 not, 1 fully): for the lights. With a film
        /// curve to roll the bright end over, light no longer has to be held under white, and the sky and the ground can fill
        /// the shade as brightly as they really do.
        /// </summary>
        public static float Working;

        /// <summary>How far the camera's eye is open just now, in stops (0 as by day; above that, opened for the dark).</summary>
        public static float Opened;

        Camera metered;
        float orderLooked = -10f;
        CommandBuffer grab;
        static Shader cleaner;              // the mod's own shader that blacks out pixels that are not numbers (see tools/shaderpack/clean.glsl)
        static bool lookedForCleaner;
        Material clean;
        Mesh sheet;
        RenderTexture small;
        Texture2D read;
        int wide, high;

        float found = float.NaN, filled;    // how bright the view was last found to be (as light), and how much of it was not black
        float eye;                          // how far the eye is open by now, in stops
        float nextRead;

        void Awake()
        {
            Daylight.Setup();
        }

        void OnDestroy()
        {
            Stars(1f, true);
            Unhook();
            Report = "";
        }

        void Unhook()
        {
            if (metered != null && grab != null) metered.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, grab);
            if (grab != null) grab.Release();
            if (small != null) { small.Release(); Destroy(small); }
            if (read != null) Destroy(read);
            metered = null; grab = null; small = null; read = null;
            if (clean != null) Destroy(clean);
            if (sheet != null) Destroy(sheet);
            clean = null; sheet = null;
        }

        /// <summary>Have the camera that draws last of the scene's own shrink its picture, before anything else is done to it, to something small enough to read.</summary>
        void Hook(Camera cam)
        {
            Unhook();
            metered = cam; wide = cam.pixelWidth; high = cam.pixelHeight;
            grab = new CommandBuffer { name = "Natural Light: how bright the view is" };
            if (!lookedForCleaner)
            {
                lookedForCleaner = true;
                cleaner = OwnShader.Load("clean.bundle", "NaturalLight/Clean", "blacking out pixels that are not numbers", out string why);
                if (cleaner == null) Kit.Log("Natural Light", "pixels that are not numbers are left to the film: " + why);
            }
            // (only a picture that keeps more than white can hold such a pixel at all)
            if (cleaner != null && cam.allowHDR
#if DEV
                && !left
#endif
                )
            {
                clean = new Material(cleaner);
                clean.SetVector("_CleanSheet", new Vector4(1f, 1f, 0.5f, 1f));
                clean.SetVector("_CleanMark", mark);
                sheet = OwnShader.Sheet();
            }
            int w = Mathf.Max(1, wide / 2), h = Mathf.Max(1, high / 2), n = 0, last = -1;
            RenderTargetIdentifier from = BuiltinRenderTextureType.CameraTarget;
            int whole = -1;
            if (WholeFirst)
            {
                // (A copy at the camera's own size first. A blit from the camera's picture straight into a smaller one can come out
                // as the lower left quarter of the picture, when something earlier in the frame drew at another size: found by
                // the Cinema Camera's meter, which shrinks the picture the same way.)
                whole = Shader.PropertyToID("_NaturalLightViewWhole");
                grab.GetTemporaryRT(whole, wide, high, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
                grab.Blit(BuiltinRenderTextureType.CameraTarget, whole);
                from = whole;
            }
            while (w > 96 || h > 54)
            {
                int id = Shader.PropertyToID("_NaturalLightView" + n++);
                grab.GetTemporaryRT(id, w, h, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
                grab.Blit(from, id);
                if (n == 1 && clean != null)
                {
                    // The picture at half its size is the first thing made, and a pixel that is not a number has spoilt the dot
                    // of it that it fell in: black goes over the pixels of every such dot, before anything else sees them.
                    grab.SetGlobalTexture("_CleanScene", id);
                    grab.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                    grab.DrawMesh(sheet, Matrix4x4.identity, clean, 0, 0);
                }
                if (last != -1) grab.ReleaseTemporaryRT(last);
                else if (whole != -1) { grab.ReleaseTemporaryRT(whole); whole = -1; }
                from = id; last = id;
                w = Mathf.Max(1, w / 2); h = Mathf.Max(1, h / 2);
            }
            small = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear, name = "Natural Light: the view, small" };
            small.Create();
            grab.Blit(from, small);
            if (last != -1) grab.ReleaseTemporaryRT(last);
            if (whole != -1) grab.ReleaseTemporaryRT(whole);
            cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, grab);
            read = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true);
            if (clean != null) Forget(cam);
        }

        static Vector4 mark = new Vector4(0f, 0f, 0f, 1f);

        /// <summary>Whether the camera's picture is copied at its own size before it is shrunk (see Hook).</summary>
        static bool WholeFirst = false;
#if DEV
        /// <summary>(For the development build: the camera's picture copied whole before it is shrunk, or not; and the small picture the eye measures then.)</summary>
        public static string Whole()
        {
            WholeFirst = !WholeFirst; again = true;
            return WholeFirst ? "copied whole first" : "shrunk straight from the camera's picture";
        }

        /// <summary>(For the development build: the small picture the eye measures, written out eight times its size as a picture, to see what of the view it really reads.)</summary>
        public static string Snap(string path)
        {
            FilmLook me = FindObjectOfType<FilmLook>();
            if (me == null || me.small == null || !me.small.IsCreated()) return "nothing is being measured";
            RenderTexture was = RenderTexture.active;
            RenderTexture.active = me.small;
            var dots = new Texture2D(me.small.width, me.small.height, TextureFormat.RGBAFloat, false, true);
            dots.ReadPixels(new Rect(0, 0, me.small.width, me.small.height), 0, 0, false);
            dots.Apply(false);
            RenderTexture.active = was;
            const int K = 8;
            var big = new Texture2D(dots.width * K, dots.height * K, TextureFormat.RGB24, false);
            for (int y = 0; y < big.height; y++)
                for (int x = 0; x < big.width; x++)
                {
                    Color c = dots.GetPixel(x / K, y / K);
                    // (as it shows, more or less: bright things rolled over so that the picture can be read)
                    big.SetPixel(x, y, new Color(c.r / (0.6f + c.r), c.g / (0.6f + c.g), c.b / (0.6f + c.b)));
                }
            big.Apply(false);
            System.IO.File.WriteAllBytes(path, big.EncodeToPNG());
            Destroy(dots); Destroy(big);
            return path + ": " + me.small.width + "x" + me.small.height + " of a camera picture " + me.wide + "x" + me.high + (WholeFirst ? ", copied whole first" : ", shrunk straight from the camera's picture");
        }
        static bool again;
        /// <summary>(For the development build: the pixels that are not numbers shown in a colour instead of blacked out, and back.)</summary>
        public static string Mark()
        {
            mark = mark.x > 0f ? new Vector4(0f, 0f, 0f, 1f) : new Vector4(6f, 0f, 3f, 1f);
            again = true;
            return (cleaner != null ? "" : "(no shader for it) ") + (mark.x > 0f ? "shown in a colour" : "blacked out");
        }
        static bool left;
        /// <summary>(For the development build: the blacking out left undone, and done again: to measure what it costs.)</summary>
        public static string Leave()
        {
            left = !left; again = true;
            return left ? "left as they are" : "blacked out";
        }
#endif

        /// <summary>
        /// The smoothing of edges over time keeps a little of every past picture, and a pixel that was not a number stays in
        /// what it keeps for good, as a dot that does not move when the camera does. Once such pixels are being blacked out,
        /// what was kept before is thrown away, once. (Unity's post-processing, which TUFX carries, has a call for it; it is
        /// looked for by name, since this mod is not built against TUFX.)
        /// </summary>
        static void Forget(Camera cam)
        {
            try
            {
                foreach (Component c in cam.GetComponents<Component>())
                    if (c != null && c.GetType().Name == "PostProcessLayer")
                    {
                        var reset = c.GetType().GetMethod("ResetHistory", System.Type.EmptyTypes);
                        if (reset != null) reset.Invoke(c, null);
                    }
            }
            catch (Exception) { }
        }

        static Camera Last()
        {
            foreach (Camera cam in Camera.allCameras) if (cam != null && cam.name == "Camera 00" && cam.isActiveAndEnabled) return cam;
            return null;
        }

        bool Wanted => Daylight.On && Daylight.Filmed != null && Daylight.Filmed && Daylight.Near && !MapView.MapIsEnabled;

        /// <summary>
        /// The small picture read, a few times a second. Reading a picture back from the graphics card makes the game wait
        /// until the card has caught up, and asked for in the middle of a frame that is most of a frame's work: it was first
        /// done there, and cost a hitch of some twelve thousandths of a second eight times a second. So it is asked for in
        /// a way that does not wait, where the card can do that (Direct3D and Metal can); and where it cannot (the game's
        /// OpenGL on a Mac), it is read at the very start of a frame, when the card has had the whole of the last frame's
        /// time to finish, and less often.
        /// </summary>
        void Read()
        {
            if (small == null || read == null || !small.IsCreated() || Time.unscaledTime < nextRead) return;
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                nextRead = Time.unscaledTime + 0.12f;
                if (!asking)
                {
                    asking = true;
                    int w = small.width, h = small.height;
                    AsyncGPUReadback.Request(small, 0, TextureFormat.RGBAHalf, got =>
                    {
                        asking = false;
                        if (got.hasError || this == null) return;
                        try
                        {
                            var raw = got.GetData<ushort>();
                            if (raw.Length < w * h * 4) return;
                            if (dots == null || dots.Length != w * h) dots = new Color[w * h];
                            for (int i = 0; i < w * h; i++) dots[i] = new Color(Mathf.HalfToFloat(raw[i * 4]), Mathf.HalfToFloat(raw[i * 4 + 1]), Mathf.HalfToFloat(raw[i * 4 + 2]), 1f);
                            Weigh(dots, w, h);
                        }
                        catch (Exception) { }
                    });
                }
                return;
            }
            nextRead = Time.unscaledTime + 0.3f;
            try
            {
                RenderTexture was = RenderTexture.active;
                RenderTexture.active = small;
                read.ReadPixels(new Rect(0, 0, small.width, small.height), 0, 0, false);
                RenderTexture.active = was;
                Weigh(read.GetPixels(), small.width, small.height);
            }
            catch (Exception) { }
        }

        bool asking;
        Color[] dots;

        float[] lights, away;
        Vector3 sunIn;                      // where the sun is in the picture (0 to 1 each way), and z above 0 if it is in front of the camera

        /// <summary>
        /// How bright the view is: the whole of it, black sky and all (which is what an eye opens or closes for); what is
        /// in it, leaving the black out; and its brightest part (which is what an eye cannot open past). The middle of the
        /// view counts for more than its edges.
        /// </summary>
        void Weigh(Color[] dots, int w, int h)
        {
            if (lights == null || lights.Length != w * h) { lights = new float[w * h]; away = new float[w * h]; }
            double sum = 0, weight = 0, all = 0, whole = 0;
            int others = 0;
            float aspect = w / (float)h;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color c = dots[x + y * w];
                    // (the game's picture is as it is shown; as light it is that to the power 2.2)
                    float shown = Mathf.Max(0f, 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b);
                    // (Some of the game's own shaders give a pixel here and there that is not a number at all, where a highlight
                    // comes out as nothing divided by nothing: the tower by the launch pad seen against a low sun, for one. The film
                    // blacks those out before it does anything else; left in here, a few of them counted as the brightest thing
                    // there could be, and shut the eye as far as it goes.)
                    if (float.IsNaN(shown) || float.IsInfinity(shown)) { lights[x + y * w] = 0f; continue; }
                    float light = Mathf.Pow(Mathf.Min(shown, 8f), 2.2f);
                    lights[x + y * w] = light;
                    // (and the same list without the sun and the glare close round it: within an eighth of the picture's height)
                    float sx = ((x + 0.5f) / w - sunIn.x) * aspect, sy = (y + 0.5f) / h - sunIn.y;
                    if (sunIn.z <= 0f || sx * sx + sy * sy > 0.125f * 0.125f) away[others++] = light;
                    float dx = (x + 0.5f) / w * 2f - 1f, dy = (y + 0.5f) / h * 2f - 1f;
                    float counts = 1f - 0.55f * Mathf.Min(1f, dx * dx + dy * dy);
                    all += counts;
                    // (the mean of the square roots, squared: between the plain mean, which a single lamp would run away with, and
                    // the mean of the logarithms, which a dark background would)
                    double root = Math.Sqrt(light);
                    whole += counts * root;
                    if (light < 0.00002f) continue;                                 // black sky is not a dark thing
                    sum += counts * root; weight += counts;
                }
#if DEV
            peak = Color.black; over = 0;
            foreach (Color c in dots) { peak.r = Mathf.Max(peak.r, c.r); peak.g = Mathf.Max(peak.g, c.g); peak.b = Mathf.Max(peak.b, c.b); if (c.r > 1.02f || c.g > 1.02f || c.b > 1.02f) over++; }
            dotsRead = dots.Length;
#endif
            filled = all > 0 ? (float)(weight / all) : 0f;
            if (all > 0) { double mean = whole / all; view = (float)(mean * mean); }
            found = weight > 0 ? (float)((sum / weight) * (sum / weight)) : 0f;
            // (the brightest three-hundredth: sorted, and the top of the list averaged. A very small share, because what matters is whether
            // there is anything bright in sight at all, however little of the view it takes up: a rocket under floodlights
            // seen from across the pad is a few dots of these, and each of those dots is the rocket and the dark beside it
            // run together.)
            Array.Sort(lights);
            // (and the middle one of the whole list: how bright the view is, whatever small bright thing is in it)
            middling = lights[lights.Length / 2];
            int n = Mathf.Max(4, lights.Length / 300);
            double best = 0;
            for (int i = lights.Length - n; i < lights.Length; i++) best += lights[i];
            top = (float)(best / n);
            // (the same, the sun left out: the sun is the one thing in a picture that nobody tries to keep from burning out)
            Array.Sort(away, 0, others);
            best = 0; int m = Mathf.Min(n, others);
            for (int i = others - m; i < others; i++) best += away[i];
            topAway = m > 0 ? (float)(best / m) : 0f;
            // (and how much of the view is much brighter than white: the list is sorted, so it is counted from the top)
            int glare = 0;
            for (int i = others - 1; i >= 0 && away[i] > 2.4f; i--) glare++;
            glaring = others > 0 ? glare / (float)others : 0f;
#if DEV
            // (where the brightest dots are, for finding out what it is that the eye shuts for)
            var order = new System.Collections.Generic.List<int>();
            for (int i = 0; i < dots.Length; i++) order.Add(i);
            order.Sort((a, b) => (dots[b].r + dots[b].g + dots[b].b).CompareTo(dots[a].r + dots[a].g + dots[a].b));
            where = "sun at " + sunIn.x.ToString("F2") + " " + sunIn.y.ToString("F2") + (sunIn.z > 0f ? "" : " (behind)") + "; brightest dots:";
            for (int i = 0; i < 14 && i < order.Count; i++) where += " " + ((order[i] % w + 0.5f) / w).ToString("F2") + "," + ((order[i] / w + 0.5f) / h).ToString("F2") + "=" + ((dots[order[i]].r + dots[order[i]].g + dots[order[i]].b) / 3f).ToString("F1");
            where += "; top " + top.ToString("F1") + ", but for the sun " + topAway.ToString("F1") + ", " + (100f * glaring).ToString("F1") + "% of the view much brighter than white";
            int bad = 0; string badAt = "";
            for (int i = 0; i < dots.Length; i++)
            {
                Color c = dots[i];
                if (float.IsNaN(c.r + c.g + c.b) || float.IsInfinity(c.r + c.g + c.b)) { bad++; if (bad <= 24) badAt += " " + ((i % w + 0.5f) / w).ToString("F2") + "," + ((i / w + 0.5f) / h).ToString("F2") + (float.IsNaN(c.r + c.g + c.b) ? "" : "inf"); }
            }
            where += "; " + bad + " dots that are not numbers:" + badAt;
#endif
        }

        float view = float.NaN, top, topAway;   // the whole view's brightness, black counted; its brightest part's; and that with the sun itself left out
        float glaring;                          // how much of the view (the sun left out) is much brighter than white, 0 to 1
        float middling;                         // the brightness of the middle dot of the view, were they put in order: the view's, and not that of a flame in it

        static Color starsWere;
        static bool starsKept;
        float stars = 1f;

        /// <summary>
        /// The stars, as bright as an eye with this much light in it would see them. An eye (or a camera) set for a sunlit
        /// planet or a floodlit rocket sees no stars at all: they are there, and thousands of times too faint. The game
        /// draws its stars at one brightness whatever else is in view (it dims them only in air by day, and when the sun
        /// itself is in sight). So they are dimmed by how bright the brightest thing in view is, quickly when it comes into
        /// view and slowly when it leaves, and come out again in the dark.
        /// </summary>
        void Stars(float wanted, bool atOnce = false)
        {
            GalaxyCubeControl sky = GalaxyCubeControl.Instance;
            if (sky == null) return;
            if (!starsKept) { starsWere = sky.maxGalaxyColor; starsKept = true; }
            stars = atOnce ? wanted : Mathf.MoveTowards(stars, wanted, (wanted < stars ? 1.6f : 0.35f) * Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            sky.maxGalaxyColor = Color.Lerp(sky.minGalaxyColor, starsWere, Mathf.Clamp01(stars));
        }


        void Update()
        {
            if (metered != null && grab != null && Time.unscaledTime - orderLooked > 1f)
            {
                // (Last of all that is drawn into the picture at this point of the frame, looked to once a second: the weather's clouds
                // and the sun's shafts are drawn at the same point, in the order they were asked for, and a meter that reads the
                // picture before the clouds are in it takes a sky of white heaps for a blue one. Found 2026-10-11: under a storm with
                // sunlit towers all round, the eye opened as for an overcast and the towers went to plain white.)
                orderLooked = Time.unscaledTime;
                CommandBuffer[] there = metered.GetCommandBuffers(CameraEvent.AfterForwardAlpha);
                if (there.Length > 0 && there[there.Length - 1] != grab)
                {
                    metered.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, grab);
                    metered.AddCommandBuffer(CameraEvent.AfterForwardAlpha, grab);
                }
            }
            if (metered != null && Daylight.Eye != null && (float)Daylight.Eye > 0f) Read();
        }

#if DEV
        /// <summary>(For the development build: the six faces of the game's sky of stars: what each is called, which way it faces, and its picture.)</summary>
        public static string Sky()
        {
            GalaxyCubeControl sky = GalaxyCubeControl.Instance;
            if (sky == null) return "no sky";
            var o = new System.Text.StringBuilder("sky at " + sky.transform.position + " turned " + sky.transform.rotation.eulerAngles + " scale " + sky.transform.lossyScale + "; brightest " + sky.maxGalaxyColor + " dimmest " + sky.minGalaxyColor + "\n");
            foreach (Renderer r in sky.GetComponentsInChildren<Renderer>(true))
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                Texture t = r.sharedMaterial != null && r.sharedMaterial.HasProperty("_MainTex") ? r.sharedMaterial.mainTexture : null;
                o.Append(r.name + ": layer " + r.gameObject.layer + " shader " + (r.sharedMaterial != null ? r.sharedMaterial.shader.name : "?") + " picture " + (t != null ? t.name + " " + t.width + "x" + t.height : "none")
                    + " faces " + r.transform.forward.ToString("F2") + " up " + r.transform.up.ToString("F2") + " at " + r.transform.localPosition.ToString("F1") + " scale " + r.transform.localScale.ToString("F1")
                    + (mf != null && mf.sharedMesh != null ? " mesh " + mf.sharedMesh.vertexCount + " corners, bounds " + mf.sharedMesh.bounds.size.ToString("F1") + (mf.sharedMesh.isReadable ? " readable" : " not readable") : "") + "\n");
            }
            return o.ToString();
        }

        static Color peak; static int over, dotsRead; static string where = "";
        /// <summary>(For the development build: the brightest the small picture got, and how many of its dots are brighter than white: whether the game is really keeping more than white, and for what.)</summary>
        public static string Peak() => (SystemInfo.supportsAsyncGPUReadback ? "read without waiting; " : "read at the start of a frame; ") + "brightest " + peak.r.ToString("F2") + " " + peak.g.ToString("F2") + " " + peak.b.ToString("F2") + "; " + over + " of " + dotsRead + " dots over white; " + where;
#endif

        /// <summary>
        /// Which way the film look is made: 1 as it is now, real light with a cinematic feel; 0 as it was before 0.9.0 (the look
        /// the user called "way too saturated"), kept for the development build's pictures of before and after.
        /// </summary>
        static int recipe = 1;
        /// <summary>
        /// How much the new recipe's exposure is under the old one's, in stops. Unity's Neutral curve leaves the middle of the picture
        /// as it is, where ACES took some of it away, and the light had been turned up to make up for that (see Daylight): without this
        /// the new look came out brighter than the old, and flatter. (Measured on the same views: -0.3 to -0.4 brings it back to as
        /// bright as the old.)
        /// </summary>
        static float trim = -0.3f;
        /// <summary>
        /// The new recipe's colour at "Contrast and colour" 1, against the picture's own (0 leaves it as it is). Tried at -0.04, to
        /// take back what the contrast adds: measured, that left the colour a tenth or more under the game's own picture, and the
        /// space centre and a sunset looked grey. Left at 0 at first; then, seeing it, the user asked for it "just oh so very
        /// slightly more up than you brought it down, just very, very slightly, and then the contrast a touch up too" (2026-10-10).
        /// </summary>
        static float colourAt1 = 0.03f;
#if DEV
        /// <summary>(For the development build: which recipe the film look is made by, 0 as it was before 0.9.0, 1 as now.)</summary>
        public static string Recipe(string which) { recipe = int.Parse(which); return "the film look's recipe: " + recipe; }
        /// <summary>(For the development build: the new recipe's exposure under the old one's, in stops.)</summary>
        public static string Trim(string stops) { trim = float.Parse(stops, System.Globalization.CultureInfo.InvariantCulture); return "the new recipe's trim: " + trim + " stops"; }
        /// <summary>(For the development build: the new recipe's colour at "Contrast and colour" 1.)</summary>
        public static string Colour(string amount) { colourAt1 = float.Parse(amount, System.Globalization.CultureInfo.InvariantCulture); return "the new recipe's colour: " + colourAt1; }
        static bool cloudsBlind;
        /// <summary>(For the development build: the eye blind to the weather's dimming of the light, as before 0.10.0, and back: for pictures of before and after.)</summary>
        public static string CloudsBlind() { cloudsBlind = !cloudsBlind; return cloudsBlind ? "the eye blind to the clouds, as in 0.9.0" : "the eye opens for the clouds"; }
#endif

        /// <summary>
        /// The film look as a grade for Keystone's film: with the eye so many stops open, and with so much sky (0 to 1), under
        /// which the bright end is warmed a trace more. It is the same in flight, at the space centre and in the editors.
        /// </summary>
        internal static Grade Look(IFilm film, float eye, float air)
        {
            float punch = Daylight.Punch != null ? (float)Daylight.Punch : 1f, glow = Daylight.Glow != null ? (float)Daylight.Glow : 1f;
            float creases = Daylight.Creases != null ? (float)Daylight.Creases : 1f, marks = Daylight.Marks != null ? (float)Daylight.Marks : 1f;
            Grade grade = Grade.None;
            if (recipe == 0)
            {
                // ---- as it was before 0.9.0
                grade.curve = 2;
                grade.exposure = (film.Wide ? 0.2f : 0f) + eye;
                grade.contrast = 0.1f * punch;
                grade.colour = 0.1f * punch;
                grade.warmth = 0.04f * punch;
                float tinted0 = Mathf.Clamp01(punch) * air;
                grade.dark = Color.Lerp(Color.white, new Color(0.965f, 0.99f, 1.05f), tinted0);
                grade.bright = Color.Lerp(Color.white, new Color(1.045f, 1f, 0.94f), Mathf.Clamp01(punch) * (0.4f + 0.6f * air));
                grade.glowTint = new Color(1f, 0.965f, 0.91f);
            }
            else
            {
                // ---- real light, with a cinematic feel. Asked for, of the look before this: "way too saturated... I still want it
                // to look like real life, but I want it to look cinematic and beautiful, not over saturated." Cinematic is contrast,
                // light and shade, and a bright end that rolls over gently, not colour turned up. Measured, the old look added a third
                // to the picture's colour by day (and a few in every hundred pixels came out at full strength), turned white paint
                // orange, and made night blue-black; the light itself (sky and ground) added nothing that could be measured. So:
                //   * Unity's "Neutral" film curve, which brings the bright end back with little change of hue or strength of colour,
                //     where its ACES curve (the old one) strengthens bright colours and turns their hue. (AgX, Blender's, was tried as
                //     well, as a lookup: it measured the same, but Unity's grading takes no exposure with a lookup of one's own, and
                //     the eye is an exposure.)
                //   * more contrast than before, the colour a hair over the picture's own (see colourAt1), and the picture as
                //     bright as it was (see trim); the slider adds colour above 1, for a player who wants it;
                //   * no cold in the shadows: Unity does that as a colour added to every pixel, which turned the night blue; and a
                //     trace of warmth in the lights, and a glow that is nearly white.
                grade.curve = 1;
                grade.exposure = (film.Wide ? 0.2f + trim : 0f) + eye;
                grade.contrast = 0.19f * punch;
                grade.colour = colourAt1 * Mathf.Clamp01(punch) + 0.12f * Mathf.Max(0f, punch - 1f);
                grade.warmth = 0.01f * punch;
                grade.bright = Color.Lerp(Color.white, new Color(1.01f, 1f, 0.988f), Mathf.Clamp01(punch) * (0.4f + 0.6f * air));
                grade.glowTint = new Color(1f, 0.985f, 0.96f);
            }
            grade.glow = 0.85f * glow;
            grade.glowFrom = 1.0f;
            grade.glowWide = 7.5f;
            grade.nooks = 0.62f * creases;
            grade.corners = 0.42f * marks;
            grade.fringe = 0.3f * marks;
            // (a picture pushed to see in the dark is grainy, as film pushed that far is; and the grain hides the steps that
            // show when the little light there was is multiplied up)
            // (kept faint: the first try at it was said at once to be too noisy and heavy)
            grade.grain = Mathf.Clamp01((eye - 0.8f) / 2.4f) * 0.12f * (Daylight.Grain != null ? (float)Daylight.Grain : 1f);
            return grade;
        }

        void LateUpdate()
        {
            IFilm film = Wanted ? Film.Best : null;
            Working = Mathf.MoveTowards(Working, film != null && film.Wide ? 1f : 0f, 2f * Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            if (!Wanted) { if (metered != null) Unhook(); Report = ""; found = float.NaN; view = float.NaN; eye = 0f; Opened = 0f; Stars(1f, true); return; }
            if (film == null) { if (metered != null) Unhook(); Stars(1f, true); Report = "Film look: " + Film.WhyNot; return; }

            // ---- the eye: open for a dark view, closed down for a bright one, and never all the way
            float follows = Daylight.Eye != null ? (float)Daylight.Eye : 1f;
            Camera cam = Last();
#if DEV
            if (again) { again = false; if (metered != null) Unhook(); }
#endif
            // (the view is shrunk for two things: for the eye to measure, and for finding the pixels that are not numbers)
            if (follows <= 0f) { found = float.NaN; view = float.NaN; }
            if (follows <= 0f && (cleaner == null && lookedForCleaner)) { if (metered != null) Unhook(); }
            else if (cam != null && (cam != metered || cam.pixelWidth != wide || cam.pixelHeight != high || (clean != null) != (cleaner != null && cam.allowHDR
#if DEV
                && !left
#endif
                ))) Hook(cam);

            sunIn = Vector3.zero;
            if (cam != null && Daylight.Near && Planetarium.fetch != null && Planetarium.fetch.Sun != null)
                sunIn = cam.WorldToViewportPoint(cam.transform.position + ((Vector3)(Planetarium.fetch.Sun.position - Daylight.Here)).normalized * 1000f);
            float wanted = 0f;
            if (!float.IsNaN(view) && follows > 0f)
            {
                const float Middle = 0.26f;                                              // a view this bright (as light) is left as it is
                // In the dark the eye opens, and what could not be seen can: by six tenths of how dark the whole view is, up to
                // a little over three stops (nine times the light). But no further than the brightest part of the view can
                // bear: with a floodlit rocket in sight, or a sunlit one against black space, it stays shut, and everything
                // else stays as dark as it is. That is why there are no stars in a photograph of a sunlit spacecraft.
                float open = Mathf.Max(0f, Mathf.Log(Middle / Mathf.Max(view, 1e-6f), 2f)) * 0.6f * follows;
                // (At the space centre, seen from above, the buildings have to be found to be clicked on, whatever lit windows
                // and floodlights are in sight: there the bright things hold it back much less.)
                open = Mathf.Min(open, Mathf.Log((HighLogic.LoadedSceneIsFlight ? 0.35f : 2.2f) / Mathf.Max(top, 1e-6f), 2f));
                open = Mathf.Clamp(open, 0f, 3.2f * Mathf.Min(follows, 1.25f));
                // In the light it closes: by two thirds of how bright what is in view is, and further for a view full of
                // something much brighter than white (a flame from close to), until that is only twice white or so. By how
                // full: all the way where a twenty-fifth of the view or more is that bright, and less for less, so that a
                // flame seen from across the pad at dawn dims the dawn and does not put it out.
                float close = found > Middle ? Mathf.Log(Middle / found, 2f) * 0.65f * follows : 0f;
                // (Not for the sun itself, though: it burns out in every picture ever taken of it, and the eye is set by the sky round it.)
                if (topAway > 2.4f) close = Mathf.Min(close, Mathf.Log(2.4f / topAway, 2f) * Mathf.Min(follows, 1f) * Mathf.Clamp01(glaring / 0.04f));
                // By day, though, hardly at all: nobody stops a camera down for a rocket's flame in daylight. The flame
                // burns out white in the picture and the day stays as it is. (Shut all the way for it, a launch watched
                // from thirty metres turned the afternoon to night as the engines lit.) By how bright the view is but for
                // the flame: the middle one of its dots, not their mean, which the flame is part of.
                float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.002f, 0.02f, middling));
                close = Mathf.Max(close, Mathf.Lerp(-2.5f, -0.6f, day));
                wanted = open > 0f ? open : close;
                // Under cloud, though, there is a tenth to a quarter of a clear day's light (less in rain), and a camera's meter opens
                // two or three stops for it, as an eye does, whatever the sky in view: an overcast sky burns out white in every
                // photograph taken under one. So by day it opens at least by six tenths of the stops the weather takes from the light
                // on the ground (Daylight.Clouded; the same share as for a dark view, above), the bright sky notwithstanding. Level
                // ground then shows about half a clear day's light under an overcast and a third in rain; a scene with much that is
                // upright, or in shadow on a sunny day (the pad), comes out about as bright as in sunshine, and flat: as a camera's
                // meter makes it. (At three quarters, tried first, the overcast pad came out a sixth brighter than the sunny one.)
#if DEV
                if (!cloudsBlind)
#endif
                // (But no further than the view can bear: until the whole of it, clouds and sky and all, comes to about four tenths
                // of white. Under an even overcast that is the two stops and more above; under one cloud's shadow on a day of
                // sunlit heaps, or under a storm with sunlit towers round it, hardly anything: opened by the weather alone, "the
                // bright sky notwithstanding", every heap in sight went to plain white whenever a cloud's shadow crossed the pad.)
                wanted = Mathf.Max(wanted, Mathf.Min(Mathf.Min(0.6f * Daylight.Clouded * follows, Mathf.Max(0f, Mathf.Log(0.42f / Mathf.Max(view, 1e-6f), 2f))), 3.2f * Mathf.Min(follows, 1.25f)));
            }
            // (it closes fast and opens slowly, as eyes do: coming out of the light it is some seconds before the dark shows anything)
            float rate = wanted < eye ? 3f : 0.45f;
            eye = Mathf.MoveTowards(eye, wanted, rate * Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            Opened = eye;
            // (by how much brighter the brightest thing in view is than the sky itself can be: the sky's own nebulae must not
            // put its stars out)
            Stars(!float.IsNaN(view) && follows > 0f ? Mathf.Lerp(1f, Mathf.Pow(0.14f / Mathf.Max(top, 0.14f), 0.9f), Mathf.Clamp01(follows)) : 1f);

            Grade grade = Look(film, eye, Daylight.Air);
            Film.Ask("Natural Light", grade);

            Report = "Film look by " + film.Name + (film.Wide ? "" : " (the game is not keeping more than white in its picture: switch \"hdr\" on in the TUFX profile for the film curve and the glow to have anything to work on)")
                + (float.IsNaN(view) ? "." : ": the view " + Mathf.LinearToGammaSpace(view).ToString("F2") + " bright (" + Mathf.RoundToInt(100f * filled) + "% of it not black, its brightest part " + Mathf.LinearToGammaSpace(top).ToString("F2") + (sunIn.z > 0f && topAway < top * 0.98f ? ", " + Mathf.LinearToGammaSpace(topAway).ToString("F2") + " but for the sun" : "") + "); the eye " + (eye >= 0f ? "+" : "") + eye.ToString("F1") + " stops" + (stars < 0.97f ? ", the stars at " + Mathf.RoundToInt(100f * stars) + "%" : "") + ".");
        }
    }

    /// <summary>
    /// The film look in the editors. The assembly building and the hangar have their own lamps and no sky, and nothing
    /// there is measured or follows the view: it is the film curve, the glow, the colour, the darkened creases and the
    /// lens marks, at one exposure, so that a craft looks in the editor as it will on the pad.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public sealed class EditorFilm : MonoBehaviour
    {
        void Awake() { Daylight.Setup(); }

        void OnDestroy() { if (Daylight.Page != null && Daylight.Page.Panel == Readout) Daylight.Page.Panel = null; }

        string said = "";

        void Readout() { GUILayout.Label(said, Host.Small); }

        void LateUpdate()
        {
            bool wanted = Daylight.On && Daylight.Filmed != null && Daylight.Filmed;
            IFilm film = wanted ? Film.Best : null;
            if (Daylight.Page != null && Daylight.Page.Panel == null) Daylight.Page.Panel = Readout;
            if (!wanted) { said = "In the editors the mod is the film look alone, and that is switched off."; return; }
            if (film == null) { said = "In the editors the mod is the film look alone. " + Film.WhyNot; return; }
            Film.Ask("Natural Light", FilmLook.Look(film, 0f, 0f));
            said = "In the editors the mod is the film look alone (by " + film.Name + "): the lights and the eye are for flight and the space centre."
                + (film.Wide ? "" : " The game is not keeping more than white in its picture here: switch \"hdr\" on in the TUFX profile for the editor.");
        }
    }
}
