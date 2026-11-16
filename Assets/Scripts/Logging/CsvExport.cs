using System;
using System.IO;

namespace ScamSim.Logging
{
    public enum CsvExportError
    {
        None,
        NoLog,
        NoFolder,
        WriteFailed
    }

    /// <summary>
    /// Saves a session as CSV next to its JSON log (same name, .csv). Desktop and Editor only:
    /// a WebGL page cannot hand the player a file this way, so the debrief offers a browser
    /// download there instead.
    /// </summary>
    public static class CsvExport
    {
        public static string PathFor(string jsonPath)
        {
            return string.IsNullOrEmpty(jsonPath) ? null : Path.ChangeExtension(jsonPath, ".csv");
        }

        /// <summary>
        /// Writes the CSV. Never throws: returns the kind of failure, with the system's own message
        /// in <paramref name="detail"/> for the developer log (it is not translated).
        /// </summary>
        public static CsvExportError TrySave(SessionLog log, string jsonPath, out string csvPath, out string detail)
        {
            csvPath = PathFor(jsonPath);
            detail = null;
            if (log == null) return CsvExportError.NoLog;
            if (csvPath == null) return CsvExportError.NoFolder;

            try
            {
                string directory = Path.GetDirectoryName(csvPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(csvPath, CsvExporter.ToBytes(log));
                return CsvExportError.None;
            }
            catch (Exception e)
            {
                detail = e.Message;
                return CsvExportError.WriteFailed;
            }
        }
    }
}
