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

        public const string FallbackFileName = "scamsim_session.csv";

        /// <summary>The CSV's file name: the log's name with .csv, or a fixed name when there is no log file.</summary>
        public static string FileNameFor(string jsonPath)
        {
            string path = PathFor(jsonPath);
            return path != null ? Path.GetFileName(path) : FallbackFileName;
        }

        /// <summary>Builds the CSV bytes. Never throws; used by both the file export and the browser download.</summary>
        public static CsvExportError TryBuild(SessionLog log, out byte[] bytes, out string detail)
        {
            bytes = null;
            detail = null;
            if (log == null) return CsvExportError.NoLog;
            try
            {
                bytes = CsvExporter.ToBytes(log);
                return CsvExportError.None;
            }
            catch (Exception e)
            {
                detail = e.Message;
                return CsvExportError.WriteFailed;
            }
        }

        /// <summary>
        /// Writes the CSV. Never throws: returns the kind of failure, with the system's own message
        /// in <paramref name="detail"/> for the developer log (it is not translated).
        /// </summary>
        public static CsvExportError TrySave(SessionLog log, string jsonPath, out string csvPath, out string detail)
        {
            csvPath = PathFor(jsonPath);
            byte[] bytes;
            CsvExportError built = TryBuild(log, out bytes, out detail);
            if (built != CsvExportError.None) return built;
            if (csvPath == null) return CsvExportError.NoFolder;

            try
            {
                string directory = Path.GetDirectoryName(csvPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(csvPath, bytes);
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
