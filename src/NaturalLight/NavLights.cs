using System;
using System.Collections.Generic;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// (Since 0.12.0.) A winged craft's own lights: what every aeroplane shows by night.
    ///
    /// By the rules every aircraft flies with (the FAA's 14 CFR 25.1385 to 25.1401, ICAO's Annex 2): a red light at the tip of
    /// the left wing and a green one at the right, each seen from ahead and its own side; a white one at the tail, seen from
    /// behind; a red beacon on the fuselage that flashes, lit before the engines start; and white strobes at the wingtips, the
    /// brightest lights an aeroplane has, flashing on the runway and in the air, by day as well as by night (most airliners
    /// two quick flashes a second or so apart). The game's aeroplanes have none of these, and a spaceplane taking off at night
    /// is a dark shape with an engine.
    ///
    /// So every craft near the camera that has wings (two or more lifting surfaces spanning at least four metres) is given
    /// them (wings, not a rocket's fins: lifting surfaces adding up to at least one and a half of the game's wing measure): the wingtips are the outermost points of its wings either side, across the way its controlling part faces (red
    /// to its left, green to its right); the tail light is its rearmost point; the beacon is on top of it, over its centre of
    /// mass. Position lights and the beacon by night (from dusk, as the floodlights); the strobes whenever it is off the
    /// ground or moving along it, and each flash lights the wing round it for a twentieth of a second (a cheap lamp, as the
    /// engines' are). Each light is a card turned to face the camera, a few pixels at the least. Nothing is added to the craft:
    /// the lights ride on its parts and are gone when it is.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class NavLights : MonoBehaviour
    {
        enum Kind { Port, Starboard, Tail, Beacon, Strobe }

        sealed class Lamp
        {
            public Kind Kind;
            public Transform At;            // the card, a child of the part it is on
            public Renderer Shows;
            public Light Flash;             // (a strobe's light on the wing)
            public Vector3 Facing;          // which way it shines, in its part's frame (a position light is not seen from the far side)
        }

        sealed class Craft
        {
            public Vessel V;
            public readonly List<Lamp> Lamps = new List<Lamp>();
            public float Phase;
        }

        readonly List<Craft> crafts = new List<Craft>();
        float lookAt, dark;
        public static string Report = "";
        static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        static readonly Color Red = new Color(1f, 0.06f, 0.03f), Green = new Color(0.1f, 1f, 0.35f), White = new Color(1f, 0.97f, 0.9f), Strobe = new Color(0.85f, 0.92f, 1f);

        void Awake()
        {
            Camera.onPreCull += Face;
#if DEV
            last = this;
#endif
        }

        void OnDestroy()
        {
            Camera.onPreCull -= Face;
            foreach (Craft c in crafts) Drop(c);
            crafts.Clear();
            Report = "";
        }

        static void Drop(Craft c)
        {
            foreach (Lamp l in c.Lamps) if (l.At != null) Destroy(l.At.gameObject);
            c.Lamps.Clear();
        }

        /// <summary>The winged craft near the camera, and the places of their lights: looked at again every two seconds.</summary>
        void Look(Vector3 eye)
        {
            crafts.RemoveAll(c =>
            {
                bool gone = c.V == null || !c.V.loaded || c.V.packed || (c.V.transform.position - eye).sqrMagnitude > 6000f * 6000f || c.Lamps.Exists(l => l.At == null);
                if (gone) Drop(c);
                return gone;
            });
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v.packed || v.parts == null || v.parts.Count < 2 || crafts.Exists(c => c.V == v)) continue;
                if ((v.transform.position - eye).sqrMagnitude > 5000f * 5000f) continue;
                var c = Make(v);
                if (c != null) crafts.Add(c);
            }
        }

        Craft Make(Vessel v)
        {
            Transform r = v.ReferenceTransform != null ? v.ReferenceTransform : v.transform;
            Vector3 nose = r.up, top = -r.forward, right = r.right;
            Vector3 com = (Vector3)v.CoMD;
            // The outermost points of the wings each way, the rearmost point of the craft and its top over the centre of mass.
            Part portPart = null, starPart = null, tailPart = null, topPart = null;
            Vector3 port = Vector3.zero, star = Vector3.zero, tail = Vector3.zero, topAt = Vector3.zero;
            float most = float.MinValue, least = float.MaxValue, rear = float.MaxValue, high = float.MinValue;
            int wings = 0;
            float area = 0f;
            foreach (Part p in v.parts)
            {
                if (p == null) continue;
                ModuleLiftingSurface lifts = p.FindModuleImplementing<ModuleLiftingSurface>();
                bool wing = lifts != null && !(lifts is ModuleControlSurface);
                if (wing) { wings++; area += lifts.deflectionLiftCoeff; }
                foreach (MeshFilter mf in p.FindModelComponents<MeshFilter>())
                {
                    Mesh mesh = mf != null ? mf.sharedMesh : null;
                    Renderer shown = mf != null ? mf.GetComponent<Renderer>() : null;
                    if (mesh == null || shown == null || !shown.enabled) continue;
                    Bounds b = mesh.bounds;
                    for (int k = 0; k < 8; k++)
                    {
                        Vector3 w = mf.transform.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3((k & 1) == 0 ? -1f : 1f, (k & 2) == 0 ? -1f : 1f, (k & 4) == 0 ? -1f : 1f)));
                        Vector3 d = w - com;
                        float x = Vector3.Dot(d, right), y = Vector3.Dot(d, nose), z = Vector3.Dot(d, top);
                        if (wing && x > most) { most = x; star = w; starPart = p; }
                        if (wing && x < least) { least = x; port = w; portPart = p; }
                        if (Mathf.Abs(x) < 1.5f && y < rear) { rear = y; tail = w; tailPart = p; }
                        if (Mathf.Abs(x) < 1.5f && Mathf.Abs(y) < 4f && z > high) { high = z; topAt = w; topPart = p; }
                    }
                }
            }
            // (an aeroplane's wings, not a rocket's fins: a basic fin lifts a tenth to a third of what a wing panel does, and a
            // spaceplane's wings add up to several)
            if (wings < 2 || area < 1.5f || portPart == null || starPart == null || most - least < 4f || most < 1f || least > -1f) return null;
            var c = new Craft { V = v, Phase = UnityEngine.Random.value };
            Add(c, Kind.Port, portPart, port, -right);
            Add(c, Kind.Starboard, starPart, star, right);
            Add(c, Kind.Strobe, portPart, port - right * 0.15f, -right);
            Add(c, Kind.Strobe, starPart, star + right * 0.15f, right);
            if (tailPart != null) Add(c, Kind.Tail, tailPart, tail, -nose);
            if (topPart != null) Add(c, Kind.Beacon, topPart, topAt + top * 0.15f, top);
            return c;
        }

        void Add(Craft c, Kind kind, Part on, Vector3 at, Vector3 facing)
        {
            Material glow = Floodlights.Glow();
            Mesh card = Floodlights.Card();
            if (glow == null || card == null) return;
            var g = new GameObject("Natural Light " + kind + " light") { layer = 0 };
            g.transform.SetParent(on.transform, true);
            g.transform.position = at;
            g.AddComponent<MeshFilter>().sharedMesh = card;
            var shows = g.AddComponent<MeshRenderer>();
            shows.sharedMaterial = glow;
            shows.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            shows.receiveShadows = false;
            shows.enabled = false;
            var l = new Lamp { Kind = kind, At = g.transform, Shows = shows, Facing = on.transform.InverseTransformDirection(facing) };
            if (kind == Kind.Strobe)
            {
                var holder = new GameObject("Natural Light strobe's light");
                holder.transform.SetParent(g.transform, false);
                l.Flash = holder.AddComponent<Light>();
                l.Flash.type = LightType.Point;
                l.Flash.renderMode = LightRenderMode.ForceVertex;
                l.Flash.cullingMask = Daylight.Lit;
                l.Flash.color = Strobe;
                l.Flash.range = 9f;
                l.Flash.intensity = 0f;
                l.Flash.shadows = LightShadows.None;
                l.Flash.enabled = false;
            }
            c.Lamps.Add(l);
        }

        void LateUpdate()
        {
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            bool wanted = Daylight.On && Daylight.NavLights != null && Daylight.NavLights && cam != null && !MapView.MapIsEnabled;
            if (!wanted)
            {
                foreach (Craft c in crafts) foreach (Lamp l in c.Lamps) { if (l.Shows != null) l.Shows.enabled = false; if (l.Flash != null) l.Flash.enabled = false; }
                return;
            }
            Vector3 eye = cam.transform.position;
            if (Time.realtimeSinceStartup > lookAt) { lookAt = Time.realtimeSinceStartup + 2f; Look(eye); }
            float want = Mathf.InverseLerp(0.05f, -0.035f, Daylight.SunUp);
            dark = Mathf.MoveTowards(dark, want, Mathf.Min(Time.unscaledDeltaTime, 0.1f) * 0.5f);
            float t = Time.time;
            int shown = 0;
            foreach (Craft c in crafts)
            {
                if (c.V == null) continue;
                // (strobes from when it rolls; position lights and the beacon by night)
                bool rolling = c.V.situation == Vessel.Situations.FLYING || c.V.srfSpeed > 3.0;
                float strobe = 0f;
                if (rolling)
                {
                    // (two flashes of a twentieth of a second, a fifth apart, every 1.2 s)
                    float ph = Mathf.Repeat(t / 1.2f + c.Phase, 1f) * 1.2f;
                    strobe = ph < 0.05f || (ph > 0.2f && ph < 0.25f) ? 1f : 0f;
                }
                // (the beacon: a flash of a sixth of a second each second)
                float beacon = Mathf.Repeat(t + c.Phase * 0.7f, 1f) < 0.16f ? 1f : 0.04f;
                foreach (Lamp l in c.Lamps)
                {
                    if (l.At == null || l.Shows == null) continue;
                    float v; Color col;
                    switch (l.Kind)
                    {
                        case Kind.Port: v = dark * 2.2f; col = Red; break;
                        case Kind.Starboard: v = dark * 2.2f; col = Green; break;
                        case Kind.Tail: v = dark * 1.6f; col = White; break;
                        case Kind.Beacon: v = dark * beacon * 1.4f; col = Red; break;
                        default: v = strobe * Mathf.Lerp(0.6f, 2.2f, dark); col = Strobe; break;
                    }
                    // (a position light is seen from its own side and ahead or behind, not through the craft)
                    if (l.Kind != Kind.Strobe && l.Kind != Kind.Beacon)
                    {
                        Vector3 way = l.At.parent != null ? l.At.parent.TransformDirection(l.Facing) : l.Facing;
                        float toward = Vector3.Dot(way.normalized, (eye - l.At.position).normalized);
                        v *= Mathf.Clamp01((toward + 0.25f) / 0.4f);
                    }
                    bool show = v > 0.01f;
                    if (l.Shows.enabled != show) l.Shows.enabled = show;
                    if (l.Flash != null)
                    {
                        bool flash = strobe > 0f;
                        if (l.Flash.enabled != flash) l.Flash.enabled = flash;
                        if (flash) l.Flash.intensity = Mathf.Lerp(0.8f, 2.4f, dark);
                    }
                    if (!show) continue;
                    shown++;
                    block.SetColor("_TintColor", new Color(col.r * v * 0.5f, col.g * v * 0.5f, col.b * v * 0.5f, 0.5f));
                    l.Shows.SetPropertyBlock(block);
                }
            }
            Report = crafts.Count > 0 ? crafts.Count + " winged craft with lights, " + shown + " lit" : "";
        }

        /// <summary>Each card turned to face the camera: a position light's glare some half a metre across close to and a few pixels far off; a strobe's flash larger.</summary>
        void Face(Camera cam)
        {
            if (cam == null || crafts.Count == 0) return;
            string name = cam.name;
            if (name != "Camera 00" && name != "Camera 01") return;
            Transform eye = cam.transform;
            float pixel = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(cam.pixelHeight, 1);
            foreach (Craft c in crafts)
                foreach (Lamp l in c.Lamps)
                {
                    if (l.At == null || l.Shows == null || !l.Shows.enabled) continue;
                    float far = (l.At.position - eye.position).magnitude;
                    l.At.rotation = eye.rotation;
                    bool big = l.Kind == Kind.Strobe;
                    float size = Mathf.Max(big ? 1.6f : 0.7f, far * pixel * (big ? 14f : 7f));
                    Vector3 parent = l.At.parent != null ? l.At.parent.lossyScale : Vector3.one;
                    l.At.localScale = new Vector3(size / Mathf.Max(parent.x, 1e-4f), size / Mathf.Max(parent.y, 1e-4f), size / Mathf.Max(parent.z, 1e-4f));
                }
        }

#if DEV
        static NavLights last;
        /// <summary>(Development build) the winged craft found, how many of their lights are lit, and where each light is: metres to the right, forward and up of the centre of mass, as the controlling part faces.</summary>
        public static string Says()
        {
            var say = new System.Text.StringBuilder(Report.Length > 0 ? Report : "no winged craft near");
            if (last == null) return say.ToString();
            foreach (Craft c in last.crafts)
            {
                if (c.V == null) continue;
                Transform r = c.V.ReferenceTransform != null ? c.V.ReferenceTransform : c.V.transform;
                Vector3 com = (Vector3)c.V.CoMD;
                say.Append("; ").Append(c.V.vesselName).Append(" (controlled from ").Append(r.name).Append("):");
                foreach (Lamp l in c.Lamps)
                {
                    if (l.At == null) continue;
                    Vector3 d = l.At.position - com;
                    say.Append(' ').Append(l.Kind).Append(" right ").Append(Vector3.Dot(d, r.right).ToString("F1")).Append(" fwd ").Append(Vector3.Dot(d, r.up).ToString("F1")).Append(" up ").Append(Vector3.Dot(d, -r.forward).ToString("F1"))
                       .Append(l.Shows != null && l.Shows.enabled ? " lit" : " dark").Append(',');
                }
            }
            return say.ToString().TrimEnd(',');
        }
#endif
    }
}
