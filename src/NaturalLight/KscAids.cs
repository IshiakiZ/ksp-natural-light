#if DEV
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>For looking into the space centre's lamps and windows, in the development build only (called through the bridge's dev_call).</summary>
    public static class KscAids
    {
        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 7; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts.ToArray());
        }

        static string Mask(Material m)
        {
            Texture t = m != null && m.HasProperty("_Emissive") ? m.GetTexture("_Emissive") : null;
            return t != null ? t.name + " " + t.width + "x" + t.height : "none";
        }

        static void Frame(Vector3 at, out Vector3 up, out Vector3 north, out Vector3 east)
        {
            CelestialBody body = Daylight.Body != null ? Daylight.Body : FlightGlobals.GetHomeBody();
            up = (at - (Vector3)body.position).normalized;
            north = Vector3.ProjectOnPlane((Vector3)body.transform.up, up).normalized;
            east = Vector3.Cross(up, north);
        }

        /// <summary>The game's own animators of glowing materials by the time of day: what each drives, and how far on it is.</summary>
        public static string TimeOfDay()
        {
            var o = new StringBuilder();
            FieldInfo instances = typeof(TimeOfDayAnimation.MaterialProperty).GetField("instances", BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (TimeOfDayAnimation a in UnityEngine.Object.FindObjectsOfType<TimeOfDayAnimation>())
            {
                o.Append(Path(a.transform)).Append(": dot ").Append(a.dot.ToString("F3")).Append(" colour ").Append(a.emissiveColor.ToString("F3")).Append(" target ").Append(a.emissiveTgtColor.ToString("F3")).Append(" property ").Append(a.emissiveColorProperty);
                if (a.emissivesCurve != null) { o.Append(" curve"); foreach (Keyframe k in a.emissivesCurve.keys) o.Append(' ').Append(k.time.ToString("F2")).Append(':').Append(k.value.ToString("F2")); }
                o.Append('\n');
                if (a.emissives == null) continue;
                foreach (TimeOfDayAnimation.MaterialProperty m in a.emissives)
                {
                    if (m == null) continue;
                    Material mat = m.material;
                    int n = -1;
                    if (instances != null && instances.GetValue(m) is System.Collections.IList list) n = list.Count;
                    o.Append("    ").Append(mat != null ? mat.name + " [" + (mat.shader != null ? mat.shader.name : "?") + "] mask " + Mask(mat) + " own " + (mat.HasProperty(m.propertyName) ? mat.GetColor(m.propertyName).ToString("F2") : "-") : "no material")
                     .Append(" property ").Append(m.propertyName).Append(", on ").Append(n).Append(" renderers\n");
                }
            }
            return o.Length > 0 ? o.ToString() : "no TimeOfDayAnimation in the scene";
        }

        /// <summary>Every renderer drawn with a glowing material whose glow picture (or material) has this in its name ("" for all): where it is, its mesh, whether that can be read.</summary>
        public static string Glowing(string filter)
        {
            filter = filter ?? "";
            var o = new StringBuilder();
            Vector3 here = Daylight.Near ? (Vector3)Daylight.Here : Vector3.zero;
            int shown = 0;
            var block = new MaterialPropertyBlock();
            foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || r is ParticleSystemRenderer) continue;
                Material[] ms = r.sharedMaterials;
                for (int i = 0; i < ms.Length; i++)
                {
                    Material m = ms[i];
                    if (m == null || !m.HasProperty("_EmissiveColor")) continue;
                    string mask = Mask(m);
                    if (filter.Length > 0 && mask.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && m.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    Mesh mesh = mf != null ? mf.sharedMesh : r is SkinnedMeshRenderer s ? s.sharedMesh : null;
                    r.GetPropertyBlock(block);
                    o.Append(Path(r.transform)).Append(" layer ").Append(r.gameObject.layer).Append(r.enabled && r.gameObject.activeInHierarchy ? "" : " (hidden)")
                     .Append(" | ").Append(i).Append(' ').Append(m.name).Append(" [").Append(m.shader != null ? m.shader.name : "?").Append("] mask ").Append(mask)
                     .Append(m.HasProperty("_Emissive") ? " st " + m.GetTextureScale("_Emissive").ToString("F2") + m.GetTextureOffset("_Emissive").ToString("F2") : "")
                     .Append(m.HasProperty("_MainTex") && m.mainTexture != null ? " main " + m.mainTexture.name : "")
                     .Append(" | colour ").Append(m.GetColor("_EmissiveColor").ToString("F2")).Append(block.isEmpty ? "" : " block " + block.GetColor("_EmissiveColor").ToString("F2"))
                     .Append(" | mesh ").Append(mesh != null ? mesh.name + " " + mesh.vertexCount + " corners " + mesh.subMeshCount + " parts" + (mesh.isReadable ? " readable" : " NOT readable") : "none")
                     .Append(" | ").Append((r.bounds.center - here).magnitude.ToString("F0")).Append(" m off, size ").Append(r.bounds.size.ToString("F1")).Append(" scale ").Append(r.transform.lossyScale.ToString("F2")).Append('\n');
                    if (++shown >= 150) return o.Append("(and more)").ToString();
                }
            }
            return o.Length > 0 ? o.ToString() : "nothing glows here";
        }

        /// <summary>A picture as dots that can be read (it is drawn into something readable first: the game's own cannot be read).</summary>
        internal static Color32[] Read(Texture t, out int w, out int h)
        {
            w = Mathf.Clamp(t.width, 1, 2048); h = Mathf.Clamp(t.height, 1, 2048);
            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture was = RenderTexture.active;
            Graphics.Blit(t, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            copy.Apply(false);
            RenderTexture.active = was;
            RenderTexture.ReleaseTemporary(rt);
            Color32[] dots = copy.GetPixels32();
            UnityEngine.Object.Destroy(copy);
            return dots;
        }

        static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out float u, out float v, out float w)
        {
            Vector2 e0 = b - a, e1 = c - a, e2 = p - a;
            float d00 = Vector2.Dot(e0, e0), d01 = Vector2.Dot(e0, e1), d11 = Vector2.Dot(e1, e1), d20 = Vector2.Dot(e2, e0), d21 = Vector2.Dot(e2, e1);
            float den = d00 * d11 - d01 * d01;
            u = v = w = 0f;
            if (Mathf.Abs(den) < 1e-14f) return false;
            v = (d11 * d20 - d01 * d21) / den;
            w = (d00 * d21 - d01 * d20) / den;
            u = 1f - v - w;
            return u >= -1e-4f && v >= -1e-4f && w >= -1e-4f;
        }

        /// <summary>
        /// Where the glowing spots of every material whose glow picture has this in its name are, found from the meshes and the picture: each
        /// triangle is looked over in the picture, and every bright dot of it taken back to where it is on the triangle. Spots within two metres
        /// of one another are one. Said in metres along and across the line they lie most along, from their middle.
        /// </summary>
        public static string Spots(string filter)
        {
            filter = filter ?? "runway";
            float size = 2f;
            string[] said = filter.Split(' ');
            if (said.Length > 1) { filter = said[0]; size = float.Parse(said[1], System.Globalization.CultureInfo.InvariantCulture); }
            string inPath = said.Length > 2 ? said[2] : "";
            Vector3 hereNow = Daylight.Near ? (Vector3)Daylight.Here : Vector3.zero;
            var o = new StringBuilder();
            var points = new List<Vector3>();
            var pictures = new Dictionary<Texture, Color32[]>();
            var sizes = new Dictionary<Texture, Vector2Int>();
            foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || r is ParticleSystemRenderer || !r.enabled || !r.gameObject.activeInHierarchy || (r.bounds.center - hereNow).magnitude > 6000f || (inPath.Length > 0 && Path(r.transform).IndexOf(inPath, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                Material[] ms = r.sharedMaterials;
                for (int i = 0; i < ms.Length; i++)
                {
                    Material m = ms[i];
                    if (m == null || !m.HasProperty("_Emissive") || Mask(m).IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    Mesh mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null || !mesh.isReadable) { o.Append(Path(r.transform)).Append(": mesh ").Append(mesh == null ? "none" : "not readable").Append('\n'); continue; }
                    Texture tex = m.GetTexture("_Emissive");
                    if (!pictures.TryGetValue(tex, out Color32[] dots)) { dots = Read(tex, out int tw, out int th); pictures[tex] = dots; sizes[tex] = new Vector2Int(tw, th); }
                    int W = sizes[tex].x, H = sizes[tex].y;
                    Vector2 scale = m.GetTextureScale("_Emissive"), offset = m.GetTextureOffset("_Emissive");
                    Vector3[] v = mesh.vertices; Vector2[] uv = mesh.uv;
                    if (uv == null || uv.Length != v.Length) { o.Append(Path(r.transform)).Append(": no uv\n"); continue; }
                    int[] tris = i < mesh.subMeshCount ? mesh.GetTriangles(i) : mesh.triangles;
                    Matrix4x4 world = r.transform.localToWorldMatrix;
                    int bright = 0, looked = 0;
                    Vector2 lo = new Vector2(float.MaxValue, float.MaxValue), hi = new Vector2(float.MinValue, float.MinValue);
                    for (int t = 0; t + 2 < tris.Length && points.Count < 400000; t += 3)
                    {
                        Vector2 a = Vector2.Scale(uv[tris[t]], scale) + offset, b = Vector2.Scale(uv[tris[t + 1]], scale) + offset, c = Vector2.Scale(uv[tris[t + 2]], scale) + offset;
                        lo = Vector2.Min(lo, Vector2.Min(a, Vector2.Min(b, c))); hi = Vector2.Max(hi, Vector2.Max(a, Vector2.Max(b, c)));
                        float minU = Mathf.Min(a.x, Mathf.Min(b.x, c.x)) * W, maxU = Mathf.Max(a.x, Mathf.Max(b.x, c.x)) * W;
                        float minV = Mathf.Min(a.y, Mathf.Min(b.y, c.y)) * H, maxV = Mathf.Max(a.y, Mathf.Max(b.y, c.y)) * H;
                        float area = Mathf.Max(1f, (maxU - minU) * (maxV - minV));
                        float step = area > 6000f ? Mathf.Sqrt(area / 6000f) : 0.7f;
                        for (float y = minV; y <= maxV; y += step)
                            for (float x = minU; x <= maxU; x += step)
                            {
                                looked++;
                                if (!Inside(new Vector2(x / W, y / H), a, b, c, out float l0, out float l1, out float l2)) continue;
                                int tx = ((int)Mathf.Floor(x) % W + W) % W, ty = ((int)Mathf.Floor(y) % H + H) % H;
                                Color32 d = dots[tx + ty * W];
                                if (Mathf.Max(d.r, Mathf.Max(d.g, d.b)) < 110) continue;
                                points.Add(world.MultiplyPoint3x4(v[tris[t]] * l0 + v[tris[t + 1]] * l1 + v[tris[t + 2]] * l2));
                                bright++;
                            }
                    }
                    o.Append(Path(r.transform)).Append(": ").Append(tris.Length / 3).Append(" triangles, uv ").Append(lo.ToString("F2")).Append(" to ").Append(hi.ToString("F2")).Append(", ").Append(looked).Append(" looked at, ").Append(bright).Append(" bright\n");
                }
            }
            if (points.Count == 0) return o.Append("no bright spots found").ToString();
            // (spots within two metres of one another are one)
            var cell = new Dictionary<long, int>();
            var sums = new List<Vector3>(); var counts = new List<int>();
            foreach (Vector3 p in points)
            {
                int cx = Mathf.FloorToInt(p.x / size), cy = Mathf.FloorToInt(p.y / size), cz = Mathf.FloorToInt(p.z / size);
                int found = -1;
                for (int dx = -1; dx <= 1 && found < 0; dx++)
                    for (int dy = -1; dy <= 1 && found < 0; dy++)
                        for (int dz = -1; dz <= 1 && found < 0; dz++)
                            if (cell.TryGetValue(((long)(cx + dx) * 73856093L) ^ ((long)(cy + dy) * 19349663L) ^ ((long)(cz + dz) * 83492791L), out int k)) found = k;
                long key = ((long)cx * 73856093L) ^ ((long)cy * 19349663L) ^ ((long)cz * 83492791L);
                if (found < 0) { found = sums.Count; sums.Add(Vector3.zero); counts.Add(0); }
                if (!cell.ContainsKey(key)) cell[key] = found;
                sums[found] += p; counts[found]++;
            }
            var spots = new List<Vector3>();
            Vector3 mean = Vector3.zero;
            for (int k = 0; k < sums.Count; k++) { spots.Add(sums[k] / counts[k]); mean += spots[k]; }
            mean /= spots.Count;
            Frame(mean, out Vector3 up, out Vector3 north, out Vector3 east);
            // (the line they lie most along: the farthest two apart)
            Vector3 along = north; float far = 0f;
            for (int a = 0; a < spots.Count; a++)
                for (int b = a + 1; b < spots.Count && spots.Count < 3000; b++)
                {
                    Vector3 d = Vector3.ProjectOnPlane(spots[b] - spots[a], up);
                    if (d.sqrMagnitude > far) { far = d.sqrMagnitude; along = d.normalized; }
                }
            Vector3 across = Vector3.Cross(up, along);
            float heading = Mathf.Atan2(Vector3.Dot(along, east), Vector3.Dot(along, north)) * Mathf.Rad2Deg;
            spots.Sort((p, q) => Vector3.Dot(p - mean, along).CompareTo(Vector3.Dot(q - mean, along)));
            o.Append(spots.Count).Append(" spots from ").Append(points.Count).Append(" bright dots; their middle ").Append(Daylight.Near ? (mean - (Vector3)Daylight.Here).magnitude.ToString("F0") + " m from here (" + Vector3.Dot(mean - (Vector3)Daylight.Here, east).ToString("F0") + " east, " + Vector3.Dot(mean - (Vector3)Daylight.Here, north).ToString("F0") + " north)" : "")
             .Append("; they lie along ").Append(heading.ToString("F1")).Append(" degrees, over ").Append(Mathf.Sqrt(far).ToString("F0")).Append(" m. along:across:up (count)\n");
            for (int k = 0; k < spots.Count && k < 400; k++)
            {
                Vector3 d = spots[k] - mean;
                o.Append(Vector3.Dot(d, along).ToString("F1")).Append(':').Append(Vector3.Dot(d, across).ToString("F1")).Append(':').Append(Vector3.Dot(d, up).ToString("F1")).Append(' ');
            }
            return o.ToString();
        }

        /// <summary>The glowing spots of one of the space centre's facilities that face up (lamps on a roof or a helipad, and skylights), each with its size: "facility [apart]".</summary>
        public static string Upward(string arg)
        {
            string[] w = (arg ?? "").Split(' ');
            float apart = w.Length > 1 ? float.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture) : 1.5f;
            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null || setup.SpaceCenterFacilities == null) return "no space centre";
            foreach (PSystemSetup.SpaceCenterFacility f in setup.SpaceCenterFacilities)
            {
                if (f == null || f.facilityTransform == null || f.name.IndexOf(w[0], StringComparison.OrdinalIgnoreCase) < 0) continue;
                var bits = new List<GlowMap.Bit>();
                foreach (Renderer r in f.facilityTransform.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || r is ParticleSystemRenderer) continue;
                    Material[] ms = r.sharedMaterials;
                    for (int i = 0; i < ms.Length; i++) if (GlowMap.Glows(ms[i])) GlowMap.Find(r, i, 256, bits);
                }
                Frame(f.facilityTransform.position, out Vector3 up, out Vector3 north, out Vector3 east);
                var upward = bits.FindAll(b => Vector3.Dot(b.Facing, up) > 0.8f);
                List<GlowMap.Bit> spots = GlowMap.Spots(upward, apart);
                spots.Sort((a, b) => b.Area.CompareTo(a.Area));
                var o = new StringBuilder(f.name + ": " + bits.Count + " glowing bits, " + upward.Count + " facing up, in " + spots.Count + " spots (east/north/up of the facility: square metres):");
                Vector3 at = f.facilityTransform.position;
                foreach (GlowMap.Bit s in spots) o.Append(" " + Vector3.Dot(s.At - at, east).ToString("F0") + "/" + Vector3.Dot(s.At - at, north).ToString("F0") + "/" + Vector3.Dot(s.At - at, up).ToString("F0") + ":" + s.Area.ToString("F2"));
                return o.ToString();
            }
            return "no such facility";
        }

        /// <summary>The space centre's camera put where it is told: "rotation elevation zoom [x z [altitude]]" (degrees, degrees above level, metres from what it looks at, metres from where it began, metres up). Nothing given: where it is.</summary>
        public static string View(string arg)
        {
            SpaceCenterCamera2 cam = UnityEngine.Object.FindObjectOfType<SpaceCenterCamera2>();
            if (cam == null) return "no space centre camera";
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
            Type k = typeof(SpaceCenterCamera2);
            string[] w = (arg ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Func<int, float> num = n => float.Parse(w[n], System.Globalization.CultureInfo.InvariantCulture);
            if (w.Length > 0) k.GetField("rotationAngle", F).SetValue(cam, num(0));
            if (w.Length > 1) k.GetField("elevationAngle", F).SetValue(cam, num(1));
            if (w.Length > 2) k.GetField("zoom", F).SetValue(cam, num(2));
            if (w.Length > 4) k.GetField("pos", F).SetValue(cam, new Vector3(num(3), 0f, num(4)));
            if (w.Length > 5) k.GetField("altitude", F).SetValue(cam, num(5));
            Transform eye = cam.GetCameraTransform();
            string where = "";
            if (eye != null && Daylight.Near)
            {
                Frame((Vector3)Daylight.Here, out Vector3 up, out Vector3 north, out Vector3 east);
                Vector3 d = eye.position - (Vector3)Daylight.Here;
                where = "; the eye " + Vector3.Dot(d, east).ToString("F0") + " east, " + Vector3.Dot(d, north).ToString("F0") + " north, " + Vector3.Dot(d, up).ToString("F0") + " up of the centre, looking " + (Mathf.Atan2(Vector3.Dot(eye.forward, east), Vector3.Dot(eye.forward, north)) * Mathf.Rad2Deg).ToString("F0") + " degrees";
            }
            return "rotation " + ((float)k.GetField("rotationAngle", F).GetValue(cam)).ToString("F1") + " elevation " + ((float)k.GetField("elevationAngle", F).GetValue(cam)).ToString("F1") + " zoom " + ((float)k.GetField("zoom", F).GetValue(cam)).ToString("F0")
                + " pos " + ((Vector3)k.GetField("pos", F).GetValue(cam)).ToString("F0") + " altitude " + ((float)k.GetField("altitude", F).GetValue(cam)).ToString("F0") + where;
        }

        /// <summary>The window lamps and floodlights the mod has put up, and every other lamp near the space centre: where, how strong, what kind.</summary>
        public static string Lamps()
        {
            var o = new StringBuilder();
            Vector3 here = Daylight.Near ? (Vector3)Daylight.Here : Vector3.zero;
            Frame(here, out Vector3 up, out Vector3 north, out Vector3 east);
            int n = 0;
            foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || l.type == LightType.Directional) continue;
                Vector3 d = l.transform.position - here;
                if (d.magnitude > 4000f) continue;
                o.Append(Path(l.transform)).Append(": ").Append(l.type).Append(' ').Append(l.renderMode).Append(l.enabled && l.gameObject.activeInHierarchy ? "" : " (off)").Append(" i ").Append(l.intensity.ToString("F2")).Append(" r ").Append(l.range.ToString("F0"))
                 .Append(" at ").Append(Vector3.Dot(d, east).ToString("F0")).Append('/').Append(Vector3.Dot(d, north).ToString("F0")).Append('/').Append(Vector3.Dot(d, up).ToString("F0")).Append(" colour ").Append(l.color.ToString("F2")).Append('\n');
                if (++n > 120) break;
            }
            return o.ToString() + "pixel lights allowed: " + QualitySettings.pixelLightCount;
        }
    }
}
#endif
