using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace NaturalLight
{
    /// <summary>
    /// The mod's own shaders: small programs for the graphics card, packed without the Unity editor (see
    /// tools/shaderpack) for the game's OpenGL, which is what it runs on a Mac and on Linux. For Direct3D, which
    /// it runs on Windows, they are compiled into one bundle (light-windows.bundle, made by
    /// tools/unityshaders/windows.py on Windows, or by the Unity editor): where such a build is in the mod's folder
    /// it is used, and where there is none what the shaders do is not done.
    /// </summary>
    static class OwnShader
    {
        public static string Folder => KSPUtil.ApplicationRootPath + "GameData/NaturalLight/PluginData/";

        /// <summary>The shader of this name: from this file of the mod's on OpenGL, from the editor's build elsewhere; or none, and why not.</summary>
        public static Shader Load(string file, string name, string what, out string why)
        {
            why = "";
            try
            {
                bool openGL = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore;
                string built = "light-" + (Application.platform == RuntimePlatform.WindowsPlayer ? "windows" : Application.platform == RuntimePlatform.LinuxPlayer ? "linux" : "mac") + ".bundle";
                string path = Folder + (openGL ? file : built);
                if (!File.Exists(path))
                {
                    why = openGL ? "the shader for " + what + " (GameData/NaturalLight/PluginData/" + file + ") is missing" : "there is no shader for " + what + " on " + SystemInfo.graphicsDeviceType + " yet";
                    return null;
                }
                AssetBundle bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null) { why = "the shader for " + what + " did not load"; return null; }
                Shader[] found = bundle.LoadAllAssets<Shader>();
                bundle.Unload(false);
                foreach (Shader one in found) if (one != null && one.name == name && one.isSupported) return one;
                why = "this graphics card cannot run the shader for " + what;
            }
            catch (Exception ex) { why = "the shader for " + what + " did not load (" + ex.Message + ")"; }
            return null;
        }

        /// <summary>One triangle that covers the whole screen, in the screen's own coordinates (-1 to 1 each way).</summary>
        public static Mesh Sheet()
        {
            var sheet = new Mesh { name = "Natural Light sheet" };
            sheet.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(3f, -1f, 0f), new Vector3(-1f, 3f, 0f) };
            sheet.triangles = new[] { 0, 1, 2 };
            return sheet;
        }
    }
}
