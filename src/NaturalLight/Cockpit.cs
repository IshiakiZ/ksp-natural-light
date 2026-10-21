using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace NaturalLight
{
    /// <summary>
    /// The windows of a cockpit, and the light that comes in at them.
    ///
    /// The game lights the inside of a cockpit with one flat grey whatever is outside, with lamps of fixed strength, and with a sun of
    /// its own that shines in only where the cockpit's walls let it (their shadows are drawn). Until 0.12 this mod gave the inside six
    /// tenths of the light all round outside instead, the same from every side and everywhere: a cabin as bright as the day outside
    /// it, and evenly lit. But a cabin is lit from its windows. The sill and the frame of a window, and whatever is close in front of
    /// it, take the light of whatever the window looks at (the sky, the ground, a planet below), as bright as it is out there right
    /// at the glass and less the further in; what is turned away from the windows is lit only by what the walls throw back; and when
    /// the sun stands outside a window its patch on the inside is so much brighter than all the rest that what it throws back fills
    /// the cabin.
    ///
    /// So each cockpit's windows are found once, by looking out from where the crew's eyes are, in two thousand directions, to the
    /// nearest of the cockpit's own walls and fittings. Where nothing is met, the way is open: those are the openings through which
    /// the game shows the outside, glass or none. Neighbouring open ways are one window; how far off its frame is (the walls met
    /// just beside it) says where the opening is and how big. Then, every frame, each window is given a lamp of its own just inside
    /// it, shining in: of the light outside in the ways it looks (as the craft turns, the planet comes into it and goes, the sky
    /// reddens), as strong at the window as that light is and fading as a window's light fades, as one over the distance squared
    /// once past its own size. The light thrown back by the walls, of the windows' light and of the sun's where it comes in, is an
    /// even glow from all round.
    ///
    /// The looking is done on another thread from a copy of the cockpit's surfaces (a few tens of thousands of triangles at most, in
    /// a grid so that a ray tests only those near it), once for each kind of cockpit, in a few tens of milliseconds.
    /// </summary>
    static class Cockpits
    {
        /// <summary>Directions looked along from each eye (spread evenly over the sphere).</summary>
        const int Rays = 2048;
        /// <summary>
        /// How much of the light that falls on the inside of a cabin it throws back (grey panels, a white suit, darker fittings): with
        /// the light thrown back again and again, ρ/(1−ρ) of what comes in at the windows ends up in the walls' own glow.
        /// </summary>
        const float Thrown = 0.4f;
        /// <summary>Lamps for the biggest windows only: a cockpit with more has the rest lit by what the walls throw back.</summary>
        const int MostWindows = 4;
        /// <summary>Ways out closer than this (about two rays apart) are one window.</summary>
        static readonly float Together = Mathf.Cos(8f * Mathf.Deg2Rad);

        /// <summary>One opening: where it is and which way it looks, in the cockpit's own frame.</summary>
        sealed class Window
        {
            public Vector3 At;                       // the middle of the opening
            public Vector3 Out;                      // the middle way out through it, from the eye
            public float Area;                       // square metres
            public float Seen;                       // how much of the eye's view it is, steradians
            public List<int> Ways = new List<int>(); // the open ways (indices into Openings.Ways) that look out through it
        }

        /// <summary>What was found for one kind of cockpit.</summary>
        sealed class Openings
        {
            public Vector3[] Ways = new Vector3[0];  // every way out open from any eye, in its own frame
            public float[] Weights = new float[0];   // steradians: a ray's share of the sphere, times the share of the eyes that see out that way
            public float Open;                       // the solid angle open in all, steradians
            public Window[] Windows = new Window[0];
            public float Size;                       // how far the walls are from the eyes, on the whole (metres)
            public volatile bool Done;
            public bool Found;                       // looked at to the end (not given up for want of seats or surfaces)
            public string Said = "being looked at";
            public double Gathered;                  // milliseconds on the game's thread, copying the surfaces
        }

        /// <summary>A copy of a cockpit's surfaces and its crew's eyes, made on the game's thread for the looking.</summary>
        sealed class Job
        {
            public float[] Tri;                      // per triangle: a corner, and the two edges from it (nine numbers)
            public Vector3[] Eyes;
            public Openings Out;
            public int Unreadable;
        }

        /// <summary>One cockpit as it stands in the cockpits' space, with the lamps at its windows.</summary>
        sealed class Placed
        {
            public InternalModel Model;
            public Openings Found;
            public Vector3[] Ways;                   // the ways out, in the cockpits' space
            public Vector3[] Out;                    // the same ways in the scene's frame, worked out each frame
            public Color[] Seen;
            public Light[] Lamps = new Light[0];
            public Bounds Where;
            public Vector3 Facing;                   // the middle way out of its windows, in the cockpits' space
            public SphericalHarmonicsL2 Probe;
            public Color Came;                       // the light come in at the windows, all of it (for the readout)
            public float SunIn;                      // how squarely the sun stood to its windows this frame (0 behind them)
        }

        static readonly Dictionary<string, Openings> kinds = new Dictionary<string, Openings>();
        static readonly Dictionary<InternalModel, Placed> placed = new Dictionary<InternalModel, Placed>();
        static readonly List<InternalModel> gone = new List<InternalModel>();
        static float lookedAt = -10f;
        static volatile bool working;
        static int madeAt = -1;
        static bool lampsOn;
        const string LampName = "Natural Light: the light in at a window";
        /// <summary>(For the development build: the windows' lamps made so many times as strong, to see where they reach.)</summary>
        internal static float LampGain = 1f;

        /// <summary>The cockpits of the craft flown, looked for once a second; any kind not yet seen is set going (one at a time).</summary>
        internal static void Look()
        {
            if (Time.realtimeSinceStartup - lookedAt < 1f) return;
            lookedAt = Time.realtimeSinceStartup;
            gone.Clear();
            foreach (KeyValuePair<InternalModel, Placed> kv in placed) if (kv.Key == null) gone.Add(kv.Key);
            foreach (InternalModel m in gone) placed.Remove(m);
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            foreach (Part part in v.parts)
            {
                InternalModel model = part.internalModel;
                if (model == null || string.IsNullOrEmpty(model.internalName)) continue;
                if (!kinds.TryGetValue(model.internalName, out Openings found))
                {
                    if (working) continue;
                    found = new Openings();
                    kinds[model.internalName] = found;
                    Job job = Gather(model, found);
                    if (job == null) { found.Done = true; continue; }
                    working = true;
                    ThreadPool.QueueUserWorkItem(Work, job);
                }
                if (found.Done && found.Found && !placed.ContainsKey(model)) placed[model] = Place(model, found);
            }
        }

        // ---------------------------------------------------------------- finding the windows

        /// <summary>The cockpit's own surfaces, in its own frame (not the crew, not glass, not the mask that hides other cockpits), and where its crew's eyes are.</summary>
        static Job Gather(InternalModel model, Openings found)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Matrix4x4 toModel = model.transform.worldToLocalMatrix;
            int unreadable = 0;
            // (which of its surfaces are walls and fittings: not the crew, not glass, not the mask, a hull round the whole cabin that
            // writes only depth so that other cockpits are not seen through the windows: it is drawn first of all and takes no light)
            var walls = new List<MeshFilter>();
            long corners3 = 0;
            foreach (MeshFilter mf in model.GetComponentsInChildren<MeshFilter>(false))
            {
                Renderer r = mf.GetComponent<Renderer>();
                Mesh mesh = mf.sharedMesh;
                if (r == null || !r.enabled || mesh == null) continue;
                if (mf.GetComponentInParent<Kerbal>() != null) continue;
                Material mat = r.sharedMaterial;
                if (mat == null || mat.shader == null) continue;
                if (mat.renderQueue >= 2450 || mat.renderQueue < 1500 || mat.shader.name.IndexOf("DepthMask", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!mesh.isReadable) { unreadable++; continue; }
                walls.Add(mf);
                for (int sub = 0; sub < mesh.subMeshCount; sub++) if (mesh.GetTopology(sub) == MeshTopology.Triangles) corners3 += mesh.GetIndexCount(sub);
            }
            // (one list of the size wanted, counted first: grown as it went, it made a few megabytes of garbage, and the collection that
            // followed took the game 50 ms once)
            var tri = new float[corners3 / 3 * 9];
            int at = 0;
            // (a cockpit's fittings share their meshes, dozens of switches to one: each mesh's corners and triangles are read out once)
            var read = new Dictionary<Mesh, KeyValuePair<Vector3[], int[]>>();
            Vector3[] corners = new Vector3[0];
            foreach (MeshFilter mf in walls)
            {
                Mesh mesh = mf.sharedMesh;
                if (!read.TryGetValue(mesh, out KeyValuePair<Vector3[], int[]> data))
                {
                    var all = new List<int>();
                    for (int sub = 0; sub < mesh.subMeshCount; sub++) if (mesh.GetTopology(sub) == MeshTopology.Triangles) all.AddRange(mesh.GetTriangles(sub));
                    data = new KeyValuePair<Vector3[], int[]>(mesh.vertices, all.ToArray());
                    read[mesh] = data;
                }
                Matrix4x4 m = toModel * mf.transform.localToWorldMatrix;
                Vector3[] from = data.Key;
                int[] t = data.Value;
                if (corners.Length < from.Length) corners = new Vector3[from.Length];
                for (int i = 0; i < from.Length; i++) corners[i] = m.MultiplyPoint3x4(from[i]);
                for (int i = 0; i + 2 < t.Length && at + 9 <= tri.Length; i += 3)
                {
                    Vector3 a = corners[t[i]], b = corners[t[i + 1]], c = corners[t[i + 2]];
                    tri[at++] = a.x; tri[at++] = a.y; tri[at++] = a.z;
                    tri[at++] = b.x - a.x; tri[at++] = b.y - a.y; tri[at++] = b.z - a.z;
                    tri[at++] = c.x - a.x; tri[at++] = c.y - a.y; tri[at++] = c.z - a.z;
                }
            }
            var eyes = new List<Vector3>();
            foreach (InternalSeat seat in model.seats)
            {
                if (seat == null) continue;
                if (seat.kerbalRef != null && seat.kerbalRef.eyeTransform != null) eyes.Add(model.transform.InverseTransformPoint(seat.kerbalRef.eyeTransform.position));
                else if (seat.seatTransform != null) eyes.Add(model.transform.InverseTransformPoint(seat.seatTransform.position + seat.seatTransform.up * 0.37f));
            }
            if (eyes.Count == 0 || at == 0) { found.Said = eyes.Count == 0 ? "no seats" : "no surfaces that can be read"; return null; }
            if (at < tri.Length) Array.Resize(ref tri, at);
            found.Gathered = clock.Elapsed.TotalMilliseconds;
            return new Job { Tri = tri, Eyes = eyes.ToArray(), Out = found, Unreadable = unreadable };
        }

        static void Work(object state)
        {
            var job = (Job)state;
            try { Find(job); }
            catch (Exception problem) { job.Out.Said = "failed: " + problem.Message; }
            finally { job.Out.Done = true; working = false; }
        }

        /// <summary>From each eye, the nearest of the cockpit's surfaces in every direction; the open ways gathered into windows.</summary>
        static void Find(Job job)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            float[] tri = job.Tri;
            int count = tri.Length / 9;
            // ---- the triangles sorted into a grid over the cockpit (and its eyes)
            Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), hi = -lo;
            for (int i = 0; i < count; i++)
            {
                int o = i * 9;
                for (int k = 0; k < 3; k++)
                {
                    var p = new Vector3(tri[o] + (k == 1 ? tri[o + 3] : k == 2 ? tri[o + 6] : 0f), tri[o + 1] + (k == 1 ? tri[o + 4] : k == 2 ? tri[o + 7] : 0f), tri[o + 2] + (k == 1 ? tri[o + 5] : k == 2 ? tri[o + 8] : 0f));
                    lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                }
            }
            foreach (Vector3 e in job.Eyes) { lo = Vector3.Min(lo, e); hi = Vector3.Max(hi, e); }
            lo -= Vector3.one * 0.01f; hi += Vector3.one * 0.01f;
            Vector3 size = hi - lo;
            float cell = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) / 24f;
            int nx = Mathf.Max(1, Mathf.CeilToInt(size.x / cell)), ny = Mathf.Max(1, Mathf.CeilToInt(size.y / cell)), nz = Mathf.Max(1, Mathf.CeilToInt(size.z / cell));
            var cells = new List<int>[nx * ny * nz];
            for (int i = 0; i < count; i++)
            {
                int o = i * 9;
                float ax = tri[o], ay = tri[o + 1], az = tri[o + 2];
                int x0 = Mathf.Clamp((int)((Mathf.Min(ax, Mathf.Min(ax + tri[o + 3], ax + tri[o + 6])) - lo.x) / cell), 0, nx - 1), x1 = Mathf.Clamp((int)((Mathf.Max(ax, Mathf.Max(ax + tri[o + 3], ax + tri[o + 6])) - lo.x) / cell), 0, nx - 1);
                int y0 = Mathf.Clamp((int)((Mathf.Min(ay, Mathf.Min(ay + tri[o + 4], ay + tri[o + 7])) - lo.y) / cell), 0, ny - 1), y1 = Mathf.Clamp((int)((Mathf.Max(ay, Mathf.Max(ay + tri[o + 4], ay + tri[o + 7])) - lo.y) / cell), 0, ny - 1);
                int z0 = Mathf.Clamp((int)((Mathf.Min(az, Mathf.Min(az + tri[o + 5], az + tri[o + 8])) - lo.z) / cell), 0, nz - 1), z1 = Mathf.Clamp((int)((Mathf.Max(az, Mathf.Max(az + tri[o + 5], az + tri[o + 8])) - lo.z) / cell), 0, nz - 1);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            int c = x + nx * (y + ny * z);
                            if (cells[c] == null) cells[c] = new List<int>();
                            cells[c].Add(i);
                        }
            }
            // ---- the directions, spread evenly (a spiral over the sphere), each looked along from every eye to the first surface
            var dirs = new Vector3[Rays];
            float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int n = 0; n < Rays; n++)
            {
                float z = 1f - (2f * n + 1f) / Rays, r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z)), a = golden * n;
                dirs[n] = new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
            }
            int eyes = job.Eyes.Length;
            var far = new float[eyes, Rays];
            double walls = 0; int met = 0;
            for (int e = 0; e < eyes; e++)
                for (int n = 0; n < Rays; n++)
                {
                    float t = Nearest(job.Eyes[e], dirs[n], tri, cells, lo, cell, nx, ny, nz);
                    far[e, n] = t;
                    if (t < float.MaxValue) { walls += t; met++; }
                }
            float share = 4f * Mathf.PI / Rays;
            Openings found = job.Out;
            found.Size = met > 0 ? (float)(walls / met) : 1f;
            // ---- the open ways, from any eye
            var index = new int[Rays];
            var ways = new List<Vector3>();
            var weights = new List<float>();
            float open = 0f;
            for (int n = 0; n < Rays; n++)
            {
                int seen = 0;
                for (int e = 0; e < eyes; e++) if (far[e, n] == float.MaxValue) seen++;
                index[n] = -1;
                if (seen == 0) continue;
                float w = share * seen / eyes;
                index[n] = ways.Count;
                ways.Add(dirs[n]); weights.Add(w); open += w;
            }
            // ---- each eye's open ways gathered into windows: neighbours together; where each is, by how far its frame is
            var windows = new List<Window>();
            var taken = new bool[Rays];
            var queue = new List<int>();
            for (int e = 0; e < eyes; e++)
            {
                Array.Clear(taken, 0, Rays);
                for (int n0 = 0; n0 < Rays; n0++)
                {
                    if (taken[n0] || far[e, n0] != float.MaxValue) continue;
                    queue.Clear(); queue.Add(n0); taken[n0] = true;
                    for (int q = 0; q < queue.Count; q++)
                        for (int n = 0; n < Rays; n++)
                            if (!taken[n] && far[e, n] == float.MaxValue && Vector3.Dot(dirs[queue[q]], dirs[n]) > Together) { taken[n] = true; queue.Add(n); }
                    if (queue.Count < 3) continue;                                   // (a crack between two of the model's pieces, not a window)
                    Vector3 mid = Vector3.zero;
                    foreach (int n in queue) mid += dirs[n];
                    mid.Normalize();
                    // (the frame: the surfaces met just beside the open ways)
                    double frame = 0; int edge = 0;
                    for (int n = 0; n < Rays; n++)
                    {
                        if (far[e, n] == float.MaxValue) continue;
                        foreach (int m in queue) if (Vector3.Dot(dirs[m], dirs[n]) > Together) { frame += far[e, n]; edge++; break; }
                    }
                    float reach = edge > 0 ? (float)(frame / edge) : found.Size;
                    var w = new Window { Out = mid, Seen = queue.Count * share, At = job.Eyes[e] + mid * reach };
                    w.Area = w.Seen * reach * reach;
                    foreach (int n in queue) if (index[n] >= 0) w.Ways.Add(index[n]);
                    // (the same window seen from another seat is one window)
                    Window same = null;
                    foreach (Window other in windows) if ((other.At - w.At).sqrMagnitude < 0.3f * 0.3f) { same = other; break; }
                    if (same == null) windows.Add(w);
                    else if (w.Area > same.Area) { same.At = w.At; same.Out = w.Out; same.Area = w.Area; same.Seen = w.Seen; foreach (int k in w.Ways) if (!same.Ways.Contains(k)) same.Ways.Add(k); }
                    else foreach (int k in w.Ways) if (!same.Ways.Contains(k)) same.Ways.Add(k);
                }
            }
            windows.Sort((a, b) => b.Area.CompareTo(a.Area));
            if (windows.Count > MostWindows) windows.RemoveRange(MostWindows, windows.Count - MostWindows);
            found.Ways = ways.ToArray();
            found.Weights = weights.ToArray();
            found.Open = open;
            found.Windows = windows.ToArray();
            found.Found = true;
            var said = new System.Text.StringBuilder(count + " surfaces, " + eyes + " eye(s), walls " + found.Size.ToString("F2") + " m off, " + (open / (4f * Mathf.PI) * 100f).ToString("F1") + "% of the view open (" + open.ToString("F2") + " sr), " + windows.Count + " window(s)");
            foreach (Window w in windows) said.Append(" [" + (w.Area * 1e4f).ToString("F0") + " cm² at " + w.At.ToString("F2") + " looking " + w.Out.ToString("F2") + "]");
            if (job.Unreadable > 0) said.Append(", " + job.Unreadable + " surfaces that could not be read (taken as open)");
            said.Append(", copied in " + found.Gathered.ToString("F1") + " ms on the game's thread, looked at in " + clock.ElapsedMilliseconds + " ms on another");
            found.Said = said.ToString();
        }

        /// <summary>How far a ray from the eye goes before it meets one of the cockpit's surfaces (either side); float.MaxValue if none. Walked through the grid cell by cell.</summary>
        static float Nearest(Vector3 o, Vector3 d, float[] tri, List<int>[] cells, Vector3 lo, float cell, int nx, int ny, int nz)
        {
            int ix = Mathf.Clamp((int)((o.x - lo.x) / cell), 0, nx - 1), iy = Mathf.Clamp((int)((o.y - lo.y) / cell), 0, ny - 1), iz = Mathf.Clamp((int)((o.z - lo.z) / cell), 0, nz - 1);
            int sx = d.x > 0f ? 1 : -1, sy = d.y > 0f ? 1 : -1, sz = d.z > 0f ? 1 : -1;
            float tx = Mathf.Abs(d.x) > 1e-9f ? (lo.x + (ix + (sx > 0 ? 1 : 0)) * cell - o.x) / d.x : float.MaxValue;
            float ty = Mathf.Abs(d.y) > 1e-9f ? (lo.y + (iy + (sy > 0 ? 1 : 0)) * cell - o.y) / d.y : float.MaxValue;
            float tz = Mathf.Abs(d.z) > 1e-9f ? (lo.z + (iz + (sz > 0 ? 1 : 0)) * cell - o.z) / d.z : float.MaxValue;
            float dx = Mathf.Abs(d.x) > 1e-9f ? cell / Mathf.Abs(d.x) : float.MaxValue;
            float dy = Mathf.Abs(d.y) > 1e-9f ? cell / Mathf.Abs(d.y) : float.MaxValue;
            float dz = Mathf.Abs(d.z) > 1e-9f ? cell / Mathf.Abs(d.z) : float.MaxValue;
            float best = float.MaxValue;
            while (true)
            {
                List<int> here = cells[ix + nx * (iy + ny * iz)];
                if (here != null)
                    foreach (int i in here)
                    {
                        float t = Hit(o, d, tri, i * 9);
                        if (t < best) best = t;
                    }
                // (a surface met in this cell nearer than where the ray leaves it is the nearest; one further on may yet be beaten)
                float leave = Mathf.Min(tx, Mathf.Min(ty, tz));
                if (best <= leave) return best;
                if (tx < ty && tx < tz) { ix += sx; if (ix < 0 || ix >= nx) return best; tx += dx; }
                else if (ty < tz) { iy += sy; if (iy < 0 || iy >= ny) return best; ty += dy; }
                else { iz += sz; if (iz < 0 || iz >= nz) return best; tz += dz; }
            }
        }

        /// <summary>A ray against one triangle, from either side (Möller and Trumbore's test): how far along, past the first millimetre; float.MaxValue if it misses.</summary>
        static float Hit(Vector3 o, Vector3 d, float[] t, int at)
        {
            float e1x = t[at + 3], e1y = t[at + 4], e1z = t[at + 5], e2x = t[at + 6], e2y = t[at + 7], e2z = t[at + 8];
            float px = d.y * e2z - d.z * e2y, py = d.z * e2x - d.x * e2z, pz = d.x * e2y - d.y * e2x;
            float det = e1x * px + e1y * py + e1z * pz;
            if (det > -1e-12f && det < 1e-12f) return float.MaxValue;
            float inv = 1f / det;
            float sx = o.x - t[at], sy = o.y - t[at + 1], sz = o.z - t[at + 2];
            float u = (sx * px + sy * py + sz * pz) * inv;
            if (u < 0f || u > 1f) return float.MaxValue;
            float qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
            float v = (d.x * qx + d.y * qy + d.z * qz) * inv;
            if (v < 0f || u + v > 1f) return float.MaxValue;
            float along = (e2x * qx + e2y * qy + e2z * qz) * inv;
            return along > 1e-3f ? along : float.MaxValue;
        }

        // ---------------------------------------------------------------- the light that comes in

        /// <summary>The openings of a kind of cockpit, turned to where this one stands in the cockpits' space, with a lamp at each window.</summary>
        static Placed Place(InternalModel model, Openings found)
        {
            var p = new Placed { Model = model, Found = found };
            int n = found.Ways.Length;
            p.Ways = new Vector3[n]; p.Out = new Vector3[n]; p.Seen = new Color[n];
            Vector3 mean = Vector3.zero;
            for (int i = 0; i < n; i++) { p.Ways[i] = model.transform.TransformDirection(found.Ways[i]); mean += p.Ways[i] * found.Weights[i]; }
            p.Facing = mean.sqrMagnitude > 1e-8f ? mean.normalized : Vector3.zero;
            bool any = false;
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(false))
            {
                if (!any) { p.Where = r.bounds; any = true; } else p.Where.Encapsulate(r.bounds);
            }
            if (!any) p.Where = new Bounds(model.transform.position, Vector3.one);
            // (any left by a copy of this mod that has since been replaced, in a development build, would go on shining as they were)
            foreach (Light old in model.GetComponentsInChildren<Light>(true)) if (old != null && old.name == LampName) UnityEngine.Object.Destroy(old.gameObject);
            p.Lamps = new Light[found.Windows.Length];
            for (int k = 0; k < found.Windows.Length; k++)
            {
                Window w = found.Windows[k];
                var holder = new GameObject(LampName);
                holder.transform.SetParent(model.transform, false);
                // (just inside the opening, shining in)
                holder.transform.localPosition = w.At - w.Out * 0.03f;
                holder.transform.localRotation = Quaternion.LookRotation(-w.Out);
                Light lamp = holder.AddComponent<Light>();
                lamp.type = LightType.Spot;
                lamp.spotAngle = 150f;
                lamp.shadows = LightShadows.None;
                lamp.renderMode = LightRenderMode.Auto;
                lamp.cullingMask = (1 << 16) | (1 << 20);              // (the insides of cockpits, and nothing else)
                // A window of area A whose light is L lights what faces it from d away by L·A/(π·d²), and by L at the glass. Unity's lamps
                // fade as 1/(1 + 25·(d/range)²): so a lamp of strength L whose range is √(25·A/π) is the window, near and far.
                lamp.range = Mathf.Clamp(Mathf.Sqrt(25f * w.Area / Mathf.PI), 0.3f, 4f);
                lamp.intensity = 0f;
                lamp.enabled = lampsOn;
                p.Lamps[k] = lamp;
            }
            return p;
        }

        /// <summary>The cockpit a camera of the insides is in: the one whose bounds hold it, or else the nearest.</summary>
        static Placed For(Camera cam)
        {
            Vector3 at = cam.transform.position;
            Placed best = null;
            float bestFar = float.MaxValue;
            foreach (KeyValuePair<InternalModel, Placed> kv in placed)
            {
                if (kv.Key == null) continue;
                if (kv.Value.Where.Contains(at)) return kv.Value;
                float far = (kv.Value.Where.center - at).sqrMagnitude;
                if (far < bestFar) { bestFar = far; best = kv.Value; }
            }
            return best;
        }

        /// <summary>
        /// The light for a camera of the insides: its cockpit's window lamps given the light outside (a probe in the scene's frame) in the
        /// ways each window looks, and the even glow of what the walls throw back of it and of the sun (as the scene drew it), with the
        /// cabin's own. False where the camera's cockpit is not known yet, and the light is to be as before.
        /// </summary>
        internal static bool Probe(Camera cam, SphericalHarmonicsL2 outside, Color glow, Color sun, out SphericalHarmonicsL2 inside)
        {
            inside = default(SphericalHarmonicsL2);
            if (madeAt != Time.frameCount)
            {
                madeAt = Time.frameCount;
#if DEV
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                foreach (KeyValuePair<InternalModel, Placed> kv in placed) if (kv.Key != null) Make(kv.Value, outside, glow, sun);
                if (!lampsOn) Lamps(true);
#if DEV
                frameTicks += System.Diagnostics.Stopwatch.GetTimestamp() - began; frames++;
#endif
            }
            Placed p = For(cam);
            if (p == null) return false;
            inside = p.Probe;
            return true;
        }

        static readonly float[] rgb = new float[3];

        static void Make(Placed p, SphericalHarmonicsL2 outside, Color glow, Color sun)
        {
            Quaternion toScene = InternalSpace.Instance != null && FlightGlobals.ActiveVessel != null ? InternalSpace.InternalToWorld(Quaternion.identity) : Quaternion.identity;
            int n = p.Ways.Length;
            for (int i = 0; i < n; i++) p.Out[i] = toScene * p.Ways[i];
            if (n > 0) outside.Evaluate(p.Out, p.Seen);
            float[] w = p.Found.Weights;
            Color came = Color.black;
            for (int i = 0; i < n; i++) came += new Color(Mathf.Max(0f, p.Seen[i].r), Mathf.Max(0f, p.Seen[i].g), Mathf.Max(0f, p.Seen[i].b)) * w[i];
            // ---- each window's lamp: the light outside in the ways it looks, as it shows (the lamp is added to the picture as it is shown)
            Window[] windows = p.Found.Windows;
            for (int k = 0; k < windows.Length && k < p.Lamps.Length; k++)
            {
                Light lamp = p.Lamps[k];
                if (lamp == null) continue;
                Color sum = Color.black; float weight = 0f;
                foreach (int i in windows[k].Ways) { sum += new Color(Mathf.Max(0f, p.Seen[i].r), Mathf.Max(0f, p.Seen[i].g), Mathf.Max(0f, p.Seen[i].b)) * w[i]; weight += w[i]; }
                Color light = weight > 0f ? (sum / weight).gamma : Color.black;
                float most = Mathf.Max(light.r, Mathf.Max(light.g, light.b));
                lamp.color = most > 1e-4f ? new Color(light.r / most, light.g / most, light.b / most) : Color.black;
                lamp.intensity = most * LampGain;
            }
            // ---- what the walls throw back, of the windows' light and of the sun's where it comes in at them: an even glow from all round
            // (The sun's patch: as much as comes in at the windows, seen from the eyes, turned to the sun: the windows' share of the view
            // times how squarely they face it, of the sunlight as the scene's camera drew it, dimmed by the clouds and reddened by the air.)
            var made = new SphericalHarmonicsL2();
            made.AddAmbientLight(came * (Thrown / (1f - Thrown) / (4f * Mathf.PI)));
            p.SunIn = 0f;
            if (Planetarium.fetch != null && Planetarium.fetch.Sun != null && FlightGlobals.ActiveVessel != null && p.Facing != Vector3.zero)
            {
                Vector3 toSun = ((Vector3)(Planetarium.fetch.Sun.position - FlightGlobals.ActiveVessel.CoMD)).normalized;
                p.SunIn = Mathf.Max(0f, Vector3.Dot(toScene * p.Facing, toSun));
                made.AddAmbientLight(sun * (p.SunIn * p.Found.Open / (4f * Mathf.PI) * Thrown / (1f - Thrown)));
            }
            made.AddAmbientLight(glow);
            p.Probe = made; p.Came = came;
        }

        /// <summary>The windows' lamps on, or off (the light inside as the game has it, or as it was before 0.12).</summary>
        internal static void Lamps(bool on)
        {
            lampsOn = on;
            foreach (KeyValuePair<InternalModel, Placed> kv in placed)
                if (kv.Key != null)
                    foreach (Light lamp in kv.Value.Lamps) if (lamp != null) lamp.enabled = on;
        }

#if DEV
        static long frameTicks; static int frames;
#endif

        /// <summary>For the readout and the development build: what is known of each kind of cockpit, and how each placed one is lit.</summary>
        internal static string Said()
        {
            var o = new System.Text.StringBuilder();
#if DEV
            if (frames > 0) o.Append("each frame's work " + (frameTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / frames).ToString("F1") + " µs on the mean over " + frames + " frames; ");
            frameTicks = 0; frames = 0;
#endif
            foreach (KeyValuePair<string, Openings> kv in kinds) o.Append(kv.Key + ": " + kv.Value.Said + "; ");
            foreach (KeyValuePair<InternalModel, Placed> kv in placed)
            {
                if (kv.Key == null) continue;
                o.Append(kv.Key.internalName + " in " + (kv.Key.part != null ? kv.Key.part.partInfo.title : "?") + ": light come in " + kv.Value.Came + ", the sun " + kv.Value.SunIn.ToString("F2") + " square to its windows");
                foreach (Light lamp in kv.Value.Lamps) if (lamp != null) o.Append(", a window's lamp " + lamp.color + " x " + lamp.intensity.ToString("F3") + " to " + lamp.range.ToString("F2") + " m" + (lamp.enabled ? "" : " (off)"));
                o.Append("; ");
            }
            return o.ToString();
        }

        /// <summary>The middle way out of a cockpit's windows, in its own frame (weighted by how much of the view each way is); nothing where none are known.</summary>
        internal static Vector3 WindowWay(InternalModel model)
        {
            if (model == null || !kinds.TryGetValue(model.internalName, out Openings found) || !found.Found) return Vector3.zero;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < found.Ways.Length; i++) sum += found.Ways[i] * found.Weights[i];
            return sum.sqrMagnitude > 1e-8f ? sum.normalized : Vector3.zero;
        }

        /// <summary>For the development build: the ways out of the cockpit a camera is in, as a map of the sphere (in its own frame: across, the turn about its up; down the page, from its up to its down), "#" where open, a digit where a window's middle is.</summary>
        internal static string Map(Camera cam)
        {
            Placed p = cam != null ? For(cam) : null;
            if (p == null) return "no cockpit";
            const int W = 72, H = 30;
            var grid = new char[H, W];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) grid[y, x] = '.';
            Action<Vector3, char> put = (d, c) =>
            {
                float turn = Mathf.Atan2(d.x, d.z), up = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f));
                grid[Mathf.Clamp((int)((0.5f - up / Mathf.PI) * H), 0, H - 1), Mathf.Clamp((int)((turn / Mathf.PI * 0.5f + 0.5f) * W), 0, W - 1)] = c;
            };
            foreach (Vector3 d in p.Found.Ways) put(d, '#');
            for (int k = 0; k < p.Found.Windows.Length; k++) put(p.Found.Windows[k].Out, (char)('1' + k));
            var o = new System.Text.StringBuilder("the turn from +z (middle) round through +x (right); up at the top\n");
            for (int y = 0; y < H; y++) { for (int x = 0; x < W; x++) o.Append(grid[y, x]); o.Append('\n'); }
            return o.ToString();
        }

        /// <summary>Forgotten, for a development build's reload and for a test (the kinds looked at again; the lamps go with their cockpits' next look).</summary>
        internal static void Forget()
        {
            foreach (KeyValuePair<InternalModel, Placed> kv in placed)
                foreach (Light lamp in kv.Value.Lamps) if (lamp != null) UnityEngine.Object.Destroy(lamp.gameObject);
            kinds.Clear(); placed.Clear(); working = false;
        }
    }
}
