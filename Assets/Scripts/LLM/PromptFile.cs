using System;

namespace ScamSim.LLM
{
    /// <summary>
    /// A prompt kept as a text file (Assets/Prompts). The first line names its version, which goes
    /// into every session log as prompt_version, so a result can always be traced to the exact prompt:
    ///   prompt_version: police-1.0
    /// </summary>
    public class PromptFile
    {
        const string VersionKey = "prompt_version:";

        public string Version { get; private set; }
        public string Body { get; private set; }

        public static PromptFile Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("The prompt file is empty.");

            string normalized = text.Replace("\r\n", "\n");
            int newline = normalized.IndexOf('\n');
            string first = (newline < 0 ? normalized : normalized.Substring(0, newline)).Trim();
            if (!first.StartsWith(VersionKey, StringComparison.Ordinal))
                throw new ArgumentException("The first line of a prompt file must be '" + VersionKey + " <version>'.");

            string version = first.Substring(VersionKey.Length).Trim();
            if (version.Length == 0) throw new ArgumentException("The prompt file has an empty version.");

            string body = newline < 0 ? "" : normalized.Substring(newline + 1).Trim();
            if (body.Length == 0) throw new ArgumentException("The prompt file has no text after the version line.");
            return new PromptFile { Version = version, Body = body };
        }
    }
}
