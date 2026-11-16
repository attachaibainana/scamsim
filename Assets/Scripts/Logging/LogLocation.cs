using System;
using System.IO;
using UnityEngine;

namespace ScamSim.Logging
{
    /// <summary>
    /// Where session logs are written:
    /// Editor → &lt;project&gt;/Logs/Sessions (gitignored) ·
    /// Windows / Linux build → Logs next to the executable, or persistentDataPath/Logs when that folder
    /// cannot be written (for example under Program Files) ·
    /// WebGL and everything else → persistentDataPath/Logs.
    /// </summary>
    public static class LogLocation
    {
        public static string Resolve()
        {
            if (Application.isEditor)
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "Sessions"));

            RuntimePlatform platform = Application.platform;
            if (platform == RuntimePlatform.WindowsPlayer || platform == RuntimePlatform.LinuxPlayer)
            {
                // dataPath is <exe folder>/<name>_Data
                string besideExe = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
                if (IsWritable(besideExe)) return besideExe;
            }

            return Path.Combine(Application.persistentDataPath, "Logs");
        }

        public static bool IsWritable(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                string probe = Path.Combine(directory, ".write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
