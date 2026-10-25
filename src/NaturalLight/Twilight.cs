using System;
using Keystone;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// (Since 0.13.0.) The stars come out at dusk as they really do.
    ///
    /// The game hides its stars in air until the sun is some 25 degrees below the horizon and then lets them all out within a
    /// degree or two: its fade by the hour is multiplied by the air's pressure in the wrong units (it comes to about 130 at the
    /// space centre where it means about 0.2), so it is many times too strong until the sun is all but at its end. And at night it
    /// keeps them at two fifths of their brightness in air, as if the air took more than two magnitudes of their light, where
    /// clean air takes a quarter of one overhead.
    ///
    /// So here, in air, they are hidden as the sky's own light hides them: by day not at all to be seen; as the sun goes down the
    /// brightest come out a few degrees after sunset and the rest through the twilight, the faintest at full night, 18 degrees
    /// down; and at night they are as bright as the air lets them be. How many are out at each height of the sun is a real sky's:
    /// an eye sees stars to magnitude -2 at sunset, 0 at 3 degrees down, 2 at 6, 3.5 at 9, 4.5 at 12, 5.5 at 15 and 6.5 at 18, and
    /// a real sky has 4 stars brighter than 0, 48 than 2, about 300 than 3.5, 900 than 4.5, 2,800 than 5.5. The game's sky has far
    /// less between its brightest and its faintest than a real one (four and a half magnitudes against seven and a half: DEV
    /// Census), so dimmed by the sky's light as a real one is, nearly all of its stars went out together: with the sun 12 degrees
    /// down, 7 in a view that had 187 at night. So it is dimmed instead so that as many of its stars are brighter than the faintest
    /// it shows at night as a real sky has brighter than what an eye sees then (the census's brightness of its 4,800th star over
    /// that of its 4th, 48th, 300th ...). That is for Kerbin's air at sea level; thinner air lights the sky less and lets them out
    /// sooner (Duna's), thicker later (Eve's). Still dimmed as the game dims them when the sun is in sight, and by this mod's eye
    /// (FilmLook). Out of air the game's own is left as it is. Natural Light's deep sky and its far lights follow (they read the
    /// game's faces after this has set them): so a bright world comes out against them as it does in a real sky, Jool-like before
    /// any star, the rest with the brightest stars.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    [DefaultExecutionOrder(100)]
    public sealed class Twilight : MonoBehaviour
    {
        // (the share of their light the stars keep with the sun this many degrees up, for Kerbin's air at sea level: the census's
        // brightness of the faintest star a night shows, over that of the star whose rank is the number of a real sky's stars an eye
        // sees then: its 1st at sunset, 4th, 48th, 300th, 900th, 2,800th; by day, next to nothing. The faintest a night shows is
        // somewhere between its 3,000th and its 4,800th (the census counts the band's clumps as stars too): taken between the two,
        // with the sun 12 degrees down a third of the stars of a night out in a view, as an eye sees at the end of nautical twilight)
        static readonly float[] heights = { 15f, 0f, -3f, -6f, -9f, -12f, -15f, -18f };
        static readonly float[] kept = { 3e-5f, 0.015f, 0.03f, 0.07f, 0.17f, 0.36f, 0.7f, 1f };

        GalaxyCubeControl skyWas;
        Renderer[] faces = new Renderer[0];
        float glare;
        static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
#if DEV
        static float sunHeld = float.NaN;
        static string said = "";
#endif

        void LateUpdate()
        {
#if DEV
            timer.Restart();
            Work();
            timer.Stop();
            float ms = (float)timer.Elapsed.TotalMilliseconds;
            meanMs = Mathf.Lerp(meanMs, ms, 0.02f); worstMs = Mathf.Max(worstMs * 0.999f, ms);
#else
            Work();
#endif
        }
#if DEV
        static readonly System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();
        static float meanMs, worstMs;
        /// <summary>(Development build: what this costs a frame, its mean and its worst lately.)</summary>
        public static string Cost() => meanMs.ToString("F3") + " ms a frame, the worst lately " + worstMs.ToString("F2");
#endif

        void Work()
        {
            GalaxyCubeControl sky = GalaxyCubeControl.Instance;
            if (!Daylight.On || Daylight.Dusk == null || !Daylight.Dusk || sky == null || MapView.MapIsEnabled || Planetarium.fetch == null || Planetarium.fetch.Sun == null) return;
            Camera eye = HighLogic.LoadedSceneIsFlight && FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : Names.CameraNamed("Camera 00") ?? Camera.main;
            CelestialBody home = HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.mainBody : FlightGlobals.GetHomeBody();
            if (eye == null || home == null || !home.atmosphere) return;
            Vector3d here = eye.transform.position, fromMiddle = here - home.position;
            double r = fromMiddle.magnitude, height = r - home.Radius;
            if (height >= home.atmosphereDepth) return;
            if (sky != skyWas)
            {
                skyWas = sky;
                var all = sky.GetComponentsInChildren<Renderer>(true);
                int n = 0;
                foreach (Renderer f in all) if (f != null && !f.name.StartsWith("Natural Light")) n++;
                faces = new Renderer[n];
                n = 0;
                foreach (Renderer f in all) if (f != null && !f.name.StartsWith("Natural Light")) faces[n++] = f;
            }

            Vector3 up = (Vector3)(fromMiddle / r);
            Vector3d toSun = Planetarium.fetch.Sun.position - here;
            float sunHigh = Mathf.Asin(Mathf.Clamp(Vector3.Dot(up, (Vector3)toSun.normalized), -1f, 1f)) * Mathf.Rad2Deg;
#if DEV
            if (!float.IsNaN(sunHeld)) sunHigh = sunHeld;
#endif
            // (how much air there is above, against Kerbin's at sea level: its weight, the pressure over the pull)
            float column = (float)(home.GetPressure(Math.Max(0.0, height)) / 101.325 / Math.Max(0.01, home.GeeASL));
            // The sky's light, and how faint a star shows against it: as light, the share of a star's light left to be seen.
            float seaLevel = Kept(sunHigh);
            float shows = 1f / (1f + column * (1f / Mathf.Max(seaLevel, 1e-7f) - 1f));
            // (and the air's own dimming of what comes through it, overhead: a quarter of a magnitude at Kerbin's sea level)
            shows *= Mathf.Pow(10f, -0.4f * 0.25f * column);
            // The sun in sight dims them as the game has it: by six tenths of their colour with the sun inside the view, less to ten
            // degrees outside it, eased in and out. (The game asks whether the sun can be seen with a ray through the planets as they
            // are drawn from afar; here, whether it is above the horizon and not behind another world.)
            double dip = Math.Sqrt(Math.Max(0.0, 1.0 - Math.Pow(home.Radius / Math.Max(r, home.Radius), 2.0)));
            bool sunSeen = Vector3.Dot(up, (Vector3)toSun.normalized) > -dip && Eclipse.Left > 0.01f;
            float target = 0f;
            if (sunSeen)
            {
                float apart = Vector3.Angle(eye.transform.forward, (Vector3)toSun), half = eye.fieldOfView * 0.5f;
                target = Mathf.Lerp(0.6f, 0f, apart < half ? 0f : Mathf.Clamp01((apart + 10f - half) * 0.05f));
            }
            glare = Mathf.Lerp(glare, target, 1f - Mathf.Pow(0.95f, Time.unscaledDeltaTime * 60f));
            // (the game's faces are coloured as shown, not as light)
            float shown = Mathf.Pow(Mathf.Clamp01(shows), 1f / 2.2f) * Mathf.Clamp01(1f - glare);
            block.Clear();
            block.SetColor("_Color", Color.Lerp(sky.minGalaxyColor, sky.maxGalaxyColor, shown));
            for (int i = 0; i < faces.Length; i++) if (faces[i] != null) faces[i].SetPropertyBlock(block);
#if DEV
            said = "the sun " + sunHigh.ToString("F1") + " degrees up, the air " + column.ToString("F2") + " of Kerbin's at sea level: the stars show " + (100f * shows).ToString("F2") + "% of their light, the sun's glare " + glare.ToString("F2") + ", their colour " + shown.ToString("F3");
#endif
        }

        /// <summary>The share kept at sea level, between the heights in the table (by its logarithm: each step is so many times).</summary>
        static float Kept(float sunHigh)
        {
            if (sunHigh >= heights[0]) return kept[0];
            for (int i = 1; i < heights.Length; i++)
                if (sunHigh >= heights[i]) return Mathf.Exp(Mathf.Lerp(Mathf.Log(kept[i]), Mathf.Log(kept[i - 1]), (sunHigh - heights[i]) / (heights[i - 1] - heights[i])));
            return kept[kept.Length - 1];
        }

#if DEV
        /// <summary>(Development build: what the stars are set to just now, and why.)</summary>
        public static string Says() => said.Length > 0 ? said : "left to the game";

        /// <summary>(Development build: the sun's height held for the stars' fade alone, to look at: "-8"; "go" lets it be.)</summary>
        public static string Hold(string v) { sunHeld = v == "go" ? float.NaN : float.Parse(v, System.Globalization.CultureInfo.InvariantCulture); return float.IsNaN(sunHeld) ? "the sun as it is" : "the stars faded as with the sun " + sunHeld + " degrees up"; }
#endif
    }
}
