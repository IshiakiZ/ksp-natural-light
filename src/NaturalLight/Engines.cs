using System;
using System.Collections.Generic;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// A running engine lights what is round it.
    ///
    /// The game draws an engine's flame and lets it light nothing: a rocket leaving the pad at night is a
    /// bright plume under a black rocket on black ground. Here a flame is a source of light, as bright as the
    /// engine is powerful and the colour of what it burns.
    ///
    /// A flame is long, and one lamp is a point. A lamp under a rocket lights the pad and the engine's bell
    /// and, at a grazing angle, next to nothing of the rocket above it. What lights the sides of a real rocket
    /// at night is the length of the flame and, far more, the ground: the pad and the smoke under it are lit
    /// white-hot and throw that light back up from all round. So each flame is made of several lights: one
    /// where it leaves the engine, which is the costly kind (everything it reaches is drawn once more for it,
    /// with highlights); three round the flame a third of the way down it, as far out as the flame is wide,
    /// which is what catches the sides of the craft in flight; and, where the flame reaches the ground, three
    /// round the place it lands, which are the ground's own glow. All but the first are of the cheap kind,
    /// worked out at the corners of what they light and not at every dot of it, which the game's shaders
    /// take four of at a time for nothing.
    ///
    /// Engines close together share a flame, and there are never more than three: the three most powerful
    /// groups get them, and the first two of those their full length and their glow from the ground.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class EngineLamps : MonoBehaviour
    {
        const int Most = 3;             // flames
        const int Full = 2;             // of which so many have their length and their glow from the ground
        const int Along = 3, Round = 3; // lights round a flame, and round where it lands

        sealed class Group
        {
            public Vector3 At, Along;       // where the flame leaves the engine, and which way it goes
            public float Power;             // kilonewtons, weighted by how brightly the fuel burns
            public Color Tint;
            public float Side;              // (to tell two alike apart the same way every frame)
            public Transform Ride;          // the model of the first engine's part, which its lamps hang from (see Hang)
        }

        /// <summary>
        /// A lamp hung from the model of the part whose flame it lights, so that it goes wherever that is drawn: the Smooth Running
        /// mod carries the craft's models on between the game's physics steps, and a lamp left loose in the scene would stand behind
        /// its flame by up to a step (six metres at three hundred metres a second). Placed in the scene's terms all the same.
        /// </summary>
        static void Hang(Light lamp, Transform on)
        {
            if (lamp != null && on != null && lamp.transform.parent != on) lamp.transform.SetParent(on, true);
        }

        readonly Light[] lamps = new Light[Most];
        readonly Light[] soft = new Light[Full * (Along + Round)];
        readonly float[] shown = new float[Most];
        readonly List<ModuleEngines> engines = new List<ModuleEngines>();
        readonly List<Group> groups = new List<Group>();
        float nextLook;
        bool others;
        public static string Report = "";

        /// <summary>A flame that is giving light just now: where, how much and what colour (as light), and how far it carries. For the other parts of the mod.</summary>
        public struct Flame { public Vector3 At; public Color Light; public float Reach; }
        public static readonly List<Flame> Flames = new List<Flame>();

        void Awake()
        {
            Daylight.Setup();
            // (another mod that does the same: leave it to that one)
            foreach (AssemblyLoader.LoadedAssembly a in AssemblyLoader.loadedAssemblies)
                if (a.name != null && a.name.Equals("EngineLight", StringComparison.OrdinalIgnoreCase)) others = true;
            if (others) Keystone.Kit.Log("Natural Light", "engine light is left to the Engine Lighting mod, which is installed");
        }

        void Start()
        {
            // (the lamps made now, and put out, and not at the first lift-off, where making seven lights at once was 8 ms of a stall:
            // 2026-10-18. The card's work for the first light of each kind is done ahead too, by the explosions mod's faint lights.)
            if (others) return;
            for (int n = 0; n < Most; n++) { lamps[n] = Lamp("Natural Light engine flame " + n, n == 0); lamps[n].enabled = false; }
            for (int n = 0; n < Full; n++)
                for (int k = 0; k < Along + Round; k++)
                {
                    Light l = soft[n * (Along + Round) + k] = Lamp("Natural Light engine flame " + n + (k < Along ? " side " + k : " off the ground " + (k - Along)), false);
                    l.enabled = false;
                }
        }

        void OnDestroy()
        {
            foreach (Light lamp in lamps) if (lamp != null) Destroy(lamp.gameObject);
            foreach (Light lamp in soft) if (lamp != null) Destroy(lamp.gameObject);
#if DEV
            foreach (GameObject g in tries) if (g != null) Destroy(g);
            tries.Clear();
#endif
            Flames.Clear();
            Report = "";
        }

        /// <summary>The colour of an engine's light, from what it burns.</summary>
        static Color TintOf(ModuleEngines engine, out float bright)
        {
            bool solid = false, xenon = false, mono = false, oxidizer = false, air = false, fuel = false;
            foreach (Propellant p in engine.propellants)
            {
                string name = p.name;
                if (name == "SolidFuel") solid = true;
                else if (name == "XenonGas") xenon = true;
                else if (name == "MonoPropellant") mono = true;
                else if (name == "Oxidizer") oxidizer = true;
                else if (name == "IntakeAir") air = true;
                else if (name == "LiquidFuel") fuel = true;
            }
            bright = 1f;
            if (solid) { bright = 1.25f; return new Color(1f, 0.84f, 0.58f); }                 // white-hot and smoky
            if (xenon) { bright = 0.5f; return new Color(0.45f, 0.8f, 1f); }                    // the blue of an ion engine
            if (mono) { bright = 0.6f; return new Color(1f, 0.92f, 0.78f); }
            if (air) { bright = 0.45f; return new Color(1f, 0.62f, 0.32f); }                    // a jet: little to see without its afterburner
            if (fuel && !oxidizer) { bright = 0.7f; return new Color(1f, 0.5f, 0.25f); }       // hot hydrogen from a nuclear engine
            return new Color(1f, 0.7f, 0.4f);                                                 // fuel and oxidiser
        }

        static void Fade(Light lamp, float left)
        {
            if (lamp == null || !lamp.enabled) return;
            if (left <= 0f) lamp.enabled = false; else lamp.intensity *= left;
        }

        Light Lamp(string name, bool costly)
        {
            var holder = new GameObject(name);
            Light lamp = holder.AddComponent<Light>();
            lamp.type = LightType.Point;
            lamp.shadows = LightShadows.None;
            lamp.renderMode = costly ? LightRenderMode.ForcePixel : LightRenderMode.ForceVertex;
            lamp.cullingMask = Daylight.Lit;
            return lamp;
        }

        void LateUpdate()
        {
            bool wanted = !others && Daylight.On && Daylight.Engines != null && Daylight.Engines && FlightGlobals.ready && FlightGlobals.ActiveVessel != null && !MapView.MapIsEnabled;
            groups.Clear();
            if (wanted)
            {
                // Which engines there are is looked up twice a second; what each is doing, every frame.
                Vessel flown = FlightGlobals.ActiveVessel;
                if (Time.realtimeSinceStartup > nextLook)
                {
                    nextLook = Time.realtimeSinceStartup + 0.5f;
                    engines.Clear();
                    Vector3 here = flown.transform.position;
                    foreach (Vessel v in FlightGlobals.VesselsLoaded)
                    {
                        if (v == null || !v.loaded || (v.transform.position - here).sqrMagnitude > 1500f * 1500f) continue;
                        foreach (Part p in v.parts)
                        {
                            if (p == null) continue;
                            foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>()) engines.Add(e);
                        }
                    }
                }
                Transform frame = flown.transform;
                foreach (ModuleEngines e in engines)
                {
                    if (e == null || e.part == null || !e.EngineIgnited || e.flameout || e.finalThrust < 0.5f || e.thrustTransforms == null || e.thrustTransforms.Count == 0) continue;
                    Vector3 at = Vector3.zero, along = Vector3.zero;
                    int n = 0;
                    foreach (Transform t in e.thrustTransforms) { if (t == null) continue; at += t.position; along += t.forward; n++; }
                    if (n == 0) continue;
                    at /= n; along = along.normalized;
                    Color tint = TintOf(e, out float bright);
                    float power = e.finalThrust * bright;
                    // Engines within a few metres of one another share a flame.
                    Group mine = null;
                    foreach (Group g in groups) if ((g.At - at).sqrMagnitude < 6f * 6f) { mine = g; break; }
                    if (mine == null) groups.Add(new Group { At = at, Along = along, Power = power, Tint = tint, Side = Vector3.Dot(at - frame.position, frame.right) + 0.37f * Vector3.Dot(at - frame.position, frame.forward), Ride = e.part.transform.Find("model") });
                    else
                    {
                        float share = power / (mine.Power + power);
                        mine.At = Vector3.Lerp(mine.At, at, share); mine.Along = Vector3.Slerp(mine.Along, along, share); mine.Tint = Color.Lerp(mine.Tint, tint, share);
                        mine.Power += power;
                    }
                }
                // (the most powerful first; two much alike, as a pair of boosters are, always the same way round, or their lights would swap about)
                groups.Sort((a, b) => Mathf.Abs(a.Power - b.Power) > 0.08f * Mathf.Max(a.Power, b.Power) ? b.Power.CompareTo(a.Power) : a.Side.CompareTo(b.Side));
                // (what is left over joins the nearest group that has a flame, so that its light is not lost)
                for (int n = groups.Count - 1; n >= Most; n--)
                {
                    Group nearest = groups[0];
                    for (int k = 1; k < Most; k++) if ((groups[k].At - groups[n].At).sqrMagnitude < (nearest.At - groups[n].At).sqrMagnitude) nearest = groups[k];
                    nearest.Power += groups[n].Power;
                    groups.RemoveAt(n);
                }
            }

            float strength = Daylight.EngineLight != null ? (float)Daylight.EngineLight : 1f;
            float time = Time.time;
            float flicker = 0.9f + 0.2f * Mathf.PerlinNoise(time * 19f, 0.37f) + 0.06f * Mathf.PerlinNoise(time * 57f, 3.1f);
            Vector3 north = FlightGlobals.currentMainBody != null ? (Vector3)FlightGlobals.currentMainBody.transform.up : Vector3.up;
            string said = "";
            Flames.Clear();
            for (int n = 0; n < Most; n++)
            {
                Group g = n < groups.Count ? groups[n] : null;
                // How bright: a small engine a gentle glow, a big one enough to light the whole pad. (It comes and goes over a moment.)
                float want = g != null ? Mathf.Clamp(0.5f + Mathf.Sqrt(g.Power) / 22f, 0.5f, 3f) * strength : 0f;
                float was = shown[n];
                shown[n] = Mathf.MoveTowards(shown[n], want, Mathf.Min(Time.unscaledDeltaTime, 0.1f) * 6f * Mathf.Max(want, shown[n], 0.5f));
                if (shown[n] <= 0.01f || g == null)
                {
                    // (the engine has stopped: its lights die away where they were, and are then put out)
                    float left = g == null && shown[n] > 0.01f && was > 1e-4f ? shown[n] / was : 0f;
                    Fade(lamps[n], left);
                    if (n < Full) for (int k = 0; k < Along + Round; k++) Fade(soft[n * (Along + Round) + k], left);
                    continue;
                }
                if (lamps[n] == null) lamps[n] = Lamp("Natural Light engine flame " + n, n == 0);

                // How long the flame is, how far its light carries, and how much room it has before it meets the ground (or a building).
                float length = Mathf.Clamp(1.5f + 0.55f * Mathf.Sqrt(g.Power), 3f, 40f);
                float reach = Mathf.Clamp(30f + 9f * Mathf.Sqrt(g.Power), 40f, 600f);
                float room = float.PositiveInfinity;
                Vector3 landsAt = Vector3.zero, slope = -g.Along;
                if (Physics.Raycast(g.At - g.Along * 0.5f, g.Along, out RaycastHit hit, reach + 0.5f, 1 << 15))
                {
                    room = Mathf.Max(0f, hit.distance - 0.5f); landsAt = hit.point; slope = hit.normal;
                }

                // Where the flame leaves the engine: so that it lights what a flame would, the engine's own bell and the ground under it.
                Light lamp = lamps[n];
                lamp.enabled = true;
                Hang(lamp, g.Ride);
                lamp.transform.position = g.At + g.Along * Mathf.Min(0.8f + 0.12f * length, Mathf.Max(0.15f, room - 0.4f));
                lamp.color = g.Tint;
                lamp.range = reach;
                lamp.intensity = shown[n] * flicker;
                Flames.Add(new Flame { At = lamp.transform.position, Light = g.Tint.linear * (shown[n] * flicker), Reach = reach });
                said += (said.Length > 0 ? ", " : "") + Mathf.RoundToInt(g.Power) + " kN reaching " + Mathf.RoundToInt(reach) + " m" + (room < reach ? ", the ground " + room.ToString("F0") + " m under it" : "");
                if (n >= Full) continue;

                // Round the flame, a third of the way down it and as far out as it is wide: a flame is not a line, and it is its width
                // that catches the sides of what it is pushing. (Set out from north, not from the craft, so that a craft that rolls
                // does not swing its own light round.)
                int first = n * (Along + Round);
                Vector3 across = Vector3.ProjectOnPlane(north, g.Along);
                if (across.sqrMagnitude < 1e-4f) across = Vector3.ProjectOnPlane(Vector3.right, g.Along);
                across.Normalize();
                Vector3 other = Vector3.Cross(g.Along, across);
                float girth = Mathf.Clamp(0.6f + 0.1f * length, 1f, 5f);
                for (int k = 0; k < Along; k++)
                {
                    Light l = soft[first + k];
                    if (l == null) l = soft[first + k] = Lamp("Natural Light engine flame " + n + " side " + k, false);
                    l.enabled = true;
                    Hang(l, g.Ride);
                    float turn = (k * 120f + 15f) * Mathf.Deg2Rad;
                    l.transform.position = g.At + g.Along * Mathf.Min(0.35f * length, Mathf.Max(0.3f, room - 0.5f)) + (across * Mathf.Cos(turn) + other * Mathf.Sin(turn)) * girth;
                    l.color = g.Tint;
                    l.range = reach;
                    l.intensity = shown[n] * 0.3f * (0.86f + 0.28f * Mathf.PerlinNoise(time * 13f, 7.7f + 3.3f * (first + k)));
                }

                // Round where it lands: the ground lit by the flame, lighting the craft back from below and from every side. Nothing of
                // it when the ground is out of the flame's reach; all of it when the flame is on the ground.
                float lands = Mathf.Min(2.2f * length + 12f, reach);
                float near = room < lands ? Mathf.Pow(1f - room / lands, 1.5f) : 0f;
                Vector3 one = Vector3.ProjectOnPlane(north, slope);
                if (one.sqrMagnitude < 1e-4f) one = Vector3.ProjectOnPlane(Vector3.right, slope);
                one.Normalize();
                Vector3 two = Vector3.Cross(slope, one);
                float wide = Mathf.Clamp(0.45f * room + 0.3f * length, 3.5f, 28f);
                Color thrown = Color.Lerp(g.Tint, new Color(1f, 0.6f, 0.34f), 0.3f);          // (what comes back off scorched ground is redder than the flame)
                for (int k = 0; k < Round; k++)
                {
                    Light l = soft[first + Along + k];
                    if (near <= 0.003f) { if (l != null && l.enabled) l.enabled = false; continue; }
                    if (l == null) l = soft[first + Along + k] = Lamp("Natural Light engine flame " + n + " off the ground " + k, false);
                    l.enabled = true;
                    float turn = (k * 120f + 40f) * Mathf.Deg2Rad;
                    l.transform.position = landsAt + (one * Mathf.Cos(turn) + two * Mathf.Sin(turn)) * wide + slope * 0.7f;
                    l.color = thrown;
                    l.range = Mathf.Clamp(0.5f * reach, 22f, 100f);
                    l.intensity = shown[n] * 0.34f * near * (0.8f + 0.4f * Mathf.PerlinNoise(time * 6.5f, 1.9f + 5.1f * (first + k)));
                }
            }
            Report = said.Length > 0 ? "Engine light: " + said + "." : "";
        }

#if DEV
        static readonly List<GameObject> tries = new List<GameObject>();

        /// <summary>
        /// (For the development build: lights put up by hand, to see what the game's shaders make of each kind. They are read
        /// from the text kept as "try": lights apart by "|", each "kind mode east north up strength reach r g b angle", where
        /// kind is point, spot (aimed at the craft) or sun (a light from that side, from everywhere), mode is pixel, vertex
        /// or auto, and the place is in metres from the craft. Nothing kept: they are taken down.)
        /// </summary>
        public static string Try()
        {
            foreach (GameObject g in tries) if (g != null) Destroy(g);
            tries.Clear();
            // (and any left behind by a copy of this that has since been replaced)
            foreach (Light left in FindObjectsOfType<Light>()) if (left != null && left.name.StartsWith("Natural Light trial")) Destroy(left.gameObject);
            Vessel v = FlightGlobals.ActiveVessel;
            string text = Daylight.Kept("try");
            if (v == null || string.IsNullOrEmpty(text) || text.Trim() == "-") return "none";
            Vector3d place = v.GetWorldPos3D();
            Vector3 up = (place - v.mainBody.position).normalized;
            Vector3 north = Vector3.ProjectOnPlane((Vector3)v.mainBody.transform.up, up).normalized, east = Vector3.Cross(up, north);
            Vector3 middle = v.CoM;
            var said = new System.Text.StringBuilder();
            foreach (string one in text.Split('|'))
            {
                string[] w = one.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (w.Length < 7) continue;
                Func<int, float, float> num = (n, otherwise) => n < w.Length && float.TryParse(w[n], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float got) ? got : otherwise;
                var holder = new GameObject("Natural Light trial " + tries.Count);
                // (kept on the planet, as the launch site's own buildings are, so that it stays where it is put)
                if (v.mainBody.pqsController != null) holder.transform.SetParent(v.mainBody.pqsController.transform, true);
                Light lamp = holder.AddComponent<Light>();
                lamp.type = w[0] == "spot" ? LightType.Spot : w[0] == "sun" ? LightType.Directional : LightType.Point;
                lamp.renderMode = w[1] == "pixel" ? LightRenderMode.ForcePixel : w[1] == "vertex" ? LightRenderMode.ForceVertex : LightRenderMode.Auto;
                Vector3 at = middle + east * num(2, 0f) + north * num(3, 0f) + up * num(4, 0f);
                holder.transform.position = at;
                if (lamp.type != LightType.Point) holder.transform.rotation = Quaternion.LookRotation((middle - at).normalized, up);
                lamp.intensity = num(5, 1f);
                lamp.range = num(6, 50f);
                lamp.color = new Color(num(7, 1f), num(8, 1f), num(9, 1f));
                lamp.spotAngle = num(10, 40f);
                lamp.shadows = w.Length > 11 && w[11] == "hard" ? LightShadows.Hard : w.Length > 11 && w[11] == "soft" ? LightShadows.Soft : LightShadows.None;
                lamp.cullingMask = Daylight.Lit;
                tries.Add(holder);
                said.Append(lamp.type + " " + lamp.renderMode + " " + (at - middle).magnitude.ToString("F0") + " m off, " + lamp.intensity + " reaching " + lamp.range + " m; ");
            }
            return said.Length > 0 ? said.ToString() : "nothing understood in: " + text;
        }
#endif
    }
}
