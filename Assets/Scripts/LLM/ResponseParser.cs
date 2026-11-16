using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScamSim.Core;

namespace ScamSim.LLM
{
    public class ParseOutcome<T>
    {
        public T Value;
        /// <summary>Short machine-readable reason, logged as provider_error (e.g. "refusal:cyber", "invalid_output").</summary>
        public string Error;
        /// <summary>True when asking again may help (bad output, rate limit, server error).</summary>
        public bool Retryable;

        public bool Ok
        {
            get { return Error == null; }
        }
    }

    /// <summary>
    /// Turns a Messages API response into a ScammerTurn and checks the rules the JSON schema cannot
    /// express (PRD 2.2). Reads content blocks by type: a response can also hold thinking or fallback blocks.
    /// </summary>
    public static class ResponseParser
    {
        public static ParseOutcome<ScammerTurn> ParseTurn(HttpResult http)
        {
            var outcome = new ParseOutcome<ScammerTurn>();
            string text;
            if (!ReadText(http, outcome, out text)) return outcome;

            ScammerTurn turn;
            try
            {
                turn = JsonConvert.DeserializeObject<ScammerTurn>(text);
            }
            catch (JsonException)
            {
                return Fail(outcome, "invalid_json", true);
            }

            string problem = Validate(turn);
            if (problem != null) return Fail(outcome, "invalid_output:" + problem, true);

            turn.ScammerLine = turn.ScammerLine.Trim();
            if (!turn.CallShouldEnd) turn.EndReason = null;
            outcome.Value = turn;
            return outcome;
        }

        public static ParseOutcome<List<string>> ParseLessons(HttpResult http, int count)
        {
            var outcome = new ParseOutcome<List<string>>();
            string text;
            if (!ReadText(http, outcome, out text)) return outcome;

            var lessons = new List<string>();
            try
            {
                JToken array = JObject.Parse(text)["lessons"];
                if (array != null && array.Type == JTokenType.Array)
                {
                    foreach (JToken item in array)
                    {
                        string lesson = item.Type == JTokenType.String ? ((string)item).Trim() : "";
                        if (lesson.Length > 0) lessons.Add(lesson);
                    }
                }
            }
            catch (JsonException)
            {
                return Fail(outcome, "invalid_json", true);
            }

            if (lessons.Count < count) return Fail(outcome, "invalid_output:lessons", true);
            outcome.Value = lessons.GetRange(0, count);
            return outcome;
        }

        /// <summary>Null when the turn can be shown, otherwise what is wrong with it.</summary>
        public static string Validate(ScammerTurn turn)
        {
            if (turn == null) return "empty";
            if (string.IsNullOrWhiteSpace(turn.ScammerLine)) return "scammer_line";
            if (turn.PressureLevel < ConversationManager.MinPressure || turn.PressureLevel > ConversationManager.MaxPressure)
                return "pressure_level";
            if (turn.RedFlags == null) return "red_flags";
            if (turn.GoalProgress == null) return "goal_progress";
            if (turn.PlayerOptions == null) return "player_options";

            if (turn.CallShouldEnd)
            {
                if (turn.EndReason != EndReasons.Scammed && turn.EndReason != EndReasons.Escaped) return "end_reason";
                return null;
            }

            if (turn.PlayerOptions.Count != OptionOrder.StandardCount) return "player_options";
            foreach (string option in turn.PlayerOptions)
            {
                if (string.IsNullOrWhiteSpace(option)) return "player_options";
            }
            return null;
        }

        /// <summary>Checks the HTTP result and the stop reason, then joins the response's text blocks.</summary>
        static bool ReadText<T>(HttpResult http, ParseOutcome<T> outcome, out string text)
        {
            text = null;
            if (http.TimedOut)
            {
                Fail(outcome, "timeout", false);
                return false;
            }
            if (http.NetworkError != null && http.Status == 0)
            {
                Fail(outcome, "network", true);
                return false;
            }
            if (http.Status != 200)
            {
                // 429 rate limit, 5xx server errors and 529 overloaded can pass; a bad key or request will not.
                bool retry = http.Status == 408 || http.Status == 429 || http.Status >= 500;
                Fail(outcome, "http_" + http.Status, retry);
                return false;
            }

            JObject body;
            try
            {
                body = JObject.Parse(http.Body);
            }
            catch (JsonException)
            {
                Fail(outcome, "invalid_response", true);
                return false;
            }

            string stopReason = (string)body["stop_reason"];
            if (stopReason == "refusal")
            {
                // The server already tried its fallback model, so asking again will not help.
                string category = (string)body.SelectToken("stop_details.category") ?? "unknown";
                Fail(outcome, "refusal:" + category, false);
                return false;
            }
            if (stopReason == "max_tokens")
            {
                // The same request would run out of room again; raise max_tokens in config.json instead.
                Fail(outcome, "max_tokens", false);
                return false;
            }

            var sb = new StringBuilder();
            JToken content = body["content"];
            if (content != null && content.Type == JTokenType.Array)
            {
                foreach (JToken block in content)
                {
                    if ((string)block["type"] == "text") sb.Append((string)block["text"]);
                }
            }
            if (sb.Length == 0)
            {
                Fail(outcome, "no_text", true);
                return false;
            }

            text = sb.ToString();
            return true;
        }

        static ParseOutcome<T> Fail<T>(ParseOutcome<T> outcome, string error, bool retryable)
        {
            outcome.Error = error;
            outcome.Retryable = retryable;
            outcome.Value = default(T);
            return outcome;
        }
    }
}
