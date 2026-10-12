using System;
using System.Collections.Generic;
using Keystone;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// In the assembly building and the hangar, the ground crew stand in the light they are in.
    ///
    /// Asked for as: "In the hangar, when the spaceship is there, the tiny little kerbals on the ground
    /// walking through the rocket's shadow aren't affected. Same for the shadows of the building itself."
    ///
    /// Why they are not, as the game ships: the building is not lit at all while the game runs. Its light
    /// and shade were worked out once by its makers and painted onto its walls and floor (a "lightmap"), and
    /// pools of light under its lamps are pictures laid on the floor. The crew, the trucks and the rocket are
    /// drawn afterwards, by another camera, lit by lamps of their own that are the same everywhere in the
    /// building; so a kerbal is as bright in the darkest corner as under a floodlight. And the rocket's shadow
    /// on the floor is not a shadow on the floor either: it is an unseen sheet lying over it that darkens
    /// what is behind it by four tenths where a faint lamp (three tenths as bright as the others) is hidden
    /// by the rocket. That lamp does shine on the crew, and the rocket does shade them from it; it is so
    /// small a part of their light that nothing shows.
    ///
    /// So each of them is made as bright as the floor it stands on:
    /// * The building's light. A small picture of the floor is taken from straight above, twice: as it is
    ///   drawn, and with only its paint (no light). One divided by the other is the light that falls on each
    ///   spot of the floor, whatever is painted there. Each of the crew is given the light of the spot it is
    ///   over, against the light of the open floor.
    /// * The rocket's shadow. From each of them a line is drawn towards the lamp that throws the rocket's
    ///   shadow; where a part is in the way, it is darkened by as much as the sheet darkens the floor.
    ///
    /// "Each of them" is whatever the game moves as one on the crew's own layer: a kerbal (a dozen pieces
    /// drawn separately), or a piece of a truck. Nothing is added to the scene that is drawn, and no light is
    /// changed: each piece's own colour is turned down, and put back when this is switched off.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public sealed class GroundCrew : MonoBehaviour
    {
        const int CrewLayer = 16, BuildingLayer = 15, PartsLayer = 0;
        const int Across = 192;                     // the floor's picture, across its longer side
        const float Above = 2.4f;                   // how far above the floor it is taken from: over heads, under the gantries
        const float Least = 0.32f, Most = 1.2f;     // (nobody goes black, and nobody glows)

        sealed class Piece
        {
            public Renderer At;                     // the piece of it whose place is taken for all of it
            public readonly List<Renderer> Its = new List<Renderer>();
            public readonly List<Color[]> Was = new List<Color[]>();      // each piece's materials' own colours (alpha below nought: one with no colour to turn down)
            public float Now = 1f, Shown = -1f;
            public float Shade;                     // how much of the rocket's shadow it is in, 0 to 1
        }

        readonly List<Piece> pieces = new List<Piece>();
        int drawnPieces;
        readonly List<Renderer> hidden = new List<Renderer>(), pools = new List<Renderer>(), floors = new List<Renderer>();
        readonly List<MaterialPropertyBlock> kept = new List<MaterialPropertyBlock>();

        static MaterialPropertyBlock Copy(MaterialPropertyBlock of)
        {
            // (a piece that had something set on it by someone else has it back afterwards: its colour is all that can be told of it)
            var copy = new MaterialPropertyBlock();
            copy.SetColor(colour, of.GetColor(colour));
            return copy;
        }
        static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        static readonly int colour = Shader.PropertyToID("_Color"), cutoff = Shader.PropertyToID("_Cutoff");
        static readonly string[] Plain = { "Unlit/Transparent Cutout", "Sprite/Simple Texture (Unlit)", "Unlit/Texture", "KSP/Unlit" };
        int plainTried;
        Light lamp;                                 // the lamp that throws the rocket's shadow
        float dark = 0.4f;                          // how much the floor is darkened in it
        float floor;                                // how high the floor is
        Camera eye;
        RenderTexture drawn, paint;
        Texture2D read;
        Shader plain;
        float[] light;                              // the light on each spot of the floor, against the open floor's (1): nothing yet until looked at
        int wide, deep;
        Rect ground;                                // what the floor's picture covers (x and z)
        float lookAt, findAt;
        int turn, looks;
        string why = "not looked yet";
        public static string Report = "";

        void OnDestroy()
        {
            Restore();
            if (eye != null) Destroy(eye.gameObject);
            if (drawn != null) drawn.Release();
            if (paint != null) paint.Release();
            if (read != null) Destroy(read);
        }

        void Restore()
        {
            foreach (Piece p in pieces)
            {
                if (p.Shown < 0f) continue;
                for (int i = 0; i < p.Its.Count; i++) if (p.Its[i] != null) for (int m = 0; m < p.Was[i].Length; m++) p.Its[i].SetPropertyBlock(null, m);
                p.Shown = -1f;
            }
        }

        bool Wanted =>
#if DEV
            Daylight.Crew == null ||
#endif
            (Daylight.Crew != null && Daylight.Crew && Daylight.On);

        void Update()
        {
            if (!Wanted) { if (pieces.Count > 0) { Restore(); pieces.Clear(); } Report = ""; return; }
            float now = Time.realtimeSinceStartup;
            if (pieces.Count == 0 || now > findAt) { Find(); findAt = now + (pieces.Count == 0 ? 3f : 20f); }
            if (pieces.Count == 0) return;
#if DEV
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            if (now > lookAt) { try { Look(); } catch (Exception ex) { light = null; Report = "The floor could not be looked at (" + ex.Message + ")."; } lookAt = now + (light == null || looks < 3 ? 2.5f : 90f); looks++; }

            // ---- the rocket's shadow: a third of them each frame
            Vector3 towards = lamp != null ? -lamp.transform.forward : Vector3.zero;
            turn = (turn + 1) % 3;
            float ease = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 8f);
            for (int i = 0; i < pieces.Count; i++)
            {
                Piece p = pieces[i];
                if (p.At == null) continue;
                Vector3 at = p.At.bounds.center;
                if (lamp != null && i % 3 == turn)
                {
                    // (three lines a little apart, so that a shadow's edge is soft)
                    int hit = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        Vector3 from = at + Vector3.Cross(towards, Vector3.forward) * ((k - 1) * 0.12f);
                        if (Physics.Raycast(from + towards * 0.25f, towards, 160f, 1 << PartsLayer, QueryTriggerInteraction.Ignore)) hit++;
                    }
                    p.Shade = hit / 3f;
                }
                float want = Mathf.Clamp(Under(at) * (1f - (dark + 0.1f) * p.Shade), Least, Most);
                p.Now = Mathf.Lerp(p.Now, want, ease);
                if (Mathf.Abs(p.Now - p.Shown) < 0.012f) continue;
                p.Shown = p.Now;
                for (int r = 0; r < p.Its.Count; r++)
                {
                    Renderer its = p.Its[r];
                    if (its == null) continue;
                    Color[] was = p.Was[r];
                    for (int m = 0; m < was.Length; m++)
                    {
                        if (was[m].a < 0f) continue;
                        its.GetPropertyBlock(block, m);
                        block.SetColor(colour, new Color(was[m].r * p.Now, was[m].g * p.Now, was[m].b * p.Now, was[m].a));
                        its.SetPropertyBlock(block, m);
                    }
                }
            }
#if DEV
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            spent += ms; frames++; if (ms > worst) worst = ms;
#endif
        }

#if DEV
        double spent, worst; int frames;
#endif

        /// <summary>The light on the floor under a place, against the light on the open floor.</summary>
        float Under(Vector3 at)
        {
            if (light == null || at.x < ground.xMin || at.x > ground.xMax || at.z < ground.yMin || at.z > ground.yMax) return 1f;
            float x = (at.x - ground.xMin) / ground.width * wide - 0.5f, z = (at.z - ground.yMin) / ground.height * deep - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, wide - 1), z0 = Mathf.Clamp(Mathf.FloorToInt(z), 0, deep - 1), x1 = Mathf.Min(x0 + 1, wide - 1), z1 = Mathf.Min(z0 + 1, deep - 1);
            float fx = Mathf.Clamp01(x - x0), fz = Mathf.Clamp01(z - z0);
            return Mathf.Lerp(Mathf.Lerp(light[x0 + z0 * wide], light[x1 + z0 * wide], fx), Mathf.Lerp(light[x0 + z1 * wide], light[x1 + z1 * wide], fx), fz);
        }

        /// <summary>The crew's pieces, the lamp that throws the rocket's shadow, and how dark that shadow is on the floor.</summary>
        void Find()
        {
            var known = new HashSet<Renderer>();
            pieces.RemoveAll(p => p.At == null);
            foreach (Piece p in pieces) foreach (Renderer r in p.Its) if (r != null) known.Add(r);
            // (What moves as one is found by what moves it: a kerbal has one thing that plays its walk. Where that would take in
            // more than a kerbal's width of pieces, each piece is by itself.)
            var groups = new Dictionary<Transform, Piece>();
            foreach (Renderer r in FindObjectsOfType<Renderer>())
            {
                if (r.gameObject.layer != CrewLayer || known.Contains(r) || !(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                Material[] its = r.sharedMaterials;
                var was = new Color[its.Length];
                bool any = false;
                for (int m = 0; m < its.Length; m++)
                {
                    // (a piece that only glows, a lamp or a wand, has no colour to turn down)
                    if (its[m] != null && its[m].HasProperty(colour)) { was[m] = its[m].GetColor(colour); if (was[m].a < 0f) was[m].a = 0f; any = true; }
                    else was[m] = new Color(1f, 1f, 1f, -1f);
                }
                if (!any) continue;
                Animator moved = r.GetComponentInParent<Animator>();
                Animation played = moved == null ? r.GetComponentInParent<Animation>() : null;
                Transform by = moved != null ? moved.transform : played != null ? played.transform : r.transform;
                if (!groups.TryGetValue(by, out Piece p)) groups[by] = p = new Piece { At = r };
                p.Its.Add(r); p.Was.Add(was);
                if (r is SkinnedMeshRenderer && r.bounds.size.sqrMagnitude > p.At.bounds.size.sqrMagnitude) p.At = r;
            }
            foreach (Piece p in groups.Values)
            {
                Bounds all = p.At.bounds;
                foreach (Renderer r in p.Its) all.Encapsulate(r.bounds);
                if (p.Its.Count == 1 || (all.size.x < 5f && all.size.z < 5f)) { pieces.Add(p); continue; }
                for (int i = 0; i < p.Its.Count; i++) { var one = new Piece { At = p.Its[i] }; one.Its.Add(p.Its[i]); one.Was.Add(p.Was[i]); pieces.Add(one); }
            }
            drawnPieces = 0;
            foreach (Piece p in pieces) drawnPieces += p.Its.Count;
            if (lamp == null)
                foreach (Light l in FindObjectsOfType<Light>())
                    if (l.type == LightType.Directional && l.shadows != LightShadows.None && l.isActiveAndEnabled && (l.cullingMask & (1 << PartsLayer)) != 0 && (l.cullingMask & (1 << CrewLayer)) != 0) { lamp = l; break; }
            foreach (Renderer r in FindObjectsOfType<Renderer>())
            {
                Material m = r.sharedMaterial;
                if (m == null || m.shader == null || !m.shader.name.Contains("Shadow Receiver")) continue;
                if (m.HasProperty("_ShadowIntensity")) dark = Mathf.Clamp01(m.GetFloat("_ShadowIntensity"));
                floor = r.bounds.center.y;
                break;
            }
            Report = pieces.Count > 0 ? "The ground crew and their trucks (" + pieces.Count + " of them, in " + drawnPieces + " pieces) take the light of the floor they are on" + (lamp != null ? ", and the rocket's shadow." : ".") : "";
        }

        /// <summary>
        /// Which of the building's pieces are which, for the floor's picture: those with its light painted on, the pools of light
        /// laid over them, and whatever else is in the way. Found once; the building does not change.
        /// </summary>
        void Sort()
        {
            hidden.Clear(); pools.Clear(); floors.Clear();
            Renderer[] all = FindObjectsOfType<Renderer>();
            bool some = false; var over = new Bounds();
            foreach (Renderer r in all)
            {
                if (r.gameObject.layer != BuildingLayer || !r.enabled || r.lightmapIndex < 0 || r.lightmapIndex >= 65534 || r.bounds.min.y > floor + Above) continue;
                floors.Add(r);
                if (!some) { over = r.bounds; some = true; } else over.Encapsulate(r.bounds);
            }
            if (!some || over.size.x > 600f || over.size.z > 600f || over.size.x < 2f || over.size.z < 2f) { floors.Clear(); return; }
            ground = Rect.MinMaxRect(over.min.x - 1f, over.min.z - 1f, over.max.x + 1f, over.max.z + 1f);
            var box = new Bounds(new Vector3(ground.center.x, floor + Above * 0.5f - 0.75f, ground.center.y), new Vector3(ground.width, Above + 1.5f, ground.height));
            foreach (Renderer r in all)
            {
                if (r.gameObject.layer != BuildingLayer || !r.enabled || r.sharedMaterial == null || floors.Contains(r) || !r.bounds.Intersects(box)) continue;
                if (r.sharedMaterial.renderQueue > 2450) pools.Add(r); else hidden.Add(r);
            }
        }

        /// <summary>The floor's picture taken from above, as drawn and as painted, and the one divided by the other.</summary>
        void Look()
        {
            // (A shader that draws a thing's own picture and takes no light. Not every one the game has will draw for this, so the
            // first of these that does is the one used: see the end of this, where one that drew nothing is passed over.)
            while (plain == null && plainTried < Plain.Length) plain = Shader.Find(Plain[plainTried++]);
            if (plain == null) { light = null; why = "no shader here draws a thing's paint alone"; return; }
            // ---- what to look at: the pieces of the building that have its light painted on, as far as they are near the floor
            if (floors.Count == 0 || floors.Exists(r => r == null)) Sort();
            if (floors.Count == 0) { light = null; why = "nothing here has its light painted on"; return; }
            int w = ground.width >= ground.height ? Across : Mathf.Max(8, Mathf.RoundToInt(Across * ground.width / ground.height));
            int d = ground.height > ground.width ? Across : Mathf.Max(8, Mathf.RoundToInt(Across * ground.height / ground.width));
            if (drawn == null || drawn.width != w || drawn.height != d)
            {
                if (drawn != null) { drawn.Release(); paint.Release(); Destroy(read); }
                drawn = new RenderTexture(w, d, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "Natural Light floor, drawn" };
                paint = new RenderTexture(w, d, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "Natural Light floor, paint" };
                read = new Texture2D(w, d, TextureFormat.RGBA32, false, true);
            }
            if (eye == null)
            {
                var holder = new GameObject("Natural Light floor eye");
                holder.transform.SetParent(transform, false);
                eye = holder.AddComponent<Camera>();
                eye.enabled = false;
                eye.orthographic = true; eye.allowHDR = false; eye.allowMSAA = false; eye.useOcclusionCulling = false;
                eye.clearFlags = CameraClearFlags.SolidColor; eye.backgroundColor = new Color(0f, 0f, 0f, 0f);
                eye.cullingMask = 1 << BuildingLayer;
                eye.renderingPath = RenderingPath.Forward;
            }
            // (looking straight down, with the picture's left to right along x and its bottom to top along z)
            eye.transform.position = new Vector3(ground.center.x, floor + Above, ground.center.y);
            eye.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            eye.orthographicSize = ground.height * 0.5f; eye.aspect = ground.width / ground.height;
            eye.nearClipPlane = 0.02f; eye.farClipPlane = Above + 1.5f;

            // What is looked at is only what the building's light was painted onto (its lightmapped pieces) and the pools of light
            // laid over them. Everything else the eye could see is hidden from it while it looks: whatever stands on the floor
            // would be seen from above in place of the floor, and what is outside the building has no painted light to read.
            var shown = new List<Renderer>();
            try
            {
                foreach (Renderer r in hidden) if (r != null && !r.forceRenderingOff) { r.forceRenderingOff = true; shown.Add(r); }
                eye.targetTexture = drawn; eye.Render();
                // (The paint alone: the same pieces drawn with a shader that takes no light, each with its colour set to white for
                // the moment, since these keep there how much they glow by themselves, which is not at all.)
                foreach (Renderer r in pools) if (r != null && !r.forceRenderingOff) { r.forceRenderingOff = true; shown.Add(r); }
                foreach (Renderer r in floors) { r.GetPropertyBlock(block); kept.Add(block.isEmpty ? null : Copy(block)); block.SetColor(colour, Color.white); block.SetFloat(cutoff, 0f); r.SetPropertyBlock(block); }
                eye.targetTexture = paint; eye.RenderWithShader(plain, "");
            }
            finally
            {
                for (int i = 0; i < floors.Count; i++) if (floors[i] != null) floors[i].SetPropertyBlock(i < kept.Count ? kept[i] : null);
                kept.Clear();
                foreach (Renderer r in shown) if (r != null) r.forceRenderingOff = false;
                eye.targetTexture = null;
            }
            Color32[] lit = Grab(drawn), bare = Grab(paint);
            wide = w; deep = d;

            // ---- light = drawn / paint, wherever there is floor
            var raw = new float[w * d];
            var open = new List<float>();
            for (int i = 0; i < raw.Length; i++)
            {
                float b = (bare[i].r * 0.3f + bare[i].g * 0.59f + bare[i].b * 0.11f) / 255f;
                // (nothing drawn there, or paint too dark to tell the light by)
                if (b < 0.03f) { raw[i] = -1f; continue; }
                raw[i] = ((lit[i].r * 0.3f + lit[i].g * 0.59f + lit[i].b * 0.11f) / 255f) / b;
                open.Add(raw[i]);
            }
            if (open.Count < 40 && plainTried < Plain.Length) { plain = null; light = null; why = "looking for a shader that draws the paint"; lookAt = 0f; return; }
            if (open.Count < 40)
            {
                int litSeen = 0, bareSeen = 0;
                for (int i = 0; i < raw.Length; i++) { if (lit[i].r + lit[i].g + lit[i].b > 6) litSeen++; if (bare[i].r + bare[i].g + bare[i].b > 6) bareSeen++; }
                light = null; why = "only " + open.Count + " spots of floor seen in " + w + " x " + d + " over " + ground + " from " + eye.transform.position.ToString("F1") + " (drawn in " + litSeen + ", painted in " + bareSeen + ", with " + plain.name + ")";
                return;
            }
            why = "";
            open.Sort();
            float usual = Mathf.Max(open[(int)(open.Count * 0.7f)], 1e-3f);                    // the open floor's light: what most of the well lit floor has
            // (smoothed over a few steps, across and then along: a kerbal's feet are not a point; and where there is no floor at
            // all near, the open floor's light)
            var sums = new float[w * d]; var counts = new int[w * d];
            for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f; int n = 0;
                    for (int xx = Mathf.Max(0, x - 2); xx <= Mathf.Min(w - 1, x + 2); xx++) { float v = raw[xx + z * w]; if (v >= 0f) { sum += v; n++; } }
                    sums[x + z * w] = sum; counts[x + z * w] = n;
                }
            var smooth = new float[w * d];
            for (int z = 0; z < d; z++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f; int n = 0;
                    for (int zz = Mathf.Max(0, z - 2); zz <= Mathf.Min(d - 1, z + 2); zz++) { sum += sums[x + zz * w]; n += counts[x + zz * w]; }
                    smooth[x + z * w] = n > 0 ? Mathf.Clamp(sum / n / usual, 0f, 2f) : 1f;
                }
            light = smooth;
        }

        Color32[] Grab(RenderTexture from)
        {
            RenderTexture was = RenderTexture.active;
            RenderTexture.active = from;
            read.ReadPixels(new Rect(0, 0, from.width, from.height), 0, 0, false);
            RenderTexture.active = was;
            return read.GetPixels32();
        }

#if DEV
        static string Out => System.IO.Path.GetTempPath();
        /// <summary>(For the development build: the floor as drawn, its paint, and the light worked out of the two, written out as pictures.)</summary>
        public static string Probe()
        {
            GroundCrew it = FindObjectOfType<GroundCrew>();
            if (it == null || it.eye == null || it.paint == null) return "not there, or not looked yet";
            it.Look();
            it.Grab(it.drawn); System.IO.File.WriteAllBytes(Out + "floor_drawn.png", ImageConversion.EncodeToPNG(it.read));
            it.Grab(it.paint); System.IO.File.WriteAllBytes(Out + "floor_paint.png", ImageConversion.EncodeToPNG(it.read));
            if (it.light == null) return "nothing: " + it.why;
            var map = new Texture2D(it.wide, it.deep, TextureFormat.RGBA32, false, true);
            var dots = new Color32[it.wide * it.deep];
            for (int i = 0; i < dots.Length; i++) { byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(it.light[i] * 160f), 0, 255); dots[i] = new Color32(v, v, v, 255); }
            map.SetPixels32(dots); map.Apply();
            System.IO.File.WriteAllBytes(Out + "floor_light.png", ImageConversion.EncodeToPNG(map));
            Destroy(map);
            return it.wide + " x " + it.deep + "; " + it.floors.Count + " painted pieces, " + it.pools.Count + " pools, " + it.hidden.Count + " hidden; with " + it.plain.name;
        }

        /// <summary>(For the development build: the floor's light as last looked at, as a picture's worth of numbers, for seeing that it is the floor's.)</summary>
        public static string Seen()
        {
            GroundCrew it = FindObjectOfType<GroundCrew>();
            if (it == null) return "not there";
            if (it.light == null) return "nothing looked at: " + it.why + " (" + it.pieces.Count + " pieces)";
            var say = new System.Text.StringBuilder();
            say.Append(it.wide).Append(" x ").Append(it.deep).Append(" over ").Append(it.ground).Append("; floor at ").Append(it.floor.ToString("F2")).Append("; shadow ").Append(it.dark.ToString("F2")).Append("; lamp ").Append(it.lamp != null ? it.lamp.name : "none").Append("; ").Append(it.pieces.Count).Append(" pieces\n");
            for (int z = it.deep - 1; z >= 0; z -= 3)
            {
                for (int x = 0; x < it.wide; x += 2) say.Append(" .:-=+*#%@"[Mathf.Clamp(Mathf.RoundToInt(it.light[x + z * it.wide] * 6f), 0, 9)]);
                say.Append('\n');
            }
            int shaded = 0; float least = 9f, most = 0f;
            foreach (Piece p in it.pieces) { if (p.Shade > 0.5f) shaded++; least = Mathf.Min(least, p.Now); most = Mathf.Max(most, p.Now); }
            say.Append(it.pieces.Count).Append(" of them in ").Append(it.drawnPieces).Append(" pieces; ");
            say.Append(shaded).Append(" pieces in the rocket's shadow; brightness from ").Append(least.ToString("F2")).Append(" to ").Append(most.ToString("F2"))
               .Append("; ").Append((it.spent / Mathf.Max(it.frames, 1)).ToString("F3")).Append(" ms a frame over ").Append(it.frames).Append(" frames, the longest ").Append(it.worst.ToString("F2")).Append(" ms; looked ").Append(it.looks).Append(" times");
            it.spent = 0; it.frames = 0; it.worst = 0;
            return say.ToString();
        }
#endif
    }
}
