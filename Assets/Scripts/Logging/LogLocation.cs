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

        /// <summary>
        /// Deletes all but the newest <paramref name="keep"/> session logs in a folder. Only for the web
        /// build: there the logs sit in browser storage that nobody can reach later (the player exports
        /// the current call as CSV), and every file is loaded into memory on each page load. Desktop
        /// logs are research data and are never pruned. Returns how many files were deleted.
        /// </summary>
        public static int PruneOldSessions(string directory, int keep)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;
            try
            {
                var files = new System.Collections.Generic.List<FileInfo>(new DirectoryInfo(directory).GetFiles("session_*.json"));
                files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                int deleted = 0;
                for (int i = Math.Max(keep, 0); i < files.Count; i++)
                {
                    files[i].Delete();
                    deleted++;
                }
                return deleted;
            }
            catch (Exception)
            {
                return 0;
            }
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
