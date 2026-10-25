using System;
using System.Collections.Generic;
using Keystone;
using UnityEngine;
using UnityEngine.Rendering;

namespace NaturalLight
{
    /// <summary>
    /// (Since 0.13.0.) A kerbal's visor mirrors what is round them: the ground and the craft, the sky, the planet below.
    ///
    /// Every photograph of anyone in a spacesuit shows it: the visor is a curved mirror with the world in it, small and bent round,
    /// the Earth below, the ship beside them, whoever holds the camera. The game draws its visors' glass with Unity's own shader
    /// for real materials ("Standard (Specular setup)", see-through), which mirrors whatever a reflection probe round it holds;
    /// and the game's one probe, on the camera, is off unless its graphics settings turn reflections on, and then holds only the
    /// ground and the planets against a flat colour for the sky. So the visor showed nothing of where the kerbal was.
    ///
    /// Here a probe of its own sits round the head of the kerbal nearest the camera, and what it holds is drawn from that head,
    /// as a cube of six pictures: the sky of stars, then the worlds far off, then the scene round about (craft, ground, other
    /// kerbals), each by the kind of camera that thing is drawn with in the game, laid one over another. One face a frame, so
    /// all six every tenth of a second. The kerbal's own body is left out of it (a probe inside a head would see the inside of
    /// the helmet). The glass mirrors it as the game's shader works it out, by the angle the light leaves it at: little straight
    /// on, nearly all of it at a glance (Fresnel's rule, as real glass does), and blurred a little, as the visor is not a perfect
    /// mirror.
    ///
    /// The idea and its three passes are TextureReplacerReplaced's (MIT: shaw, then HaArLiNsH and others), which draws real
    /// reflections on visors and windows; worked out anew here, with the game's own visor shader and a probe in place of a shader
    /// of its own (so nothing new is needed on Windows' Direct3D).
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public sealed class Visors : MonoBehaviour
    {
        const int Size = 128;                      // each face of the cube, in dots
        const float Reach = 60f;                   // a kerbal further than this from the camera is not worth it (m)
        const float Smallest = 10f;                // nor one whose visor shows smaller than this on the screen (dots)
        public static string Report = "";
        static Visors running;

        Camera cam;
        RenderTexture cube, shown;                  // drawn into, and the one the visor reads (swapped when all six are new)
        ReflectionProbe probe;
        KerbalEVA who;
        Renderer visor;
        readonly List<Renderer> body = new List<Renderer>();
        readonly List<bool> wasOff = new List<bool>();
        int face;
        string words = "", farWords = "";
        Vector3 drawnAt;
        float lookedAt = -10f;
        bool filled;
#if DEV
        static float meanMs, worstMs;
        static readonly float[] passMs = new float[3];
        static bool lean = true;
        /// <summary>(Development build: the scene pass drawn lean, without shadows and with the sun alone by the pixel, or with the game's settings.)</summary>
        public static string Lean(string v) { lean = v != "off"; return lean ? "lean" : "with the game's own settings"; }
        static readonly System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();
#endif

        void Awake() => running = this;

        void OnDestroy()
        {
            if (running == this) running = null;
            Show(false);
            if (probe != null) Destroy(probe.gameObject);
            if (cam != null) Destroy(cam.gameObject);
            if (cube != null) { cube.Release(); Destroy(cube); }
            if (shown != null) { shown.Release(); Destroy(shown); }
        }

        void Show(bool on)
        {
            if (probe != null && probe.enabled != on) probe.enabled = on;
        }

        void LateUpdate()
        {
            bool wanted = Daylight.On && Daylight.Reflections != null && Daylight.Reflections && Daylight.FlightReady && !MapView.MapIsEnabled;
            Camera eye = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (!wanted || eye == null || GalaxyCubeControl.Instance == null || ScaledCamera.Instance == null || ScaledCamera.Instance.cam == null) { Show(false); Report = ""; return; }

            // ---- whose visor: the kerbal outside nearest the camera, looked for twice a second
            if (Time.unscaledTime - lookedAt > 0.5f || who == null || visor == null)
            {
                lookedAt = Time.unscaledTime;
                Pick(eye);
            }
            if (who == null || visor == null || !visor.gameObject.activeInHierarchy) { Show(false); Report = ""; return; }
            Vector3 head = visor.bounds.center;
            float far = Vector3.Distance(head, eye.transform.position);
            float perDot = 2f * Mathf.Tan(eye.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, eye.pixelHeight);
            float shows = 2f * visor.bounds.extents.magnitude / Mathf.Max(far * perDot, 1e-4f);
            if (far > Reach || shows < Smallest) { Report = farWords; return; }

            Make();
            probe.transform.position = head;
            Show(filled);
            // (the cube is fixed in the world, not to the camera: only the kerbal moving changes what is in it, and slowly the light.
            // So while they move, a face every other frame (the whole cube a fifth of a second); while they stand, or hold a ladder, a
            // face every eighth frame (in under a second). A face every frame cost 4 to 8 ms a frame on the floodlit pad; on the Mun,
            // walking, nothing that could be measured)
            bool moving = (head - drawnAt).sqrMagnitude > 0.05f * 0.05f;
#if DEV
            if (frozen) { Report = "Visors: held"; return; }
#endif
            if (filled && Time.frameCount % (moving ? 2 : 8) != 0) { Report = words; return; }
            drawnAt = head;
#if DEV
            timer.Restart();
#endif
            Draw(head, filled ? 1 << face : 0x3f);
            face = (face + 1) % 6;
            // (a whole cube drawn: it is the one the visor reads now, and the other is drawn into next, so that the visor never
            // shows one half new and half old)
            if (face == 0 || !filled)
            {
                RenderTexture was = shown; shown = cube; cube = was;
                probe.customBakedTexture = shown;
            }
            filled = true;
            Show(true);
#if DEV
            timer.Stop();
            float ms = (float)timer.Elapsed.TotalMilliseconds;
            meanMs = Mathf.Lerp(meanMs, ms, 0.05f); worstMs = Mathf.Max(worstMs * 0.995f, ms);
#endif
            Report = words;
        }

        /// <summary>The kerbal outside nearest the camera whose visor is in sight, and the renderers that draw them.</summary>
        void Pick(Camera eye)
        {
            KerbalEVA best = null; Renderer bestVisor = null; float nearest = Reach;
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null) continue;
                KerbalEVA k = v.evaController;
                // (a kerbal in a seat outside is a part of the craft it sits on)
                if (k == null && v.parts.Count > 1) foreach (Part p in v.parts) { k = p.FindModuleImplementing<KerbalEVA>(); if (k != null) break; }
                if (k == null) continue;
                Renderer r = VisorOf(k);
                if (r == null || !r.gameObject.activeInHierarchy || !r.isVisible) continue;
                float d = Vector3.Distance(r.bounds.center, eye.transform.position);
                if (d < nearest) { nearest = d; best = k; bestVisor = r; }
            }
            if (best == who) return;
            filled = false; face = 0;
            who = best; visor = bestVisor;
            body.Clear();
            if (who == null) return;
            foreach (Renderer r in who.GetComponentsInChildren<Renderer>(false)) body.Add(r);
            // (its words made once for each kerbal: made every frame they would be garbage)
            string name = who.part != null && who.part.protoModuleCrew.Count > 0 ? who.part.protoModuleCrew[0].name : who.vessel.vesselName;
            words = "Visors: " + name + "'s mirrors what is round them.";
            farWords = "Visors: " + name + "'s too far off to be worth it.";
        }

        /// <summary>The kerbal's visor: the renderer called so, drawn with Unity's shader for real materials (the game's suits: the
        /// default, the vintage and the future suit all call it "visor").</summary>
        static readonly Dictionary<KerbalEVA, Renderer> visors = new Dictionary<KerbalEVA, Renderer>();

        static Renderer VisorOf(KerbalEVA k)
        {
            if (visors.TryGetValue(k, out Renderer known) && known != null) return known;
            if (visors.Count > 64) visors.Clear();
            Renderer found = Find(k);
            visors[k] = found;
            return found;
        }

        static Renderer Find(KerbalEVA k)
        {
            Renderer found = null;
            foreach (Renderer r in k.GetComponentsInChildren<Renderer>(true))
            {
                Material m = r.sharedMaterial;
                if (m == null || m.shader == null || !m.shader.name.StartsWith("Standard")) continue;
                if (r.name == "visor") return r;
                if (found == null && r.name.IndexOf("visor", StringComparison.OrdinalIgnoreCase) >= 0) found = r;
            }
            return found;
        }

        void Make()
        {
            if (cube == null) cube = Cube();
            if (shown == null) shown = Cube();
            if (cam == null)
            {
                cam = new GameObject("Natural Light visor camera").AddComponent<Camera>();
                cam.enabled = false;
                cam.allowHDR = true; cam.allowMSAA = false;
                cam.renderingPath = RenderingPath.Forward;
                cam.useOcclusionCulling = false;
            }
            if (probe == null)
            {
                probe = new GameObject("Natural Light visor probe").AddComponent<ReflectionProbe>();
                // (a probe of the kind whose picture is given it: a realtime one handed a picture of its own was never read by the
                // visor, which went on showing the game's sky; painted magenta to see, it stayed as it was)
                probe.mode = ReflectionProbeMode.Custom;
                probe.hdr = true;
                probe.size = new Vector3(3f, 3f, 3f);
                probe.importance = 100;                 // (over the game's own, which covers 500 m round the camera when it is on)
                probe.boxProjection = false;
                probe.customBakedTexture = shown;
                probe.enabled = false;
            }
        }

        static RenderTexture Cube()
        {
            // (as light, past white: the game's near camera draws so, with TUFX, and this mod's eye stops the picture down after. Drawn
            // into a picture of 0 to 1, as at first, the sunlit Mun was white all over in the visor, as everything in sight was)
            var c = new RenderTexture(Size, Size, 16, SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf) ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                dimension = TextureDimension.Cube, useMipMap = true, autoGenerateMips = true, name = "Natural Light visor", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear,
            };
            c.Create();
            return c;
        }

        /// <summary>The faces asked for, drawn from the head: the stars, the worlds far off, then the scene, each over the last.</summary>
        void Draw(Vector3 head, int faces)
        {
            // (the kerbal's own body out of it, as the game's own does for a moment: not by switching renderers off and on, which
            // changes how the game's smoothing between frames sees them)
            wasOff.Clear();
            foreach (Renderer r in body) { wasOff.Add(r != null && r.forceRenderingOff); if (r != null) r.forceRenderingOff = true; }
            try
            {
                Camera scaled = ScaledCamera.Instance.cam, near = FlightCamera.fetch.mainCamera;
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
                cam.transform.position = GalaxyCubeControl.Instance.transform.position;
                cam.cullingMask = 1 << 18; cam.nearClipPlane = 0.1f; cam.farClipPlane = 100f;
                cam.RenderToCubemap(cube, faces);
#if DEV
                long t1 = timer.ElapsedTicks;
#endif

                cam.clearFlags = CameraClearFlags.Depth;
                cam.transform.position = ScaledSpace.LocalToScaledSpace(head);
                cam.cullingMask = scaled.cullingMask; cam.nearClipPlane = scaled.nearClipPlane; cam.farClipPlane = scaled.farClipPlane;
                cam.RenderToCubemap(cube, faces);
#if DEV
                long t2 = timer.ElapsedTicks;
#endif

                cam.transform.position = head;
                cam.cullingMask = near.cullingMask; cam.nearClipPlane = 0.05f; cam.farClipPlane = 30000f;
                // (without shadows, the sun alone lighting by the pixel and the coarser shapes of what has them: drawn with the game's
                // own settings, the sun's shadows worked out again and every floodlight's pass, the scene cost 5 to 9 ms a frame on the
                // pad, where the sky and the planets cost nothing that could be measured. A visor is a small curved mirror: in it a
                // shadow is a few dots, and the lamps still light it, from round about)
                float shadowsWere = QualitySettings.shadowDistance, lodWere = QualitySettings.lodBias;
                int lightsWere = QualitySettings.pixelLightCount;
                try
                {
#if DEV
                    if (lean)
#endif
                    { QualitySettings.shadowDistance = 0f; QualitySettings.pixelLightCount = 1; QualitySettings.lodBias = lodWere * 0.4f; }
                    cam.RenderToCubemap(cube, faces);
                }
                finally { QualitySettings.shadowDistance = shadowsWere; QualitySettings.pixelLightCount = lightsWere; QualitySettings.lodBias = lodWere; }
#if DEV
                long t3 = timer.ElapsedTicks;
                double perMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
                passMs[0] = Mathf.Lerp(passMs[0], (float)(t1 / perMs), 0.05f); passMs[1] = Mathf.Lerp(passMs[1], (float)((t2 - t1) / perMs), 0.05f); passMs[2] = Mathf.Lerp(passMs[2], (float)((t3 - t2) / perMs), 0.05f);
#endif
            }
            finally
            {
                for (int i = 0; i < body.Count; i++) if (body[i] != null) body[i].forceRenderingOff = wasOff[i];
            }
        }

#if DEV
        /// <summary>(Development build: whose visor, what it costs a frame, and every kerbal outside with its visor's material.)</summary>
        public static string Look()
        {
            var o = new System.Text.StringBuilder();
            if (running != null) o.Append(Report + " " + meanMs.ToString("F2") + " ms a frame (the stars " + passMs[0].ToString("F2") + ", the worlds " + passMs[1].ToString("F2") + ", the scene " + passMs[2].ToString("F2") + "), the worst lately " + worstMs.ToString("F1") + (running.visor != null ? "; the visor's probe " + (running.probe != null && running.probe.enabled ? "on" : "off") + ", its texture " + (running.probe != null && running.probe.customBakedTexture != null ? running.probe.customBakedTexture.name : "none") : "") + "\n");
            o.Append("default reflection: " + RenderSettings.defaultReflectionMode + ", probes in the scene " + FindObjectsOfType<ReflectionProbe>().Length + "\n");
            foreach (ReflectionProbe p in FindObjectsOfType<ReflectionProbe>())
                o.Append("probe " + p.name + ": " + p.mode + " " + p.refreshMode + ", at " + p.transform.position.ToString("F1") + " size " + p.size.ToString("F0") + ", importance " + p.importance + ", texture " + (p.texture != null ? p.texture.name + " " + p.texture.width : "none") + (p.enabled ? "" : " (off)") + "\n");
            foreach (KerbalEVA k in FindObjectsOfType<KerbalEVA>())
            {
                Renderer r = VisorOf(k);
                if (r == null) { o.Append(k.name + ": no visor\n"); continue; }
                Material m = r.sharedMaterial;
                o.Append((k.vessel != null ? k.vessel.vesselName : k.name) + "'s visor: " + (m != null ? m.shader.name + ", smooth " + (m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness").ToString("F2") : "?") + ", keywords " + string.Join(",", m.shaderKeywords) : "no material") + ", seen " + r.isVisible + ", probes " + r.reflectionProbeUsage + "\n");
            }
            return o.ToString();
        }

        /// <summary>(Development build: the nearest kerbal's dark visor lowered, or raised again: "down", "up".)</summary>
        public static string Visor(string v)
        {
            if (running == null || running.who == null) return "no kerbal";
            if (v == "down") running.who.LowerVisor(false); else running.who.RaiseVisor(false);
            return running.who.vessel.vesselName + "'s visor " + (v == "down" ? "lowered" : "raised");
        }

        static bool frozen;
        /// <summary>(Development build: the cube the visor reads painted one colour and held so: "1 0 1" magenta, "off" lets it be drawn again.)</summary>
        public static string Paint(string v)
        {
            if (v == "off") { frozen = false; return "drawn again"; }
            if (running == null || running.shown == null) return "no cube";
            string[] w = v.Split(' ');
            var c = new Color(float.Parse(w[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(w[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(w[2], System.Globalization.CultureInfo.InvariantCulture), 1f);
            RenderTexture before = RenderTexture.active;
            foreach (RenderTexture rt in new[] { running.shown, running.cube })
                for (int f = 0; f < 6; f++) { Graphics.SetRenderTarget(rt, 0, (CubemapFace)f); GL.Clear(true, true, c); }
            RenderTexture.active = before;
            running.shown.GenerateMips(); running.cube.GenerateMips();
            frozen = true;
            return "the cube painted " + c + " and held";
        }

        /// <summary>(Development build: the cube the visor holds, its six faces written out as pictures in a folder.)</summary>
        public static string Faces(string folder)
        {
            if (running == null || running.cube == null) return "no cube yet";
            System.IO.Directory.CreateDirectory(folder);
            var read = new Texture2D(Size, Size, TextureFormat.RGBAHalf, false);
            var shown = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            RenderTexture before = RenderTexture.active;
            RenderTexture which = running.shown != null ? running.shown : running.cube;
            for (int f = 0; f < 6; f++)
            {
                Graphics.SetRenderTarget(which, 0, (CubemapFace)f);
                read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                read.Apply(false);
                Color[] px = read.GetPixels();
                for (int i = 0; i < px.Length; i++) { Color p = px[i] * 0.3f; px[i] = new Color(Mathf.Clamp01(p.r), Mathf.Clamp01(p.g), Mathf.Clamp01(p.b), 1f); }
                shown.SetPixels(px); shown.Apply(false);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "face" + f + ".png"), shown.EncodeToPNG());
            }
            RenderTexture.active = before;
            Destroy(read); Destroy(shown);
            return "six faces written to " + folder;
        }
#endif
    }
}
