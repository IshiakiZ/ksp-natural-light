using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NaturalLight
{
    /// <summary>
    /// What is round the craft, found by casting rays.
    ///
    /// The light from all round (see Daylight) is worked out as if the craft stood alone on a plain: the
    /// whole sky above it, the ground below. Beside a hangar wall, in a crater, under an overhang, that is
    /// wrong twice over: half the sky is not there, and in its place is a wall, which, if the sun is on it,
    /// is a great deal brighter than sky and the colour of whatever it is made of.
    ///
    /// So rays are cast from the craft, sixty-four of them spread evenly over every direction (eight a frame,
    /// so that all are fresh within an eighth of a second), against the ground and the buildings. A ray that
    /// meets nothing within four hundred metres is open sky, or open ground far below. One that meets
    /// something has found a surface: what it is made of is looked up (the mean colour of its picture, once
    /// for each material), which way it faces is known, and a second ray is cast from it towards the sun to
    /// see whether the sun reaches it: past the buildings, the ground, and the craft itself, which throws
    /// its own shadow on the wall beside it and the ground beneath it. From those:
    ///
    ///   * the sky lights the craft only from the directions it can be seen in;
    ///   * every surface found lights the craft from where it is, in its own colour, as brightly as the sun
    ///     and the sky are on it. Close to the ground this takes the place of the worked-out ground light.
    ///
    /// It is one set of rays for the whole scene, cast from the middle of the craft being flown: enough to
    /// tell a craft in the open from one beside a wall, and a wall in sun from one in shade, and not a
    /// picture of the place. It is for near the ground; a kilometre and more up there is nothing to find.
    /// </summary>
    public static class Surroundings
    {
        public const int Count = 64;
        public const float Reach = 400f;

        struct Ray
        {
            public Vector3 Dir;         // in the place's own frame: x east, y up, z north
            public bool Known, Hit;
            public float Sun;           // how much of the sun's light the surface it met is getting (0 none; 1 facing the sun and lit)
            public Color Colour;        // of that surface, as light
        }

        static readonly Ray[] rays = new Ray[Count];
        static int next;
        static bool made;
        public static string Report = "";

        static void Make()
        {
            // (a spiral over the sphere: as even a spread as that many points can have)
            float turn = Mathf.PI * (3f - Mathf.Sqrt(5f));
            for (int i = 0; i < Count; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / Count, r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y)), at = i * turn;
                rays[i].Dir = new Vector3(r * Mathf.Cos(at), y, r * Mathf.Sin(at));
                rays[i].Known = false;
            }
            made = true;
        }

        /// <summary>Forget what was found (the craft has gone somewhere else at a stroke).</summary>
        public static void Clear() { made = false; Report = ""; }

        /// <summary>Cast this frame's share of the rays.</summary>
        public static void Cast(Vector3 from, Vector3 up, Vector3 north, Vector3 toSun, float sunUp, int perFrame, Color ground)
        {
            if (!made) Make();
            Vector3 east = Vector3.Cross(up, north);
            float day = Mathf.InverseLerp(-0.03f, 0.03f, sunUp);
            for (int k = 0; k < perFrame; k++)
            {
                int i = next; next = (next + 1) % Count;
                Vector3 d = east * rays[i].Dir.x + up * rays[i].Dir.y + north * rays[i].Dir.z;
                if (Physics.Raycast(from, d, out RaycastHit hit, Reach, 1 << 15, QueryTriggerInteraction.Ignore))
                {
                    float facing = Mathf.Max(0f, Vector3.Dot(hit.normal, toSun)) * day;
                    // (does the sun reach it? Past the ground and the buildings, and past the craft: layer 0 is parts)
                    if (facing > 0.01f && Physics.Raycast(hit.point + hit.normal * 0.3f, toSun, 4000f, (1 << 15) | (1 << 0), QueryTriggerInteraction.Ignore)) facing = 0f;
                    rays[i].Sun = rays[i].Known && rays[i].Hit ? Mathf.Lerp(rays[i].Sun, facing, 0.6f) : facing;
                    rays[i].Colour = ColourOf(hit.collider, ground);
                    rays[i].Hit = true;
                }
                else rays[i].Hit = false;
                rays[i].Known = true;
            }
        }

        /// <summary>
        /// How much of the sky can be seen: overhead (as level ground would count it, what is straight up counting most),
        /// and towards each of north, east, south and west near the horizon; and how much of what is below is something a
        /// ray found (and so is lit by what was found there, and not by the worked-out ground).
        /// </summary>
        public static void Open(out float above, float[] sides, out float foundBelow)
        {
            float up = 0f, upAll = 0f, down = 0f, downAll = 0f;
            float[] side = new float[4], sideAll = new float[4];
            int hits = 0, lit = 0;
            for (int i = 0; i < Count; i++)
            {
                Ray r = rays[i];
                bool open = !r.Known || !r.Hit;
                if (r.Known && r.Hit) { hits++; if (r.Sun > 0.05f) lit++; }
                if (r.Dir.y > 0f) { upAll += r.Dir.y; if (open) up += r.Dir.y; }
                else { downAll += -r.Dir.y; if (!open) down += -r.Dir.y; }
                if (r.Dir.y > -0.1f && r.Dir.y < 0.75f)
                {
                    // (which of the four sides it looks to: north 0, east 1, south 2, west 3)
                    int q = Mathf.Abs(r.Dir.z) >= Mathf.Abs(r.Dir.x) ? (r.Dir.z >= 0f ? 0 : 2) : (r.Dir.x >= 0f ? 1 : 3);
                    sideAll[q] += 1f; if (open) side[q] += 1f;
                }
            }
            above = upAll > 0f ? up / upAll : 1f;
            foundBelow = downAll > 0f ? down / downAll : 0f;
            for (int q = 0; q < 4; q++) sides[q] = sideAll[q] > 0f ? side[q] / sideAll[q] : 1f;
            Report = "By rays: " + Mathf.RoundToInt(100f * above) + "% of the sky overhead open, " + hits + " of " + Count + " rays meet something within " + Reach.ToString("F0") + " m, " + lit + " of those in sun.";
        }

        /// <summary>
        /// The light of everything the rays found, added: each surface from where it is, in its own colour, by how much of
        /// the sun's light (a colour, as it falls on what faces it) and the sky's (as it falls on level ground) it has.
        /// </summary>
        public static void Bounce(ref SphericalHarmonicsL2 made, Vector3 up, Vector3 north, Color sun, Color sky, float amount)
        {
            if (amount <= 0f) return;
            Vector3 east = Vector3.Cross(up, north);
            for (int i = 0; i < Count; i++)
            {
                Ray r = rays[i];
                if (!r.Known || !r.Hit) continue;
                // (a wall sees about half the sky; ground, all of it: taken as six tenths for both)
                Color falls = sun * r.Sun + sky * 0.6f;
                Color thrown = new Color(r.Colour.r * falls.r, r.Colour.g * falls.g, r.Colour.b * falls.b);
                Vector3 d = east * r.Dir.x + up * r.Dir.y + north * r.Dir.z;
                // (each ray stands for its share of every direction there is: four pi over the number of them; and a surface
                // that throws back light evenly is one over pi as bright as the light on it. Four over the number, then.)
                made.AddDirectionalLight(d, thrown, 4f / Count * 0.7f * amount);
            }
        }

        // ---------------------------------------------------------------- what a surface is made of

        static readonly Dictionary<Material, Color> colours = new Dictionary<Material, Color>();
        static readonly Color Concrete = new Color(0.19f, 0.185f, 0.175f);          // (as light: about 0.47 as it shows)
        static float lastRead;

        static Color ColourOf(Collider met, Color ground)
        {
            if (met == null) return Concrete;
            // (the planet's own ground: its colour is known already, from the planet's picture)
            if (met.GetComponent<PQ>() != null) return ground;
            Renderer shown = met.GetComponent<Renderer>();
            if (shown == null) shown = met.GetComponentInParent<Renderer>();
            Material of = shown != null ? shown.sharedMaterial : null;
            if (of == null) return Concrete;
            if (colours.TryGetValue(of, out Color known)) return known;
            // (reading a picture back from the graphics card makes the game wait for it: once for each material, and no more
            // than four a second; until then it is taken as concrete)
            if (Time.realtimeSinceStartup - lastRead < 0.25f) return Concrete;
            lastRead = Time.realtimeSinceStartup;
            Color mean = Concrete;
            try
            {
                Color tint = of.HasProperty("_Color") ? of.color : Color.white;
                Texture picture = of.HasProperty("_MainTex") ? of.mainTexture : null;
                if (picture != null)
                {
                    RenderTexture small = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    RenderTexture was = RenderTexture.active;
                    Graphics.Blit(picture, small);
                    RenderTexture.active = small;
                    var copy = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                    copy.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                    copy.Apply();
                    RenderTexture.active = was;
                    RenderTexture.ReleaseTemporary(small);
                    float rr = 0f, gg = 0f, bb = 0f;
                    Color32[] dots = copy.GetPixels32();
                    foreach (Color32 c in dots) { rr += c.r; gg += c.g; bb += c.b; }
                    UnityEngine.Object.Destroy(copy);
                    float n = dots.Length * 255f;
                    mean = new Color(rr / n * tint.r, gg / n * tint.g, bb / n * tint.b).linear;
                }
                else mean = new Color(tint.r * 0.6f, tint.g * 0.6f, tint.b * 0.6f).linear;
            }
            catch (Exception) { }
            colours[of] = mean;
            return mean;
        }
    }
}
