using System.Collections.Generic;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// Light laid on the ground as a picture: what a lamp throws on the concrete round it, or a lit window on the
    /// pavement in front of it.
    ///
    /// The game's lamps of either kind are worked out at the corners of what they light (the cheap kind) or for
    /// every dot, at the cost of drawing everything they reach once more (the costly kind, of which the game
    /// allows eight in all). The runway and the space centre's paving are a few great flat pieces with corners
    /// hundreds of metres apart: a cheap lamp lights nothing there that can be seen, and a hundred lamps of the
    /// costly kind cannot be had. So the light on the ground is drawn: a soft patch laid on it, added to what is
    /// there, brightest where the lamp is and fading out, as the assembly building's own pools of light on its
    /// floor are drawn. One mesh for each of the game's lamp meshes, and one for each building, drawn with one of
    /// the game's own particle shaders: next to nothing to draw, and nothing that needs a shader of the mod's own
    /// (so the same on Windows, where the runway's lamps and the windows' light were seen at night).
    ///
    /// What stands on the ground (a craft, a kerbal) is lit by real lamps of the cheap kind, a few of them, near it.
    /// </summary>
    static class Pools
    {
        static Material round, spill;

        /// <summary>
        /// 0 below a, 1 above b, and a smooth S between: as a shader's smoothstep. (Unity's Mathf.SmoothStep is not that: it goes from its
        /// first number to its second as the third goes from 0 to 1, and taken for this it made every picture here a fraction of itself.)
        /// </summary>
        public static float Ramp(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        /// <summary>How far above the ground (as its colliders have it) the light is laid.</summary>
        public static float Lift = 0.35f;

        /// <summary>
        /// The game's own shader for its lamps' glow (the runway's lamps are drawn with it). (KSP/Particles/Additive, which the floodlights'
        /// lamps use, draws nothing of a mesh laid on the ground: found by trying both on the runway.)
        /// </summary>
        public static Shader Additive() => Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Particles/Additive") ?? Shader.Find("KSP/Particles/Additive");

        /// <summary>The light of a small lamp on the ground round it: a bright middle and a long soft fall, as light from a point does.</summary>
        public static Material Round()
        {
            if (round != null) return round;
            Shader shader = Additive();
            if (shader == null) return null;
            const int N = 128;
            var picture = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "Natural Light pool" };
            var dots = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f) / N * 2f - 1f, dy = (y + 0.5f) / N * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
                    // (a point's light on a plane below it falls off as 1 / (1 + (r / h)^2)^1.5, h its height; and the air and the
                    // roughness of the ground round it give a wider, fainter halo: the two together, faded out at the edge)
                    float core = 1f / Mathf.Pow(1f + (r / 0.16f) * (r / 0.16f), 1.5f);
                    float halo = Mathf.Exp(-(r / 0.5f) * (r / 0.5f));
                    float v = (0.55f * core + 0.45f * halo) * (1f - Ramp(0.72f, 1f, r));
                    byte b = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(v));
                    // (the light in the colour alone: the game's additive particle shader multiplies its colour by its alpha as well)
                    dots[x + y * N] = new Color32(b, b, b, 255);
                }
            picture.SetPixels32(dots);
            picture.Apply(true, true);
            round = new Material(shader) { mainTexture = picture, name = "Natural Light pool" };
            return round;
        }

        /// <summary>
        /// The light of a lit window on the ground in front of its wall: across (u) soft at both ends, outward (v, 0 at the
        /// wall) brightest by the wall and fading over the length of the patch.
        /// </summary>
        public static Material Spill()
        {
            if (spill != null) return spill;
            Shader shader = Additive();
            if (shader == null) return null;
            const int W = 64, H = 128;
            var picture = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "Natural Light spill" };
            var dots = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = (x + 0.5f) / W, v = (y + 0.5f) / H;
                    float side = Ramp(0f, 0.3f, u) * (1f - Ramp(0.7f, 1f, u));
                    // (light from a window comes down and out: on the ground it is strongest a little way out from the wall, under
                    // the window, and fades with distance; and the wall's own foot is in a little shade of the sill)
                    float outward = Ramp(0f, 0.06f, v) * Mathf.Exp(-2.6f * v) * (1f - Ramp(0.65f, 1f, v));
                    byte b = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(side * outward));
                    dots[x + y * W] = new Color32(b, b, b, 255);
                }
            picture.SetPixels32(dots);
            picture.Apply(true, true);
            spill = new Material(shader) { mainTexture = picture, name = "Natural Light spill" };
            return spill;
        }

        /// <summary>
        /// A patch laid on the ground: a grid of so many cells each way between the four corners given (world space), each corner of
        /// the grid let down onto the ground under it and lifted a hand's breadth clear of it, in the given space. A corner whose
        /// ground is far above or below the patch's own (a wall, a roof, a drop) is faded out, so the patch does not climb walls.
        /// Added to the lists (for one mesh of many patches).
        /// </summary>
        public static void Lay(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 up, int cells, Matrix4x4 toLocal, float level,
                               List<Vector3> corners, List<Vector2> uvs, List<Color> colours, List<int> triangles, float strength = 1f)
        {
            int first = corners.Count, n = cells + 1;
            var places = new Vector3[n * n];
            var grounds = new float[n * n];
            var found = new bool[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    float s = i / (float)cells, t = j / (float)cells;
                    // (a to b is across, a to d outward)
                    Vector3 p = Vector3.Lerp(Vector3.Lerp(a, b, s), Vector3.Lerp(d, c, s), t);
                    places[i + j * n] = p;
                    if (Physics.Raycast(p + up * 6f, -up, out RaycastHit hit, 30f, 1 << 15, QueryTriggerInteraction.Ignore))
                    {
                        grounds[i + j * n] = Vector3.Dot(hit.point, up);
                        found[i + j * n] = true;
                    }
                }
            // (no level given: the ground right round the middle of the patch, the highest of what is under the corners next to the
            // middle one, which is the lamp's own: a lamp at a runway's edge has the runway on one side and lower grass on the other,
            // and the light is laid from the runway down)
            if (float.IsNaN(level))
            {
                int mid = cells / 2;
                for (int j = mid - 1; j <= mid + 1; j++)
                    for (int i = mid - 1; i <= mid + 1; i++)
                        if (i >= 0 && j >= 0 && i < n && j < n && (i != mid || j != mid) && found[i + j * n])
                            level = float.IsNaN(level) ? grounds[i + j * n] : Mathf.Max(level, grounds[i + j * n]);
                if (float.IsNaN(level)) return;
            }
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int k = i + j * n;
                    Vector3 p = places[k];
                    float fade = 0f;
                    if (found[k])
                    {
                        // (more than a metre above the patch's own level is a wall, a roof's edge or something standing there, which the
                        // light is not laid on; below it, the light goes on down a bank as light does, and only stops at a real drop)
                        float off = grounds[k] - level;
                        fade = off > 0f ? Mathf.Clamp01(1f - (off - 0.6f) / 0.8f) : Mathf.Clamp01(1f - (-off - 3f) / 2f);
                        p += up * (grounds[k] - Vector3.Dot(p, up));
                    }
                    // (lifted clear of the ground as it is drawn, which stands a little above the ground the game's colliders say: the
                    // runway's surface is drawn some centimetres over its collider)
                    corners.Add(toLocal.MultiplyPoint3x4(p + up * Lift));
                    uvs.Add(new Vector2(i / (float)cells, j / (float)cells));
                    colours.Add(new Color(1f, 1f, 1f, Mathf.Clamp01(fade * strength)));
                }
            // (both ways round: the game's particle shaders draw only the side of a triangle that faces the camera)
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    int k = first + j * n + i;
                    triangles.Add(k); triangles.Add(k + n); triangles.Add(k + 1);
                    triangles.Add(k + 1); triangles.Add(k + n); triangles.Add(k + n + 1);
                    triangles.Add(k); triangles.Add(k + 1); triangles.Add(k + n);
                    triangles.Add(k + 1); triangles.Add(k + n + 1); triangles.Add(k + n);
                }
        }

        /// <summary>One mesh from the lists.</summary>
        public static Mesh Make(string name, List<Vector3> corners, List<Vector2> uvs, List<Color> colours, List<int> triangles)
        {
            var mesh = new Mesh { name = name };
            if (corners.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(corners);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(colours);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Something drawn with one of these materials, hung under a transform, on the layer of the scenery, throwing and taking no shadows.</summary>
        public static MeshRenderer Show(string name, Transform under, Mesh mesh, Material material)
        {
            var holder = new GameObject(name) { layer = 15 };
            holder.transform.SetParent(under, false);
            holder.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer r = holder.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return r;
        }
    }
}
