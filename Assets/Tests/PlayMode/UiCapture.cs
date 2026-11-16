using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamSim.Tests
{
    /// <summary>
    /// Renders a UI Toolkit panel into a texture and saves it as PNG, so screens can be checked
    /// from the command line without opening the Editor. Files go to SCAMSIM_SCREENSHOT_DIR,
    /// or to Logs/Screenshots in the project (gitignored).
    /// </summary>
    public static class UiCapture
    {
        public const int Width = 1920;
        public const int Height = 1080;

        public static string Directory
        {
            get
            {
                string fromEnv = Environment.GetEnvironmentVariable("SCAMSIM_SCREENSHOT_DIR");
                if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "Screenshots"));
            }
        }

        /// <summary>Points the document at an off-screen texture. Call before the frames you want to capture.</summary>
        public static RenderTexture Redirect(UIDocument document, int width = Width, int height = Height)
        {
            var texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            texture.Create();
            PanelSettings settings = UnityEngine.Object.Instantiate(document.panelSettings);
            settings.targetTexture = texture;
            settings.clearColor = true;
            settings.colorClearValue = Color.black;
            document.panelSettings = settings;
            return texture;
        }

        /// <summary>Waits a few frames for layout and repaint, then writes the texture to name.png.</summary>
        public static IEnumerator Save(RenderTexture texture, string name, Action<string> onSaved = null)
        {
            for (int i = 0; i < 4; i++) yield return null;

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            image.Apply();
            RenderTexture.active = previous;

            System.IO.Directory.CreateDirectory(Directory);
            string path = Path.Combine(Directory, name + ".png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
            Debug.Log("[UiCapture] saved " + path);
            if (onSaved != null) onSaved(path);
        }
    }
}
