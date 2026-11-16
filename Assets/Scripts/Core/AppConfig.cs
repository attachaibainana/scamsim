using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace ScamSim.Core
{
    /// <summary>
    /// Settings read from config.json (gitignored; copy config.example.json). The file sits OUTSIDE
    /// Assets: at the project root in the Editor and next to the .exe in a desktop build, so the API key
    /// is never copied into a build (everything in StreamingAssets would be). A WebGL page would expose
    /// the key to anyone who opens it, so the web demo never reads a config and always runs offline.
    /// </summary>
    public class AppConfig
    {
        public const string FileName = "config.json";
        public const string DefaultModel = "claude-sonnet-5-5";
        public const string DefaultEffort = "low";
        public const int DefaultTimeoutSeconds = 20;
        public const int DefaultMaxTokens = 4096;

        static readonly string[] Efforts = { "low", "medium", "high", "xhigh", "max" };

        [JsonProperty("anthropic_api_key")] public string AnthropicApiKey;
        [JsonProperty("model")] public string Model = DefaultModel;
        [JsonProperty("effort")] public string Effort = DefaultEffort;
        [JsonProperty("timeout_seconds")] public int TimeoutSeconds = DefaultTimeoutSeconds;
        [JsonProperty("max_tokens")] public int MaxTokens = DefaultMaxTokens;

        /// <summary>Values that were replaced by defaults, for the developer log.</summary>
        [JsonIgnore] public readonly List<string> Warnings = new List<string>();

        [JsonIgnore]
        public bool HasApiKey
        {
            get { return !string.IsNullOrWhiteSpace(AnthropicApiKey); }
        }

        /// <summary>Project root in the Editor; the folder that holds the .exe in a desktop build.</summary>
        public static string FilePath
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..", FileName)); }
        }

        /// <summary>Parses config JSON and replaces missing or invalid values with defaults. Null on unreadable JSON.</summary>
        public static AppConfig Parse(string json, out string error)
        {
            error = null;
            AppConfig config;
            try
            {
                config = JsonConvert.DeserializeObject<AppConfig>(json);
            }
            catch (Exception e)
            {
                error = "config.json is not valid JSON: " + e.Message;
                return null;
            }
            if (config == null)
            {
                error = "config.json is empty";
                return null;
            }

            if (string.IsNullOrWhiteSpace(config.Model))
            {
                config.Warnings.Add("model is empty, using " + DefaultModel);
                config.Model = DefaultModel;
            }
            config.Model = config.Model.Trim();

            string effort = (config.Effort ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(Efforts, effort) < 0)
            {
                config.Warnings.Add("effort '" + config.Effort + "' is not one of low/medium/high/xhigh/max, using " + DefaultEffort);
                effort = DefaultEffort;
            }
            config.Effort = effort;

            if (config.TimeoutSeconds < 5 || config.TimeoutSeconds > 120)
            {
                config.Warnings.Add("timeout_seconds " + config.TimeoutSeconds + " is outside 5-120, using " + DefaultTimeoutSeconds);
                config.TimeoutSeconds = DefaultTimeoutSeconds;
            }
            if (config.MaxTokens < 512 || config.MaxTokens > 64000)
            {
                config.Warnings.Add("max_tokens " + config.MaxTokens + " is outside 512-64000, using " + DefaultMaxTokens);
                config.MaxTokens = DefaultMaxTokens;
            }
            if (config.AnthropicApiKey != null) config.AnthropicApiKey = config.AnthropicApiKey.Trim();
            return config;
        }

        /// <summary>
        /// Reads config.json. Returns defaults without a key (offline mode) on WebGL, when the file is
        /// missing, or when it cannot be read; error explains why for the developer log.
        /// </summary>
        public static AppConfig Load(out string error)
        {
            error = null;
            if (Application.platform == RuntimePlatform.WebGLPlayer) return new AppConfig();

            string path = FilePath;
            if (!File.Exists(path)) return new AppConfig();

            try
            {
                return Parse(File.ReadAllText(path), out error) ?? new AppConfig();
            }
            catch (Exception e)
            {
                error = "could not read config.json: " + e.Message;
                return new AppConfig();
            }
        }
    }
}
