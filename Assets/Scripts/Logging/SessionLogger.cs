using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using ScamSim.Core;

namespace ScamSim.Logging
{
    public class TurnLog
    {
        [JsonProperty("turn_index")] public int TurnIndex;
        [JsonProperty("timestamp")] public string Timestamp;
        [JsonProperty("source")] public string Source;
        /// <summary>Why the model's answer was not used for this line (e.g. "refusal:cyber", "invalid_output:player_options"), or null.</summary>
        [JsonProperty("provider_error")] public string ProviderError;
        [JsonProperty("provider_attempts")] public int ProviderAttempts = 1;
        /// <summary>False when the player hung up before this line appeared; its red flags are not counted.</summary>
        [JsonProperty("line_shown")] public bool LineShown = true;
        [JsonProperty("scammer_line")] public string ScammerLine;
        [JsonProperty("pressure_level")] public int PressureLevel;
        [JsonProperty("red_flags")] public List<string> RedFlags = new List<string>();
        [JsonProperty("player_options")] public List<string> PlayerOptions = new List<string>();
        /// <summary>
        /// Option index in each on-screen slot, top to bottom: [2,0,1] means the resist option was shown first.
        /// Null when the line had no options (the call ended on it).
        /// </summary>
        [JsonProperty("option_display_order")] public List<int> OptionDisplayOrder;
        /// <summary>0/1/2 for an option, "free" for typed text, "hang_up", or null when the call ended on this line.</summary>
        [JsonProperty("player_choice")] public object PlayerChoice;
        [JsonProperty("player_text")] public string PlayerText;
        [JsonProperty("player_stance")] public string PlayerStance;
        [JsonProperty("player_stance_source")] public string PlayerStanceSource;
        [JsonProperty("pii_redacted")] public bool PiiRedacted;
        [JsonProperty("response_time_ms")] public long? ResponseTimeMs;
        /// <summary>Everything lost so far, including anything given in the reply to this line.</summary>
        [JsonProperty("goal_progress")] public GoalProgress GoalProgress = new GoalProgress();
        /// <summary>True when the reply to this line was a refusal, a question or a hang up.</summary>
        [JsonProperty("red_flags_noticed")] public bool? RedFlagsNoticed;
    }

    public class SessionLog
    {
        [JsonProperty("session_id")] public string SessionId;
        [JsonProperty("scenario_id")] public string ScenarioId;
        [JsonProperty("app_version")] public string AppVersion;
        [JsonProperty("provider")] public string Provider;
        [JsonProperty("model_id")] public string ModelId;
        [JsonProperty("prompt_version")] public string PromptVersion;
        /// <summary>Always null: API version 2026-10-21 no longer accepts temperature. See effort.</summary>
        [JsonProperty("temperature")] public double? Temperature;
        [JsonProperty("effort")] public string Effort;
        [JsonProperty("option_shuffle_seed")] public int OptionShuffleSeed;
        [JsonProperty("start_time")] public string StartTime;
        [JsonProperty("end_time")] public string EndTime;
        [JsonProperty("end_reason")] public string EndReason;
        [JsonProperty("ended_by")] public string EndedBy;
        [JsonProperty("total_turns")] public int TotalTurns;
        [JsonProperty("red_flags_total")] public int RedFlagsTotal;
        [JsonProperty("red_flags_player_reacted_to")] public int RedFlagsPlayerReactedTo;
        [JsonProperty("lessons")] public List<string> Lessons = new List<string>();
        [JsonProperty("turns")] public List<TurnLog> Turns = new List<TurnLog>();
    }

    public class SessionMeta
    {
        public string SessionId;
        public string ScenarioId;
        public string AppVersion;
        public string Provider;
        public string ModelId;
        public string PromptVersion;
        public double? Temperature;
        public string Effort;
        public int OptionShuffleSeed;
        public DateTime StartUtc;
    }

    /// <summary>
    /// One session = one JSON file (PRD 2.4). The log is rebuilt from the conversation state
    /// and rewritten after every turn, so a crash never loses more than the turn in progress.
    /// </summary>
    public class SessionLogger
    {
        public SessionMeta Meta { get; private set; }
        public string FilePath { get; private set; }
        public SessionLog Latest { get; private set; }

        const int MaxIdCharsInFileName = 40;

        /// <summary>
        /// Raised after a log file was written. The WebGL build uses it to flush the browser's file
        /// system to IndexedDB, so the log survives a reload.
        /// </summary>
        public static event Action<string> FileWritten;

        readonly string _directory;

        public SessionLogger(SessionMeta meta, string directory)
        {
            Meta = meta;
            _directory = directory;
            if (!string.IsNullOrEmpty(directory)) FilePath = Path.Combine(directory, FileName(meta));
        }

        /// <summary>
        /// session_{local start time}_{session id}.json. The id keeps two sessions that start in the
        /// same second from writing to the same file.
        /// </summary>
        public static string FileName(SessionMeta meta)
        {
            var name = new StringBuilder("session_");
            name.Append(meta.StartUtc.ToLocalTime().ToString("yyyyMMdd_HHmmss"));

            string id = meta.SessionId ?? "";
            bool first = true;
            for (int i = 0; i < id.Length && i < MaxIdCharsInFileName; i++)
            {
                char c = id[i];
                bool safe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!safe) continue;
                if (first) name.Append('_');
                first = false;
                name.Append(c);
            }

            name.Append(".json");
            return name.ToString();
        }

        public static SessionLog Build(SessionMeta meta, ConversationManager manager, DateTime? endUtc, List<string> lessons)
        {
            var log = new SessionLog
            {
                SessionId = meta.SessionId,
                ScenarioId = meta.ScenarioId,
                AppVersion = meta.AppVersion,
                Provider = meta.Provider,
                ModelId = meta.ModelId,
                PromptVersion = meta.PromptVersion,
                Temperature = meta.Temperature,
                Effort = meta.Effort,
                OptionShuffleSeed = meta.OptionShuffleSeed,
                StartTime = meta.StartUtc.ToString("o"),
                EndTime = endUtc.HasValue ? endUtc.Value.ToString("o") : null,
                EndReason = manager.IsOver ? manager.EndReason : null,
                EndedBy = manager.IsOver ? manager.EndedByWhat : null,
                TotalTurns = manager.TurnCount
            };
            if (lessons != null) log.Lessons.AddRange(lessons);

            for (int i = 0; i < manager.State.Exchanges.Count; i++)
            {
                Exchange exchange = manager.State.Exchanges[i];
                ScammerTurn scammer = exchange.Scammer;
                var turn = new TurnLog
                {
                    TurnIndex = i,
                    Timestamp = exchange.TimestampUtc,
                    Source = exchange.Source,
                    ProviderError = exchange.ProviderError,
                    ProviderAttempts = exchange.ProviderAttempts,
                    LineShown = exchange.LineShown,
                    ScammerLine = scammer.ScammerLine ?? "",
                    PressureLevel = scammer.PressureLevel,
                    RedFlags = scammer.RedFlags != null ? new List<string>(scammer.RedFlags) : new List<string>(),
                    PlayerOptions = scammer.PlayerOptions != null ? new List<string>(scammer.PlayerOptions) : new List<string>(),
                    GoalProgress = scammer.GoalProgress != null ? scammer.GoalProgress.Clone() : new GoalProgress(),
                    OptionDisplayOrder = exchange.OptionOrder != null ? new List<int>(exchange.OptionOrder) : null
                };

                PlayerReply reply = exchange.Reply;
                if (reply != null)
                {
                    if (reply.HungUp) turn.PlayerChoice = "hang_up";
                    else if (reply.OptionIndex >= 0) turn.PlayerChoice = reply.OptionIndex;
                    else turn.PlayerChoice = "free";

                    turn.PlayerText = reply.Text;
                    turn.PlayerStance = StanceNames.ToName(reply.Stance);
                    turn.PlayerStanceSource = reply.StanceSource;
                    turn.PiiRedacted = reply.PiiRedacted;
                    turn.ResponseTimeMs = reply.ResponseTimeMs;
                    turn.RedFlagsNoticed = reply.HungUp || reply.Stance == Stance.Resist;
                    if (turn.RedFlagsNoticed == true) log.RedFlagsPlayerReactedTo += turn.RedFlags.Count;
                }

                // A line the player never saw released no red flags to them.
                if (turn.LineShown) log.RedFlagsTotal += turn.RedFlags.Count;
                log.Turns.Add(turn);
            }

            return log;
        }

        public static string ToJson(SessionLog log)
        {
            return JsonConvert.SerializeObject(log, Formatting.Indented);
        }

        /// <summary>
        /// Rebuilds the log and writes it to disk. Never throws: logging must not crash a session.
        /// On failure <paramref name="error"/> is set and the last log that could be built is returned.
        /// </summary>
        public SessionLog Save(ConversationManager manager, DateTime? endUtc, List<string> lessons, out string error)
        {
            error = null;
            try
            {
                Latest = Build(Meta, manager, endUtc, lessons);
                if (string.IsNullOrEmpty(FilePath)) return Latest;

                // Write the new version next to the old one, then swap. A crash while writing leaves the
                // previous file whole; a crash during the swap leaves the complete new version in the .tmp file.
                string temp = FilePath + ".tmp";
                Directory.CreateDirectory(_directory);
                File.WriteAllText(temp, ToJson(Latest), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(temp, FilePath);
            }
            catch (Exception e)
            {
                error = e.Message;
                return Latest;
            }

            // Outside the try above: a listener failing does not mean the log failed to save.
            try
            {
                if (FileWritten != null) FileWritten(FilePath);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[ScamSim] After saving the log: " + e.Message);
            }
            return Latest;
        }
    }
}
