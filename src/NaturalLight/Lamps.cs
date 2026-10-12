using System;
using System.Collections.Generic;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// The lamps along a runway made to light what is round them.
    ///
    /// The game draws the space centre's runway lamps (two rows of edge lamps, fifty-two a side and forty-seven metres
    /// apart, and the big lamps at its four corners) as glowing paint on meshes of their own, and switches them on at
    /// dusk; and they light nothing: a row of bright spots on black concrete. A real runway's edge lamps throw a pool of
    /// light on the concrete and the grass round each one, longer along the runway than across it, and an aircraft
    /// rolling past is lit by each in turn. Here each lamp is found from the game's own mesh and its glow picture
    /// (GlowMap), and gets:
    ///
    ///   * its pool of light on the ground (see Pools), lying along the row and fading out from the lamp;
    ///   * a faint glow round the lamp itself, turned to whichever camera is drawing, a little larger with distance so
    ///     that the far end of a row still reads as lamps and not as dots;
    ///   * near the craft being flown, a real lamp of the cheap kind at each of the nearest few, so that what stands or
    ///     rolls on the runway is lit as it passes them. (Those light the craft and kerbals only: the ground has its pools.)
    ///
    /// They come on with the game's own lamps, by the game's own switch for them, faded in and out over a moment.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class RunwayLamps : MonoBehaviour
    {
        /// <summary>The lamps of one of the game's lamp meshes (a stretch of the runway's rows, a corner lamp).</summary>
        sealed class Row
        {
            public string Name;
            public Transform At;                                            // the game's own lamp mesh: everything of this hangs under it, and is hidden with it
            public Renderer Shown;
            public Vector3[] Spots;                                         // in At's space
            public Vector3 Along;                                           // the long way of the runway, in At's space
            public Vector3[] Lamps;                                         // the spots in the glow's own space
            public float[] Sizes;                                           // how big each lamp is against the place's usual one
            public GameObject Rig;
            public MeshRenderer Pools, Glows;
            public Mesh GlowMesh;
            public Vector3[] GlowCorners;
            public TimeOfDayAnimation Clock;                                // the game's own switch for them, where one can be found
            public float On;
        }

        readonly List<Row> rows = new List<Row>();
        readonly HashSet<Renderer> looked = new HashSet<Renderer>();
        const int Nearby = 6;
        readonly Light[] near = new Light[Nearby];
        float nextLook = 2f;
        int looks;
        static Material halo;
        static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        public static string Report = "";

        /// <summary>The colour of the lamps' light: the warm white of a runway's edge lamps.</summary>
        static readonly Color Lamp = new Color(1f, 0.9f, 0.74f);
        const float PoolAlong = 15f, PoolAcross = 10f;      // half the length and half the width of a lamp's pool of light, metres
        // How strong the pools and the glows are (the tint the game's additive shader is given). Kept well under the lamps' own brightness:
        // the camera's eye opens no further than the brightest thing in view allows, and a pool brighter than its lamp would keep the
        // whole space centre dark at night.
        const float PoolLight = 0.5f, GlowLight = 0.22f;

        void Awake()
        {
            Daylight.Setup();
            Camera.onPreCull += Face;
            // (a copy of this from before a hot reload leaves its meshes hanging under the runway: they go)
            foreach (MeshRenderer r in FindObjectsOfType<MeshRenderer>()) if (r != null && r.name.StartsWith("Natural Light runway lamps")) Destroy(r.transform.parent != null && r.transform.parent.name.StartsWith("Natural Light runway lamps") ? r.transform.parent.gameObject : r.gameObject);
        }

        void OnDestroy()
        {
            Camera.onPreCull -= Face;
            foreach (Row row in rows) if (row.Rig != null) Destroy(row.Rig);
            rows.Clear();
            foreach (Light l in near) if (l != null) Destroy(l.gameObject);
            Report = "";
        }

        /// <summary>Whether a renderer is one of a place's lamps: something under the place with "light" in its name or its holder's (Section1_lights, runway_light_SW).</summary>
        static bool IsLamp(Transform t, Transform place)
        {
            for (int i = 0; t != null && t != place && i < 6; i++, t = t.parent)
                if (t.name.IndexOf("light", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>The places craft are rolled out to and the space centre's facilities: each looked over once for lamps, if it is near.</summary>
        void Look()
        {
            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null || Daylight.Body == null) return;
            var places = new List<KeyValuePair<Transform, string>>();
            try
            {
                if (setup.SpaceCenterFacilities != null)
                    foreach (PSystemSetup.SpaceCenterFacility f in setup.SpaceCenterFacilities)
                        if (f != null && f.facilityTransform != null) places.Add(new KeyValuePair<Transform, string>(f.facilityTransform, f.name));
            }
            catch (Exception) { }
            try
            {
                if (setup.LaunchSites != null)
                    foreach (LaunchSite s in setup.LaunchSites)
                        if (s != null && s.launchSiteTransform != null) places.Add(new KeyValuePair<Transform, string>(s.launchSiteTransform, s.name));
            }
            catch (Exception) { }
            Vector3 here = Daylight.Here;
            foreach (KeyValuePair<Transform, string> place in places)
            {
                Transform at = place.Key;
                if (!at.gameObject.activeInHierarchy || (at.position - here).sqrMagnitude > 6000f * 6000f) continue;
                float began = Time.realtimeSinceStartup;
                var found = new List<KeyValuePair<Renderer, List<GlowMap.Bit>>>();
                // (inactive ones too: the game may hide its lamps by day. What is made for them hangs under them, and is hidden with them.)
                foreach (Renderer r in at.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || r is ParticleSystemRenderer || looked.Contains(r) || !IsLamp(r.transform, at)) continue;
                    looked.Add(r);
                    var bits = new List<GlowMap.Bit>();
                    Material[] ms = r.sharedMaterials;
                    for (int i = 0; i < ms.Length; i++) if (GlowMap.Glows(ms[i])) GlowMap.Find(r, i, 4000, bits);
                    if (bits.Count > 0) found.Add(new KeyValuePair<Renderer, List<GlowMap.Bit>>(r, GlowMap.Spots(bits, 2.5f)));
                }
                if (found.Count == 0) continue;
                // (the long way of the place, from all its lamps together; and how big each lamp is against the place's usual one:
                // the corner lamps are bigger than the edge lamps)
                var all = new List<Vector3>();
                var areas = new List<float>();
                foreach (KeyValuePair<Renderer, List<GlowMap.Bit>> f in found) foreach (GlowMap.Bit s in f.Value) { all.Add(s.At); areas.Add(s.Area); }
                areas.Sort();
                float usual = Mathf.Max(areas[areas.Count / 2], 1e-4f);
                Vector3 up = (all[0] - (Vector3)Daylight.Body.position).normalized;
                Vector3 north = Vector3.ProjectOnPlane((Vector3)Daylight.Body.transform.up, up).normalized;
                Vector3 along = GlowMap.Lengthwise(all, up, north);
                foreach (KeyValuePair<Renderer, List<GlowMap.Bit>> f in found)
                {
                    Transform lamp = f.Key.transform;
                    var row = new Row { Name = place.Value + "/" + lamp.name, At = lamp, Shown = f.Key, Spots = new Vector3[f.Value.Count], Sizes = new float[f.Value.Count] };
                    for (int k = 0; k < f.Value.Count; k++)
                    {
                        row.Spots[k] = lamp.InverseTransformPoint(f.Value[k].At);
                        row.Sizes[k] = Mathf.Clamp(Mathf.Sqrt(f.Value[k].Area / usual), 0.8f, 1.8f);
                    }
                    row.Along = lamp.InverseTransformDirection(along);
                    row.Clock = ClockOf(lamp);
                    rows.Add(row);
                }
                Keystone.Kit.Log("Natural Light", "the lamps of " + place.Value + ": " + all.Count + " in " + found.Count + " meshes, found in " + Mathf.RoundToInt(1000f * (Time.realtimeSinceStartup - began)) + " ms");
            }
        }

        /// <summary>The game's own switch for a place's glowing things: its time-of-day animation, which is on the place or something it is part of.</summary>
        static TimeOfDayAnimation ClockOf(Transform at)
        {
            for (Transform t = at; t != null; t = t.parent)
            {
                TimeOfDayAnimation a = t.GetComponent<TimeOfDayAnimation>();
                if (a != null) return a;
            }
            return null;
        }

        /// <summary>A lamp's own glow, seen from in front: a small bright middle and a soft round glare.</summary>
        static Material Halo()
        {
            if (halo != null) return halo;
            Shader shader = Pools.Additive();
            if (shader == null) return null;
            const int N = 64;
            var dot = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "Natural Light lamp glow" };
            var dots = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
                    float v = (0.5f * Mathf.Exp(-(r / 0.09f) * (r / 0.09f)) + 0.5f / (1f + (r / 0.18f) * (r / 0.18f))) * (1f - Pools.Ramp(0.6f, 1f, r));
                    byte b = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(v));
                    dots[x + y * N] = new Color32(b, b, b, 255);
                }
            dot.SetPixels32(dots);
            dot.Apply(true, true);
            halo = new Material(shader) { mainTexture = dot, name = "Natural Light lamp glow" };
            return halo;
        }

        /// <summary>The pools of light of a row's lamps, laid on the ground, and the cards their glow is drawn on.</summary>
        void Build(Row row)
        {
            CelestialBody body = Daylight.Body;
            row.Rig = new GameObject("Natural Light runway lamps at " + row.Name);
            row.Rig.transform.SetParent(row.At, false);
            Vector3 middle = Vector3.zero;
            var places = new List<Vector3>();
            foreach (Vector3 s in row.Spots) { Vector3 p = row.At.TransformPoint(s); places.Add(p); middle += p; }
            middle /= places.Count;
            Vector3 up = (middle - (Vector3)body.position).normalized;
            // (the long way of the runway, which the rows run along)
            Vector3 along = Vector3.ProjectOnPlane(row.At.TransformDirection(row.Along), up).normalized, across = Vector3.Cross(up, along);
            Matrix4x4 toLocal = row.Rig.transform.worldToLocalMatrix;
            var corners = new List<Vector3>(); var uvs = new List<Vector2>(); var colours = new List<Color>(); var triangles = new List<int>();
            for (int k = 0; k < places.Count; k++)
            {
                Vector3 p = places[k];
                // (the level of the ground the pool lies on is the ground under most of it: under the lamp itself is the lamp)
                float level = float.NaN;
                float size = row.Sizes[k];
                Vector3 la = along * (PoolAlong * size), lc = across * (PoolAcross * size);
                Pools.Lay(p - la - lc, p - la + lc, p + la + lc, p + la - lc, up, 4, toLocal, level, corners, uvs, colours, triangles);
            }
            Material round = Pools.Round();
            if (round != null) row.Pools = Pools.Show("Natural Light runway lamps' pools", row.Rig.transform, Pools.Make("Natural Light runway lamps' pools", corners, uvs, colours, triangles), round);
            Material glow = Halo();
            if (glow != null)
            {
                int n = places.Count;
                var cards = new List<Vector3>(); var cardUvs = new List<Vector2>(); var cardColours = new List<Color>(); var cardTriangles = new List<int>();
                Bounds bounds = default;
                for (int k = 0; k < n; k++)
                {
                    Vector3 local = toLocal.MultiplyPoint3x4(places[k]);
                    if (k == 0) bounds = new Bounds(local, Vector3.zero); else bounds.Encapsulate(local);
                    for (int c = 0; c < 4; c++) { cards.Add(local); cardColours.Add(new Color(1f, 1f, 1f, Mathf.Clamp01(0.6f * row.Sizes[k]))); }
                    cardUvs.Add(new Vector2(0f, 0f)); cardUvs.Add(new Vector2(1f, 0f)); cardUvs.Add(new Vector2(1f, 1f)); cardUvs.Add(new Vector2(0f, 1f));
                    int f = k * 4;
                    cardTriangles.Add(f); cardTriangles.Add(f + 2); cardTriangles.Add(f + 1); cardTriangles.Add(f); cardTriangles.Add(f + 3); cardTriangles.Add(f + 2);
                    cardTriangles.Add(f); cardTriangles.Add(f + 1); cardTriangles.Add(f + 2); cardTriangles.Add(f); cardTriangles.Add(f + 2); cardTriangles.Add(f + 3);
                }
                row.GlowMesh = Pools.Make("Natural Light runway lamps' glow", cards, cardUvs, cardColours, cardTriangles);
                row.GlowMesh.MarkDynamic();
                // (the cards are moved every frame: bounds wide enough for them wherever they are turned)
                bounds.Expand(80f);
                row.GlowMesh.bounds = bounds;
                row.GlowCorners = cards.ToArray();
                row.Lamps = new Vector3[n];
                for (int k = 0; k < n; k++) row.Lamps[k] = toLocal.MultiplyPoint3x4(places[k]);
                row.Glows = Pools.Show("Natural Light runway lamps' glow", row.Rig.transform, row.GlowMesh, glow);
            }
        }

        void LateUpdate()
        {
            bool wanted = Daylight.Near && Daylight.Body != null && Daylight.On && Daylight.Runway != null && Daylight.Runway && !MapView.MapIsEnabled;
            if (wanted && looks < 6 && Time.timeSinceLevelLoad > nextLook)
            {
                nextLook = Time.timeSinceLevelLoad + 5f; looks++;
                Look();
            }
            float strength = Daylight.RunwayLight != null ? (float)Daylight.RunwayLight : 1f;
            // (dark enough: the game's own switch, where it has one for these lamps; else by the sun, as the floodlights go)
            float dusk = Mathf.InverseLerp(0.03f, -0.02f, Daylight.SunUp);
            int lit = 0, lamps = 0;
            foreach (Row row in rows)
            {
                if (row.At == null) { if (row.Rig != null) Destroy(row.Rig); row.Rig = null; continue; }
                bool shown = row.Shown != null && row.Shown.enabled && row.Shown.gameObject.activeInHierarchy;
                // (and within a few kilometres of the craft, as the floodlights and the windows are)
                shown &= (row.At.position - (Vector3)Daylight.Here).sqrMagnitude < 8000f * 8000f;
                float want = wanted && shown ? (row.Clock != null ? Mathf.Clamp01(row.Clock.emissiveColor.a) : dusk) : 0f;
                row.On = wanted ? Mathf.MoveTowards(row.On, want, Mathf.Min(Time.unscaledDeltaTime, 0.1f) * 0.6f) : 0f;
                bool on = row.On > 0.002f;
                if (on && row.Rig == null) Build(row);
                if (row.Rig == null) continue;
                if (row.Pools != null) row.Pools.enabled = on;
                if (row.Glows != null) row.Glows.enabled = on;
                if (!on) continue;
                lit++; lamps += row.Spots.Length;
                float level = row.On * strength;
#if DEV
                level *= loud;
#endif
                // (the legacy additive particle shader adds twice the tint over its picture: 0.5 is as the picture is)
                block.Clear();
                block.SetColor("_TintColor", new Color(Lamp.r * PoolLight * level, Lamp.g * PoolLight * level, Lamp.b * PoolLight * level, 0.5f));
                if (row.Pools != null) row.Pools.SetPropertyBlock(block);
                block.Clear();
                block.SetColor("_TintColor", new Color(Lamp.r * GlowLight * level, Lamp.g * GlowLight * level, Lamp.b * GlowLight * level, 0.5f));
                if (row.Glows != null) row.Glows.SetPropertyBlock(block);
            }
            Nearest(wanted, strength);
            Report = lit > 0 ? "Runway lamps lighting the ground: " + lamps + "." : "";
        }

        readonly List<KeyValuePair<float, Vector3>> close = new List<KeyValuePair<float, Vector3>>();

        /// <summary>Real lamps of the cheap kind at the lamps nearest the craft being flown, for the craft itself (and kerbals): the ground has its pools.</summary>
        void Nearest(bool wanted, float strength)
        {
            close.Clear();
            Vessel v = wanted && HighLogic.LoadedSceneIsFlight && FlightGlobals.ready ? FlightGlobals.ActiveVessel : null;
            // (the middle of the runway is thirty-two metres from either row: a craft there is still lit, faintly, by the nearest lamps)
            const float Reach = 45f;
            if (v != null)
            {
                Vector3 at = v.CoM;
                foreach (Row row in rows)
                {
                    if (row.Rig == null || row.On <= 0.002f || row.At == null) continue;
                    for (int k = 0; k < row.Spots.Length; k++)
                    {
                        Vector3 p = row.At.TransformPoint(row.Spots[k]);
                        float d = (p - at).sqrMagnitude;
                        if (d < Reach * Reach * 1.2f) close.Add(new KeyValuePair<float, Vector3>(d, p + (at - p).normalized * 0.2f + (Vector3)(FlightGlobals.getUpAxis(p)) * 0.8f));
                    }
                }
                close.Sort((a, b) => a.Key.CompareTo(b.Key));
            }
            float on = 0f;
            foreach (Row row in rows) on = Mathf.Max(on, row.On);
            for (int n = 0; n < Nearby; n++)
            {
                if (n >= close.Count) { if (near[n] != null && near[n].enabled) near[n].enabled = false; continue; }
                if (near[n] == null)
                {
                    var holder = new GameObject("Natural Light runway lamp " + n);
                    Light l = holder.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.shadows = LightShadows.None;
                    l.renderMode = LightRenderMode.ForceVertex;
                    // (what stands on the ground, not the ground: that has its pools, and a lamp of this kind on the runway's few great
                    // pieces would be a smear)
                    l.cullingMask = Daylight.Lit & ~(1 << 15);
                    l.color = Lamp;
                    near[n] = l;
                }
                Light lamp = near[n];
                lamp.enabled = true;
                lamp.transform.position = close[n].Value;
                lamp.range = Reach;
                lamp.intensity = 1.1f * on * strength;
            }
        }

        /// <summary>Each lamp's glow is a card turned to face whichever camera is about to draw it, and larger with distance.</summary>
        void Face(Camera cam)
        {
            if (cam == null || rows.Count == 0 || cam.targetTexture != null || (cam.cullingMask & (1 << 15)) == 0) return;
            Transform eye = cam.transform;
            Vector3 from = eye.position, right = eye.right, up = eye.up;
            foreach (Row row in rows)
            {
                if (row.Glows == null || !row.Glows.enabled || row.GlowMesh == null || row.Lamps == null) continue;
                Transform space = row.Glows.transform;
                Matrix4x4 toLocal = space.worldToLocalMatrix;
                float unit = 1f / Mathf.Max(space.lossyScale.x, 1e-4f);
                Vector3 r = toLocal.MultiplyVector(right).normalized, u = toLocal.MultiplyVector(up).normalized;
                Vector3[] corners = row.GlowCorners;
                Vector3 eyeHere = toLocal.MultiplyPoint3x4(from);
                for (int k = 0; k < row.Spots.Length; k++)
                {
                    Vector3 lamp = row.Lamps[k];
                    Vector3 toEye = eyeHere - lamp;
                    float far = toEye.magnitude * Mathf.Max(space.lossyScale.x, 1e-4f);
                    // (the game's edge lamps are two metres across: the glow round one is a little larger than the lamp close to, and
                    // grows with distance so that a far lamp still reads as a lamp)
                    float half = Mathf.Clamp(0.02f * far, 2.4f, 16f) * row.Sizes[k] * unit;
                    // (drawn a little nearer the camera than the lamp: a card standing in the lamp would be half under the ground it stands on)
                    Vector3 middle = lamp + toEye.normalized * Mathf.Min(1.6f * half, 0.5f * toEye.magnitude);
                    Vector3 a = r * half, b = u * half;
                    corners[k * 4] = middle - a - b; corners[k * 4 + 1] = middle + a - b; corners[k * 4 + 2] = middle + a + b; corners[k * 4 + 3] = middle - a + b;
                }
                row.GlowMesh.vertices = corners;
            }
        }

#if DEV
        /// <summary>(For the development build: "queue loud": the pools' and glows' render queue, and a tint many times too strong if loud is 1, to see whether they are drawn at all; and the render queues of what is drawn near the runway.)</summary>
        public static string Queue(string arg)
        {
            string[] w = (arg ?? "").Split(' ');
            if (w.Length > 1 && w[0] == "shader")
            {
                Shader s = Shader.Find(arg.Substring(7));
                if (s == null) return "no shader " + arg.Substring(7);
                var m = new Material(s) { mainTexture = Pools.Round().mainTexture };
                RunwayLamps it = FindObjectOfType<RunwayLamps>();
                int n = 0;
                if (it != null) foreach (Row row in it.rows) if (row.Pools != null) { row.Pools.sharedMaterial = m; n++; }
                return n + " pools now drawn with " + s.name + " (supported " + s.isSupported + ", has _TintColor " + m.HasProperty("_TintColor") + ")";
            }
            int q = w.Length > 0 && int.TryParse(w[0], out int got) ? got : -1;
            loud = w.Length > 1 && w[1] == "1" ? 8f : 1f;
            foreach (Material m in new[] { Pools.Round(), Pools.Spill(), Halo() }) if (m != null && q > 0) m.renderQueue = q;
            var o = new System.Text.StringBuilder("pools at " + (Pools.Round() != null ? Pools.Round().renderQueue.ToString() : "?") + ", loud " + loud + "; near the runway:");
            RunwayLamps me = FindObjectOfType<RunwayLamps>();
            var seen = new HashSet<string>();
            if (me != null)
                foreach (Row row in me.rows)
                    if (row.At != null && row.At.parent != null)
                        foreach (Renderer r in row.At.parent.parent != null ? row.At.parent.parent.GetComponentsInChildren<Renderer>() : row.At.parent.GetComponentsInChildren<Renderer>())
                            if (r != null && r.sharedMaterial != null && seen.Add(r.name + r.sharedMaterial.shader.name)) o.Append(" " + r.name + "[" + r.sharedMaterial.shader.name + " q" + r.sharedMaterial.renderQueue + "]");
            return o.ToString();
        }
        static float loud = 1f;

        /// <summary>(For the development build: what is drawn for the first row with pools: renderer, shader, tint, and where its corners are against its first lamp.)</summary>
        public static string Drawn(string which)
        {
            RunwayLamps me = FindObjectOfType<RunwayLamps>();
            if (me == null) return "not running";
            foreach (Row row in me.rows)
            {
                if (row.Pools == null || row.Name.IndexOf(which ?? "", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var o = new System.Text.StringBuilder(row.Name + ": rig " + (row.Rig != null && row.Rig.activeInHierarchy ? "active" : "inactive") + " layer " + row.Pools.gameObject.layer);
                foreach (MeshRenderer r in new[] { row.Pools, row.Glows })
                {
                    if (r == null) continue;
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    r.GetPropertyBlock(block);
                    o.Append(" | " + r.name + ": enabled " + r.enabled + " visible " + r.isVisible + " shader " + (r.sharedMaterial != null ? r.sharedMaterial.shader.name + (r.sharedMaterial.shader.isSupported ? "" : " (unsupported)") + " queue " + r.sharedMaterial.renderQueue : "none") + " tint " + block.GetColor("_TintColor").ToString("F3")
                        + " has _TintColor " + (r.sharedMaterial != null && r.sharedMaterial.HasProperty("_TintColor")) + " bounds " + r.bounds.center.ToString("F0") + " size " + r.bounds.size.ToString("F0") + " mesh " + (mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount + " corners" : "none"));
                }
                Vector3 lamp = row.At.TransformPoint(row.Spots[0]);
                Vector3 up = FlightGlobals.getUpAxis(lamp);
                Mesh m = row.Pools.GetComponent<MeshFilter>().sharedMesh;
                Vector3[] v = m.vertices; Color[] c = m.colors;
                o.Append(" | first lamp at " + lamp.ToString("F0") + "; corners against it (up, then alpha):");
                Transform t = row.Pools.transform;
                for (int i = 0; i < v.Length && i < 25; i += 3) { Vector3 w = t.TransformPoint(v[i]) - lamp; o.Append(" " + (w - up * Vector3.Dot(w, up)).magnitude.ToString("F0") + "m:" + Vector3.Dot(w, up).ToString("F2") + "/" + (i < c.Length ? c[i].a.ToString("F2") : "?")); }
                if (Physics.Raycast(lamp + up * 6f, -up, out RaycastHit hit, 30f, 1 << 15, QueryTriggerInteraction.Ignore)) o.Append(" | ground under the lamp " + Vector3.Dot(hit.point - lamp, up).ToString("F2") + " (" + hit.collider.name + ")");
                else o.Append(" | no ground found under the lamp");
                return o.ToString();
            }
            return "no pools";
        }

        /// <summary>(For the development build: the lamps found, where, how lit.)</summary>
        public static string Rows()
        {
            RunwayLamps me = FindObjectOfType<RunwayLamps>();
            if (me == null) return "not running";
            var o = new System.Text.StringBuilder(me.rows.Count + " rows after " + me.looks + " looks; ");
            foreach (Row row in me.rows)
            {
                bool shown = false;
                if (row.Shown != null && row.Shown.enabled && row.Shown.gameObject.activeInHierarchy) shown = true;
                o.Append(row.Name + ": " + row.Spots.Length + " lamps" + (shown ? " (drawn)" : " (the game's are not drawn)") + ", the game's switch " + (row.Clock != null ? row.Clock.emissiveColor.a.ToString("F2") : "none") + ", on " + row.On.ToString("F2") + (row.Rig != null ? ", built" : "") + "; sizes");
                for (int k = 0; k < row.Sizes.Length && k < 12; k++) o.Append(' ').Append(row.Sizes[k].ToString("F2"));
                o.Append("; ");
            }
            foreach (Light l in me.near) if (l != null && l.enabled) o.Append("[lamp by the craft " + (l.transform.position - (FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.CoM : Vector3.zero)).magnitude.ToString("F0") + " m off] ");
            return o.ToString();
        }
#endif
    }
}
