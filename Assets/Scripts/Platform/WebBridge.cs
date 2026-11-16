using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ScamSim.Platform
{
    /// <summary>
    /// The things the WebGL build does through the browser (Assets/Plugins/WebGL/ScamSimWeb.jslib):
    /// keep session logs after a reload and download the CSV export. No-ops on other platforms.
    /// Not yet verified in a WebGL build (the first build ran out of memory); check with the first tested one.
    /// </summary>
    public static class WebBridge
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void ScamSim_SyncFs();
        [DllImport("__Internal")] static extern int ScamSim_Download(string name, string mime, string base64);
#endif

        public static bool IsWeb
        {
            get { return Application.platform == RuntimePlatform.WebGLPlayer; }
        }

        /// <summary>Flushes the browser's file system to IndexedDB after a log was written.</summary>
        public static void SyncFiles()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            ScamSim_SyncFs();
#endif
        }

        /// <summary>Offers bytes to the player as a downloaded file. True when the browser started the download.</summary>
        public static bool Download(string fileName, string mimeType, byte[] bytes)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return ScamSim_Download(fileName, mimeType, Convert.ToBase64String(bytes)) == 1;
#else
            return false;
#endif
        }
    }
}
