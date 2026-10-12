using System;
using System.Collections.Generic;
using System.Threading;
using Keystone;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// A deep sky: nebulae, a few far galaxies and brighter coloured stars, laid over the game's own sky
    /// of stars.
    ///
    /// Asked for as: "In space, I want beautiful, gorgeous nebulas and stuff, and space to be beautiful, but
    /// still look like space." So it is mostly black, as space is. What is added is faint by day's measure
    /// and is nearly all near the band of the galaxy, where such things are: half a dozen or so nebulae of
    /// glowing gas (the red of hydrogen at the heart, teal and blue at the edges; some blue ones that only
    /// throw back starlight; a ring left by a dead star), two or three small far galaxies well off it, and
    /// some hundreds of stars brighter than the game's own, in the colours stars have. The band itself is
    /// the game's, and is left alone.
    ///
    /// Nothing is drawn by hand and no picture is shipped: it is all worked out from noise when the game
    /// starts, on other threads so that the game does not wait for it (about a second), from a number that
    /// can be changed for another sky. Where the band of the galaxy lies is found from the game's own sky,
    /// by looking at where that is brightest, so that the nebulae sit on the band the game already draws,
    /// whatever sky is installed.
    ///
    /// It is drawn as six more faces just inside the game's six, with light added to what is there, by one
    /// of the game's own shaders. The game dims its sky in air by day and when the sun is in view, and this
    /// mod dims it when anything bright is (see FilmLook): the added sky is dimmed by exactly as much, by
    /// reading back how much the game's own faces have been.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class DeepSky : MonoBehaviour
    {
        const int Size = 1024;

        sealed class Face
        {
            public string Name;
            public Renderer Its;                    // the game's own face
            public Vector3 Corner, Across, Up;      // where its picture's bottom left is (in the sky's own frame), and the way to its bottom right and its top left
            public Color32[] Dots;                  // the added picture, once made
            public Texture2D Picture;
            public GameObject Added;
            public Renderer Shown;
        }

        readonly List<Face> faces = new List<Face>();
        GalaxyCubeControl sky;
        Material ink;
        volatile int made;                          // how many faces' pictures have been worked out
        bool working, applied;
        int seedMade = -1;
        float brightest;
        static readonly MaterialPropertyBlock block = new MaterialPropertyBlock(), mine = new MaterialPropertyBlock();
        public static string Report = "";

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Daylight.Setup();
        }

        void OnDestroy()
        {
            foreach (Face f in faces) { if (f.Added != null) Destroy(f.Added); if (f.Picture != null) Destroy(f.Picture); }
            if (ink != null) Destroy(ink);
        }

        void Update()
        {
            GalaxyCubeControl now = GalaxyCubeControl.Instance;
            bool wanted = Daylight.Deep != null && Daylight.Deep && Daylight.On;
            int seed = Daylight.SkySeed != null ? Mathf.RoundToInt((float)Daylight.SkySeed) : 7;
            if (now == null || !wanted) { Show(false); Report = ""; return; }

            // ---- a new sky to work out: the first time, when the game has made its sky anew, or when another is asked for
            if ((now != sky || seed != seedMade) && !working)
            {
                foreach (Face f in faces) { if (f.Added != null) Destroy(f.Added); if (f.Picture != null) Destroy(f.Picture); }
                faces.Clear();
                sky = now; seedMade = seed; applied = false; made = 0;
                if (!Measure()) { sky = null; return; }
                working = true;
                Vector3 pole, heart;
                Band(out pole, out heart);
                var plan = new Plan(seed, pole, heart);
                foreach (Face f in faces)
                {
                    Face one = f;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { one.Dots = plan.Draw(one.Corner, one.Across, one.Up, Size); }
                        catch (Exception) { one.Dots = new Color32[Size * Size]; }
                        Interlocked.Increment(ref made);
                    });
                }
            }
            if (working && made >= faces.Count)
            {
                working = false;
                Attach();
                applied = true;
            }
            if (!applied) { Report = "The deep sky is being worked out."; return; }

            // ---- as bright as the game's own sky is just now (it is dimmed in air by day, towards the sun, and by this mod's eye)
            Show(true);
            float most = Mathf.Max(sky.maxGalaxyColor.r, Mathf.Max(sky.maxGalaxyColor.g, sky.maxGalaxyColor.b));
            brightest = Mathf.Max(brightest, most);
            float strength = Daylight.SkyGlow != null ? (float)Daylight.SkyGlow : 1f;
            foreach (Face f in faces)
            {
                if (f.Its == null || f.Shown == null) continue;
                f.Its.GetPropertyBlock(block);
                Color c = block.isEmpty ? sky.maxGalaxyColor : block.GetColor("_Color");
                float at = brightest > 1e-4f ? Mathf.Clamp01(Mathf.Max(c.r, Mathf.Max(c.g, c.b)) / brightest) : 1f;
                // (the game's shader for adding light doubles what it is given: half is full strength; and the game shows its own
                // stars at about three quarters of their picture's brightness, so the added sky is shown at about as much)
                float tint = 0.5f * at * 0.8f * strength;
                mine.SetColor("_TintColor", new Color(tint, tint, tint, 0.5f));
                f.Shown.SetPropertyBlock(mine);
            }
            Report = "";
        }

        void Show(bool on)
        {
            foreach (Face f in faces) if (f.Shown != null && f.Shown.enabled != on) f.Shown.enabled = on;
        }

        /// <summary>The game's six faces: where each one's picture lies, in the sky's own frame.</summary>
        bool Measure()
        {
            foreach (Renderer r in sky.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r.name.StartsWith("Natural Light")) continue;
                MeshFilter filter = r.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !mesh.isReadable || mesh.vertexCount < 4) continue;
                Vector3[] at = mesh.vertices;
                Vector2[] uv = mesh.uv;
                if (uv == null || uv.Length != at.Length) continue;
                // (the corners of the picture: the ones whose place in it is nearest (0,0), (1,0) and (0,1))
                int c00 = 0, c10 = 0, c01 = 0;
                for (int i = 1; i < at.Length; i++)
                {
                    if ((uv[i] - new Vector2(0f, 0f)).sqrMagnitude < (uv[c00] - new Vector2(0f, 0f)).sqrMagnitude) c00 = i;
                    if ((uv[i] - new Vector2(1f, 0f)).sqrMagnitude < (uv[c10] - new Vector2(1f, 0f)).sqrMagnitude) c10 = i;
                    if ((uv[i] - new Vector2(0f, 1f)).sqrMagnitude < (uv[c01] - new Vector2(0f, 1f)).sqrMagnitude) c01 = i;
                }
                Vector3 p00 = sky.transform.InverseTransformPoint(r.transform.TransformPoint(at[c00]));
                Vector3 p10 = sky.transform.InverseTransformPoint(r.transform.TransformPoint(at[c10]));
                Vector3 p01 = sky.transform.InverseTransformPoint(r.transform.TransformPoint(at[c01]));
                faces.Add(new Face { Name = r.name, Its = r, Corner = p00, Across = p10 - p00, Up = p01 - p00 });
            }
            return faces.Count > 0;
        }

        /// <summary>
        /// Where the band of the galaxy lies in the game's own sky: the pole of its plane, and the way to its brightest
        /// part. Each face's picture is shrunk to a few hundred dots and read; the band is the plane the bright dots lie
        /// nearest (found by trying a few thousand poles), and its heart is where, in that plane, they are brightest.
        /// </summary>
        void Band(out Vector3 pole, out Vector3 heart)
        {
            pole = Vector3.up; heart = Vector3.forward;
            try
            {
                const int N = 24;
                var where = new List<Vector3>(); var bright = new List<float>();
                foreach (Face f in faces)
                {
                    Texture picture = f.Its.sharedMaterial != null && f.Its.sharedMaterial.HasProperty("_MainTex") ? f.Its.sharedMaterial.mainTexture : null;
                    if (picture == null) continue;
                    RenderTexture small = RenderTexture.GetTemporary(N, N, 0, RenderTextureFormat.ARGB32);
                    RenderTexture was = RenderTexture.active;
                    Graphics.Blit(picture, small);
                    RenderTexture.active = small;
                    var copy = new Texture2D(N, N, TextureFormat.RGBA32, false);
                    copy.ReadPixels(new Rect(0, 0, N, N), 0, 0);
                    copy.Apply();
                    RenderTexture.active = was;
                    RenderTexture.ReleaseTemporary(small);
                    Color32[] dots = copy.GetPixels32();
                    Destroy(copy);
                    for (int y = 0; y < N; y++)
                        for (int x = 0; x < N; x++)
                        {
                            Color32 c = dots[x + y * N];
                            where.Add((f.Corner + f.Across * ((x + 0.5f) / N) + f.Up * ((y + 0.5f) / N)).normalized);
                            bright.Add((c.r + c.g + c.b) / 765f);
                        }
                }
                if (where.Count < 100) return;
                float mean = 0f;
                foreach (float b in bright) mean += b / bright.Count;
                // (only what is brighter than the sky's mean counts, by how much)
                var rng = new System.Random(12345);
                float best = float.MaxValue;
                for (int t = 0; t < 3000; t++)
                {
                    var p = new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f);
                    if (p.sqrMagnitude < 0.01f || p.sqrMagnitude > 1f) continue;
                    p.Normalize();
                    float off = 0f, all = 0f;
                    for (int i = 0; i < where.Count; i++)
                    {
                        float w = bright[i] - mean;
                        if (w <= 0f) continue;
                        float d = Vector3.Dot(where[i], p);
                        off += w * d * d; all += w;
                    }
                    if (all > 0f && off / all < best) { best = off / all; pole = p; }
                }
                // (a band there is, if the bright dots lie much nearer that plane than they would scattered evenly: a third)
                Vector3 towards = Vector3.zero;
                for (int i = 0; i < where.Count; i++)
                {
                    float w = bright[i] - mean;
                    if (w > 0f && Mathf.Abs(Vector3.Dot(where[i], pole)) < 0.3f) towards += where[i] * w;
                }
                towards = Vector3.ProjectOnPlane(towards, pole);
                heart = towards.sqrMagnitude > 1e-6f ? towards.normalized : Vector3.Cross(pole, Vector3.right).normalized;
                Kit.Log("Natural Light", "the galaxy's band in this sky: pole " + pole.ToString("F2") + ", brightest towards " + heart.ToString("F2") + " (bright dots lie " + best.ToString("F3") + " off the plane, where 0.333 is no band at all)");
            }
            catch (Exception ex) { Kit.Log("Natural Light", "could not read the game's sky to find the galaxy's band (" + ex.Message + "): the deep sky is laid out without it"); }
        }

        void Attach()
        {
            Shader shader = Shader.Find("KSP/Particles/Additive") ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive");
            if (shader == null) { Kit.Log("Natural Light", "no shader to add the deep sky with"); return; }
            if (ink == null) ink = new Material(shader);
            foreach (Face f in faces)
            {
                if (f.Its == null || f.Dots == null) continue;
                f.Picture = new Texture2D(Size, Size, TextureFormat.RGB24, true) { wrapMode = TextureWrapMode.Clamp, name = "Natural Light deep sky " + f.Name, anisoLevel = 2 };
                f.Picture.SetPixels32(f.Dots);
                f.Picture.Apply(true, true);
                f.Dots = null;
                f.Added = new GameObject("Natural Light deep sky " + f.Name) { layer = f.Its.gameObject.layer };
                f.Added.transform.SetParent(f.Its.transform, false);
                f.Added.transform.localScale = Vector3.one * 0.985f;                    // (just inside the game's own face, so that it is in front of it)
                f.Added.AddComponent<MeshFilter>().sharedMesh = f.Its.GetComponent<MeshFilter>().sharedMesh;
                MeshRenderer r = f.Added.AddComponent<MeshRenderer>();
                r.sharedMaterial = ink;
                r.material.mainTexture = f.Picture;                                    // (a copy of its own, which goes when it does)
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                f.Shown = r;
            }
            Kit.Log("Natural Light", "the deep sky is laid over " + faces.Count + " faces of the game's sky");
        }

        // ================================================================ working the sky out

        /// <summary>One sky: where its nebulae, galaxies and stars are, and how each texel of a face is coloured. Used from other threads: nothing of the game's is touched.</summary>
        sealed class Plan
        {
            struct Cloud { public Vector3 At, A, B; public float Wide, Bright; public int Kind; public Color Heart, Edge; public float Twist; }
            struct Far { public Vector3 At, A, B; public float Wide, Thin; public Color Tint; }
            struct Star { public Vector3 At; public Color Tint; public float Bright, Wide; }

            readonly List<Cloud> clouds = new List<Cloud>();
            readonly List<Far> fars = new List<Far>();
            readonly List<Star> stars = new List<Star>();
            readonly Vector3 pole, heart, round;
            readonly int[] perm = new int[512];
            readonly float shift;

            public Plan(int seed, Vector3 pole, Vector3 heart)
            {
                this.pole = pole.normalized; this.heart = heart.normalized; round = Vector3.Cross(this.pole, this.heart).normalized;
                var rng = new System.Random(seed * 7919 + 13);
                var p = new int[256];
                for (int i = 0; i < 256; i++) p[i] = i;
                for (int i = 255; i > 0; i--) { int j = rng.Next(i + 1); int t = p[i]; p[i] = p[j]; p[j] = t; }
                for (int i = 0; i < 512; i++) perm[i] = p[i & 255];
                shift = (float)rng.NextDouble() * 50f;

                // ---- nebulae: most of them on the band, more towards the galaxy's heart
                int count = 7 + rng.Next(3);
                for (int n = 0; n < count; n++)
                {
                    float along = Gauss(rng) * 1.3f, off = Gauss(rng) * 0.13f;
                    if (n >= count - 2) { along = (float)(rng.NextDouble() * Math.PI * 2.0); off = Gauss(rng) * 0.35f; }         // (one or two anywhere)
                    Vector3 at = (heart * Mathf.Cos(along) + round * Mathf.Sin(along) + this.pole * off).normalized;
                    int kind = n == 1 ? 2 : rng.NextDouble() < 0.3 ? 1 : 0;       // 0 glowing gas, 1 dust lit by stars, 2 a ring
                    var c = new Cloud { At = at, Kind = kind, Twist = (float)rng.NextDouble() * 20f };
                    Frame(at, rng, out c.A, out c.B);
                    c.Wide = kind == 2 ? Mathf.Lerp(0.04f, 0.075f, (float)rng.NextDouble()) : Mathf.Lerp(0.09f, 0.26f, (float)(rng.NextDouble() * rng.NextDouble()) + 0.15f * (n == 0 ? 1f : 0f));
                    c.Bright = Mathf.Lerp(0.5f, 1f, (float)rng.NextDouble()) * (kind == 2 ? 1.1f : 1f);
                    if (kind == 0)
                    {
                        // the red of glowing hydrogen at the heart, and where the gas is thin, the teal of oxygen or the blue of starlight
                        c.Heart = new Color(1f, Mathf.Lerp(0.16f, 0.36f, (float)rng.NextDouble()), Mathf.Lerp(0.22f, 0.5f, (float)rng.NextDouble()));
                        c.Edge = rng.NextDouble() < 0.5 ? new Color(0.2f, 0.75f, 0.85f) : new Color(0.3f, 0.45f, 1f);
                    }
                    else if (kind == 1) { c.Heart = new Color(0.45f, 0.62f, 1f); c.Edge = new Color(0.25f, 0.35f, 0.8f); }
                    else { c.Heart = new Color(0.25f, 0.9f, 0.75f); c.Edge = new Color(1f, 0.3f, 0.25f); }
                    clouds.Add(c);
                }
                // ---- far galaxies: well off the band
                int galaxies = 2 + rng.Next(2);
                for (int n = 0; n < galaxies; n++)
                {
                    Vector3 at;
                    do at = new Vector3(Gauss(rng), Gauss(rng), Gauss(rng)).normalized; while (Mathf.Abs(Vector3.Dot(at, this.pole)) < 0.45f);
                    var g = new Far { At = at, Wide = Mathf.Lerp(0.012f, 0.03f, (float)rng.NextDouble()), Thin = Mathf.Lerp(0.25f, 0.8f, (float)rng.NextDouble()) };
                    Frame(at, rng, out g.A, out g.B);
                    g.Tint = Color.Lerp(new Color(1f, 0.9f, 0.75f), new Color(0.75f, 0.85f, 1f), (float)rng.NextDouble());
                    fars.Add(g);
                }
                // ---- stars: brighter than the game's own, in the colours stars have; thicker on the band, and in knots by the nebulae
                for (int n = 0; n < 520; n++)
                {
                    Vector3 at;
                    if (n % 5 == 0 && clouds.Count > 0)
                    {
                        Cloud by = clouds[rng.Next(clouds.Count)];
                        at = (by.At + (by.A * Gauss(rng) + by.B * Gauss(rng)) * by.Wide * 0.6f).normalized;
                    }
                    else
                    {
                        at = new Vector3(Gauss(rng), Gauss(rng), Gauss(rng)).normalized;
                        if (rng.NextDouble() < 0.55) at = (at - this.pole * (Vector3.Dot(at, this.pole) * 0.8f)).normalized;      // (drawn in towards the band)
                    }
                    double heat = rng.NextDouble();
                    Color tint = heat < 0.12 ? new Color(1f, 0.62f, 0.4f) : heat < 0.3 ? new Color(1f, 0.85f, 0.62f) : heat < 0.6 ? new Color(1f, 0.97f, 0.92f) : heat < 0.86 ? new Color(0.8f, 0.88f, 1f) : new Color(0.62f, 0.75f, 1f);
                    float bright = Mathf.Pow((float)rng.NextDouble(), 5f);         // (few bright, many faint)
                    stars.Add(new Star { At = at, Tint = tint, Bright = 0.2f + 0.8f * bright, Wide = 0.0007f + 0.0012f * bright });
                }
            }

            static float Gauss(System.Random rng)
            {
                double u = 1.0 - rng.NextDouble(), v = rng.NextDouble();
                return (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v));
            }

            static void Frame(Vector3 at, System.Random rng, out Vector3 a, out Vector3 b)
            {
                Vector3 any = new Vector3(Gauss(rng), Gauss(rng), Gauss(rng));
                a = Vector3.Cross(at, any).normalized; b = Vector3.Cross(at, a);
            }

            // ---- noise: smooth, the same from every thread
            static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
            static float Grad(int hash, float x, float y, float z)
            {
                int h = hash & 15;
                float u = h < 8 ? x : y, v = h < 4 ? y : h == 12 || h == 14 ? x : z;
                return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
            }
            float Noise(float x, float y, float z)
            {
                int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
                x -= xi; y -= yi; z -= zi; xi &= 255; yi &= 255; zi &= 255;
                float u = Fade(x), v = Fade(y), w = Fade(z);
                int a = perm[xi] + yi, aa = perm[a] + zi, ab = perm[a + 1] + zi, b = perm[xi + 1] + yi, ba = perm[b] + zi, bb = perm[b + 1] + zi;
                float l1 = Mathf.Lerp(Mathf.Lerp(Grad(perm[aa], x, y, z), Grad(perm[ba], x - 1, y, z), u), Mathf.Lerp(Grad(perm[ab], x, y - 1, z), Grad(perm[bb], x - 1, y - 1, z), u), v);
                float l2 = Mathf.Lerp(Mathf.Lerp(Grad(perm[aa + 1], x, y, z - 1), Grad(perm[ba + 1], x - 1, y, z - 1), u), Mathf.Lerp(Grad(perm[ab + 1], x, y - 1, z - 1), Grad(perm[bb + 1], x - 1, y - 1, z - 1), u), v);
                return Mathf.Lerp(l1, l2, w);
            }
            float Billows(Vector3 p, int octaves)
            {
                float sum = 0f, size = 1f, all = 0f;
                for (int o = 0; o < octaves; o++)
                {
                    sum += Noise(p.x * size + shift, p.y * size - shift, p.z * size + 2f * shift) / size;
                    all += 1f / size; size *= 2.03f;
                }
                return sum / all;                                                      // about -0.6 to 0.6
            }
            /// <summary>Filaments: bright where the noise crosses nought.</summary>
            float Threads(Vector3 p, int octaves)
            {
                float sum = 0f, size = 1f, all = 0f;
                for (int o = 0; o < octaves; o++)
                {
                    float n = 1f - Mathf.Abs(Noise(p.x * size - shift, p.y * size + 3f * shift, p.z * size + shift)) * 2.2f;
                    sum += Mathf.Max(0f, n) * Mathf.Max(0f, n) / size;
                    all += 1f / size; size *= 2.1f;
                }
                return sum / all;
            }

            /// <summary>The colour added at one direction of the sky (as light, each of red, green and blue from 0 up).</summary>
            Vector3 At(Vector3 d)
            {
                float r = 0f, g = 0f, b = 0f;
                // (The band of the galaxy itself is left to the game's own sky. A glow was laid along it at first, and it lay over
                // the game's Milky Way like a haze: what is added now is only what the game's sky has not got.)
                // ---- the nebulae
                for (int n = 0; n < clouds.Count; n++)
                {
                    Cloud c = clouds[n];
                    float facing = Vector3.Dot(d, c.At);
                    if (facing < 0.5f) continue;
                    // where in the cloud, across and up, in cloud-widths
                    float x = Vector3.Dot(d, c.A) / (facing * c.Wide), y = Vector3.Dot(d, c.B) / (facing * c.Wide);
                    float far2 = x * x + y * y;
                    if (far2 > 6.5f) continue;
                    var p = new Vector3(x * 1.3f + c.Twist, y * 1.3f - c.Twist, c.Twist * 0.37f);
                    // (the shape is pulled about by slower noise, so that no cloud is round)
                    var pull = new Vector3(Billows(p * 0.7f + new Vector3(7.1f, 0f, 0f), 3), Billows(p * 0.7f + new Vector3(0f, 3.3f, 0f), 3), 0f);
                    Vector3 q = p + pull * 1.6f;
                    if (c.Kind == 2)
                    {
                        // a ring: what a dead star threw off, brightest at its rim, in threads
                        float rim = Mathf.Sqrt(far2) + 0.1f * pull.x, ring = Mathf.Exp(-(rim - 1f) * (rim - 1f) / 0.012f), fill = Mathf.Exp(-far2 / 0.45f) * 0.1f;
                        float threads = 0.55f + 1.1f * Threads(q * 1.4f, 3);
                        float light = (ring * threads + fill) * 0.1f * c.Bright;
                        // (and the dead star itself, in the middle)
                        light += Mathf.Exp(-far2 * 900f) * 0.5f;
                        float outer = Mathf.Clamp01((rim - 0.9f) * 3f);
                        r += light * Mathf.Lerp(c.Heart.r, c.Edge.r, outer); g += light * Mathf.Lerp(c.Heart.g, c.Edge.g, outer); b += light * Mathf.Lerp(c.Heart.b, c.Edge.b, outer);
                        continue;
                    }
                    float body = Mathf.Exp(-far2 / 1.5f);
                    float gas = Mathf.Clamp01((Billows(q, 6) + 0.03f) * 3.1f) * body;
                    if (gas <= 0.002f) continue;
                    float threads2 = Threads(q * 1.7f + new Vector3(11f, 5f, 2f), 4);
                    // (dark lanes of dust across it)
                    float dust = Mathf.Clamp01(1.25f - 2.6f * Mathf.Clamp01(Billows(q * 1.9f + new Vector3(31f, 17f, 0f), 4) + 0.18f));
                    float thick = gas * gas;
                    // (a soft glow all through it, brighter where it is thick, with threads of brighter gas across it)
                    float light2 = (gas * 0.4f + thick * 0.5f + gas * threads2 * 0.14f) * dust * 0.095f * c.Bright;
                    // thick gas is the heart's colour; thin gas, at the edges, the edge's
                    float edge = Mathf.Clamp01(1.15f - gas * 1.9f);
                    r += light2 * Mathf.Lerp(c.Heart.r, c.Edge.r, edge); g += light2 * Mathf.Lerp(c.Heart.g, c.Edge.g, edge); b += light2 * Mathf.Lerp(c.Heart.b, c.Edge.b, edge);
                    // (and at the very heart of the thickest, nearly white)
                    float white = thick * thick * thick * 0.04f * c.Bright * dust;
                    r += white; g += white * 0.9f; b += white * 0.85f;
                }
                // ---- far galaxies: a bright middle, a soft disc seen at a slant, a hint of arms
                for (int n = 0; n < fars.Count; n++)
                {
                    Far f = fars[n];
                    float facing = Vector3.Dot(d, f.At);
                    if (facing < 0.98f) continue;
                    float x = Vector3.Dot(d, f.A) / (facing * f.Wide), y = Vector3.Dot(d, f.B) / (facing * f.Wide * f.Thin);
                    float far2 = x * x + y * y;
                    if (far2 > 12f) continue;
                    float arms = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.Atan2(y, x) + 2.2f * Mathf.Sqrt(far2));
                    float light = (Mathf.Exp(-far2 * 6f) * 0.3f + Mathf.Exp(-Mathf.Sqrt(far2) * 1.6f) * 0.09f * arms);
                    r += light * f.Tint.r; g += light * f.Tint.g; b += light * f.Tint.b;
                }
                return new Vector3(r, g, b);
            }

            public Color32[] Draw(Vector3 corner, Vector3 across, Vector3 up, int size)
            {
                var light = new Vector3[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        light[x + y * size] = At((corner + across * ((x + 0.5f) / size) + up * ((y + 0.5f) / size)).normalized);

                // ---- the stars that fall on this face: each a small soft dot with a faint glow round it
                Vector3 normal = Vector3.Cross(across, up).normalized;
                float plane = Vector3.Dot(normal, corner);
                if (plane < 0f) { normal = -normal; plane = -plane; }
                float acrossLong = across.sqrMagnitude, upLong = up.sqrMagnitude, texel = Mathf.Sqrt(acrossLong) / size / Mathf.Max(plane, 1e-4f);     // (the angle one texel covers, at the face's middle)
                foreach (Star s in stars)
                {
                    float facing = Vector3.Dot(s.At, normal);
                    if (facing < 0.3f) continue;
                    Vector3 on = s.At * (plane / facing) - corner;
                    float u = Vector3.Dot(on, across) / acrossLong, v = Vector3.Dot(on, up) / upLong;
                    if (u < -0.02f || u > 1.02f || v < -0.02f || v > 1.02f) continue;
                    float wide = Mathf.Max(s.Wide / texel, 0.6f);                              // in texels
                    int reach = Mathf.CeilToInt(wide * 5f), cx = Mathf.FloorToInt(u * size), cy = Mathf.FloorToInt(v * size);
                    for (int yy = cy - reach; yy <= cy + reach; yy++)
                    {
                        if (yy < 0 || yy >= size) continue;
                        for (int xx = cx - reach; xx <= cx + reach; xx++)
                        {
                            if (xx < 0 || xx >= size) continue;
                            float dx = xx + 0.5f - u * size, dy = yy + 0.5f - v * size, far2 = (dx * dx + dy * dy) / (wide * wide);
                            float dot = Mathf.Exp(-far2 * 1.4f) * s.Bright + Mathf.Exp(-Mathf.Sqrt(far2) * 1.5f) * 0.1f * s.Bright * s.Bright;
                            if (dot < 0.004f) continue;
                            Vector3 at = light[xx + yy * size];
                            light[xx + yy * size] = new Vector3(at.x + dot * s.Tint.r, at.y + dot * s.Tint.g, at.z + dot * s.Tint.b);
                        }
                    }
                }

                // ---- as a picture: what is added is light, and a picture is how it shows, so each is raised to 1/2.2;
                // with a little unevenness so that so faint a thing does not show in steps
                var dots = new Color32[size * size];
                uint salt = (uint)(corner.GetHashCode() * 2654435761u) | 1u;
                for (int i = 0; i < dots.Length; i++)
                {
                    salt ^= salt << 13; salt ^= salt >> 17; salt ^= salt << 5;
                    float uneven = ((salt & 1023) / 1023f - 0.5f) / 255f;
                    Vector3 l = light[i];
                    dots[i] = new Color32(Show(l.x, uneven), Show(l.y, uneven), Show(l.z, uneven), 255);
                }
                return dots;
            }

            static byte Show(float light, float uneven)
            {
                float shown = Mathf.Pow(Mathf.Max(0f, light), 1f / 2.2f) + uneven;
                return (byte)Mathf.Clamp(Mathf.RoundToInt(shown * 255f), 0, 255);
            }
        }
    }
}
