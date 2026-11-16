using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScamSim.Core;
using ScamSim.Logging;

namespace ScamSim.LLM
{
    public class AnthropicSettings
    {
        public string ApiKey;
        public string Model = AppConfig.DefaultModel;
        public string Effort = AppConfig.DefaultEffort;
        public int TimeoutSeconds = AppConfig.DefaultTimeoutSeconds;
        public int MaxTokens = AppConfig.DefaultMaxTokens;

        public static AnthropicSettings From(AppConfig config)
        {
            return new AnthropicSettings
            {
                ApiKey = config.AnthropicApiKey,
                Model = config.Model,
                Effort = config.Effort,
                TimeoutSeconds = config.TimeoutSeconds,
                MaxTokens = config.MaxTokens
            };
        }
    }

    /// <summary>
    /// Builds Messages API requests as plain JSON (raw HTTP, because the C# SDK cannot run on WebGL).
    /// The conversation is rebuilt from the ConversationState every turn; every earlier message is
    /// recomputed exactly as it was first sent, so the prefix stays identical and can be cached.
    /// </summary>
    public static class AnthropicRequestBuilder
    {
        public const string Url = "https://api.anthropic.com/v1/messages";
        public const string ApiVersion = "2026-10-21";
        /// <summary>Server-side refusal fallback: a declined request is re-run on Anthropic's recommended model.</summary>
        public const string FallbackBeta = "server-side-fallback-2026-07-01";

        public static Dictionary<string, string> Headers(AnthropicSettings settings)
        {
            return new Dictionary<string, string>
            {
                { "content-type", "application/json" },
                { "x-api-key", settings.ApiKey ?? "" },
                { "anthropic-version", ApiVersion },
                { "anthropic-beta", FallbackBeta }
            };
        }

        // ---- Scammer turn ----------------------------------------------------------------------

        public static JObject TurnBody(AnthropicSettings settings, string systemPrompt, ProviderRequest request)
        {
            return Body(settings, settings.MaxTokens, systemPrompt, TurnMessages(request), TurnSchema());
        }

        /// <summary>
        /// user (call starts) → assistant (scammer turn JSON) → user (player reply + context) → ...
        /// The last message is always a user message carrying the target pressure for the next line.
        /// </summary>
        public static JArray TurnMessages(ProviderRequest request)
        {
            var messages = new JArray();
            List<Exchange> exchanges = request.State.Exchanges;

            messages.Add(Message("user", "สายเริ่มแล้ว ผู้เล่นกดรับสาย พูดประโยคเปิดสาย\n"
                                         + Context(1, ConversationManager.NextPressure(ConversationManager.StartPressure, null),
                                             false, new GoalProgress())));

            for (int i = 0; i < exchanges.Count; i++)
            {
                Exchange exchange = exchanges[i];
                messages.Add(Message("assistant", TurnJson(exchange.Scammer)));

                PlayerReply reply = exchange.Reply;
                if (reply == null || reply.HungUp) break;

                int nextTurn = i + 2;
                bool isLast = i == exchanges.Count - 1;
                int target = isLast
                    ? request.TargetPressure
                    : ConversationManager.NextPressure(exchange.Scammer.PressureLevel, reply.Stance);
                bool mustEnd = isLast ? request.MustEnd : nextTurn >= ConversationManager.MaxTurns;
                messages.Add(Message("user", ReplyText(reply) + "\n" + Context(nextTurn, target, mustEnd, exchange.Scammer.GoalProgress)));
            }
            return messages;
        }

        static string ReplyText(PlayerReply reply)
        {
            string how = reply.OptionIndex >= 0 ? "เลือกตัวเลือก" : "พิมพ์เอง";
            return "ผู้เล่นตอบ (" + how + ", stance: " + StanceNames.ToName(reply.Stance) + "): \"" + reply.Text + "\"";
        }

        static string Context(int turn, int targetPressure, bool mustEnd, GoalProgress goals)
        {
            goals = goals ?? new GoalProgress();
            var sb = new StringBuilder();
            sb.Append("turn: ").Append(turn.ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(ConversationManager.MaxTurns.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("target_pressure: ").Append(targetPressure.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("must_end: ").Append(mustEnd ? "true" : "false").Append('\n');
            sb.Append("goals_so_far: ").Append(JsonConvert.SerializeObject(goals, Formatting.None));
            return sb.ToString();
        }

        /// <summary>The scammer's earlier line exactly as the schema describes it.</summary>
        public static string TurnJson(ScammerTurn turn)
        {
            return JsonConvert.SerializeObject(turn, Formatting.None);
        }

        public static JObject TurnSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["scammer_line"] = new JObject { ["type"] = "string" },
                    ["pressure_level"] = new JObject { ["type"] = "integer", ["enum"] = new JArray(1, 2, 3, 4, 5) },
                    ["red_flags_in_this_line"] = StringArray(),
                    ["player_options"] = StringArray(),
                    ["goal_progress"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["id_number"] = new JObject { ["type"] = "boolean" },
                            ["transfer"] = new JObject { ["type"] = "boolean" },
                            ["link_click"] = new JObject { ["type"] = "boolean" }
                        },
                        ["required"] = new JArray("id_number", "transfer", "link_click"),
                        ["additionalProperties"] = false
                    },
                    ["call_should_end"] = new JObject { ["type"] = "boolean" },
                    // "hung_up" is left out on purpose: only the game sets it, when the player hangs up.
                    ["end_reason"] = new JObject
                    {
                        ["anyOf"] = new JArray(
                            new JObject { ["type"] = "string", ["enum"] = new JArray(EndReasons.Scammed, EndReasons.Escaped) },
                            new JObject { ["type"] = "null" })
                    }
                },
                ["required"] = new JArray("scammer_line", "pressure_level", "red_flags_in_this_line", "player_options",
                    "goal_progress", "call_should_end", "end_reason"),
                ["additionalProperties"] = false
            };
        }

        // ---- Lessons ---------------------------------------------------------------------------

        public static JObject LessonsBody(AnthropicSettings settings, string systemPrompt, SessionLog log)
        {
            var messages = new JArray { Message("user", LessonsInput(log)) };
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["lessons"] = StringArray() },
                ["required"] = new JArray("lessons"),
                ["additionalProperties"] = false
            };
            return Body(settings, settings.MaxTokens, systemPrompt, messages, schema);
        }

        /// <summary>
        /// The call as the lesson writer sees it, built from the same DebriefSummary the debrief screen
        /// shows, so the lessons and the screen judge the call the same way.
        /// </summary>
        public static string LessonsInput(SessionLog log)
        {
            DebriefSummary summary = DebriefSummary.From(log);
            var turns = new JArray();
            foreach (DebriefItem item in summary.Items)
            {
                var flags = new JArray();
                foreach (DebriefFlag flag in item.Flags)
                    flags.Add(new JObject { ["flag"] = flag.Text, ["status"] = FlagStatusName(flag.Status) });

                turns.Add(new JObject
                {
                    ["scammer_line"] = item.FullScammerLine,
                    ["red_flags"] = flags,
                    ["player_reply"] = item.PlayerText,
                    ["player_hung_up"] = item.PlayerHungUp
                });
            }

            var input = new JObject
            {
                ["end_reason"] = summary.EndReason,
                ["lost"] = new JArray(summary.Lost.ToArray()),
                ["flags_noticed"] = summary.FlagsNoticed,
                ["flags_answerable"] = summary.FlagsAnswerable,
                ["turns"] = turns
            };
            return input.ToString(Formatting.None);
        }

        static string FlagStatusName(FlagStatus status)
        {
            switch (status)
            {
                case FlagStatus.Noticed: return "noticed";
                case FlagStatus.Missed: return "missed";
                default: return "call_ended";
            }
        }

        // ---- Shared ----------------------------------------------------------------------------

        static JObject Body(AnthropicSettings settings, int maxTokens, string systemPrompt, JArray messages, JObject schema)
        {
            return new JObject
            {
                ["model"] = settings.Model,
                ["max_tokens"] = maxTokens,
                ["system"] = systemPrompt,
                ["messages"] = messages,
                // Thinking stays at its adaptive default; effort keeps it short for a live phone call.
                // No temperature: API version 2026-10-21 rejects sampling parameters.
                ["output_config"] = new JObject
                {
                    ["effort"] = settings.Effort,
                    ["format"] = new JObject { ["type"] = "json_schema", ["schema"] = schema }
                },
                ["fallbacks"] = "default",
                // Automatic prompt caching: the system prompt and earlier turns never change.
                ["cache_control"] = new JObject { ["type"] = "ephemeral" }
            };
        }

        static JObject Message(string role, string text)
        {
            return new JObject { ["role"] = role, ["content"] = text };
        }

        static JObject StringArray()
        {
            return new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } };
        }
    }
}
