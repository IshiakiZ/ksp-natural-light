using System;
using System.Collections.Generic;
using Keystone;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// Where the space centre's glowing materials glow: its windows and its lamps.
    ///
    /// The game draws a lit window or a runway lamp as part of a building's or a runway's mesh, with a picture (the
    /// material's "_Emissive") that says which of it glows. There is no list of lamps or windows anywhere. So they are
    /// found from the two together: each triangle of the mesh is looked up in the picture, and where the picture is
    /// bright the triangle glows. What comes back is a list of bits: where on the triangle the glowing part of it is,
    /// which way the triangle faces, and how much of its area glows.
    ///
    /// A picture is read once (it is drawn into one that can be read: the game's own cannot) and kept, with a running
    /// count of its bright dots so that a triangle with nothing bright under it is passed over at once.
    /// </summary>
    static class GlowMap
    {
        /// <summary>A glowing piece of a triangle: where (the middle of its glowing part), which way the triangle faces, how much of it glows (square metres), all in world space.</summary>
        public struct Bit
        {
            public Vector3 At, Facing;
            public float Area;
        }

        sealed class Picture
        {
            public int W, H;
            public byte[] Bright;
            public int[] Count;             // bright dots in [0, x) x [0, y), (W + 1) x (H + 1)
        }

        const byte Least = 110;             // how bright a dot of the picture must be to count as glowing (of 255)
        static readonly Dictionary<int, Picture> pictures = new Dictionary<int, Picture>();

        /// <summary>A picture as dots that can be read: drawn into a picture of the mod's own first (the game's own cannot be read).</summary>
        static Color32[] Read(Texture t, out int w, out int h)
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

        static Picture Of(Texture t)
        {
            if (t == null) return null;
            int id = t.GetInstanceID();
            if (pictures.TryGetValue(id, out Picture known)) return known;
            Picture p = null;
            try
            {
                Color32[] dots = Read(t, out int w, out int h);
                p = new Picture { W = w, H = h, Bright = new byte[w * h], Count = new int[(w + 1) * (h + 1)] };
                for (int i = 0; i < w * h; i++) { Color32 c = dots[i]; p.Bright[i] = Math.Max(c.r, Math.Max(c.g, c.b)); }
                for (int y = 0; y < h; y++)
                {
                    int row = 0;
                    for (int x = 0; x < w; x++)
                    {
                        if (p.Bright[x + y * w] >= Least) row++;
                        p.Count[(x + 1) + (y + 1) * (w + 1)] = p.Count[(x + 1) + y * (w + 1)] + row;
                    }
                }
            }
            catch (Exception ex) { Kit.Log("Natural Light", "could not read where " + t.name + " glows: " + ex.Message); p = null; }
            pictures[id] = p;
            return p;
        }

        static int Rect(Picture p, int x0, int y0, int x1, int y1)
        {
            int w = p.W + 1;
            return p.Count[x1 + y1 * w] - p.Count[x0 + y1 * w] - p.Count[x1 + y0 * w] + p.Count[x0 + y0 * w];
        }

        /// <summary>How many bright dots lie in a box of the picture given in dots, which may run off its edges (the picture repeats).</summary>
        static int Bright(Picture p, int x0, int y0, int x1, int y1)
        {
            if (x1 - x0 >= p.W || y1 - y0 >= p.H) return 1;          // (as wide as the picture or more: look, whatever the count)
            int ax = ((x0 % p.W) + p.W) % p.W, ay = ((y0 % p.H) + p.H) % p.H;
            int bx = ax + (x1 - x0), by = ay + (y1 - y0);
            int n = 0;
            // (split where it runs over an edge: up to four boxes)
            int xa1 = Math.Min(bx, p.W), ya1 = Math.Min(by, p.H);
            n += Rect(p, ax, ay, xa1, ya1);
            if (bx > p.W) n += Rect(p, 0, ay, bx - p.W, ya1);
            if (by > p.H) n += Rect(p, ax, 0, xa1, by - p.H);
            if (bx > p.W && by > p.H) n += Rect(p, 0, 0, bx - p.W, by - p.H);
            return n;
        }

        /// <summary>Whether a material glows by a picture (and so can be looked up here).</summary>
        public static bool Glows(Material m) => m != null && m.HasProperty("_Emissive") && m.HasProperty("_EmissiveColor") && m.GetTexture("_Emissive") != null;

        public static string MaskName(Material m) => Glows(m) ? m.GetTexture("_Emissive").name : "";

        /// <summary>
        /// The glowing bits of the part of a renderer's mesh drawn with its material number `sub`. False if the mesh cannot be
        /// read. At most so many looks into the picture for each triangle (more for small lamps, fewer for walls of windows).
        /// </summary>
        public static bool Find(Renderer r, int sub, int looks, List<Bit> into)
        {
            Material[] materials = r.sharedMaterials;
            if (sub >= materials.Length || !Glows(materials[sub])) return false;
            Material m = materials[sub];
            MeshFilter mf = r.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) return false;
            Picture p = Of(m.GetTexture("_Emissive"));
            if (p == null) return false;
            Vector3[] v = mesh.vertices;
            Vector2[] uv = mesh.uv;
            if (uv == null || uv.Length != v.Length) return false;
            int[] tris = sub < mesh.subMeshCount ? mesh.GetTriangles(sub) : mesh.triangles;
            Vector2 scale = m.GetTextureScale("_Emissive"), offset = m.GetTextureOffset("_Emissive");
            Matrix4x4 world = r.transform.localToWorldMatrix;
            int W = p.W, H = p.H;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                float ax = (uv[i0].x * scale.x + offset.x) * W, ay = (uv[i0].y * scale.y + offset.y) * H;
                float bx = (uv[i1].x * scale.x + offset.x) * W, by = (uv[i1].y * scale.y + offset.y) * H;
                float cx = (uv[i2].x * scale.x + offset.x) * W, cy = (uv[i2].y * scale.y + offset.y) * H;
                float minX = Math.Min(ax, Math.Min(bx, cx)), maxX = Math.Max(ax, Math.Max(bx, cx));
                float minY = Math.Min(ay, Math.Min(by, cy)), maxY = Math.Max(ay, Math.Max(by, cy));
                int x0 = (int)Math.Floor(minX), y0 = (int)Math.Floor(minY), x1 = (int)Math.Ceiling(maxX), y1 = (int)Math.Ceiling(maxY);
                if (x1 <= x0) x1 = x0 + 1;
                if (y1 <= y0) y1 = y0 + 1;
                if (Bright(p, x0, y0, x1, y1) == 0) continue;
                // (the triangle in the picture, for telling which dots of the box are on it)
                float e0x = bx - ax, e0y = by - ay, e1x = cx - ax, e1y = cy - ay;
                float d00 = e0x * e0x + e0y * e0y, d01 = e0x * e1x + e0y * e1y, d11 = e1x * e1x + e1y * e1y;
                float den = d00 * d11 - d01 * d01;
                if (Math.Abs(den) < 1e-9f) continue;
                Vector3 A = world.MultiplyPoint3x4(v[i0]), B = world.MultiplyPoint3x4(v[i1]), C = world.MultiplyPoint3x4(v[i2]);
                Vector3 across = Vector3.Cross(B - A, C - A);
                float area = across.magnitude * 0.5f;
                if (area < 1e-6f) continue;
                float step = Math.Max(0.6f, (float)Math.Sqrt((maxX - minX) * (maxY - minY) / Math.Max(1, looks)));
                int inside = 0, lit = 0;
                Vector3 sum = Vector3.zero;
                for (float y = minY + step * 0.5f; y < maxY; y += step)
                    for (float x = minX + step * 0.5f; x < maxX; x += step)
                    {
                        float e2x = x - ax, e2y = y - ay;
                        float d20 = e2x * e0x + e2y * e0y, d21 = e2x * e1x + e2y * e1y;
                        float l1 = (d11 * d20 - d01 * d21) / den, l2 = (d00 * d21 - d01 * d20) / den, l0 = 1f - l1 - l2;
                        if (l0 < -1e-4f || l1 < -1e-4f || l2 < -1e-4f) continue;
                        inside++;
                        int px = (((int)Math.Floor(x)) % W + W) % W, py = (((int)Math.Floor(y)) % H + H) % H;
                        if (p.Bright[px + py * W] < Least) continue;
                        lit++;
                        sum += A * l0 + B * l1 + C * l2;
                    }
                if (lit == 0) continue;
                into.Add(new Bit { At = sum / lit, Facing = across / (2f * area), Area = area * lit / Math.Max(1, inside) });
            }
            return true;
        }

        /// <summary>Bits within so far of one another taken together, as one spot each (where their glowing area is, weighted by it), with how much glows there.</summary>
        public static List<Bit> Spots(List<Bit> bits, float apart)
        {
            var spots = new List<Bit>();
            var sums = new List<Vector3>();
            var cells = new Dictionary<long, List<int>>();
            float cell = apart;
            foreach (Bit b in bits)
            {
                int cx = Mathf.FloorToInt(b.At.x / cell), cy = Mathf.FloorToInt(b.At.y / cell), cz = Mathf.FloorToInt(b.At.z / cell);
                int found = -1;
                for (int dx = -1; dx <= 1 && found < 0; dx++)
                    for (int dy = -1; dy <= 1 && found < 0; dy++)
                        for (int dz = -1; dz <= 1 && found < 0; dz++)
                            if (cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out List<int> near))
                                foreach (int k in near)
                                    if ((spots[k].At - b.At).sqrMagnitude < apart * apart) { found = k; break; }
                float w = Math.Max(b.Area, 1e-4f);
                if (found < 0)
                {
                    found = spots.Count;
                    spots.Add(new Bit { At = b.At, Facing = Vector3.zero, Area = 0f });
                    sums.Add(Vector3.zero);
                    long key = Key(cx, cy, cz);
                    if (!cells.TryGetValue(key, out List<int> list)) cells[key] = list = new List<int>();
                    list.Add(found);
                }
                Bit s = spots[found];
                sums[found] += b.At * w;
                s.Area += w;
                s.Facing += b.Facing * w;
                s.At = sums[found] / s.Area;
                spots[found] = s;
            }
            for (int k = 0; k < spots.Count; k++) { Bit s = spots[k]; s.Facing = s.Facing.sqrMagnitude > 1e-12f ? s.Facing.normalized : Vector3.up; spots[k] = s; }
            return spots;
        }

        static long Key(int x, int y, int z) => ((long)x * 73856093L) ^ ((long)y * 19349663L) ^ ((long)z * 83492791L);

        /// <summary>The level line a set of places lies most along (the long way of a runway): its main axis in the plane square to `up`.</summary>
        public static Vector3 Lengthwise(List<Vector3> places, Vector3 up, Vector3 otherwise)
        {
            if (places.Count < 2) return otherwise;
            Vector3 one = Vector3.ProjectOnPlane(otherwise, up).normalized, two = Vector3.Cross(up, one);
            Vector3 mean = Vector3.zero;
            foreach (Vector3 p in places) mean += p;
            mean /= places.Count;
            float xx = 0f, xy = 0f, yy = 0f;
            foreach (Vector3 p in places)
            {
                float a = Vector3.Dot(p - mean, one), b = Vector3.Dot(p - mean, two);
                xx += a * a; xy += a * b; yy += b * b;
            }
            // (the larger eigenvector of the 2 x 2 spread)
            float angle = 0.5f * Mathf.Atan2(2f * xy, xx - yy);
            return (one * Mathf.Cos(angle) + two * Mathf.Sin(angle)).normalized;
        }
    }
}
