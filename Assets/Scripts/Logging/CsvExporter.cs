using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ScamSim.Logging
{
    /// <summary>
    /// Flattens a session into one CSV row per turn (RFC 4180, CRLF line ends).
    /// A session with no turns (the player declined the call) still gets one row, with the turn
    /// columns left empty, so it is not lost from the data set.
    /// </summary>
    public static class CsvExporter
    {
        public static readonly string[] Columns =
        {
            "session_id", "scenario_id", "app_version", "provider", "model_id", "prompt_version", "temperature",
            "option_shuffle_seed",
            "start_time", "end_time", "end_reason", "ended_by", "total_turns",
            "red_flags_total", "red_flags_player_reacted_to",
            "turn_index", "timestamp", "source", "scammer_line", "pressure_level",
            "red_flags", "red_flag_count", "red_flags_noticed",
            "player_options", "player_choice", "player_stance", "player_stance_source", "player_text",
            "pii_redacted", "response_time_ms",
            "goal_id_number", "goal_transfer", "goal_link_click"
        };

        const string ListSeparator = " | ";

        public static string ToCsv(SessionLog log)
        {
            var sb = new StringBuilder();
            AppendRow(sb, Columns);

            if (log.Turns.Count == 0)
            {
                List<string> row = SessionFields(log);
                while (row.Count < Columns.Length) row.Add("");
                AppendRow(sb, row);
                return sb.ToString();
            }

            foreach (TurnLog turn in log.Turns)
            {
                List<string> row = SessionFields(log);
                row.Add(turn.TurnIndex.ToString(CultureInfo.InvariantCulture));
                row.Add(turn.Timestamp);
                row.Add(turn.Source);
                row.Add(turn.ScammerLine);
                row.Add(turn.PressureLevel.ToString(CultureInfo.InvariantCulture));
                row.Add(string.Join(ListSeparator, turn.RedFlags));
                row.Add(turn.RedFlags.Count.ToString(CultureInfo.InvariantCulture));
                row.Add(Bool(turn.RedFlagsNoticed));
                row.Add(string.Join(ListSeparator, turn.PlayerOptions));
                row.Add(turn.PlayerChoice == null ? "" : System.Convert.ToString(turn.PlayerChoice, CultureInfo.InvariantCulture));
                row.Add(turn.PlayerStance);
                row.Add(turn.PlayerStanceSource);
                row.Add(turn.PlayerText);
                row.Add(Bool(turn.PiiRedacted));
                row.Add(turn.ResponseTimeMs.HasValue ? turn.ResponseTimeMs.Value.ToString(CultureInfo.InvariantCulture) : "");
                row.Add(Bool(turn.GoalProgress.IdNumber));
                row.Add(Bool(turn.GoalProgress.Transfer));
                row.Add(Bool(turn.GoalProgress.LinkClick));
                AppendRow(sb, row);
            }

            return sb.ToString();
        }

        /// <summary>UTF-8 with BOM, so Excel opens Thai text correctly on double-click.</summary>
        public static byte[] ToBytes(SessionLog log)
        {
            byte[] body = Encoding.UTF8.GetBytes(ToCsv(log));
            byte[] bom = Encoding.UTF8.GetPreamble();
            var bytes = new byte[bom.Length + body.Length];
            bom.CopyTo(bytes, 0);
            body.CopyTo(bytes, bom.Length);
            return bytes;
        }

        /// <summary>
        /// Quotes a field for CSV. A field that starts with = + - @ (or tab / CR) gets a leading
        /// apostrophe, because spreadsheet apps would otherwise run typed player text as a formula.
        /// Analysts reading the CSV with code should strip that one apostrophe.
        /// </summary>
        public static string Escape(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (StartsFormula(field[0])) field = "'" + field;

            bool mustQuote = field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
                             || field.StartsWith(" ") || field.EndsWith(" ");
            if (!mustQuote) return field;
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        static bool StartsFormula(char c)
        {
            return c == '=' || c == '+' || c == '-' || c == '@' || c == '\t' || c == '\r';
        }

        /// <summary>The session columns, in the order of <see cref="Columns"/>. Repeated on every row.</summary>
        static List<string> SessionFields(SessionLog log)
        {
            var row = new List<string>(Columns.Length)
            {
                log.SessionId,
                log.ScenarioId,
                log.AppVersion,
                log.Provider,
                log.ModelId,
                log.PromptVersion,
                log.Temperature.HasValue ? log.Temperature.Value.ToString(CultureInfo.InvariantCulture) : "",
                log.OptionShuffleSeed.ToString(CultureInfo.InvariantCulture),
                log.StartTime,
                log.EndTime,
                log.EndReason,
                log.EndedBy,
                log.TotalTurns.ToString(CultureInfo.InvariantCulture),
                log.RedFlagsTotal.ToString(CultureInfo.InvariantCulture),
                log.RedFlagsPlayerReactedTo.ToString(CultureInfo.InvariantCulture)
            };
            return row;
        }

        static string Bool(bool? value)
        {
            if (!value.HasValue) return "";
            return value.Value ? "true" : "false";
        }

        static void AppendRow(StringBuilder sb, IList<string> fields)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Escape(fields[i]));
            }
            sb.Append("\r\n");
        }
    }
}
