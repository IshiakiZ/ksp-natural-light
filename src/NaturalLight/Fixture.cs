using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Keystone;
using UnityEngine;
using UnityEngine.Rendering;

namespace NaturalLight
{
    /// <summary>
    /// The floodlights' fixtures as Model/floodlight.py makes them in Blender: a xenon searchlight (a round projector head, its
    /// glass, the faceted reflector and the lamp behind it, cooling fins round its back) on a yoke, on a steel post with its ballast
    /// box, on a concrete footing. Its meshes (floodlight.mesh) and pictures (PNG files) are read from the mod's PluginData folder
    /// the first time a launch site's floodlights are put up, and drawn with the game's own part shaders, so that the sun, the sky
    /// and every lamp light them as they light the rest of the space centre, and there is nothing of ours to build for each system.
    ///
    /// The mesh file (laid out as On Foot's tug's is, and read here by Natural Light's own reader): "NLMESH01", how many pieces,
    /// and for each its name, where its frame is in its parent's, its vertices (position, normal, tangent, uv: 12 floats each, in
    /// Unity's axes and metres) and its submeshes (a material's name and its triangles' indices). A piece with no vertices is a
    /// marker, a place in another piece's frame: "glass", the middle of the front glass in the head's.
    /// </summary>
    static class FloodlightModel
    {
        /// <summary>
        /// A piece of the fixture, split in two for drawing: its painted body (one material for the stand and the head alike, so that
        /// a site's four bodies are joined into one mesh and drawn at once, shadow and all), and what of it glows (the reflector, the
        /// lamp, the glass: drawn piece by piece, each lit on its own, and throwing no shadow).
        /// </summary>
        internal sealed class Piece
        {
            public string Name;
            /// <summary>Where the piece's own frame is in its parent's (the head's: on the tilt axis, over the post).</summary>
            public Vector3 Position;
            public Mesh Body;
            /// <summary>
            /// What glows (none for the stand), kept as numbers in the piece's own frame: each searchlight's is made into a mesh of
            /// its own where it stands (LampAt). Only the corners its own triangles use; a list of triangles for each material.
            /// </summary>
            public Vector3[] LampPositions, LampNormals;
            public Vector4[] LampTangents;
            public Vector2[] LampUv;
            public int[][] LampTriangles;
            public Material[] LampMaterials;
            /// <summary>Which of the lamp's submeshes glow when it is lit (the reflector, and the lamp itself), and how brightly each.</summary>
            public readonly List<KeyValuePair<int, float>> Lit = new List<KeyValuePair<int, float>>();
            public int Triangles;
        }

        static Piece stand, head;
        static Material paint;
        static Vector3 glass;
        static string failed;

        internal static Piece Stand => stand;
        internal static Piece Head => head;
        /// <summary>What the bodies are drawn with: grey paint, concrete and steel, in one picture.</summary>
        internal static Material Paint => paint;
        /// <summary>The middle of the front glass, in the head's frame (metres; z is where the lamp points).</summary>
        internal static Vector3 Glass => glass;
        internal static string Folder => Path.Combine(KSPUtil.ApplicationRootPath, "GameData/NaturalLight/PluginData");

        /// <summary>Whether the fixture can be drawn (read on the first call); if not, why not.</summary>
        internal static bool Ready(out string why)
        {
            why = failed;
            // (kept from one scene to the next; read again should the game ever have thrown them away)
            if (stand != null && head != null && Alive()) return true;
            // (read twice in a game, 0.42 s and 0.47 s, the second at the first flight after the space centre (2026-10-19): what was gone)
            if (stand != null || head != null) Kit.Log("Natural Light", "the floodlights' fixture was thrown away (" + Gone() + "): read again");
            stand = head = null;
            if (failed != null) return false;
            try
            {
                Load();
                return true;
            }
            catch (Exception ex)
            {
                failed = why = "the floodlights' fixtures could not be read: " + ex.Message;
                Kit.Log("Natural Light", failed + "\n" + ex);
                stand = head = null;
                return false;
            }
        }

        static string Gone()
        {
            if (paint == null) return "its paint";
            if (paint.mainTexture == null) return "its paint's picture";
            foreach (Piece p in new[] { stand, head })
            {
                if (p == null) return "a piece";
                if (p.Body == null) return "the mesh of its " + p.Name;
                if (p.LampMaterials != null)
                    foreach (Material m in p.LampMaterials)
                    {
                        if (m == null) return "a lamp's material in its " + p.Name;
                        if (m.mainTexture == null) return "the picture of " + m.name;
                    }
            }
            return "nothing found gone";
        }

        static bool Alive()
        {
            if (paint == null || paint.mainTexture == null) return false;
            foreach (Piece p in new[] { stand, head })
            {
                if (p.Body == null) return false;
                if (p.LampMaterials != null) foreach (Material m in p.LampMaterials) if (m == null || m.mainTexture == null) return false;
            }
            return true;
        }

        static void Load()
        {
            float started = Time.realtimeSinceStartup;
            keptPictures = 0;
            Texture2D colour = Picture("floodlight_colour.png", false);
            Texture2D bumps = Picture("floodlight_normal.png", true);
            float pictures = Time.realtimeSinceStartup - started;
            paint = Make("KSP/Bumped Specular", colour, bumps, new Color(0.3f, 0.3f, 0.3f), 0.3f);
            var mats = new Dictionary<string, Material>
            {
                ["paint"] = paint,
                // (the reflector glows with its own picture, so that its facets show in the light; the lamp's quartz glows white)
                ["reflector"] = Lamp(colour, colour, new Color(0.9f, 0.9f, 0.9f), 0.92f),
                ["bulb"] = Lamp(colour, Flat(new Color32(255, 255, 255, 255)), new Color(0.9f, 0.9f, 0.9f), 0.95f),
                ["glass"] = Clear(),
            };

            byte[] data = File.ReadAllBytes(Path.Combine(Folder, "floodlight.mesh"));
            var found = new Dictionary<string, Piece>();
            var markers = new Dictionary<string, Vector3>();
            using (var r = new BinaryReader(new MemoryStream(data)))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(8)) != "NLMESH01") throw new InvalidDataException("floodlight.mesh is not a mesh file of this Natural Light's (remake it with Model/floodlight.py)");
                uint count = r.ReadUInt32();
                for (int m = 0; m < count; m++)
                {
                    var piece = new Piece { Name = Text(r) };
                    piece.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    r.ReadBytes(16);                                        // (its rotation: none, in this file)
                    int n = checked((int)r.ReadUInt32());
                    var pos = new Vector3[n]; var nor = new Vector3[n]; var tan = new Vector4[n]; var uv = new Vector2[n];
                    for (int i = 0; i < n; i++)
                    {
                        pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        nor[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        tan[i] = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                    }
                    int subs = checked((int)r.ReadUInt32());
                    if (n == 0 && subs == 0) { markers[piece.Name] = piece.Position; continue; }
                    var body = new List<int>();
                    var lamps = new List<int[]>();
                    var lampMats = new List<Material>();
                    for (int s = 0; s < subs; s++)
                    {
                        string mat = Text(r);
                        int k = checked((int)r.ReadUInt32());
                        var idx = new int[k];
                        for (int i = 0; i < k; i++)
                        {
                            idx[i] = checked((int)r.ReadUInt32());
                            if (idx[i] >= n) throw new InvalidDataException("floodlight.mesh: an index past the end in " + piece.Name);
                        }
                        if (!mats.TryGetValue(mat, out Material material) || material == paint) { body.AddRange(idx); piece.Triangles += k / 3; continue; }
                        // (no glass at all, where the game has no see-through shader for it: better than an opaque disc over the lamp)
                        if (material == null) continue;
                        if (mat == "reflector") piece.Lit.Add(new KeyValuePair<int, float>(lamps.Count, 1f));
                        if (mat == "bulb") piece.Lit.Add(new KeyValuePair<int, float>(lamps.Count, 4f));
                        lamps.Add(idx);
                        lampMats.Add(material);
                        piece.Triangles += k / 3;
                    }
                    // (the bodies are kept readable: each site's four are joined into one mesh where they stand)
                    piece.Body = Made("Natural Light floodlight " + piece.Name, pos, nor, tan, uv, new List<int[]> { body.ToArray() }, true);
                    if (lamps.Count > 0)
                    {
                        // (and what glows is kept as numbers, with only the corners its own triangles use)
                        var now = new Dictionary<int, int>();
                        var lp = new List<Vector3>(); var ln = new List<Vector3>(); var lt = new List<Vector4>(); var lu = new List<Vector2>();
                        piece.LampTriangles = new int[lamps.Count][];
                        for (int s = 0; s < lamps.Count; s++)
                        {
                            int[] from = lamps[s], to = new int[from.Length];
                            for (int i = 0; i < from.Length; i++)
                            {
                                if (!now.TryGetValue(from[i], out int at))
                                {
                                    at = lp.Count; now[from[i]] = at;
                                    lp.Add(pos[from[i]]); ln.Add(nor[from[i]]); lt.Add(tan[from[i]]); lu.Add(uv[from[i]]);
                                }
                                to[i] = at;
                            }
                            piece.LampTriangles[s] = to;
                        }
                        piece.LampPositions = lp.ToArray(); piece.LampNormals = ln.ToArray(); piece.LampTangents = lt.ToArray(); piece.LampUv = lu.ToArray();
                        piece.LampMaterials = lampMats.ToArray();
                    }
                    found[piece.Name] = piece;
                }
            }
            if (!found.TryGetValue("stand", out stand) || !found.TryGetValue("head", out head) || head.LampTriangles == null) throw new InvalidDataException("floodlight.mesh has no stand, no head or no lamp in its head");
            if (!markers.TryGetValue("glass", out glass)) throw new InvalidDataException("floodlight.mesh does not say where the glass is");
            Kit.Log("Natural Light", "the floodlights' fixture read in " + ((Time.realtimeSinceStartup - started) * 1000f).ToString("F0") + " ms (its pictures " + (pictures * 1000f).ToString("F0") + " ms, "
                + (keptPictures == 2 ? "both as kept" : keptPictures == 1 ? "one as kept" : "made from their files") + "): " + (stand.Triangles + head.Triangles) + " triangles, its picture "
                + colour.width + " across" + (mats["glass"] == null ? "; no see-through shader for its glass, which is left out" : ""));
        }

        static Mesh Made(string name, Vector3[] pos, Vector3[] nor, Vector4[] tan, Vector2[] uv, List<int[]> subs, bool readable)
        {
            var mesh = new Mesh { name = name };
            if (pos.Length > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = pos; mesh.normals = nor; mesh.tangents = tan; mesh.uv = uv;
            // (the second set of coordinates too, the same: the game's emissive shader reads its glow's picture through that set. Without
            // it the reflector, whose glow is its own picture, did not glow at all, while the lamp's, a flat white, did)
            if (!readable) mesh.uv2 = uv;
            mesh.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s], s, false);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(!readable);
            mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return mesh;
        }

        /// <summary>
        /// What glows in a head (the reflector, the lamp, the glass) as a mesh in another frame: `frame` is where the head is in it.
        /// Each searchlight's is made so, in the very frame its body is joined into, and drawn from the same place as the bodies
        /// (see Floodlights.Build for why).
        /// </summary>
        internal static Mesh LampAt(Matrix4x4 frame, string name)
        {
            Piece p = head;
            int n = p.LampPositions.Length;
            var pos = new Vector3[n]; var nor = new Vector3[n]; var tan = new Vector4[n];
            // (normals go through the frame's inverse turned over, which keeps them square to their faces whatever its scale; a frame
            // that mirrors turns every triangle round, and the tangents' handedness with it)
            Matrix4x4 across = frame.inverse.transpose;
            bool mirrored = frame.determinant < 0f;
            for (int i = 0; i < n; i++)
            {
                pos[i] = frame.MultiplyPoint3x4(p.LampPositions[i]);
                nor[i] = across.MultiplyVector(p.LampNormals[i]).normalized;
                Vector4 t = p.LampTangents[i];
                Vector3 along = frame.MultiplyVector(new Vector3(t.x, t.y, t.z)).normalized;
                tan[i] = new Vector4(along.x, along.y, along.z, mirrored ? -t.w : t.w);
            }
            var subs = new List<int[]>(p.LampTriangles.Length);
            foreach (int[] tris in p.LampTriangles)
            {
                if (!mirrored) { subs.Add(tris); continue; }
                var turned = new int[tris.Length];
                for (int i = 0; i + 2 < tris.Length; i += 3) { turned[i] = tris[i]; turned[i + 1] = tris[i + 2]; turned[i + 2] = tris[i + 1]; }
                subs.Add(turned);
            }
            return Made(name, pos, nor, tan, p.LampUv, subs, false);
        }

        static string Text(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));

        /// <summary>
        /// A picture from the PluginData folder, with its smaller sizes made and squeezed as the game's own are. A normal map
        /// (`bumps`) is laid out as the game's shaders read one: its x in alpha, its y in green, red and blue full.
        /// </summary>
        // (Since 0.12.0: what is made of each picture (unpacked, its smaller copies made, squeezed into the graphics card's own format)
        // is kept beside it, in PluginData/kept, and read back while the picture's file is what it was. Unpacking and squeezing the two
        // pictures of 2048 took most of the 0.44 s the fixture took to read at the first space centre of every game (2026-10-20).)
        static int keptPictures;
        const string KeptMark = "NLTEX001";

        static string KeptPath(string file) => Path.Combine(Path.Combine(Folder, "kept"), file + ".tex");

        static Texture2D FromKept(string file, FileInfo source, bool bumps)
        {
            Texture2D tex = null;
            try
            {
                string kept = KeptPath(file);
                if (!File.Exists(kept)) return null;
                using (var r = new BinaryReader(File.OpenRead(kept)))
                {
                    if (Encoding.ASCII.GetString(r.ReadBytes(8)) != KeptMark) return null;
                    if (r.ReadInt64() != source.Length || r.ReadInt64() != source.LastWriteTimeUtc.Ticks || r.ReadBoolean() != bumps) return null;
                    var format = (TextureFormat)r.ReadInt32();
                    int w = r.ReadInt32(), h = r.ReadInt32(), mips = r.ReadInt32(), n = r.ReadInt32();
                    byte[] data = r.ReadBytes(n);
                    if (data.Length != n) return null;
                    tex = new Texture2D(w, h, format, mips > 1, bumps) { name = "Natural Light " + file, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4, hideFlags = HideFlags.DontUnloadUnusedAsset };
                    if (tex.mipmapCount != mips) { UnityEngine.Object.Destroy(tex); return null; }
                    tex.LoadRawTextureData(data);
                    tex.Apply(false, true);
                    keptPictures++;
                    return tex;
                }
            }
            // (a kept file that cannot be read back is passed over: the picture is made from its file again, and kept anew)
            catch (Exception) { if (tex != null) UnityEngine.Object.Destroy(tex); return null; }
        }

        static void Keep(string file, FileInfo source, bool bumps, Texture2D tex)
        {
            // (only a squeezed one: the unsqueezed would be four times the size of the file it came from)
            if (tex.format != TextureFormat.DXT1 && tex.format != TextureFormat.DXT5) return;
            byte[] data = tex.GetRawTextureData();
            var format = tex.format; int w = tex.width, h = tex.height, mips = tex.mipmapCount;
            long size = source.Length, stamp = source.LastWriteTimeUtc.Ticks;
            string kept = KeptPath(file);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(kept));
                    string making = kept + ".making";
                    using (var o = new BinaryWriter(File.Create(making)))
                    {
                        o.Write(Encoding.ASCII.GetBytes(KeptMark)); o.Write(size); o.Write(stamp); o.Write(bumps);
                        o.Write((int)format); o.Write(w); o.Write(h); o.Write(mips); o.Write(data.Length); o.Write(data);
                    }
                    if (File.Exists(kept)) File.Delete(kept);
                    File.Move(making, kept);
                }
                catch (Exception) { }
            });
        }

        static Texture2D Picture(string file, bool bumps)
        {
            string path = Path.Combine(Folder, file);
            var source = new FileInfo(path);
            Texture2D had = FromKept(file, source, bumps);
            if (had != null) return had;
            var raw = new Texture2D(4, 4, TextureFormat.RGBA32, false, bumps);
            if (!raw.LoadImage(File.ReadAllBytes(path))) throw new InvalidDataException("could not read " + file);
            Color32[] px = raw.GetPixels32();
            if (bumps)
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, px[i].g, 255, px[i].r);
            var tex = new Texture2D(raw.width, raw.height, TextureFormat.RGBA32, true, bumps) { name = "Natural Light " + file, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4, hideFlags = HideFlags.DontUnloadUnusedAsset };
            UnityEngine.Object.Destroy(raw);
            tex.SetPixels32(px);
            tex.Apply(true, false);
            try { tex.Compress(true); } catch (Exception ex) { Kit.Log("Natural Light", "could not squeeze " + file + ": " + ex.Message); }
            try { Keep(file, source, bumps, tex); } catch (Exception) { }
            tex.Apply(false, true);
            return tex;
        }

        static Texture2D Flat(Color32 c)
        {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "Natural Light flat", hideFlags = HideFlags.DontUnloadUnusedAsset };
            var px = new Color32[16];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            t.SetPixels32(px);
            t.Apply(false, true);
            return t;
        }

        static Material Make(string shader, Texture2D main, Texture2D bumps, Color spec, float shine)
        {
            Shader s = Shader.Find(shader);
            if (s == null) throw new InvalidDataException("the game has no shader " + shader);
            var m = new Material(s) { name = "Natural Light floodlight " + shader, hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.SetTexture("_MainTex", main);
            if (bumps != null) m.SetTexture("_BumpMap", bumps);
            m.SetColor("_Color", Color.white);
            m.SetColor("_SpecColor", spec);
            m.SetFloat("_Shininess", shine);
            Plain(m);
            return m;
        }

        /// <summary>What glows when the lamp is lit: drawn with the game's emissive part shader, dark until it is told otherwise.</summary>
        static Material Lamp(Texture2D main, Texture2D glow, Color spec, float shine)
        {
            Shader s = Shader.Find("KSP/Emissive/Specular");
            if (s == null) throw new InvalidDataException("the game has no shader KSP/Emissive/Specular");
            var m = new Material(s) { name = "Natural Light floodlight lamp", hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.SetTexture("_MainTex", main);
            m.SetTexture("_Emissive", glow);
            m.SetColor("_EmissiveColor", Color.black);
            m.SetColor("_Color", Color.white);
            m.SetColor("_SpecColor", spec);
            m.SetFloat("_Shininess", shine);
            Plain(m);
            return m;
        }

        /// <summary>The front glass: clear, with the sun's glint on it. (Left out, where the game has none of these shaders.)</summary>
        static Material Clear()
        {
            Shader s = Shader.Find("KSP/Alpha/Translucent Specular") ?? Shader.Find("KSP/Alpha/Translucent") ?? Shader.Find("Legacy Shaders/Transparent/Specular");
            if (s == null) return null;
            var m = new Material(s) { name = "Natural Light floodlight glass", hideFlags = HideFlags.DontUnloadUnusedAsset };
            m.SetTexture("_MainTex", Flat(new Color32(150, 160, 168, 40)));
            m.SetColor("_Color", Color.white);
            if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", new Color(0.9f, 0.9f, 0.9f));
            if (m.HasProperty("_Shininess")) m.SetFloat("_Shininess", 0.97f);
            Plain(m);
            return m;
        }

        /// <summary>The game's part shaders' own extras (heat, burning, the rim light, fading) set to nothing.</summary>
        static void Plain(Material m)
        {
            if (m.HasProperty("_Opacity")) m.SetFloat("_Opacity", 1f);
            if (m.HasProperty("_RimFalloff")) m.SetFloat("_RimFalloff", 2f);
            if (m.HasProperty("_RimColor")) m.SetColor("_RimColor", Color.black);
            if (m.HasProperty("_TemperatureColor")) m.SetColor("_TemperatureColor", Color.clear);
            if (m.HasProperty("_BurnColor")) m.SetColor("_BurnColor", Color.white);
        }
    }
}
