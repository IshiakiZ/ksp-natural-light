using System;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// (Since 0.12.0.) Eclipses: another world before the sun.
    ///
    /// The game takes the sun away behind the world the craft is at (its horizon, with a little fade) and behind nothing else. But
    /// the Mun goes round Kerbin in its plane: every month it passes before the sun for somewhere on Kerbin, as wide in the sky as
    /// the sun is (an eclipse there is total or nearly), and every month Kerbin's shadow sweeps over the Mun and every craft
    /// near it. Until now a craft at the Mun in Kerbin's shadow sat in full sunlight, and Kerbin under the Mun's.
    ///
    /// So here: for every world but the sun and the one the craft is at, how much of the sun's disc it covers seen from the craft
    /// (two discs in the sky, the sun's and the world's, each as wide as its radius over its distance; the share of the sun's
    /// that the two have in common), and the sun and the sky's light (which is the sun's, scattered) are that much dimmer. A
    /// world with air lets a little light round its edge, bent in through its sunsets and as red as they are: from inside
    /// Kerbin's shadow its rim glows copper, which is why the Moon in a lunar eclipse is red. The sun's limb is taken as bright
    /// as its middle. Looked for every frame (sixteen worlds: a few microseconds).
    /// </summary>
    static class Eclipse
    {
        /// <summary>How much of the sun's light reaches the craft past the other worlds (1: none in the way).</summary>
        public static float Left = 1f;
        /// <summary>The light of a world's sunsets round its rim, where the sun is all behind it (as light; black when it has no air).</summary>
        public static Color Ring = Color.black;
        /// <summary>The world in front of the sun, if any.</summary>
        public static string By = "";
#if DEV
        static float held = -1f;
        /// <summary>(Development build: the sun held so far covered, to look at: "0.95"; "go" lets it be.)</summary>
        public static string Hold(string v) { held = v == "go" ? -1f : float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); return held < 0f ? "as the worlds have it" : "the sun " + held + " covered"; }
        public static string Says() => By.Length > 0 ? By + " covers " + (100f * (1f - Left)).ToString("F1") + "% of the sun" : "no eclipse";
#endif

        public static void Look(Vector3d place, CelestialBody home, CelestialBody sun)
        {
            Left = 1f; Ring = Color.black; By = "";
            if (sun == null || FlightGlobals.Bodies == null) return;
            Vector3d toSun = sun.position - place;
            double ds = toSun.magnitude;
            if (ds <= sun.Radius) return;
            double rs = Math.Asin(Math.Min(1.0, sun.Radius / ds));
            Vector3d sunWay = toSun / ds;
            float covered = 0f;
            foreach (CelestialBody b in FlightGlobals.Bodies)
            {
                if (b == null || b == sun || b == home) continue;
                Vector3d to = b.position - place;
                double db = to.magnitude;
                // (only what is between the craft and the sun)
                if (db >= ds || db <= b.Radius) continue;
                double along = Vector3d.Dot(to, sunWay);
                if (along <= 0.0) continue;
                double rb = Math.Asin(Math.Min(1.0, b.Radius / db));
                double apart = Math.Acos(Math.Max(-1.0, Math.Min(1.0, along / db)));
                if (apart >= rs + rb) continue;
                float share = Shared(rs, rb, apart);
                if (share > covered)
                {
                    covered = share; By = b.bodyName;
                    // (a world with air: its sunsets' light round its rim, once the sun is wholly behind it; about a ten-thousandth of
                    // the sun's, as the Moon in Earth's shadow has, and as red as a sunset)
                    Ring = b.atmosphere && share > 0.999f ? new Color(1f, 0.32f, 0.12f) * 0.0004f : Color.black;
                }
            }
#if DEV
            if (held >= 0f) { covered = held; By = "(held)"; Ring = Color.black; }
#endif
            Left = 1f - covered;
        }

        /// <summary>How much of a disc of angular radius rs a disc of radius rb whose middle is `apart` from its own covers (both small: taken as flat).</summary>
        static float Shared(double rs, double rb, double apart)
        {
            if (apart >= rs + rb) return 0f;
            if (apart <= Math.Abs(rb - rs)) return rb >= rs ? 1f : (float)(rb * rb / (rs * rs));
            double a = rs * rs * Math.Acos(Math.Max(-1.0, Math.Min(1.0, (apart * apart + rs * rs - rb * rb) / (2.0 * apart * rs))))
                     + rb * rb * Math.Acos(Math.Max(-1.0, Math.Min(1.0, (apart * apart + rb * rb - rs * rs) / (2.0 * apart * rb))))
                     - 0.5 * Math.Sqrt(Math.Max(0.0, (-apart + rs + rb) * (apart + rs - rb) * (apart - rs + rb) * (apart + rs + rb)));
            return (float)Math.Min(1.0, Math.Max(0.0, a / (Math.PI * rs * rs)));
        }
    }
}
