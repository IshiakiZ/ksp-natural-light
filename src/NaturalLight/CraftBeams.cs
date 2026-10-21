using System;
using System.Collections.Generic;
using Keystone;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// (Since 0.12.0.) A craft's own lamps seen as beams in murky air.
    ///
    /// A spotlight's beam is not seen in clean air: there is nothing in it to throw the light back. In fog, rain, snow or dust the
    /// air is full of drops and grains, and the beam stands out as a cone of light, the more the thicker the air (a car's headlights
    /// in fog; a rover's lamps in a Duna dust storm). By day the sky's light drowns it; by night it is plain. So every lamp of a
    /// craft near the camera that shines one way (a spotlight: the game's lamps and the rovers' lights) shows a beam from its glass
    /// along the way it points, as long as its light reaches and as wide as its cone, as bright as its light, as the air is thick
    /// (how much of the light a metre of it scatters: 3.9 over how far one can see, Koschmieder's rule, from the weather's own
    /// visibility; next to nothing in clear air) and as the dark lets it show. Drawn as the floodlights' beams are: a strip from
    /// the lamp turned about its length to face the camera, in their additive picture of a beam seen from the side.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class CraftBeams : MonoBehaviour
    {
        sealed class Beam
        {
            public Light Lamp;
            public Part Part;
            public GameObject Holder;
            public Mesh Mesh;
            public MeshRenderer Shows;
            public Material Paint;
        }

        const int Cuts = 10;
        readonly List<Beam> beams = new List<Beam>();
        readonly Vector3[] corners = new Vector3[(Cuts + 1) * 3];
        static readonly int tint = Shader.PropertyToID("_TintColor");
        float lookAt, murk, murkAt = -100f;
        public static string Report = "";

        void OnDestroy()
        {
            foreach (Beam b in beams) Drop(b);
            beams.Clear();
        }

        static void Drop(Beam b)
        {
            if (b.Holder != null) Destroy(b.Holder);
            if (b.Mesh != null) Destroy(b.Mesh);
            if (b.Paint != null) Destroy(b.Paint);
        }

        /// <summary>The lamps that shine one way on the craft within half a kilometre of the camera.</summary>
        void Find(Vector3 eye)
        {
            beams.RemoveAll(b =>
            {
                bool gone = b.Lamp == null || b.Part == null || b.Part.vessel == null || !b.Part.vessel.loaded || (b.Part.transform.position - eye).sqrMagnitude > 700f * 700f;
                if (gone) Drop(b);
                return gone;
            });
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.parts == null || (v.transform.position - eye).sqrMagnitude > 500f * 500f) continue;
                foreach (Part p in v.parts)
                {
                    if (p == null || p.FindModuleImplementing<ModuleLight>() == null) continue;
                    foreach (Light l in p.GetComponentsInChildren<Light>(true))
                    {
                        if (l == null || l.type != LightType.Spot) continue;
                        bool known = false;
                        foreach (Beam b in beams) if (b.Lamp == l) { known = true; break; }
                        if (!known) beams.Add(Make(l, p));
                    }
                }
            }
        }

        Beam Make(Light lamp, Part part)
        {
            var b = new Beam { Lamp = lamp, Part = part };
            Material shaft = Floodlights.Shaft();
            if (shaft == null) return b;
            b.Holder = new GameObject("Natural Light lamp beam") { layer = 15 };
            b.Holder.transform.SetParent(lamp.transform, false);
            b.Mesh = new Mesh { name = "Natural Light lamp beam" };
            b.Mesh.MarkDynamic();
            var uv = new Vector2[(Cuts + 1) * 3];
            var tris = new int[Cuts * 24];
            for (int i = 0; i <= Cuts; i++)
                for (int c = 0; c < 3; c++) uv[i * 3 + c] = new Vector2(c * 0.5f, Cut(i));
            for (int i = 0, t = 0; i < Cuts; i++)
                for (int c = 0; c < 2; c++)
                {
                    int a = i * 3 + c, q = a + 1, d = a + 3, e = a + 4;
                    tris[t++] = a; tris[t++] = d; tris[t++] = q; tris[t++] = q; tris[t++] = d; tris[t++] = e;
                    tris[t++] = a; tris[t++] = q; tris[t++] = d; tris[t++] = q; tris[t++] = e; tris[t++] = d;
                }
            b.Mesh.vertices = new Vector3[(Cuts + 1) * 3];
            b.Mesh.uv = uv;
            b.Mesh.triangles = tris;
            b.Holder.AddComponent<MeshFilter>().sharedMesh = b.Mesh;
            b.Shows = b.Holder.AddComponent<MeshRenderer>();
            b.Paint = new Material(shaft);
            b.Shows.sharedMaterial = b.Paint;
            b.Shows.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            b.Shows.receiveShadows = false;
            b.Shows.enabled = false;
            return b;
        }

        static float Cut(int i) { float f = i / (float)Cuts; return f * f; }

        /// <summary>
        /// How much a metre of the air here scatters, against thick fog's (0.02 a metre: 200 m of seeing): from the weather's own
        /// "how far one can see" (Koschmieder: 3.9 over it); clean air some thirty kilometres, next to nothing. Without a weather
        /// mod, clean air.
        /// </summary>
        float Murk(Vector3 at)
        {
            if (Time.time - murkAt < 1f) return murk;
            murkAt = Time.time;
            murk = 0.01f;
            try
            {
                if (Keystone.Weather.There)
                {
                    CelestialBody body = FlightGlobals.currentMainBody;
                    if (body != null && body.atmosphere)
                    {
                        WeatherHere here = Keystone.Weather.At(body, (Vector3d)at);
                        float seeing = Mathf.Max(here.Seeing, 40f);
                        murk = Mathf.Clamp((3.9f / seeing) / 0.02f, 0.01f, 1.5f);
                        // (and falling rain or snow through the beam, which the weather's seeing counts only in part)
                        murk = Mathf.Max(murk, Mathf.Clamp01(here.Rain) * (here.Snow > 0.3f ? 0.9f : 0.5f));
                    }
                }
            }
            catch (Exception) { }
            return murk;
        }

        void LateUpdate()
        {
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            bool wanted = Daylight.LampBeams != null && Daylight.LampBeams && cam != null && !MapView.MapIsEnabled;
            if (!wanted)
            {
                foreach (Beam b in beams) if (b.Shows != null && b.Shows.enabled) b.Shows.enabled = false;
                return;
            }
            Vector3 eye = cam.transform.position;
            if (Time.realtimeSinceStartup > lookAt) { lookAt = Time.realtimeSinceStartup + 1f; Find(eye); }
            CelestialBody body = FlightGlobals.currentMainBody;
            bool air = body != null && body.atmosphere && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.atmDensity > 0.05;
            float thick = air ? Murk(eye) : 0f;
            // (the dark it needs: the day's light drowns a beam, the twilight half does)
            float dark = 1f - 0.92f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.1f, 0.15f, Daylight.SunUp));
            int shown = 0;
            foreach (Beam b in beams)
            {
                if (b.Shows == null || b.Mesh == null) continue;
                Light l = b.Lamp;
                bool lit = l != null && l.enabled && l.gameObject.activeInHierarchy && l.intensity > 0.01f;
                float strength = lit ? l.intensity * thick * dark : 0f;
                if (strength < 0.01f) { if (b.Shows.enabled) b.Shows.enabled = false; continue; }
                // (as long as its light reaches, the cone as wide as its angle at that length)
                float reach = Mathf.Clamp(l.range, 4f, 80f);
                float half = Mathf.Tan(Mathf.Clamp(l.spotAngle, 5f, 150f) * 0.5f * Mathf.Deg2Rad);
                Transform space = b.Holder.transform;
                Vector3 from = space.InverseTransformPoint(eye);
                Vector3 along = Vector3.forward, end = along * reach;
                Vector3 across = Vector3.Cross(along, from - end * 0.3f);
                if (across.sqrMagnitude < 1e-6f) { if (b.Shows.enabled) b.Shows.enabled = false; continue; }
                across.Normalize();
                float unit = 1f / Mathf.Max(space.lossyScale.x, 1e-4f);
                for (int i = 0; i <= Cuts; i++)
                {
                    float f = Cut(i), w = Mathf.Max(0.15f, reach * f * half) * unit;
                    Vector3 mid = end * f * unit;
                    corners[i * 3] = mid - across * w; corners[i * 3 + 1] = mid; corners[i * 3 + 2] = mid + across * w;
                }
                b.Mesh.vertices = corners;
                b.Mesh.RecalculateBounds();
                Color c = l.color * Mathf.Clamp(0.35f * strength, 0f, 0.9f);
                c.a = 1f;
                c *= 0.5f; c.a = 0.5f;
                b.Paint.SetColor(tint, c);
                if (!b.Shows.enabled) b.Shows.enabled = true;
                shown++;
            }
            Report = shown > 0 ? shown + " lamp beam" + (shown == 1 ? "" : "s") + " in the air (" + (100f * thick).ToString("F0") + "% of fog's murk)" : "";
        }
    }
}
