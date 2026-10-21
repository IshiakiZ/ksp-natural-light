using System;
using System.Collections.Generic;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>
    /// (Since 0.12.0.) The red lamps on the space centre's tall buildings by night.
    ///
    /// Anything that stands high enough to be a danger to aircraft is marked by night with red lamps at its top (the rules of
    /// the FAA's Advisory Circular 70/7460-1 and ICAO's Annex 14): steady red lamps (L-810, some 32 candela) at the top corners
    /// of a wide building, and at the top of a tall one a beacon that flashes red (L-864, some 2,000 candela, twenty to forty
    /// flashes a minute; on the Vehicle Assembly Building at Kennedy, and on the masts round its pads). They are among the
    /// few colours of a launch site at night, beside the floodlights' white, and the game's space centre has none.
    ///
    /// So each building of the space centre is measured once the scene has settled (its meshes, in its own frame) and, where
    /// it stands 40 m or more over its own foot, given red lamps: at the corners of its roof where the roof is wide, or one at
    /// its top where it is narrow (a mast, a dish, a tower); and from 75 m its top ones flash, thirty times a minute, rising
    /// and falling as an incandescent lamp's filament does. Lit from dusk to dawn, as the floodlights are. Each is a card
    /// turned to face the camera (the floodlights' glare), as large as the lamp is near to and a few pixels far off; no light
    /// is thrown (a lamp of 32 candela lights nothing a camera would see).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.FlightAndKSC, false)]
    public sealed class Beacons : MonoBehaviour
    {
        sealed class Mark
        {
            public Transform At;            // the card, a child of the building
            public Renderer Shows;
            public bool Flashes;
            public float Phase;             // (each beacon at its own moment: they are not wired together)
        }

        readonly List<Mark> marks = new List<Mark>();
        readonly HashSet<Transform> measured = new HashSet<Transform>();
        float nextLook = 3f, on;
        int looks;
        public static string Report = "";
#if DEV
        /// <summary>(Development build) what was measured, and how many lamps are lit.</summary>
        public static string Says() => Report + " (" + (last != null ? last.marks.Count + " lamps, on " + last.on.ToString("F2") : "none") + ")";
        static Beacons last;
#endif
        static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        static readonly Color Red = new Color(1f, 0.09f, 0.03f);
        const float Tall = 40f, Flashing = 75f, Wide = 18f;

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
            foreach (Mark m in marks) if (m.At != null) Destroy(m.At.gameObject);
            marks.Clear();
            Report = "";
        }

        /// <summary>The buildings, measured once each: how high they stand and where their tops are.</summary>
        void Look()
        {
            PSystemSetup setup = PSystemSetup.Instance;
            if (setup == null || setup.SpaceCenterFacilities == null) return;
            Material glow = Floodlights.Glow();
            Mesh card = Floodlights.Card();
            if (glow == null || card == null) return;
            var said = new System.Text.StringBuilder();
            foreach (PSystemSetup.SpaceCenterFacility f in setup.SpaceCenterFacilities)
            {
                Transform t = f != null ? f.facilityTransform : null;
                if (t == null || !t.gameObject.activeInHierarchy || measured.Contains(t)) continue;
                // Its meshes in its own frame: the box of them all, and of each the top (its own box's, or where the mesh can be
                // read, the corners of the points of its roof).
                bool any = false;
                Bounds all = default;
                var tops = new List<Vector3>();
                float top = float.MinValue;
                foreach (MeshFilter mf in t.GetComponentsInChildren<MeshFilter>(false))
                {
                    Mesh mesh = mf != null ? mf.sharedMesh : null;
                    Renderer r = mf != null ? mf.GetComponent<Renderer>() : null;
                    if (mesh == null || r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                    Matrix4x4 into = t.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                    Bounds b = mesh.bounds;
                    Bounds mine = default;
                    for (int c = 0; c < 8; c++)
                    {
                        Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1f : 1f, (c & 2) == 0 ? -1f : 1f, (c & 4) == 0 ? -1f : 1f));
                        Vector3 p = into.MultiplyPoint3x4(corner);
                        if (c == 0) mine = new Bounds(p, Vector3.zero); else mine.Encapsulate(p);
                    }
                    if (!any) { all = mine; any = true; } else all.Encapsulate(mine);
                    if (mine.max.y > top - 0.5f)
                    {
                        if (mine.max.y > top + 0.5f) tops.Clear();
                        top = Mathf.Max(top, mine.max.y);
                        tops.AddRange(RoofOf(mesh, into, mine));
                    }
                }
                if (!any) continue;
                measured.Add(t);
                float high = all.max.y - all.min.y;
                said.Append(f.name).Append(' ').Append(high.ToString("F0")).Append(" m");
                // (not the flag on its pole, nor anything said to stand higher than any building of the space centre does: the flag's
                // meshes measured 305 m, its cloth's box)
                if (high < Tall || high > 160f || tops.Count == 0 || f.name.IndexOf("Flag", StringComparison.OrdinalIgnoreCase) >= 0) { said.Append("; "); continue; }
                // (the roof's corners: the points of the top furthest out each way, where they are far enough apart to be a roof)
                Vector3 lo = tops[0], hi = tops[0];
                foreach (Vector3 p in tops) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
                var at = new List<Vector3>();
                if (hi.x - lo.x > Wide && hi.z - lo.z > Wide)
                {
                    foreach (Vector3 want in new[] { new Vector3(lo.x, 0f, lo.z), new Vector3(hi.x, 0f, lo.z), new Vector3(lo.x, 0f, hi.z), new Vector3(hi.x, 0f, hi.z) })
                    {
                        // (the point of the roof nearest each corner of its box, so that a lamp is on the building, not in the air beside it)
                        Vector3 best = tops[0];
                        float bestD = float.MaxValue;
                        foreach (Vector3 p in tops) { float d = (p.x - want.x) * (p.x - want.x) + (p.z - want.z) * (p.z - want.z); if (d < bestD) { bestD = d; best = p; } }
                        at.Add(best);
                    }
                }
                else
                {
                    Vector3 mid = Vector3.zero;
                    foreach (Vector3 p in tops) mid += p;
                    mid /= tops.Count;
                    at.Add(new Vector3(mid.x, top, mid.z));
                }
                bool flashes = high >= Flashing;
                foreach (Vector3 p in at)
                {
                    var g = new GameObject("Natural Light beacon on " + f.name) { layer = 15 };
                    g.transform.SetParent(t, false);
                    g.transform.localPosition = p + Vector3.up * 0.4f;
                    g.AddComponent<MeshFilter>().sharedMesh = card;
                    var shows = g.AddComponent<MeshRenderer>();
                    shows.sharedMaterial = glow;
                    shows.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    shows.receiveShadows = false;
                    shows.enabled = false;
                    marks.Add(new Mark { At = g.transform, Shows = shows, Flashes = flashes, Phase = UnityEngine.Random.value });
                }
                said.Append(": ").Append(at.Count).Append(flashes ? " flashing" : " steady").Append("; ");
            }
            if (said.Length > 0) Report = "Buildings: " + said.ToString().TrimEnd(' ', ';');
        }

        /// <summary>The points of a mesh's roof in the building's frame: where the mesh can be read, its points within half a metre of its top; else its box's top corners.</summary>
        static IEnumerable<Vector3> RoofOf(Mesh mesh, Matrix4x4 into, Bounds box)
        {
            var found = new List<Vector3>();
            if (mesh.isReadable)
            {
                Vector3[] points = mesh.vertices;
                float most = float.MinValue;
                var lifted = new Vector3[points.Length];
                for (int i = 0; i < points.Length; i++) { lifted[i] = into.MultiplyPoint3x4(points[i]); most = Mathf.Max(most, lifted[i].y); }
                for (int i = 0; i < lifted.Length; i++) if (lifted[i].y > most - 0.5f) found.Add(lifted[i]);
                if (found.Count > 0) return found;
            }
            found.Add(new Vector3(box.min.x, box.max.y, box.min.z)); found.Add(new Vector3(box.max.x, box.max.y, box.min.z));
            found.Add(new Vector3(box.min.x, box.max.y, box.max.z)); found.Add(new Vector3(box.max.x, box.max.y, box.max.z));
            return found;
        }

        void LateUpdate()
        {
            bool present = Daylight.Near && Daylight.Body != null && Daylight.On && (Daylight.RedLamps == null || Daylight.RedLamps) && !MapView.MapIsEnabled;
            if (present && looks < 6 && Time.timeSinceLevelLoad > nextLook) { nextLook = Time.timeSinceLevelLoad + 5f; looks++; Look(); }
            // (on at dusk with the floodlights, slowly; at once off when the mod is)
            float dark = present ? Mathf.InverseLerp(0.05f, -0.035f, Daylight.SunUp) : 0f;
            on = present ? Mathf.MoveTowards(on, dark, Mathf.Min(Time.unscaledDeltaTime, 0.1f) * 0.45f) : 0f;
            float t = Time.time;
            foreach (Mark m in marks)
            {
                if (m.At == null || m.Shows == null) continue;
                float lit = on;
                if (m.Flashes)
                {
                    // (thirty flashes a minute, each some half a second, the filament's warming and cooling a tenth of a second)
                    float phase = Mathf.Repeat(t * 0.5f + m.Phase, 1f);
                    lit *= Mathf.SmoothStep(0f, 1f, phase / 0.1f) * (1f - Mathf.SmoothStep(0f, 1f, (phase - 0.45f) / 0.12f));
                }
                bool show = lit > 0.01f;
                if (m.Shows.enabled != show) m.Shows.enabled = show;
                if (!show) continue;
                float v = (m.Flashes ? 2.4f : 1.3f) * lit;
                block.SetColor("_TintColor", new Color(Red.r * v * 0.5f, Red.g * v * 0.5f, Red.b * v * 0.5f, 0.5f));
                m.Shows.SetPropertyBlock(block);
            }
        }

        /// <summary>Each card turned to face the camera, as large as a lamp's glare is near to and never smaller than a few pixels.</summary>
        void Face(Camera cam)
        {
            if (cam == null || marks.Count == 0 || on <= 0f) return;
            string name = cam.name;
            if (name != "Camera 00" && name != "Camera 01") return;
            Transform eye = cam.transform;
            float pixel = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(cam.pixelHeight, 1);
            foreach (Mark m in marks)
            {
                if (m.At == null || !m.Shows.enabled) continue;
                float far = (m.At.position - eye.position).magnitude;
                m.At.rotation = eye.rotation;
                float size = Mathf.Max(m.Flashes ? 2.4f : 1.4f, far * pixel * (m.Flashes ? 11f : 7f));
                Vector3 parent = m.At.parent != null ? m.At.parent.lossyScale : Vector3.one;
                m.At.localScale = new Vector3(size / Mathf.Max(parent.x, 1e-4f), size / Mathf.Max(parent.y, 1e-4f), size / Mathf.Max(parent.z, 1e-4f));
            }
        }
    }
}
