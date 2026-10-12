using System.Collections.Generic;
using Keystone;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// Smoke that takes the light.
    ///
    /// The game's smoke (an engine's trail, the cloud at a launch, the puff of a decoupler) is drawn in one
    /// brightness whatever light falls on it: at midnight as white as at noon, which among dark ground and a
    /// rocket lit only by its own flame looks pasted on. Here every puff is tinted by the light where it
    /// comes from: the sun as it reaches the craft (gold at sunrise, gone at night), the sky and the ground,
    /// a flame if one is near, and the floodlights of a launch site. By day it is as the game has it.
    ///
    /// It is one tint for all the smoke of one source, by the light at the source: smoke that has drifted a
    /// long way from a flame is still lit as if beside it. And it is the tint the game's own smoke shaders
    /// have, set for each drawing of them and not in the materials, so nothing is left changed.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class LitSmoke : MonoBehaviour
    {
        public static string Report = "";

        sealed class Puff
        {
            public ParticleSystemRenderer Shows;
            public Material Of;
            public Color Its;                   // the tint its material has
        }

        readonly List<Puff> puffs = new List<Puff>();
        readonly HashSet<int> known = new HashSet<int>();
        MaterialPropertyBlock block;
        float nextLook;
        int tint;

        // (the light of full day, as these sums have it: what leaves the game's smoke as it is)
        const float Day = 0.55f;

        void Awake()
        {
            Daylight.Setup();
            tint = Shader.PropertyToID("_TintColor");
            block = new MaterialPropertyBlock();
        }

        void OnDestroy() { Back(); Report = ""; }

        void Back()
        {
            foreach (Puff p in puffs) if (p.Shows != null) p.Shows.SetPropertyBlock(null);
            puffs.Clear(); known.Clear();
        }

        /// <summary>Whether this is smoke: drawn over what is behind it, by one of the game's own shaders for that. (Flame and sparks are added to what is behind them, and are their own light.)</summary>
        static bool Smoke(Material m)
        {
            if (m == null || m.shader == null || !m.HasProperty("_TintColor")) return false;
            // (the weather's own drops, flakes and dust light themselves, by the scene's light as it is drawn: left alone. Taken for smoke
            // and given a tint of their own once found, the flakes showed as dark specks in any light)
            if (m.name.StartsWith("NaturalWeather")) return false;
            // (and the sea's spray, which Natural Ocean lights with the light it lights its foam by: tinted here as well, a
            // puff of spray off a white crest was orange in the afternoon)
            if (m.name.StartsWith("Natural Ocean")) return false;
            string name = m.shader.name;
            return name.Contains("Alpha Blended") && (name.StartsWith("KSP/") || name.StartsWith("Particles/") || name.StartsWith("Legacy Shaders/Particles/") || name.StartsWith("Mobile/Particles/"));
        }

        void Look()
        {
            puffs.RemoveAll(p => p.Shows == null || p.Shows.sharedMaterial != p.Of);
            known.Clear();
            foreach (Puff p in puffs) known.Add(p.Shows.GetInstanceID());
            foreach (ParticleSystemRenderer shows in FindObjectsOfType<ParticleSystemRenderer>())
            {
                if (shows == null || known.Contains(shows.GetInstanceID())) continue;
                Material m = shows.sharedMaterial;
                if (!Smoke(m)) continue;
                puffs.Add(new Puff { Shows = shows, Of = m, Its = m.GetColor(tint) });
                known.Add(shows.GetInstanceID());
            }
        }

        void LateUpdate()
        {
            bool wanted = Daylight.On && Daylight.Smoke != null && Daylight.Smoke && Daylight.Near && !MapView.MapIsEnabled;
            if (!wanted) { if (puffs.Count > 0) Back(); Report = ""; return; }
            if (Time.unscaledTime > nextLook) { nextLook = Time.unscaledTime + 0.5f; Look(); }
            if (puffs.Count == 0) { Report = ""; return; }

            // What there is everywhere: the sun as it reaches here (half and a bit of it: smoke is lit on one side and shaded on the
            // other), and the sky and the ground from all round.
            Light sunLight = Sun.Instance != null ? Sun.Instance.sunLight : null;
            Color sun = sunLight != null ? Daylight.Sunlight.linear * (sunLight.intensity * 0.6f) : Color.black;
            Color everywhere = sun + Daylight.Around;
            // (Full day is whatever the day is here, and leaves the smoke as the game has it. And a flame or a lamp shows on smoke by
            // as much as the day has gone: in full sun it adds nothing that could be seen.)
            float seen = 0.2126f * everywhere.r + 0.7152f * everywhere.g + 0.0722f * everywhere.b;
            if (seen > Day) everywhere *= Day / seen;
            float dark = 1f - Mathf.Clamp01(seen / Day);
            int lit = 0;
            Color shown = Color.black;
            foreach (Puff p in puffs)
            {
                if (p.Shows == null || !p.Shows.enabled || !p.Shows.gameObject.activeInHierarchy) continue;
                Vector3 at = p.Shows.transform.position;
                Color light = everywhere;
                // (a flame near where the smoke comes from: by how near, out to as far as its light carries)
                foreach (EngineLamps.Flame f in EngineLamps.Flames)
                {
                    float near = 1f - Mathf.Clamp01((f.At - at).magnitude / Mathf.Max(f.Reach, 1f));
                    light += f.Light * (0.2f * near * near * dark);
                }
                // (and the floodlights of a launch site, to the edge of its ring and a little beyond)
                foreach (Floodlights.Shining s in Floodlights.Lit)
                    light += Floodlights.LampColour.linear * (0.12f * s.On * dark * (1f - Mathf.Clamp01(((s.At - at).magnitude - 40f) / 80f)));
                // (as it shows, against full day; never quite nothing, and a little over where a flame is right beside it)
                Color takes = new Color(Shown(light.r), Shown(light.g), Shown(light.b));
                Color with = new Color(p.Its.r * takes.r, p.Its.g * takes.g, p.Its.b * takes.b, p.Its.a);
                p.Shows.GetPropertyBlock(block);
                block.SetColor(tint, with);
                p.Shows.SetPropertyBlock(block);
                if (lit == 0) shown = takes;
                lit++;
            }
            Report = lit > 0 ? "Smoke takes " + Mathf.RoundToInt(100f * (0.2126f * shown.r + 0.7152f * shown.g + 0.0722f * shown.b)) + "% of the light of day (" + lit + " sources of it)." : "";
        }

        static float Shown(float light) => Mathf.Clamp(Mathf.Pow(Mathf.Max(light, 0f) / Day, 1f / 2.2f), 0.06f, 1.25f);

#if DEV
        /// <summary>(For the development build: every particle drawing there is, by its shader, with how many there are of each and whether it is taken for smoke.)</summary>
        public static string List()
        {
            var count = new Dictionary<string, int>();
            foreach (ParticleSystemRenderer shows in FindObjectsOfType<ParticleSystemRenderer>())
            {
                Material m = shows.sharedMaterial;
                string key = (m != null && m.shader != null ? m.shader.name : "no shader") + (Smoke(m) ? " [smoke]" : "") + (m != null && m.HasProperty("_TintColor") ? " tint " + m.GetColor("_TintColor") : "") + " (" + (m != null ? m.name : "") + ")";
                count[key] = count.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            var o = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, int> pair in count) o.Append(pair.Value + " x " + pair.Key + "\n");
            return o.ToString();
        }
#endif
    }
}
