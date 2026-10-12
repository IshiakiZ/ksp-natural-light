#if DEV
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NaturalLight
{
    /// <summary>For looking into the editors' scenes, in the development build only (called through the bridge's dev_call).</summary>
    public static class EditorAids
    {
        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 6; i++, t = t.parent) parts.Insert(0, t.name);
            return string.Join("/", parts.ToArray());
        }

        /// <summary>The lights of the scene, and everything in it that looks like one of the ground crew.</summary>
        public static string Crew()
        {
            var say = new StringBuilder();
            foreach (Light l in Object.FindObjectsOfType<Light>())
                say.Append("LIGHT ").Append(Path(l.transform)).Append(": ").Append(l.type).Append(l.enabled && l.gameObject.activeInHierarchy ? "" : " (off)").Append(" intensity ").Append(l.intensity.ToString("F2"))
                   .Append(" colour ").Append(l.color.ToString("F2")).Append(" shadows ").Append(l.shadows).Append(" strength ").Append(l.shadowStrength.ToString("F2")).Append(" mask ").Append(System.Convert.ToString(l.cullingMask, 2))
                   .Append(" mode ").Append(l.renderMode).Append(" baked ").Append(l.bakingOutput.lightmapBakeType).Append("\n");
            var seen = new Dictionary<string, int>();
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
            {
                string path = Path(r.transform), low = path.ToLowerInvariant();
                if (!(low.Contains("kerbal") || low.Contains("crew") || low.Contains("worker") || low.Contains("truck") || low.Contains("vehicle") || r is SkinnedMeshRenderer)) continue;
                Material m = r.sharedMaterial;
                string line = r.GetType().Name + " layer " + r.gameObject.layer + " (" + LayerMask.LayerToName(r.gameObject.layer) + ") shader " + (m != null && m.shader != null ? m.shader.name : "none") + " queue " + (m != null ? m.renderQueue : 0)
                            + " receives " + r.receiveShadows + " casts " + r.shadowCastingMode + " lightmap " + r.lightmapIndex + " probes " + r.lightProbeUsage + (r.enabled && r.gameObject.activeInHierarchy ? "" : " (off)");
                string key = line + " | e.g. " + path;
                string kind = line;
                if (seen.ContainsKey(kind)) { seen[kind]++; continue; }
                seen[kind] = 1;
                say.Append("RENDERER ").Append(key).Append("\n");
            }
            foreach (KeyValuePair<string, int> k in seen) if (k.Value > 1) say.Append("  x").Append(k.Value).Append(": ").Append(k.Key.Substring(0, Mathf.Min(k.Key.Length, 90))).Append("\n");
            say.Append("ambient ").Append(RenderSettings.ambientMode).Append(" ").Append(RenderSettings.ambientLight.ToString("F2")).Append("; shadows ").Append(QualitySettings.shadows).Append(" distance ").Append(QualitySettings.shadowDistance.ToString("F0"))
               .Append(" cascades ").Append(QualitySettings.shadowCascades).Append("; lightmaps ").Append(LightmapSettings.lightmaps.Length).Append("; probes ").Append(LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0);
            return say.ToString();
        }

        /// <summary>The ground crew's light, tried in the development build's own copy: on, and off again.</summary>
        public static string CrewOn()
        {
            if (Object.FindObjectOfType<GroundCrew>() != null) return "there already";
            new GameObject("Natural Light ground crew (tried)").AddComponent<GroundCrew>();
            return "on";
        }

        public static string CrewOff()
        {
            GroundCrew it = Object.FindObjectOfType<GroundCrew>();
            if (it == null) return "not there";
            Object.Destroy(it.gameObject);
            return "off";
        }

        /// <summary>Every shader the game has loaded, by name.</summary>
        public static string Shaders()
        {
            var names = new List<string>();
            foreach (Shader sh in Resources.FindObjectsOfTypeAll<Shader>()) if (!names.Contains(sh.name)) names.Add(sh.name);
            names.Sort(System.StringComparer.Ordinal);
            return string.Join("\n", names.ToArray());
        }

        /// <summary>The editor's camera brought down near the floor on the side the rocket's shadow falls across: for a picture of the crew in it.</summary>
        public static string Near()
        {
            VABCamera cam = Object.FindObjectOfType<VABCamera>();
            if (cam == null) return "no camera of the assembly building's here";
            cam.camPitch = near.x; cam.camHdg = near.y;
            cam.PlaceCamera(new Vector3(0f, near.z, 0f), near.w);
            return "pitch " + cam.camPitch.ToString("F2") + " heading " + cam.camHdg.ToString("F2") + " height " + near.z.ToString("F1") + " distance " + near.w.ToString("F0");
        }
        /// <summary>(pitch and heading in radians, the height looked at, the distance: set through dev_set-like calls below)</summary>
        static Vector4 near = new Vector4(0.42f, 3.6f, 2.5f, 21f);
        public static string NearA() { near = new Vector4(0.50f, 2.2f, 1.5f, 13f); return Near(); }
        public static string NearB() { near = new Vector4(0.50f, 2.2f, 2.0f, 24f); return Near(); }
        public static string NearC() { near = new Vector4(0.55f, 2.5f, 1.5f, 14f); return Near(); }
        public static string NearD() { near = new Vector4(0.45f, 1.9f, 1.5f, 12f); return Near(); }

        /// <summary>The cameras of the scene, what is under the rocket, and the lightmaps.</summary>
        public static string Floor()
        {
            var say = new StringBuilder();
            foreach (Camera c in Camera.allCameras)
                say.Append("CAMERA ").Append(c.name).Append(" depth ").Append(c.depth).Append(" mask ").Append(System.Convert.ToString(c.cullingMask, 2)).Append(" clear ").Append(c.clearFlags).Append(" near ").Append(c.nearClipPlane.ToString("F2")).Append(" far ").Append(c.farClipPlane.ToString("F0"))
                   .Append(" path ").Append(c.actualRenderingPath).Append(c.allowHDR ? " hdr" : "").Append(" at ").Append(c.transform.position.ToString("F1")).Append("\n");
            Vector3 from = EditorLogic.RootPart != null ? EditorLogic.RootPart.transform.position : Vector3.up * 10f;
            say.Append("the rocket's root at ").Append(from.ToString("F2")).Append("\n");
            foreach (RaycastHit hit in Physics.RaycastAll(from + Vector3.up * 2f, Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore))
            {
                Renderer r = hit.collider.GetComponentInParent<Renderer>() ?? hit.collider.GetComponentInChildren<Renderer>();
                say.Append("BELOW at ").Append(hit.point.y.ToString("F2")).Append(": ").Append(Path(hit.collider.transform)).Append(" (").Append(hit.collider.GetType().Name).Append(", layer ").Append(hit.collider.gameObject.layer).Append(")")
                   .Append(r != null ? " drawn by " + r.name + " layer " + r.gameObject.layer + " shader " + (r.sharedMaterial != null ? r.sharedMaterial.shader.name : "none") + " lightmap " + r.lightmapIndex + " scale " + r.lightmapScaleOffset.ToString("F3") + " receives " + r.receiveShadows : "").Append(" lightmap uv ").Append(hit.lightmapCoord.ToString("F3")).Append("\n");
            }
            // (the big flat things of the building, by layer: where the floor is likely to be)
            var layers = new Dictionary<int, int>();
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
            {
                if (!Path(r.transform).Contains("VAB") && !Path(r.transform).Contains("SPH")) continue;
                layers[r.gameObject.layer] = (layers.ContainsKey(r.gameObject.layer) ? layers[r.gameObject.layer] : 0) + 1;
                if (r.bounds.size.x > 30f && r.bounds.size.z > 30f && r.bounds.size.y < 6f)
                    say.Append("WIDE ").Append(Path(r.transform)).Append(" layer ").Append(r.gameObject.layer).Append(" shader ").Append(r.sharedMaterial != null ? r.sharedMaterial.shader.name : "none").Append(" lightmap ").Append(r.lightmapIndex).Append(" bounds ").Append(r.bounds.center.ToString("F1")).Append(" ").Append(r.bounds.size.ToString("F1")).Append(" receives ").Append(r.receiveShadows).Append("\n");
            }
            foreach (KeyValuePair<int, int> k in layers) say.Append("building renderers on layer ").Append(k.Key).Append(" (").Append(LayerMask.LayerToName(k.Key)).Append("): ").Append(k.Value).Append("\n");
            LightmapData[] maps = LightmapSettings.lightmaps;
            for (int i = 0; i < maps.Length; i++)
                say.Append("LIGHTMAP ").Append(i).Append(": ").Append(maps[i].lightmapColor != null ? maps[i].lightmapColor.width + "x" + maps[i].lightmapColor.height + " " + maps[i].lightmapColor.format + (maps[i].lightmapColor.isReadable ? " readable" : "") : "none")
                   .Append(maps[i].lightmapDir != null ? " +dir" : "").Append(maps[i].shadowMask != null ? " +shadowmask" : "").Append("\n");
            say.Append("lightmaps mode ").Append(LightmapSettings.lightmapsMode);
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
            {
                if (r.name != "ShadowPlane" || r.sharedMaterial == null) continue;
                Shader sh = r.sharedMaterial.shader;
                say.Append("\nSHADOW PLANE material ").Append(r.sharedMaterial.name).Append(" queue ").Append(r.sharedMaterial.renderQueue).Append(" at ").Append(r.transform.position.ToString("F2")).Append(":");
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    string n = sh.GetPropertyName(i);
                    var t = sh.GetPropertyType(i);
                    say.Append(" ").Append(n).Append("=").Append(t == UnityEngine.Rendering.ShaderPropertyType.Color ? r.sharedMaterial.GetColor(n).ToString("F2") : t == UnityEngine.Rendering.ShaderPropertyType.Texture ? "(texture)" : t == UnityEngine.Rendering.ShaderPropertyType.Vector ? r.sharedMaterial.GetVector(n).ToString("F2") : r.sharedMaterial.GetFloat(n).ToString("F2"));
                }
            }
            foreach (Light l in Object.FindObjectsOfType<Light>())
                if (l.name == "Shadow Light") say.Append("\nSHADOW LIGHT shines along ").Append(l.transform.forward.ToString("F3")).Append(" bias ").Append(l.shadowBias.ToString("F3")).Append(" resolution ").Append(l.shadowResolution);
            int lit = 0, all = 0;
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>()) if (r.gameObject.layer == 15) { all++; if (r.lightmapIndex >= 0 && r.lightmapIndex < 65000) lit++; }
            say.Append("\nlayer 15 renderers: ").Append(all).Append(", of them lightmapped: ").Append(lit);
            foreach (Part p in EditorLogic.fetch.ship.parts) { Renderer r = p.GetComponentInChildren<Renderer>(); if (r != null) { say.Append("\na part's renderer: layer ").Append(r.gameObject.layer).Append(" casts ").Append(r.shadowCastingMode).Append(" shader ").Append(r.sharedMaterial.shader.name); break; } }
            return say.ToString();
        }
    }
}
#endif
