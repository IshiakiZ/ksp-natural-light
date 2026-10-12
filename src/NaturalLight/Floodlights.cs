using System;
using System.Collections.Generic;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// Launch sites lit by night.
    ///
    /// A real launch pad at night is the brightest thing for miles: the rocket stands white in a ring of
    /// floodlights. The game's pad is dark, and darker still once the night is as dark as it should be. So
    /// every place a craft can be rolled out to (the pad, the runway, and the other sites the game or its
    /// expansions have) gets four floodlights round it, which come on one after another as the sun goes down
    /// and go out at dawn. Each is a xenon searchlight on a post (FloodlightModel), there by day as well, turned
    /// to face the site; its glass glows when it is lit, and its light and a beam that can be seen in the night
    /// air come out of that glass, slanting up over the site and crossing the other three above it.
    ///
    /// Round a pad they stand on the diagonals, all four the same distance out (20 metres where the pad's top is
    /// big enough). Round a runway they stand off its surface, beside it, two each side of where the craft stands,
    /// turned in across it: never where anything is rolled out or lands. They have nothing a craft could hit (no
    /// colliders), so that one put where a craft goes after all (a site some mod has made) cannot wreck it.
    ///
    /// The lights are of the cheap kind (see the engines), so they cost next to nothing to draw. They throw no
    /// shadows: the game's shaders have none for any light but the sun.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class Floodlights : MonoBehaviour
    {
        const int Each = 4;
        const float Off = 20f;              // metres from where the craft stands, round a pad
        const float Near = 3500f;           // put up when the craft being flown is within this of the site, and lit by night

        sealed class Site
        {
            public string Name;
            public Transform At;
            public Transform Facility;      // the pad or runway it is part of (its surface is told from the ground round it by this)
            public GameObject Rig;
            public Light[] Lamps;
            public Light Haze;              // the glow of all that light in the air above the site
            public Transform[] Heads;       // each lamp's glare, a card turned to the camera
            public Renderer[] Glows;
            public Mesh[] Beams;            // each lamp's beam in the air: a strip from the lamp, turned to face the camera
            public Renderer[] BeamShows;
            public Vector3[] BeamEnds;      // where each beam ends, in its own lamp's space
            public Vector3[] Aims;          // which way each lamp points, in the rig's space
            public Renderer[] Fixtures;     // what glows in each searchlight's head (glass, reflector, lamp), lit when it is
            public Mesh[] LampMeshes;       // (each a mesh of its own, in the rig's frame)
            public Transform[] Frames;      // where each searchlight's head is: on its yoke's tilt axis, turned as it is turned (nothing is drawn from it)
            public Renderer Bodies;         // the four searchlights' bodies, joined
            public Mesh Joined;
            public float[] Shone;           // what each head's reflector and lamp were last told (how lit, and how much of the light round it shows)
            public bool Runway;             // stood beside a runway, not round a pad
            public string Placed = "";      // (how they were placed, for the development build's report)
#if DEV
            // (for the development build's report: where each head's and each post's body was joined in, in the rig's frame, and where each lamp is in the frame it is drawn from)
            public Matrix4x4[] HeadsIn = new Matrix4x4[Each], PostsIn = new Matrix4x4[Each], LampsIn = new Matrix4x4[Each];
#endif
            public float On;                // 0 dark .. 1 fully lit
        }

        readonly List<Site> sites = new List<Site>();

        /// <summary>A building of the space centre whose windows are lit, and the light they throw on the ground round it.</summary>
        sealed class Building
        {
            public string Name;
            public Transform At;
            public readonly List<Material> Lit = new List<Material>();      // its materials that glow by night
            public readonly List<KeyValuePair<Renderer, int>> Glass = new List<KeyValuePair<Renderer, int>>();  // what is drawn with them, and as which of its materials
            public Bounds Glowing;                                          // what is drawn with them
            public GameObject Rig;
            public readonly List<Light> Lamps = new List<Light>();
            public MeshRenderer Pools;                                      // the light of its windows, laid on the ground in front of them
            public TimeOfDayAnimation Clock;                                // the game's own switch for its windows
            public int Patches;
#if DEV
            public readonly List<string> Said = new List<string>();         // (each stretch as found, for the development build's report)
#endif
            public bool Found;                                              // (the windows were found on the building itself, not guessed at)
            public float On;
        }

        readonly List<Building> buildings = new List<Building>();
        float nextLook = 2f;
        int looks;
        static Material glow, shaft;
        static Mesh card;
        const float BeamLong = 150f, BeamNarrow = 0.85f, BeamWide = 30f;    // (as wide as the searchlight's glass where it starts)
        const int BeamCuts = 14;
        /// <summary>How far along the beam its i-th cut is (0 at the lamp, 1 at the end): closer together near the lamp.</summary>
        static float Cut(int i) { float f = i / (float)BeamCuts; return f * f; }
        public static string Report = "";

        /// <summary>A launch site whose floodlights are on just now: where, and how far on (with how bright they are set). For the other parts of the mod.</summary>
        public struct Shining { public Vector3 At; public float On; }
        public static readonly List<Shining> Lit = new List<Shining>();
        public static Color LampColour => Lamplight;

        void Awake()
        {
            Daylight.Setup();
            Camera.onPreCull += Face;
#if DEV
            Camera.onPreCull += EyeOn;
            Camera.onPostRender += EyeOff;
#endif
        }

        void OnDestroy()
        {
            Camera.onPreCull -= Face;
#if DEV
            Camera.onPreCull -= EyeOn;
            Camera.onPostRender -= EyeOff;
            eyeAt = null;
#endif
            foreach (Site s in sites) Unbuild(s);
            sites.Clear();
            foreach (Building b in buildings) if (b.Rig != null) Destroy(b.Rig);
            buildings.Clear();
            Lit.Clear();
            Report = "";
        }

        /// <summary>The places craft are rolled out to: found once the scene has settled, and looked for again a few times in case it had not.</summary>
        void Look()
        {
            var found = new List<Site>();
            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null) return;
            try
            {
                foreach (PSystemSetup.SpaceCenterFacility f in setup.SpaceCenterFacilityLaunchSites)
                {
                    if (f == null || f.spawnPoints == null || f.facilityTransform == null) continue;
                    foreach (PSystemSetup.SpaceCenterFacility.SpawnPoint p in f.spawnPoints)
                    {
                        // (asked for by name here, since the game's own way of asking writes a complaint to the log for every one that is not there)
                        Transform t = p != null && !string.IsNullOrEmpty(p.spawnTransformURL) ? f.facilityTransform.Find(p.spawnTransformURL) : null;
                        if (t != null && t.gameObject.activeInHierarchy) found.Add(new Site { At = t, Name = f.name, Facility = f.facilityTransform });
                    }
                }
            }
            catch (Exception) { }
            try
            {
                foreach (LaunchSite s in setup.LaunchSites)
                {
                    if (s == null || s.spawnPoints == null || s.launchSiteTransform == null) continue;
                    foreach (LaunchSite.SpawnPoint p in s.spawnPoints)
                    {
                        Transform t = p != null && !string.IsNullOrEmpty(p.spawnTransformURL) ? s.launchSiteTransform.Find(p.spawnTransformURL) : null;
                        if (t != null && t.gameObject.activeInHierarchy) found.Add(new Site { At = t, Name = s.name, Facility = s.launchSiteTransform });
                    }
                }
            }
            catch (Exception) { }
            // The buildings with windows: whatever of the space centre is drawn with a material that glows.
            try
            {
                if (setup.SpaceCenterFacilities != null)
                    foreach (PSystemSetup.SpaceCenterFacility f in setup.SpaceCenterFacilities)
                    {
                        if (f == null || f.facilityTransform == null || buildings.Exists(b => b.At == f.facilityTransform)) continue;
                        var one = new Building { Name = f.name, At = f.facilityTransform };
                        bool any = false;
                        foreach (Renderer r in f.facilityTransform.GetComponentsInChildren<Renderer>(false))
                        {
                            if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                            Material[] ms = r.sharedMaterials;
                            for (int i = 0; i < ms.Length; i++)
                            {
                                Material m = ms[i];
                                if (m == null || m.shader == null || !m.shader.name.Contains("Emissive") || !m.HasProperty("_EmissiveColor")) continue;
                                // (the runway's and the pad's own lamps are drawn the same way, and are not windows)
                                Texture where = m.HasProperty("_Emissive") ? m.GetTexture("_Emissive") : null;
                                if (where != null && where.name.Contains("runway")) continue;
                                if (!one.Lit.Contains(m)) one.Lit.Add(m);
                                one.Glass.Add(new KeyValuePair<Renderer, int>(r, i));
                                if (!any) { one.Glowing = r.bounds; any = true; } else one.Glowing.Encapsulate(r.bounds);
                            }
                        }
                        one.Clock = ClockOf(f.facilityTransform);
                        if (any && f.name != "Runway" && f.name != "LaunchPad") buildings.Add(one);
                    }
            }
            catch (Exception) { }
            foreach (Site f in found)
            {
                bool have = false;
                // (several ways out onto the same pad or runway are one site)
                foreach (Site s in sites) if (s.At == f.At || (s.At != null && (s.At.position - f.At.position).sqrMagnitude < 120f * 120f)) { have = true; break; }
                if (!have) sites.Add(f);
            }
        }

        static Material Glow()
        {
            if (glow != null) return glow;
            Shader shader = Shader.Find("KSP/Particles/Additive") ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive");
            if (shader == null) return null;
            // A lamp seen from in front: a white-hot middle and a soft glare round it.
            const int N = 64;
            var dot = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            var dots = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
                    float middle = Mathf.Clamp01(1f - r / 0.16f), glare = Mathf.Pow(Mathf.Clamp01(1f - r), 3f) * 0.4f;
                    // (and a faint streak each way, as a bright lamp has to the eye)
                    float streak = Mathf.Clamp01(1f - r) * 0.35f * (Mathf.Exp(-dy * dy * 900f) + 0.5f * Mathf.Exp(-dx * dx * 900f));
                    byte v = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(middle * middle * 1.5f + glare + streak));
                    dots[x + y * N] = new Color32(v, v, v, v);
                }
            dot.SetPixels32(dots); dot.Apply(true, true);
            glow = new Material(shader) { mainTexture = dot };
            return glow;
        }

        /// <summary>A beam seen from the side: brightest along its middle and at the lamp, thinning out to nothing at its far end.</summary>
        static Material Shaft()
        {
            if (shaft != null) return shaft;
            Shader shader = Shader.Find("KSP/Particles/Additive") ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive");
            if (shader == null) return null;
            const int W = 32, H = 128;
            var strip = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            var dots = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float across = ((x + 0.5f) / W - 0.5f) / 0.2f, along = (y + 0.5f) / H;
                    float v = Mathf.Exp(-across * across) * Mathf.Pow(1f - along, 1.5f) * (0.22f + 0.78f * Mathf.Exp(-5f * along));
                    // (and it does not start at full strength in the lamp itself, which has its own glare)
                    v *= Mathf.Clamp01(along / 0.02f);
                    byte b = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(v));
                    dots[x + y * W] = new Color32(b, b, b, b);
                }
            strip.SetPixels32(dots); strip.Apply(true, true);
            shaft = new Material(shader) { mainTexture = strip };
            return shaft;
        }

        static Mesh Card()
        {
            if (card != null) return card;
            card = new Mesh
            {
                vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) },
                uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) },
                triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }
            };
            card.RecalculateBounds();
            return card;
        }

        static readonly Color Lamplight = new Color(0.86f, 0.93f, 1f);         // the cold white of a discharge lamp

        void Build(Site site, CelestialBody body)
        {
            Vector3 centre = site.At.position;
            Vector3 up = (centre - (Vector3)body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane((Vector3)body.transform.up, up).normalized, east = Vector3.Cross(up, north);
            site.Rig = new GameObject("Natural Light floodlights at " + site.Name);
            site.Rig.transform.SetParent(site.At, false);
            Transform rig = site.Rig.transform;
            site.Lamps = new Light[Each]; site.Heads = new Transform[Each]; site.Glows = new Renderer[Each];
            site.Beams = new Mesh[Each]; site.BeamShows = new Renderer[Each]; site.BeamEnds = new Vector3[Each];
            site.Aims = new Vector3[Each]; site.Fixtures = new Renderer[Each]; site.Shone = new float[Each];
            site.Frames = new Transform[Each]; site.LampMeshes = new Mesh[Each];
            for (int k = 0; k < Each; k++) site.Shone[k] = -1f;
            Material lampGlow = Glow(), lampShaft = Shaft();
            bool drawn = FloodlightModel.Ready(out string why);
            Vector3[] feet = Spots(site, centre, up, north, east, out Vector3[] targets);
            if (!drawn) site.Placed += "; no fixtures: " + why;
            var bodies = new List<CombineInstance>();
            for (int k = 0; k < Each; k++)
            {
                // (where it stands, and where its tilt axis is: in the fixture's yoke, or, if it cannot be drawn, a lamp low on the ground
                // as before 0.9.1)
                Vector3 foot = feet[k], at = foot + up * (drawn ? FloodlightModel.Head.Position.y * Size : 0.9f);
                Vector3 aim = (targets[k] - at).normalized;
                Vector3 glassIn = rig.InverseTransformPoint(at);        // (where the light comes from, in the rig's frame)
                if (drawn)
                {
                    // The searchlight: its post turned to face where it points, its head tilted up on the yoke to point there. Two
                    // empty frames say where: the post's in the rig's, and the head's in the post's. Nothing is drawn from them.
                    var post = new GameObject("floodlight " + k);
                    post.transform.position = foot;
                    post.transform.localScale = Vector3.one * Size;
                    post.transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(aim, up).normalized, up);
                    post.transform.SetParent(rig, true);
                    var head = new GameObject("head");
                    head.transform.SetParent(post.transform, false);
                    head.transform.localPosition = FloodlightModel.Head.Position;
                    head.transform.localRotation = Quaternion.Euler(-Mathf.Asin(Mathf.Clamp(Vector3.Dot(aim, up), -1f, 1f)) * Mathf.Rad2Deg, 0f, 0f);
                    site.Frames[k] = head.transform;
                    // Every piece of it is drawn in the rig's frame and from the rig's own place: its body joined to the others' (below),
                    // and what glows in its head as a mesh of its own (lit on its own, and throwing no shadow). Where each piece is in
                    // that frame comes from the frames' own small numbers, the head's in the post's and the post's in the rig's.
                    // (Never from where the game says a frame is in the world, and nothing drawn from a frame of its own. The space
                    // centre hangs from a point 600 km from the middle of its planet; the game's numbers are no finer than a few
                    // centimetres that far out, and what it says of a frame there does not add up. Measured on the pad: the rig's way
                    // into the world and its way back out of it (its two matrices) 12.5 cm apart, nearly all of it up and down, and a
                    // head's place against its post's, each worked out on its own, 3 to 9 cm; differently in another load of the
                    // game. Until 0.10.3 each body was joined in through both of the rig's matrices, 15 cm and more too high, and
                    // each lamp was drawn from its head's own frame: the glass, the reflector and the lamp hung 12.5 cm below their
                    // housing.)
                    Matrix4x4 postIn = Matrix4x4.TRS(post.transform.localPosition, post.transform.localRotation, post.transform.localScale);
                    Matrix4x4 headIn = postIn * Matrix4x4.TRS(head.transform.localPosition, head.transform.localRotation, Vector3.one);
                    Transform from = rig;
                    Matrix4x4 lampIn = headIn;
#if DEV
                    if (asBefore)
                    {
                        // (as until 0.10.2, for pictures of before and after: each piece's place asked of the world, and the lamp drawn
                        // from the head's own frame)
                        Matrix4x4 into = rig.worldToLocalMatrix;
                        postIn = into * post.transform.localToWorldMatrix; headIn = into * head.transform.localToWorldMatrix;
                        from = head.transform; lampIn = Matrix4x4.identity;
                    }
                    site.HeadsIn[k] = headIn; site.PostsIn[k] = postIn; site.LampsIn[k] = lampIn;
#endif
                    bodies.Add(new CombineInstance { mesh = FloodlightModel.Stand.Body, transform = postIn });
                    bodies.Add(new CombineInstance { mesh = FloodlightModel.Head.Body, transform = headIn });
                    var lit = new GameObject("searchlight lamp " + k);
                    lit.layer = 15;
                    lit.transform.SetParent(from, false);
                    site.LampMeshes[k] = FloodlightModel.LampAt(lampIn, "Natural Light searchlight lamp at " + site.Name);
                    lit.AddComponent<MeshFilter>().sharedMesh = site.LampMeshes[k];
                    MeshRenderer lamps = lit.AddComponent<MeshRenderer>();
                    lamps.sharedMaterials = FloodlightModel.Head.LampMaterials;
                    lamps.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lamps.receiveShadows = true;
                    site.Fixtures[k] = lamps;
                    // (gone when it is too small to see: a pixel or so. Reckoned by the whole head's size, 0.94 m across as it is made.)
                    LODGroup small = lit.AddComponent<LODGroup>();
                    small.SetLODs(new[] { new LOD(0.0035f, new Renderer[] { lamps }) });
                    small.RecalculateBounds();
                    small.size = 0.94f * (from == rig ? post.transform.localScale.x : 1f);
                    // (the light and the beam come out of its glass, where that is drawn)
                    glassIn = headIn.MultiplyPoint3x4(FloodlightModel.Glass);
                    at = rig.TransformPoint(glassIn);
                    aim = rig.TransformDirection(headIn.MultiplyVector(Vector3.forward)).normalized;
                }
                site.Aims[k] = rig.InverseTransformDirection(aim);
                // (the lamp's light, its glare and its beam, at the glass by the rig's own numbers. They are drawn from this place of
                // their own all the same, so they may sit some centimetres off the glass: soft things, on which that does not show.)
                var holder = new GameObject("floodlight lamp " + k);
                holder.transform.SetParent(rig, false);
                holder.transform.localPosition = glassIn;
                // (the light itself a little out in front of the glass, so that it lights what the lamp points at and not its own head)
                var bulb = new GameObject("light");
                bulb.transform.SetParent(holder.transform, true);
                bulb.transform.position = at + aim * (drawn ? 1.2f : 0f);
                Light lamp = bulb.AddComponent<Light>();
                lamp.type = LightType.Point;
                lamp.shadows = LightShadows.None;
                lamp.renderMode = LightRenderMode.ForceVertex;
                lamp.cullingMask = Daylight.Lit;
                lamp.color = Lamplight;
                lamp.range = 120f;
                lamp.intensity = 0f;
                lamp.enabled = false;
                site.Lamps[k] = lamp;
                if (lampGlow != null)
                {
                    var head = new GameObject("lamp");
                    head.layer = 15;
                    head.transform.SetParent(holder.transform, false);
                    head.AddComponent<MeshFilter>().sharedMesh = Card();
                    MeshRenderer r = head.AddComponent<MeshRenderer>();
                    r.sharedMaterial = lampGlow;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    r.enabled = false;
                    site.Heads[k] = head.transform; site.Glows[k] = r;
                }
                if (lampShaft != null)
                {
                    // The beam: aimed over the middle of the site at about the height of a rocket's shoulder, and on up into the sky.
                    var beam = new GameObject("beam");
                    beam.layer = 15;
                    beam.transform.SetParent(holder.transform, false);
                    var mesh = new Mesh { name = "Natural Light floodlight beam" };
                    mesh.MarkDynamic();
                    // (a strip that widens cannot be two triangles: the picture on it would be skewed across each. It is cut into
                    // short lengths, with a row of corners down its middle, so that its brightest line stays in the middle.)
                    var uv = new Vector2[(BeamCuts + 1) * 3];
                    var tris = new int[BeamCuts * 24];
                    for (int i = 0; i <= BeamCuts; i++)
                        for (int c = 0; c < 3; c++) uv[i * 3 + c] = new Vector2(c * 0.5f, Cut(i));
                    for (int i = 0, t = 0; i < BeamCuts; i++)
                        for (int c = 0; c < 2; c++)
                        {
                            int a = i * 3 + c, b = a + 1, d = a + 3, e = a + 4;
                            tris[t++] = a; tris[t++] = d; tris[t++] = b; tris[t++] = b; tris[t++] = d; tris[t++] = e;
                            tris[t++] = a; tris[t++] = b; tris[t++] = d; tris[t++] = b; tris[t++] = e; tris[t++] = d;
                        }
                    mesh.vertices = new Vector3[(BeamCuts + 1) * 3];
                    mesh.uv = uv;
                    mesh.triangles = tris;
                    beam.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer r = beam.AddComponent<MeshRenderer>();
                    r.sharedMaterial = lampShaft;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    r.enabled = false;
                    site.Beams[k] = mesh; site.BeamShows[k] = r;
                    site.BeamEnds[k] = beam.transform.InverseTransformPoint(at + aim * BeamLong);
                }
            }
            if (bodies.Count > 0)
            {
                // The searchlights' bodies, all four in one mesh, drawn with their shadows at once: a draw for each (and its shadow, four
                // more) cost half a thousandth of a second a frame on a Mac, more than all the rest of the floodlights put together.
                var joined = new Mesh { name = "Natural Light searchlights at " + site.Name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                joined.CombineMeshes(bodies.ToArray(), true, true);
                joined.RecalculateBounds();
                joined.UploadMeshData(true);
                site.Joined = joined;
                var all = new GameObject("searchlights");
                all.layer = 15;
                all.transform.SetParent(site.Rig.transform, false);
                all.AddComponent<MeshFilter>().sharedMesh = joined;
                MeshRenderer r = all.AddComponent<MeshRenderer>();
                r.sharedMaterial = FloodlightModel.Paint;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.receiveShadows = true;
                site.Bodies = r;
                // (gone when they are too small to see)
                LODGroup lod = all.AddComponent<LODGroup>();
                lod.SetLODs(new[] { new LOD(0.002f, new Renderer[] { r }) });
                lod.RecalculateBounds();
            }
            // Four lamps in damp night air light the air too, and the lit ground lights whatever stands round it: the site is an
            // island of light in the dark, not four separate pools. That is one more light, weak and wide, well above the middle.
            var over = new GameObject("glow over the site");
            over.transform.SetParent(site.Rig.transform, true);
            over.transform.position = centre + up * 34f;
            site.Haze = over.AddComponent<Light>();
            site.Haze.type = LightType.Point;
            site.Haze.shadows = LightShadows.None;
            site.Haze.renderMode = LightRenderMode.ForceVertex;
            site.Haze.cullingMask = Daylight.Lit;
            site.Haze.color = Color.Lerp(Lamplight, Color.white, 0.5f);
            site.Haze.range = 260f;
            site.Haze.enabled = false;
        }

        static void Unbuild(Site site)
        {
            if (site.Rig != null) Destroy(site.Rig);
            if (site.Joined != null) Destroy(site.Joined);
            if (site.LampMeshes != null) foreach (Mesh m in site.LampMeshes) if (m != null) Destroy(m);
            site.Rig = null; site.Joined = null; site.Bodies = null; site.LampMeshes = null;
        }

        /// <summary>The searchlight drawn a quarter as big again as the model is made (a head a metre across, three metres to its top): a
        /// big xenon unit of the kind banked round a pad, and one that can be made out from where the game's camera usually is.</summary>
        const float Size = 1.25f;
        const float Clear = 4f;            // metres between the edge of a runway's surface (its shoulders included) and the floodlights beside it

        /// <summary>
        /// Where each of a site's floodlights stands, on the ground, and what it is turned towards. Round a pad: on the diagonals,
        /// up to 20 metres out, turned up at about the height of a rocket's shoulder. Beside a runway (a site whose surface runs
        /// far one way and not the other): off its surface, 4 metres out from each edge, two each side, 14 metres either way along
        /// it from where the craft stands, turned in across it a little above the craft. The four are placed together, so that
        /// they stand alike (Square, Beside); only where that cannot be done is each placed on its own, moved to the nearest spot
        /// that will do (Footing).
        /// </summary>
        Vector3[] Spots(Site site, Vector3 centre, Vector3 up, Vector3 north, Vector3 east, out Vector3[] targets)
        {
            var feet = new Vector3[Each];
            targets = new Vector3[Each];
            float level = Vector3.Dot(centre, up);
            if (Physics.Raycast(centre + up * 40f, -up, out RaycastHit under, 120f, 1 << 15, QueryTriggerInteraction.Ignore)) level = Vector3.Dot(under.point, up);
            var said = new System.Text.StringBuilder();
            site.Runway = Strip(site.Facility, centre, up, north, east, level, out Vector3 along, out float left, out float right, said);
            Vector3 across = Vector3.Cross(up, along);
            for (int k = 0; k < Each; k++) targets[k] = centre + up * (site.Runway ? 10f : 24f);
            bool together = site.Runway ? Beside(site, centre, up, level, along, across, left, right, feet, said) : Square(site, centre, up, north, east, level, feet, said);
            for (int k = 0; k < Each && !together; k++)
            {
                Vector3 want = site.Runway ? Aside(k, centre, along, across, left, right, 0f) : centre + Diagonal(k, 0f, north, east) * Off;
                feet[k] = Footing(site, want, up, level, Vector3.ProjectOnPlane(centre - want, up).normalized, said, k);
            }
            site.Placed = (site.Runway ? "beside a runway: " : "round a pad: ") + said;
            return feet;
        }

        const float Least = 12f;            // metres from where the craft stands: no nearer than this, round a pad
        static readonly float[] Turns = { 0f, 15f, -15f, 30f, -30f, 45f };

        /// <summary>The way out from where the craft stands to floodlight k round a pad: along the compass's diagonals, turned `turn` degrees.</summary>
        static Vector3 Diagonal(int k, float turn, Vector3 north, Vector3 east)
        {
            float a = (45f + turn + 90f * k) * Mathf.Deg2Rad;
            return north * Mathf.Cos(a) + east * Mathf.Sin(a);
        }

        /// <summary>
        /// Round a pad the four stand alike: at the corners of one square about where the craft stands, each the same distance out.
        /// The widest square on the compass's diagonals, from 20 metres out down to 12, whose four corners will all do (Will) is taken;
        /// if none on the diagonals will, one turned 15, 30 or 45 degrees from them. False if no square will do at all.
        /// (Until 0.10.3 each of the four was moved on its own to the nearest spot that would do. The top of the space centre's pad
        /// is too small for 20 metres out: its two eastern lamps came in to 12 metres, by the corners of the grate, and its two
        /// western ones went four metres aside, onto the pad's shoulders, 17 and 20 metres out. Now all four stand 12 metres
        /// out there, at the corners of the square painted round the grate: 13 is too near the edge of the pad's top.)
        /// </summary>
        static bool Square(Site site, Vector3 centre, Vector3 up, Vector3 north, Vector3 east, float level, Vector3[] feet, System.Text.StringBuilder said)
        {
#if DEV
            if (asBefore) return false;
#endif
            foreach (float turn in Turns)
                for (float off = Off; off >= Least; off -= 1f)
                {
                    bool all = true;
                    for (int k = 0; k < Each && all; k++)
                    {
                        Vector3 way = Diagonal(k, turn, north, east);
                        all = Will(site, centre + way * off, up, level, -way, out feet[k]);
                    }
                    if (!all) continue;
                    said.Append("all four " + off.ToString("F0") + " m out" + (turn != 0f ? ", the square turned " + turn.ToString("F0") + " degrees" : "") + "; ");
                    return true;
                }
            return false;
        }

        /// <summary>Where floodlight k is wanted beside a runway: 14 metres one way or the other along it from where the craft stands, 4 metres and `more` out from the edge of its surface on one side or the other.</summary>
        static Vector3 Aside(int k, Vector3 centre, Vector3 along, Vector3 across, float left, float right, float more)
        {
            float side = (k & 1) == 0 ? -1f : 1f, fore = (k & 2) == 0 ? -1f : 1f;
            return centre + along * (14f * fore) + across * (side > 0 ? right + Clear + more : -(left + Clear + more));
        }

        /// <summary>
        /// Beside a runway the four stand alike too: each side's two the same distance out from its edge, 4 metres if all four can
        /// stand so, else as much further out (by twos, up to 12 metres more) as lets all four. False if they cannot.
        /// (Until 0.10.3 each was moved on its own: beside the space centre's runway one stood 6 metres from its place and two 2 metres.)
        /// </summary>
        static bool Beside(Site site, Vector3 centre, Vector3 up, float level, Vector3 along, Vector3 across, float left, float right, Vector3[] feet, System.Text.StringBuilder said)
        {
#if DEV
            if (asBefore) return false;
#endif
            for (float more = 0f; more <= 12f; more += 2f)
            {
                bool all = true;
                for (int k = 0; k < Each && all; k++)
                {
                    Vector3 want = Aside(k, centre, along, across, left, right, more);
                    all = Will(site, want, up, level, Vector3.ProjectOnPlane(centre - want, up).normalized, out feet[k]);
                }
                if (!all) continue;
                if (more > 0f) said.Append("all four " + more.ToString("F0") + " m further out; ");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a site is a runway: its surface (whatever is part of the site, round where the craft stands) reaching far one way
        /// and not the other. If it is, which way it runs, and how far its surface reaches to the right and the left of where the craft
        /// stands.
        /// </summary>
        static bool Strip(Transform facility, Vector3 centre, Vector3 up, Vector3 north, Vector3 east, float level, out Vector3 along, out float left, out float right, System.Text.StringBuilder said)
        {
            left = right = 0f;
            float best = 0f, bearing = 0f;
            Func<float, Vector3> way = deg => north * Mathf.Cos(deg * Mathf.Deg2Rad) + east * Mathf.Sin(deg * Mathf.Deg2Rad);
            Func<float, float> reach = deg => Reach(facility, centre, way(deg), up, level, 320f) + Reach(facility, centre, -way(deg), up, level, 320f);
            for (int d = 0; d < 36; d++)                // (every 5 degrees, both ways at once)
            {
                float r = reach(d * 5f);
                if (r > best) { best = r; bearing = d * 5f; }
            }
            // A long strip reaches as far a few degrees to either side of the way it runs as straight along it, the more so as a
            // reach is cut short at 320 metres: the way it runs is the middle of the ways that reach as far, found to a degree.
            // (Until 0.10.3 the first of them was taken: 10 degrees off at the space centre's runway, whose four floodlights
            // stood askew to it, one side's pair 20 metres further along than the other's.)
            bool fine = best >= 200f;
#if DEV
            fine &= !asBefore;
#endif
            if (fine)
            {
                float lo = bearing, hi = bearing;
                while (hi - bearing < 30f && reach(hi + 1f) >= best - 4f) hi += 1f;
                while (bearing - lo < 30f && reach(lo - 1f) >= best - 4f) lo -= 1f;
                bearing = 0.5f * (lo + hi);
            }
            along = way(bearing);
            Vector3 across = Vector3.Cross(up, along);
            right = Reach(facility, centre, across, up, level, 160f);
            left = Reach(facility, centre, -across, up, level, 160f);
            said.Append("its surface " + best.ToString("F0") + " m along (bearing " + bearing.ToString("F0") + "), " + left.ToString("F0") + " + " + right.ToString("F0") + " m across; ");
            return best >= 200f && left + right <= 120f && best >= 3f * (left + right);
        }

        /// <summary>How far out from `from`, this way, the ground is still the site's own surface (in steps of 4 metres, up to `most`).</summary>
        static float Reach(Transform facility, Vector3 from, Vector3 way, Vector3 up, float level, float most)
        {
            float d = 0f;
            for (float s = 4f; s <= most; s += 4f)
            {
                if (!Physics.Raycast(from + way * s + up * 30f, -up, out RaycastHit hit, 80f, 1 << 15, QueryTriggerInteraction.Ignore)) break;
                if (!OwnSurface(hit, facility) || Mathf.Abs(Vector3.Dot(hit.point, up) - level) > 4f) break;
                d = s;
            }
            return d;
        }

        /// <summary>Whether what a ray has hit is the site's own surface: part of the site, or (where a site's surface is not part of it) a runway by its name.</summary>
        static bool OwnSurface(RaycastHit hit, Transform facility)
        {
            if (hit.collider == null) return false;
            Transform t = hit.collider.transform;
            if (facility != null && t.IsChildOf(facility)) return true;
            for (int i = 0; i < 4 && t != null; i++, t = t.parent)
                if (t.name.IndexOf("runway", StringComparison.OrdinalIgnoreCase) >= 0 || t.name.IndexOf("airstrip", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Where a floodlight wanted at `want` stands: on the ground there, if that will do (see Will), else at the nearest place
        /// round it that will, out to 12 metres; else at `want` as it is.
        /// </summary>
        static Vector3 Footing(Site site, Vector3 want, Vector3 up, float level, Vector3 facing, System.Text.StringBuilder said, int k)
        {
            Vector3 side = Vector3.Cross(up, facing);
            for (int ring = 0; ring <= 6; ring++)
                for (int i = 0, around = ring == 0 ? 1 : 8; i < around; i++)
                {
                    float a = i * Mathf.PI / 4f;
                    Vector3 p = want + (facing * Mathf.Cos(a) + side * Mathf.Sin(a)) * (2f * ring);
                    if (!Will(site, p, up, level, facing, out Vector3 foot)) continue;
                    if (ring > 0) said.Append("lamp " + k + " moved " + (2 * ring) + " m; ");
                    return foot;
                }
            said.Append("lamp " + k + " has no good place; ");
            return Physics.Raycast(want + up * 40f, -up, out RaycastHit hit, 120f, 1 << 15, QueryTriggerInteraction.Ignore) ? hit.point : want;
        }

        /// <summary>
        /// Whether a floodlight can stand here: on ground that is level enough, at about the height of the ground the craft stands on
        /// (not on a roof, nor down in a pit), not on a runway's own surface, and with room for the whole fixture (nothing standing
        /// where its post, its yoke and its head would be).
        /// </summary>
        static bool Will(Site site, Vector3 p, Vector3 up, float level, Vector3 facing, out Vector3 foot) => WhyNot(site, p, up, level, facing, out foot) == 0;

        /// <summary>Why a floodlight cannot stand here (0: it can): 1 no ground, 2 too high, too low or sloping, 3 a runway's own surface, 4 not level all round (an edge), 5 something in its way.</summary>
        static int WhyNot(Site site, Vector3 p, Vector3 up, float level, Vector3 facing, out Vector3 foot)
        {
            foot = p;
            if (!Physics.Raycast(p + up * 40f, -up, out RaycastHit hit, 120f, 1 << 15, QueryTriggerInteraction.Ignore)) return 1;
            foot = hit.point;
            // (on level ground: its footing is level, and on a slope one side of it would stand in the air)
            if (Mathf.Abs(Vector3.Dot(hit.point, up) - level) > 6f || Vector3.Dot(hit.normal, up) < 0.985f) return 2;
            if (site.Runway && OwnSurface(hit, site.Facility)) return 3;
            // (and level all round it, three and a half metres out: not at an edge. A pad's colliders are not quite its shape, and
            // reach a little past its corners: there one stood in the air over the pad's sloping side.)
            Vector3 side = Vector3.Cross(up, facing);
            float h = Vector3.Dot(hit.point, up);
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI / 4f;
                Vector3 q = hit.point + (facing * Mathf.Cos(a) + side * Mathf.Sin(a)) * 3.5f;
                if (!Physics.Raycast(q + up * 3f, -up, out RaycastHit round, 6f, 1 << 15, QueryTriggerInteraction.Ignore) || Mathf.Abs(Vector3.Dot(round.point, up) - h) > 0.35f) return 4;
            }
            return Physics.CheckBox(hit.point + up * 1.55f * Size, new Vector3(0.7f, 1.05f, 0.7f) * Size, Quaternion.LookRotation(facing, up), 1 << 15, QueryTriggerInteraction.Ignore) ? 5 : 0;
        }

        static readonly MaterialPropertyBlock shine = new MaterialPropertyBlock();

        /// <summary>
        /// A searchlight's reflector and its lamp glowing this brightly (0 dark). And, lit or not, its polished reflector showing the
        /// light all round, as a mirror shows the sky and the ground: the game's shaders reflect nothing, and without it a searchlight
        /// by day looks dead behind its glass. (Told again only when either has changed.)
        /// </summary>
        static void Shine(Site site, int k, float amount)
        {
            Renderer r = site.Fixtures != null ? site.Fixtures[k] : null;
            if (r == null || FloodlightModel.Head == null) return;
            Color around = Daylight.Around;
            float most = Mathf.Max(around.r, Mathf.Max(around.g, around.b));
            float seen = Mathf.Min(0.8f * most, 0.45f);
            float key = 1000f * amount + seen;
            if (Mathf.Abs(site.Shone[k] - key) < 0.005f) return;
            site.Shone[k] = key;
            Color mirror = most > 1e-4f ? around * (seen / most) : Color.black;
            foreach (KeyValuePair<int, float> lit in FloodlightModel.Head.Lit)
            {
                shine.Clear();
                float v = 2.2f * amount * lit.Value;
                Color c = new Color(Lamplight.r * v, Lamplight.g * v, Lamplight.b * v, 1f);
                if (lit.Value <= 1f) c += new Color(mirror.r, mirror.g, mirror.b, 0f);       // (the reflector, not the lamp)
                shine.SetColor("_EmissiveColor", c);
                r.SetPropertyBlock(shine, lit.Key);
            }
        }

        static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        static readonly Vector3[] beamCorners = new Vector3[(BeamCuts + 1) * 3];

        void LateUpdate()
        {
            // (put up near whatever is being lit, the craft in flight or the space centre seen from its own grounds, by day too; and
            // lit by night)
            bool present = Daylight.Near && Daylight.Body != null && Daylight.On && Daylight.Floods != null && Daylight.Floods;
            bool wanted = present && !MapView.MapIsEnabled;
            Vector3 here = Daylight.Here;
            if (present && looks < 6 && Time.timeSinceLevelLoad > nextLook)
            {
                nextLook = Time.timeSinceLevelLoad + 5f; looks++;
                Look();
            }
            // Dark enough: they come on as the sun reaches the horizon and are all on a little after it has gone.
            float dark = wanted ? Mathf.InverseLerp(0.05f, -0.035f, Daylight.SunUp) : 0f;
            float strength = Daylight.FloodLight != null ? (float)Daylight.FloodLight : 1f;
            int lit = 0;
            Lit.Clear();
            foreach (Site site in sites)
            {
                if (site.At == null) { Unbuild(site); continue; }
                bool near = present && (site.At.position - here).sqrMagnitude < Near * Near;
                if (!near)
                {
                    Unbuild(site);
                    site.On = 0f;
                    continue;
                }
                if (site.Rig == null) Build(site, Daylight.Body);
                // (their shadows only while the sun is up: by night there is no shadow of the sun's to throw, and drawing one costs)
                if (site.Bodies != null)
                {
                    var throws = Daylight.SunUp > -0.06f ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                    if (site.Bodies.shadowCastingMode != throws) site.Bodies.shadowCastingMode = throws;
                }
                // (slowly, as dusk comes; at once when it is a switch that has been thrown, or the craft has gone)
                site.On = wanted ? Mathf.MoveTowards(site.On, dark, Mathf.Min(Time.unscaledDeltaTime, 0.1f) * 0.45f) : 0f;
                if (site.On <= 0f)
                {
                    for (int k = 0; k < Each; k++)
                    {
                        site.Lamps[k].enabled = false;
                        if (site.Glows[k] != null) site.Glows[k].enabled = false;
                        if (site.BeamShows[k] != null) site.BeamShows[k].enabled = false;
                        Shine(site, k, 0f);
                    }
                    site.Haze.enabled = false;
                    continue;
                }
                lit++;
                float all = 0f;
                for (int k = 0; k < Each; k++)
                {
                    // (one after another, each with the stutter of a lamp striking)
                    float mine = Mathf.Clamp01((site.On - 0.16f * k) / 0.45f);
                    if (mine > 0f && mine < 0.5f) mine *= Mathf.PerlinNoise(Time.unscaledTime * 23f, 4.1f * k) > 0.45f ? 1f : 0.15f;
                    Light lamp = site.Lamps[k];
                    lamp.enabled = mine > 0f;
                    lamp.intensity = 1.1f * strength * mine;
                    all += mine / Each;
                    Shine(site, k, mine * Mathf.Sqrt(strength));
                    Renderer g = site.Glows[k];
                    if (g != null)
                    {
                        g.enabled = mine > 0f;
                        block.SetColor("_TintColor", new Color(Lamplight.r * 0.5f, Lamplight.g * 0.5f, Lamplight.b * 0.5f, 0.5f) * Mathf.Min(1.6f, 0.4f + mine * (0.7f + 0.5f * strength)));
                        g.SetPropertyBlock(block);
                    }
                    Renderer b = site.BeamShows[k];
                    if (b != null)
                    {
                        float beams = Daylight.Beams != null ? (float)Daylight.Beams : 1f;
                        b.enabled = mine > 0f && beams > 0.01f;
                        float faint = 0.085f * beams * mine * Mathf.Sqrt(strength);
                        block.SetColor("_TintColor", new Color(Lamplight.r * faint, Lamplight.g * faint, Lamplight.b * faint, 0.5f));
                        b.SetPropertyBlock(block);
                    }
                }
                Lit.Add(new Shining { At = site.At.position, On = all * strength });
                site.Haze.enabled = all > 0f;
                site.Haze.intensity = 0.45f * strength * all;
            }
            // ---- the windows of the buildings
            int glowing = 0;
            bool spill = Daylight.Near && Daylight.Body != null && Daylight.On && Daylight.Windows != null && Daylight.Windows && !MapView.MapIsEnabled;
            float bright = Daylight.WindowLight != null ? (float)Daylight.WindowLight : 1f;
            // (where the game's own switch for the windows cannot be found: by the sun, as the floodlights go)
            float dusk = Mathf.InverseLerp(0.03f, -0.02f, Daylight.SunUp);
            foreach (Building b in buildings)
            {
                if (b.At == null) { if (b.Rig != null) Destroy(b.Rig); b.Rig = null; continue; }
                // How brightly its windows are lit just now. The game lights them at dusk and puts them out at dawn, not in their
                // materials (whose colour never changes: reading that, as this did until 0.8.0, had the window light on by day too)
                // but by its time-of-day animation, which hands each window a colour whose alpha is how far it is lit.
                bool near = spill && (b.At.position - here).sqrMagnitude < 6000f * 6000f;
                float want = near ? (b.Clock != null ? Mathf.Clamp01(b.Clock.emissiveColor.a) : dusk) : 0f;
                b.On = near ? Mathf.MoveTowards(b.On, want, Mathf.Min(Time.unscaledDeltaTime, 0.1f) * 0.6f) : 0f;
                if (b.On <= 0.01f)
                {
                    if (b.Rig != null && !near) { Destroy(b.Rig); b.Rig = null; b.Lamps.Clear(); b.Pools = null; }
                    else
                    {
                        foreach (Light l in b.Lamps) if (l != null) l.enabled = false;
                        if (b.Pools != null) b.Pools.enabled = false;
                    }
                    continue;
                }
                if (b.Rig == null) Spill(b, Daylight.Body);
                glowing++;
                foreach (Light l in b.Lamps)
                {
                    if (l == null) continue;
                    l.enabled = true;
                    l.color = WindowLamp;
                    l.intensity = 0.8f * bright * b.On;
                }
                if (b.Pools != null)
                {
                    b.Pools.enabled = true;
                    float k = WindowPool * bright * b.On;
                    block.Clear();
                    block.SetColor("_TintColor", new Color(WindowLamp.r * k, WindowLamp.g * k, WindowLamp.b * k, 0.5f));
                    b.Pools.SetPropertyBlock(block);
                }
            }
            Report = (lit > 0 ? "Floodlights on at " + lit + (lit == 1 ? " launch site." : " launch sites.") : "") + (glowing > 0 ? (lit > 0 ? "  " : "") + "Windows lit in " + glowing + (glowing == 1 ? " building." : " buildings.") : "");
        }

        /// <summary>The warm white of lit rooms as it falls on the pavement outside them.</summary>
        static readonly Color WindowLamp = new Color(1f, 0.86f, 0.66f);
        const float WindowPool = 0.34f;

        /// <summary>The game's own switch for a place's glowing things: the time-of-day animation on it or on something it is part of.</summary>
        static TimeOfDayAnimation ClockOf(Transform at)
        {
            for (Transform t = at; t != null; t = t.parent)
            {
                TimeOfDayAnimation a = t.GetComponent<TimeOfDayAnimation>();
                if (a != null) return a;
            }
            return null;
        }

        /// <summary>A stretch of a building's wall with lit windows in it, all facing one way: where its foot is, which way it faces and runs, how wide and how high its windows are, how much of it is glass.</summary>
        struct Patch
        {
            public Vector3 Foot, Facing, Along;     // the middle of the wall's foot (world), out from the wall, along it
            public float Half, Height, Glass;       // half the width of its windows, how high their middle is above the ground, square metres lit
        }

        /// <summary>
        /// The light a building's lit windows throw on the ground in front of them.
        ///
        /// Where the windows are is found on the building itself: its meshes, looked up in the pictures that say where they glow
        /// (GlowMap). The glowing bits are sorted by the way they face, then by how far out their wall stands, then along the wall,
        /// into stretches of lit wall; and in front of each stretch light is laid on the ground (Pools): as wide as its windows and a
        /// little wider, brightest a step out from the wall's foot and fading outward, the further the higher the windows are, and
        /// the brighter the more of the wall is glass. The building's own wall is not lit (a window does not light the wall it is
        /// in), and a stretch whose windows face a wall close in front of them throws nothing.
        ///
        /// A few lamps of the cheap kind stand in front of the brightest stretches, for what is standing there: a craft, a kerbal,
        /// a truck. Not for the ground and the walls, which are a few great flat pieces that such a lamp only smears.
        ///
        /// (Where the building's meshes cannot be read, the walls are found as they were before 0.8.0, by rays cast in at the
        /// building from all round, and each wall met is taken to have windows.)
        /// </summary>
        void Spill(Building b, CelestialBody body)
        {
            b.Rig = new GameObject("Natural Light window light at " + b.Name);
            b.Rig.transform.SetParent(b.At, false);
            b.Lamps.Clear();
            Vector3 middle = b.Glowing.center;
            Vector3 up = (middle - (Vector3)body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane((Vector3)body.transform.up, up).normalized, east = Vector3.Cross(up, north);
            float began = Time.realtimeSinceStartup;
            var patches = new List<Patch>();
            var bits = new List<GlowMap.Bit>();
            foreach (KeyValuePair<Renderer, int> g in b.Glass) if (g.Key != null) GlowMap.Find(g.Key, g.Value, 64, bits);
            if (bits.Count > 0) Facades(bits, middle, up, north, east, patches);
            b.Found = patches.Count > 0;
            if (patches.Count == 0) Guess(b, middle, up, north, east, patches);
            b.Patches = patches.Count;
            // ---- the light on the ground
            Matrix4x4 toLocal = b.Rig.transform.worldToLocalMatrix;
            var corners = new List<Vector3>(); var uvs = new List<Vector2>(); var colours = new List<Color>(); var triangles = new List<int>();
            foreach (Patch p in patches)
            {
                // (light from higher windows falls further out, and is spread thinner)
                float reach = Mathf.Clamp(5f + 0.55f * p.Height, 6f, 24f);
                float margin = Mathf.Clamp(0.25f * reach, 1.5f, 4f);
                float width = 2f * (p.Half + margin);
                float strength = Mathf.Clamp(0.3f + p.Glass / width / 1.6f, 0.3f, 1.2f) / (1f + Mathf.Max(0f, p.Height - 6f) / 22f);
#if DEV
                Vector3 off = p.Foot - middle;
                b.Said.Add(Vector3.Dot(off, east).ToString("F0") + "/" + Vector3.Dot(off, north).ToString("F0") + " facing " + (Mathf.Atan2(Vector3.Dot(p.Facing, east), Vector3.Dot(p.Facing, north)) * Mathf.Rad2Deg).ToString("F0")
                    + " wide " + (2f * p.Half).ToString("F0") + " high " + p.Height.ToString("F1") + " glass " + p.Glass.ToString("F0") + " reach " + reach.ToString("F0") + " strength " + strength.ToString("F2"));
#endif
                Vector3 side = p.Along * (p.Half + margin), outward = p.Facing * reach;
                float level = Vector3.Dot(p.Foot, up);
                int cells = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(width, reach) / 5f), 2, 12);
                Pools.Lay(p.Foot - side, p.Foot + side, p.Foot + side + outward, p.Foot - side + outward, up, cells, toLocal, level, corners, uvs, colours, triangles, strength);
            }
            Material spill = Pools.Spill();
            if (spill != null && corners.Count > 0) b.Pools = Pools.Show("Natural Light window light", b.Rig.transform, Pools.Make("Natural Light window light", corners, uvs, colours, triangles), spill);
            // ---- and lamps for what stands in it, in front of the brightest stretches
            patches.Sort((p, q) => q.Glass.CompareTo(p.Glass));
            for (int k = 0; k < patches.Count && k < 4; k++)
            {
                Patch p = patches[k];
                var holder = new GameObject("window light " + k);
                holder.transform.SetParent(b.Rig.transform, true);
                holder.transform.position = p.Foot + p.Facing * Mathf.Clamp(3f + 0.25f * p.Height, 3f, 8f) + up * 3f;
                Light lamp = holder.AddComponent<Light>();
                lamp.type = LightType.Point;
                lamp.shadows = LightShadows.None;
                lamp.renderMode = LightRenderMode.ForceVertex;
                lamp.cullingMask = Daylight.Lit & ~(1 << 15);
                lamp.range = 26f;
                lamp.intensity = 0f;
                lamp.enabled = false;
                b.Lamps.Add(lamp);
            }
            Keystone.Kit.Log("Natural Light", "the windows of " + b.Name + ": " + patches.Count + " lit stretches of wall " + (b.Found ? "found from " + bits.Count + " glowing bits" : "guessed at by rays") + ", in " + Mathf.RoundToInt(1000f * (Time.realtimeSinceStartup - began)) + " ms");
        }

        /// <summary>The glowing bits of a building sorted into stretches of lit wall: by the way they face, then how far out their wall stands, then along it.</summary>
        static void Facades(List<GlowMap.Bit> bits, Vector3 middle, Vector3 up, Vector3 north, Vector3 east, List<Patch> into)
        {
            const int Ways = 16;
            var byWay = new List<GlowMap.Bit>[Ways];
            foreach (GlowMap.Bit bit in bits)
            {
                Vector3 level = Vector3.ProjectOnPlane(bit.Facing, up);
                // (only glass in a wall, within thirty degrees of upright: glass in a roof, the spaceplane hangar's great curved one among
                // them, lights the sky; and glass facing down lights the room inside)
                if (level.sqrMagnitude < 0.75f) continue;
                level.Normalize();
                float angle = Mathf.Atan2(Vector3.Dot(level, east), Vector3.Dot(level, north));
                int way = ((Mathf.RoundToInt(angle / (2f * Mathf.PI / Ways)) % Ways) + Ways) % Ways;
                if (byWay[way] == null) byWay[way] = new List<GlowMap.Bit>();
                byWay[way].Add(bit);
            }
            for (int w = 0; w < Ways; w++)
            {
                List<GlowMap.Bit> list = byWay[w];
                if (list == null) continue;
                Vector3 facing = Vector3.zero;
                foreach (GlowMap.Bit bit in list) facing += Vector3.ProjectOnPlane(bit.Facing, up).normalized * bit.Area;
                if (facing.sqrMagnitude < 1e-10f) continue;
                facing.Normalize();
                Vector3 along = Vector3.Cross(up, facing);
                // (walls facing the same way at different depths, a building's wings: apart where the depths part by three metres)
                list.Sort((p, q) => Vector3.Dot(p.At - middle, facing).CompareTo(Vector3.Dot(q.At - middle, facing)));
                int first = 0;
                for (int i = 1; i <= list.Count; i++)
                {
                    if (i < list.Count && Vector3.Dot(list[i].At - list[i - 1].At, facing) < 3f) continue;
                    // (and along the wall, apart where the windows part by more than ten metres: closer than that, their light runs together)
                    List<GlowMap.Bit> wall = list.GetRange(first, i - first);
                    wall.Sort((p, q) => Vector3.Dot(p.At - middle, along).CompareTo(Vector3.Dot(q.At - middle, along)));
                    int from = 0;
                    for (int j = 1; j <= wall.Count; j++)
                    {
                        if (j < wall.Count && Vector3.Dot(wall[j].At - wall[j - 1].At, along) < 10f) continue;
                        Stretch(wall, from, j, up, facing, along, into);
                        from = j;
                    }
                    first = i;
                }
            }
        }

        /// <summary>One stretch of lit wall from its glowing bits: where its foot is on the ground in front of it, and whether it faces out into the open.</summary>
        static void Stretch(List<GlowMap.Bit> wall, int from, int to, Vector3 up, Vector3 facing, Vector3 along, List<Patch> into)
        {
            float glass = 0f, a0 = float.MaxValue, a1 = float.MinValue, depth = 0f, low = float.MaxValue;
            Vector3 centre = Vector3.zero, origin = wall[from].At;
            for (int i = from; i < to; i++)
            {
                GlowMap.Bit bit = wall[i];
                glass += bit.Area;
                centre += bit.At * bit.Area;
                depth += Vector3.Dot(bit.At - origin, facing) * bit.Area;
                float s = Vector3.Dot(bit.At - origin, along);
                a0 = Mathf.Min(a0, s); a1 = Mathf.Max(a1, s);
                low = Mathf.Min(low, Vector3.Dot(bit.At, up));
            }
            // (less than this is a lit sign or a lamp, not windows)
            if (glass < 2f) return;
            centre /= glass; depth /= glass;
            // (facing out into the open, and not at a wall close in front of it)
            if (Physics.Raycast(centre + facing * 0.3f, facing, 2.5f, 1 << 15, QueryTriggerInteraction.Ignore)) return;
            // The foot of the wall: under the middle of the windows, a little out from the wall, on whatever ground is there (not on the
            // roof of a lower part of the building in front of it: the ground must be below the lowest window).
            Vector3 foot = origin + along * ((a0 + a1) * 0.5f) + facing * (depth + 0.4f);
            float wallUp = Vector3.Dot(foot, up);
            foot += up * (low - wallUp);
            if (!Physics.Raycast(foot + up * 0.5f, -up, out RaycastHit ground, 120f, 1 << 15, QueryTriggerInteraction.Ignore)) return;
            foot = ground.point;
            float height = Vector3.Dot(centre, up) - Vector3.Dot(foot, up);
            if (height < -0.5f) return;
            into.Add(new Patch { Foot = foot, Facing = facing, Along = along, Half = Mathf.Max(1.5f, (a1 - a0) * 0.5f + 1f), Height = Mathf.Max(0.5f, height), Glass = glass });
        }

        /// <summary>
        /// Where a building's meshes cannot be read: its walls found by casting rays in at it from all round, level with the ground
        /// floor, and each wall met taken to have windows along twenty metres of it. (So an L-shaped building has light along its
        /// own outline and none in the crook, and none falls over a ramp or into a pit.)
        /// </summary>
        static void Guess(Building b, Vector3 middle, Vector3 up, Vector3 north, Vector3 east, List<Patch> into)
        {
            // (how far out to start from: beyond the building's furthest corner)
            float out2 = new Vector2(Vector3.Dot(b.Glowing.extents, new Vector3(Mathf.Abs(east.x), Mathf.Abs(east.y), Mathf.Abs(east.z))), Vector3.Dot(b.Glowing.extents, new Vector3(Mathf.Abs(north.x), Mathf.Abs(north.y), Mathf.Abs(north.z)))).magnitude + 15f;
            int tries = Mathf.Clamp(Mathf.RoundToInt(2f * Mathf.PI * out2 / 40f), 6, 20);
            for (int k = 0; k < tries && into.Count < 12; k++)
            {
                float turn = (k + 0.5f) / tries * 2f * Mathf.PI;
                Vector3 from = middle + (north * Mathf.Cos(turn) + east * Mathf.Sin(turn)) * out2;
                // the ground out there, then in towards the building a little above it
                if (!Physics.Raycast(from + up * 150f, -up, out RaycastHit floor, 400f, 1 << 15, QueryTriggerInteraction.Ignore)) continue;
                Vector3 start = floor.point + up * 4f, inward = Vector3.ProjectOnPlane(middle - start, up).normalized;
                if (!Physics.Raycast(start, inward, out RaycastHit wall, out2 * 1.2f, 1 << 15, QueryTriggerInteraction.Ignore)) continue;
                // (a wall, not a slope of the ground: it has to stand up)
                if (Mathf.Abs(Vector3.Dot(wall.normal, up)) > 0.5f) continue;
                Vector3 facing = Vector3.ProjectOnPlane(wall.normal, up).normalized;
                Vector3 foot = wall.point + facing * 0.4f;
                if (Physics.Raycast(foot + up * 2f, -up, out RaycastHit under, 60f, 1 << 15, QueryTriggerInteraction.Ignore)) foot = under.point;
                into.Add(new Patch { Foot = foot, Facing = facing, Along = Vector3.Cross(up, facing), Half = 10f, Height = 3f, Glass = 12f });
            }
        }

        /// <summary>Each lamp is a card turned to face whichever camera is about to draw it, and large enough to be seen as a point of light from afar.</summary>
        void Face(Camera cam)
        {
            if (cam == null || sites.Count == 0) return;
            string name = cam.name;
            if (name != "Camera 00" && name != "Camera 01") return;
            Transform eye = cam.transform;
            foreach (Site site in sites)
            {
                if (site.Rig == null || site.On <= 0f) continue;
                for (int k = 0; k < Each; k++)
                {
                    Transform head = site.Heads[k];
                    if (head == null) continue;
                    float far = (head.position - eye.position).magnitude;
                    head.rotation = eye.rotation;
                    float size = Mathf.Clamp(0.055f * far, 2.2f, 90f);
                    if (site.Fixtures[k] != null)
                    {
                        // A searchlight dazzles only from in front: from the side its glass glows and no more, from behind it shows nothing.
                        // (smaller close to, too: its glass, not a blot, is what is seen there)
                        Vector3 aim = site.Rig.transform.TransformDirection(site.Aims[k]);
                        float facing = Vector3.Dot(aim, (eye.position - head.position) / Mathf.Max(far, 1e-3f));
                        size = Mathf.Clamp(0.055f * far, 1.3f, 90f) * Mathf.Clamp01((facing + 0.05f) / 0.7f);
                    }
                    Vector3 parent = head.parent != null ? head.parent.lossyScale : Vector3.one;
                    head.localScale = new Vector3(size / Mathf.Max(parent.x, 1e-4f), size / Mathf.Max(parent.y, 1e-4f), size / Mathf.Max(parent.z, 1e-4f));
                    // The beam is a strip from the lamp to where it ends, turned about its own length to face the camera.
                    Mesh beam = site.Beams[k];
                    Renderer shows = site.BeamShows[k];
                    if (beam == null || shows == null || !shows.enabled) continue;
                    Transform space = shows.transform;
                    Vector3 end = site.BeamEnds[k], from = space.InverseTransformPoint(eye.position);
                    Vector3 along = end.normalized, across = Vector3.Cross(along, from - end * 0.3f);
                    if (across.sqrMagnitude < 1e-6f) continue;
                    across.Normalize();
                    // (in the lamp's own space, which may be scaled: widths are metres there only if it is not)
                    float unit = 1f / Mathf.Max(space.lossyScale.x, 1e-4f);
                    for (int i = 0; i <= BeamCuts; i++)
                    {
                        float far2 = Cut(i), half = Mathf.Lerp(BeamNarrow, BeamWide, far2) * 0.5f * unit;
                        Vector3 middle = end * far2;
                        beamCorners[i * 3] = middle - across * half; beamCorners[i * 3 + 1] = middle; beamCorners[i * 3 + 2] = middle + across * half;
                    }
                    beam.vertices = beamCorners;
                    beam.RecalculateBounds();
                }
            }
        }

#if DEV
        /// <summary>(For the development build: the space centre's buildings: where each is, how big, and what in its materials glows.)</summary>
        public static string Buildings()
        {
            var o = new System.Text.StringBuilder();
            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null || setup.SpaceCenterFacilities == null) return "no space centre";
            foreach (PSystemSetup.SpaceCenterFacility f in setup.SpaceCenterFacilities)
            {
                if (f == null || f.facilityTransform == null) { o.Append((f != null ? f.name : "?") + ": no transform\n"); continue; }
                Bounds all = default; bool any = false; int shown = 0;
                var glows = new Dictionary<string, int>();
                foreach (Renderer r in f.facilityTransform.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                    shown++;
                    if (!any) { all = r.bounds; any = true; } else all.Encapsulate(r.bounds);
                    foreach (Material m in r.sharedMaterials)
                    {
                        if (m == null || m.shader == null) continue;
                        string k = m.shader.name;
                        foreach (string name in new[] { "_EmissiveColor", "_EmissionColor", "_LightColor", "_Emissive", "_NightColor", "_Opacity" })
                            if (m.HasProperty(name)) { try { k += " " + name + "=" + (name == "_Emissive" ? (m.GetTexture(name) != null ? m.GetTexture(name).name : "none") : name == "_Opacity" ? m.GetFloat(name).ToString("F2") : m.GetColor(name).ToString()); } catch (Exception) { } }
                        glows[k] = glows.TryGetValue(k, out int n) ? n + 1 : 1;
                    }
                }
                o.Append(f.name + ": " + shown + " drawn pieces, " + (any ? all.size.x.ToString("F0") + " x " + all.size.y.ToString("F0") + " x " + all.size.z.ToString("F0") + " m" : "nothing") + (Daylight.Near ? ", " + (f.facilityTransform.position - (Vector3)Daylight.Here).magnitude.ToString("F0") + " m off" : "") + "\n");
                foreach (KeyValuePair<string, int> kv in glows) o.Append("     " + kv.Value + " x " + kv.Key + "\n");
            }
            return o.ToString();
        }

        /// <summary>(For the development build: the light laid on the ground this far over the colliders, and every building's window light made again.)</summary>
        public static string Relay(string lift)
        {
            Pools.Lift = float.Parse(lift, System.Globalization.CultureInfo.InvariantCulture);
            Floodlights f = FindObjectOfType<Floodlights>();
            if (f == null) return "not running";
            int n = 0;
            foreach (Building b in f.buildings) if (b.Rig != null) { Destroy(b.Rig); b.Rig = null; b.Lamps.Clear(); b.Pools = null; b.Said.Clear(); n++; }
            return n + " buildings' window light to be made again, " + Pools.Lift + " m over the ground";
        }

        /// <summary>(For the development build: one building's lit stretches of wall, and the layers its pieces are drawn on.)</summary>
        public static string Stretches(string which)
        {
            Floodlights f = FindObjectOfType<Floodlights>();
            if (f == null) return "not running";
            foreach (Building b in f.buildings)
            {
                if (b.Name.IndexOf(which ?? "", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var layers = new Dictionary<int, int>();
                if (b.At != null) foreach (Renderer r in b.At.GetComponentsInChildren<Renderer>()) layers[r.gameObject.layer] = layers.TryGetValue(r.gameObject.layer, out int n) ? n + 1 : 1;
                var o = new System.Text.StringBuilder(b.Name + ": layers");
                foreach (KeyValuePair<int, int> kv in layers) o.Append(" " + kv.Key + "x" + kv.Value);
                o.Append("; " + b.Said.Count + " stretches (east/north of its middle):\n");
                foreach (string s in b.Said) o.Append("  " + s + "\n");
                return o.ToString();
            }
            return "no such building";
        }

        /// <summary>(For the development build: the launch sites found, and how far each is.)</summary>
        public static string Sites()
        {
            Floodlights f = FindObjectOfType<Floodlights>();
            if (f == null) return "not running";
            var o = new System.Text.StringBuilder(f.sites.Count + " sites after " + f.looks + " looks: ");
            foreach (Building b in f.buildings) o.Append("{" + b.Name + ": " + b.Lit.Count + " glowing materials (" + b.Glass.Count + " drawn) over " + b.Glowing.size.ToString("F0") + ", the game's switch " + (b.Clock != null ? b.Clock.emissiveColor.a.ToString("F2") : "none") + ", on " + b.On.ToString("F2") + ", " + b.Patches + " lit stretches" + (b.Found ? " found" : b.Rig != null ? " guessed" : "") + ", " + b.Lamps.Count + " lamps} ");
            foreach (Site s in f.sites) if (s.Rig != null && s.BeamShows != null && s.BeamShows[0] != null)
                {
                    Transform t = s.BeamShows[0].transform;
                    Mesh m = s.Beams[0];
                    Vector3[] vs = m.vertices;
                    o.Append("[beam 0 of " + s.Name + ": scale " + t.lossyScale.ToString("F3") + ", ends " + s.BeamEnds[0].magnitude.ToString("F1") + " off, corners " + (t.TransformPoint(vs[0]) - t.TransformPoint(vs[2])).magnitude.ToString("F2") + " m and " + (t.TransformPoint(vs[vs.Length - 1]) - t.TransformPoint(vs[vs.Length - 3])).magnitude.ToString("F2") + " m wide, " + (t.TransformPoint(vs[vs.Length - 2]) - t.position).magnitude.ToString("F1") + " m long, bounds " + s.BeamShows[0].bounds.size.ToString("F0") + "] ");
                }
            foreach (Site s in f.sites) o.Append(s.Name + " (" + (s.At != null ? s.At.name + (Daylight.Near ? ", " + (s.At.position - (Vector3)Daylight.Here).magnitude.ToString("F0") + " m off" : "") : "gone") + ", on " + s.On.ToString("F2") + (s.Rig != null ? ", built " + s.Placed : "") + "); ");
            return o.ToString();
        }

        /// <summary>(For the development build: each floodlight of the nearest site: where it stands from the craft, how high its glass is, which way it points, and what is under it.)</summary>
        public static string Where()
        {
            Floodlights f = FindObjectOfType<Floodlights>();
            if (f == null || !Daylight.Near) return "not running";
            Vector3 here = Daylight.Here;
            Site near = null;
            foreach (Site s in f.sites) if (s.Rig != null && (near == null || (s.At.position - here).sqrMagnitude < (near.At.position - here).sqrMagnitude)) near = s;
            if (near == null) return "no floodlights put up";
            Vector3 up = (near.At.position - (Vector3)Daylight.Body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane((Vector3)Daylight.Body.transform.up, up).normalized, east = Vector3.Cross(up, north);
            var o = new System.Text.StringBuilder(near.Name + " (" + near.Placed + "; the spawn point's facing " + (Mathf.Atan2(Vector3.Dot(near.At.forward, east), Vector3.Dot(near.At.forward, north)) * Mathf.Rad2Deg).ToString("F0") + ", its up " + Vector3.Dot(near.At.up, up).ToString("F2") + "):\n");
            for (int k = 0; k < Each; k++)
            {
                Transform post = near.Frames != null && near.Frames[k] != null ? near.Frames[k].parent : null;
                Vector3 glass = near.Heads[k] != null ? near.Heads[k].position : Vector3.zero;
                Vector3 aim = near.Rig.transform.TransformDirection(near.Aims[k]);
                o.Append("  " + k + ": ");
                if (post != null)
                {
                    Vector3 d = post.position - near.At.position;
                    o.Append(Vector3.Dot(d, east).ToString("F1") + " east, " + Vector3.Dot(d, north).ToString("F1") + " north, " + Vector3.Dot(d, up).ToString("F1") + " up of the craft's spot; ");
                    if (Physics.Raycast(post.position + up * 3f, -up, out RaycastHit hit, 10f, 1 << 15, QueryTriggerInteraction.Ignore))
                        o.Append("on " + hit.collider.name + (OwnSurface(hit, near.Facility) ? " (the site's own)" : "") + " " + (Vector3.Dot(post.position - hit.point, up)).ToString("F2") + " m over it; ");
                }
                o.Append("glass " + Vector3.Dot(glass - near.At.position, up).ToString("F1") + " m up, pointing " + (Mathf.Atan2(Vector3.Dot(aim, east), Vector3.Dot(aim, north)) * Mathf.Rad2Deg).ToString("F0") + " degrees, " + (Mathf.Asin(Mathf.Clamp(Vector3.Dot(aim, up), -1f, 1f)) * Mathf.Rad2Deg).ToString("F0") + " up\n");
            }
            return o.ToString();
        }

        /// <summary>
        /// (For the development build: the ground round the nearest site as the placing of its floodlights sees it, north up and east
        /// to the right, a letter every `step` metres out to `half` metres each way from where the craft stands: "half step". Three
        /// maps. Whether a floodlight could stand there: '#' it could, ' ' no ground, '/' sloping or much higher or lower, 'r' a
        /// runway's own surface, 'e' not level all round, 'b' something in its way; '@' where the craft stands, and 0 to 3 where the
        /// floodlights do. How high the ground is over the craft's: '0' within 5 cm, '+' and '-' within 35, 1 to 9 metres higher (rounded
        /// up), a to i metres lower, '^' and 'v' beyond. And which collider it is, named under the map.)
        /// </summary>
        public static string Ground(string arg)
        {
            Floodlights f = FindObjectOfType<Floodlights>();
            if (f == null || !Daylight.Near || Daylight.Body == null) return "not running";
            string[] w = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float half = w.Length > 0 ? float.Parse(w[0], System.Globalization.CultureInfo.InvariantCulture) : 36f;
            float step = w.Length > 1 ? float.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture) : 1f;
            Vector3 here = Daylight.Here;
            Site near = null;
            foreach (Site s in f.sites) if (s.At != null && (near == null || (s.At.position - here).sqrMagnitude < (near.At.position - here).sqrMagnitude)) near = s;
            if (near == null) return "no site";
            Vector3 centre = near.At.position;
            Vector3 up = (centre - (Vector3)Daylight.Body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane((Vector3)Daylight.Body.transform.up, up).normalized, east = Vector3.Cross(up, north);
            float level = Vector3.Dot(centre, up);
            if (Physics.Raycast(centre + up * 40f, -up, out RaycastHit under, 120f, 1 << 15, QueryTriggerInteraction.Ignore)) level = Vector3.Dot(under.point, up);
            int n = Mathf.Clamp(Mathf.RoundToInt(half / step), 1, 80), side = 2 * n + 1;
            var can = new char[side, side]; var high = new char[side, side]; var what = new char[side, side];
            var names = new List<string>(); var counts = new List<int>();
            const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            for (int j = 0; j < side; j++)
                for (int i = 0; i < side; i++)
                {
                    Vector3 p = centre + east * ((i - n) * step) + north * ((n - j) * step);
                    Vector3 facing = Vector3.ProjectOnPlane(centre - p, up);
                    facing = facing.sqrMagnitude > 1e-4f ? facing.normalized : north;
                    can[j, i] = "# /reb"[WhyNot(near, p, up, level, facing, out Vector3 foot)];
                    high[j, i] = what[j, i] = ' ';
                    if (!Physics.Raycast(p + up * 40f, -up, out RaycastHit hit, 120f, 1 << 15, QueryTriggerInteraction.Ignore)) continue;
                    float h = Vector3.Dot(hit.point, up) - level;
                    high[j, i] = Mathf.Abs(h) <= 0.05f ? '0' : Mathf.Abs(h) <= 0.35f ? (h > 0f ? '+' : '-')
                        : h > 9f ? '^' : h < -9f ? 'v' : h > 0f ? (char)('0' + Mathf.CeilToInt(h)) : (char)('a' + Mathf.CeilToInt(-h) - 1);
                    string name = hit.collider.name + (hit.collider.transform.parent != null ? " (in " + hit.collider.transform.parent.name + ")" : "") + (OwnSurface(hit, near.Facility) ? "" : ", not the site's");
                    int at = names.IndexOf(name);
                    if (at < 0) { at = names.Count; names.Add(name); counts.Add(0); }
                    counts[at]++;
                    what[j, i] = letters[Mathf.Min(at, letters.Length - 1)];
                }
            can[n, n] = '@';
            for (int k = 0; k < Each && near.Rig != null && near.Frames != null; k++)
            {
                if (near.Frames[k] == null) continue;
                Vector3 d = near.Frames[k].parent.position - centre;
                int i = n + Mathf.RoundToInt(Vector3.Dot(d, east) / step), j = n - Mathf.RoundToInt(Vector3.Dot(d, north) / step);
                if (i >= 0 && i < side && j >= 0 && j < side) can[j, i] = (char)('0' + k);
            }
            var o = new System.Text.StringBuilder(near.Name + ", " + half + " m each way, every " + step + " m (north up); the site's surface " + (near.Runway ? "a runway's" : "a pad's") + "; " + near.Placed + "\n");
            foreach (char[,] map in new[] { can, high, what })
            {
                for (int j = 0; j < side; j++)
                {
                    for (int i = 0; i < side; i++) o.Append(map[j, i]);
                    o.Append('\n');
                }
                o.Append('\n');
            }
            for (int k = 0; k < names.Count; k++) o.Append(letters[Mathf.Min(k, letters.Length - 1)] + " = " + names[k] + ", " + counts[k] + "\n");
            return o.ToString();
        }

        /// <summary>
        /// (For the development build: the frames the nearest site's searchlights are drawn in. The scale, turn and place of
        /// everything the rig hangs from; and for each searchlight what its lamp is drawn from, its post's scale, where its head is
        /// and how it is turned, the head's own axes in the world (their lengths and the angles between them: a frame that is not
        /// square shows here), and, in millimetres east, north and up: how far the middle of its glass is drawn from where the head's
        /// body was joined in, how far the head's body was joined in from its yoke, how far its post was joined in from its own
        /// place, and how far the world now says the head is from its yoke.)
        /// </summary>
        public static string Frames()
        {
            Floodlights f = FindObjectOfType<Floodlights>();
            if (f == null || !Daylight.Near) return "not running";
            Vector3 here = Daylight.Here;
            Site near = null;
            foreach (Site s in f.sites) if (s.Rig != null && s.Fixtures != null && (near == null || (s.At.position - here).sqrMagnitude < (near.At.position - here).sqrMagnitude)) near = s;
            if (near == null) return "no floodlights put up";
            var o = new System.Text.StringBuilder(near.Name + ": ");
            for (Transform t = near.Rig.transform; t != null; t = t.parent)
                o.Append(t.name + " scale " + t.localScale.ToString("F4") + " turn " + t.localEulerAngles.ToString("F2") + " at " + t.localPosition.ToString("F2") + " < ");
            o.Append("the world; the rig's scale in it " + near.Rig.transform.lossyScale.ToString("F4") + ", the rig at " + near.Rig.transform.position.ToString("F2") + "\n");
            Vector3 up = (near.At.position - (Vector3)Daylight.Body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane((Vector3)Daylight.Body.transform.up, up).normalized, east = Vector3.Cross(up, north);
            // (a small step in the world, said in millimetres east, north and up)
            Func<Vector3, string> mm = v => (Vector3.Dot(v, east) * 1000f).ToString("F1") + " east " + (Vector3.Dot(v, north) * 1000f).ToString("F1") + " north " + (Vector3.Dot(v, up) * 1000f).ToString("F1") + " up";
            Matrix4x4 rig = near.Rig.transform.localToWorldMatrix;
            for (int k = 0; k < Each; k++)
            {
                if (near.Fixtures[k] == null || near.Frames[k] == null) { o.Append("  " + k + ": not drawn\n"); continue; }
                Transform head = near.Frames[k], post = head.parent, drawn = near.Fixtures[k].transform;
                o.Append("  " + k + ": its lamp drawn from " + drawn.parent.name + ", post scale " + post.localScale.ToString("F4") + " (in the world " + post.lossyScale.ToString("F4") + "), head at " + head.localPosition.ToString("F3")
                    + " turned " + head.localEulerAngles.ToString("F2") + "; the lamp's bounds " + near.Fixtures[k].bounds.size.ToString("F2") + (near.Bodies != null ? ", the bodies' " + near.Bodies.bounds.size.ToString("F1") : "") + "\n");
                Matrix4x4 m = head.localToWorldMatrix;
                Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
                // where the lamp (glass, reflector) is drawn against where the head's body was joined in
                Vector3 lampOff = (drawn.localToWorldMatrix * near.LampsIn[k]).MultiplyPoint3x4(FloodlightModel.Glass) - (rig * near.HeadsIn[k]).MultiplyPoint3x4(FloodlightModel.Glass);
                // where the head's body was joined in against where the post's was (the yoke that holds it), by the frames' own numbers
                Matrix4x4 exactPost = Matrix4x4.TRS(post.localPosition, post.localRotation, post.localScale), exactHead = exactPost * Matrix4x4.TRS(head.localPosition, head.localRotation, head.localScale);
                Vector3 headOff = rig.MultiplyVector((Vector3)(near.HeadsIn[k].GetColumn(3) - near.PostsIn[k].GetColumn(3)) - (Vector3)(exactHead.GetColumn(3) - exactPost.GetColumn(3)));
                Vector3 postOff = rig.MultiplyVector((Vector3)near.PostsIn[k].GetColumn(3) - (Vector3)exactPost.GetColumn(3));
                // and the same asked of the world now: the head's place less its post's, against what the head's own numbers say
                Vector3 nowOff = (head.position - post.position) - post.TransformVector(head.localPosition);
                o.Append("     its axes " + x.magnitude.ToString("F4") + " " + y.magnitude.ToString("F4") + " " + z.magnitude.ToString("F4") + " long, "
                    + Vector3.Angle(x, y).ToString("F2") + " " + Vector3.Angle(y, z).ToString("F2") + " " + Vector3.Angle(z, x).ToString("F2") + " degrees apart; its lamp drawn " + mm(lampOff) + " mm from its body; its body joined in " + mm(headOff)
                    + " mm from its yoke; its post joined in " + mm(postOff) + " mm from its place; in the world now the head is " + mm(nowOff) + " mm from its yoke\n");
            }
            return o.ToString();
        }


        static Vector3? eyeAt;
        static Quaternion eyeTurn;
        static Transform eyeOn;
        static readonly Dictionary<Camera, KeyValuePair<Vector3, Quaternion>> eyeWas = new Dictionary<Camera, KeyValuePair<Vector3, Quaternion>>();

        /// <summary>
        /// (For the development build: the flight's cameras put `distance` metres from floodlight k of the nearest site, looking at its head
        /// from the compass bearing `heading` (the way the camera looks) and `pitch` degrees above it: "k distance heading pitch". With
        /// nothing, the cameras are the game's again. The flight camera itself will not come close to anything but the craft.)
        /// </summary>
        public static string Eye(string arg)
        {
            string[] w = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (w.Length < 4) { eyeAt = null; eyeOn = null; return "the cameras are the game's"; }
            Floodlights f = FindObjectOfType<Floodlights>();
            if (f == null || !Daylight.Near) return "not running";
            Func<int, float> num = n => float.Parse(w[n], System.Globalization.CultureInfo.InvariantCulture);
            int k = (int)num(0);
            Site near = null;
            foreach (Site s in f.sites) if (s.Rig != null && s.Fixtures != null && (near == null || (s.At.position - (Vector3)Daylight.Here).sqrMagnitude < (near.At.position - (Vector3)Daylight.Here).sqrMagnitude)) near = s;
            if (near == null || k < 0 || k >= Each || near.Frames[k] == null) return "no such floodlight";
            eyeOn = near.Frames[k];
            Vector3 up = (eyeOn.position - (Vector3)Daylight.Body.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane((Vector3)Daylight.Body.transform.up, up).normalized, east = Vector3.Cross(up, north);
            // (the heading may be said from where the lamp points: "front", "quarter" (front, sixty degrees round), "side", "back")
            Vector3 aim = near.Rig.transform.TransformDirection(near.Aims[k]);
            float points = Mathf.Atan2(Vector3.Dot(aim, east), Vector3.Dot(aim, north)) * Mathf.Rad2Deg;
            float bearing = w[2] == "front" ? points + 180f : w[2] == "quarter" ? points + 240f : w[2] == "side" ? points + 90f : w[2] == "back" ? points : num(2);
            float hdg = bearing * Mathf.Deg2Rad, pitch = num(3) * Mathf.Deg2Rad;
            Vector3 look = (north * Mathf.Cos(hdg) + east * Mathf.Sin(hdg)) * Mathf.Cos(pitch) - up * Mathf.Sin(pitch);
            // (kept in the head's own space: the game slides its whole world about under the craft)
            eyeAt = eyeOn.InverseTransformPoint(eyeOn.position - look * num(1));
            eyeTurn = Quaternion.Inverse(eyeOn.rotation) * Quaternion.LookRotation(look, up);
            return "the cameras " + num(1) + " m from floodlight " + k + " of " + near.Name;
        }

        static void EyeOn(Camera cam)
        {
            if (eyeAt == null || eyeOn == null || cam == null) return;
            bool flight = cam.name == "Camera 00" || cam.name == "Camera 01";
            // (the sky's cameras turned the same way, where they are)
            if (!flight && cam.name != "Camera ScaledSpace" && cam.name != "GalaxyCamera") return;
            eyeWas[cam] = new KeyValuePair<Vector3, Quaternion>(cam.transform.localPosition, cam.transform.localRotation);
            if (flight) cam.transform.position = eyeOn.TransformPoint(eyeAt.Value);
            cam.transform.rotation = eyeOn.rotation * eyeTurn;
        }

        static void EyeOff(Camera cam)
        {
            if (cam == null || !eyeWas.TryGetValue(cam, out KeyValuePair<Vector3, Quaternion> was)) return;
            cam.transform.localPosition = was.Key;
            cam.transform.localRotation = was.Value;
            eyeWas.Remove(cam);
        }

        /// <summary>(For the development build: what floodlight 0 of the nearest site's head is drawn with, and what each of its lamp's parts is told to glow.)</summary>
        public static string Glass()
        {
            Floodlights f = FindObjectOfType<Floodlights>();
            if (f == null || !Daylight.Near) return "not running";
            foreach (Site s in f.sites)
            {
                if (s.Rig == null || s.Fixtures == null || s.Fixtures[0] == null) continue;
                Renderer r = s.Fixtures[0];
                var o = new System.Text.StringBuilder(s.Name + ": ");
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                o.Append("uv2 " + (mesh.uv2 != null && mesh.uv2.Length > 0) + "; ");
                var pb = new MaterialPropertyBlock();
                for (int i = 0; i < r.sharedMaterials.Length; i++)
                {
                    Material m = r.sharedMaterials[i];
                    r.GetPropertyBlock(pb, i);
                    o.Append(i + " " + (m != null ? m.shader.name : "none") + (m != null && m.HasProperty("_EmissiveColor") ? " own " + m.GetColor("_EmissiveColor") + " told " + pb.GetColor("_EmissiveColor") : "") + "; ");
                }
                return o.ToString();
            }
            return "no fixtures";
        }

        static bool asBefore;
        /// <summary>
        /// (For the development build: the searchlights placed and drawn as until 0.10.2 ("1": each on its own nearest spot; each
        /// piece's place asked of the world, the lamp drawn from the head's own frame) or as now ("0"), and every site's put up again:
        /// for pictures of before and after.)
        /// </summary>
        public static string Before(string on)
        {
            asBefore = on == "1" || on == "true";
            Floodlights f = FindObjectOfType<Floodlights>();
            if (f == null) return "not running";
            int n = 0;
            foreach (Site s in f.sites) if (s.Rig != null) { Unbuild(s); n++; }
            return n + " sites' floodlights to be put up again, " + (asBefore ? "as until 0.10.2" : "as they are now");
        }

        static bool hidden;
        /// <summary>(For the development build: the fixtures drawn or not, the lamps as they are: to measure what drawing them costs.)</summary>
        public static string Fixtures(string on)
        {
            hidden = on == "0" || on == "false";
            Floodlights f = FindObjectOfType<Floodlights>();
            if (f == null) return "not running";
            int n = 0;
            foreach (Site s in f.sites)
                if (s.Rig != null && s.Fixtures != null)
                {
                    if (s.Bodies != null) s.Bodies.enabled = !hidden;
                    foreach (Renderer r in s.Fixtures) if (r != null) { r.enabled = !hidden; n++; }
                }
            return n + " fixtures " + (hidden ? "hidden" : "shown");
        }
#endif
    }
}
