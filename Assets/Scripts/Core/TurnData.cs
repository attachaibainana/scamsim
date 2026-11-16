using System.Collections.Generic;
using Newtonsoft.Json;
using ScamSim.Logging;

namespace ScamSim.Core
{
    /// <summary>How the player answered a scammer line. Option index 0/1/2 maps to these in order.</summary>
    public enum Stance
    {
        Comply = 0,
        Hesitate = 1,
        Resist = 2
    }

    public static class StanceNames
    {
        public const string Comply = "comply";
        public const string Hesitate = "hesitate";
        public const string Resist = "resist";

        public static string ToName(Stance stance)
        {
            switch (stance)
            {
                case Stance.Comply: return Comply;
                case Stance.Hesitate: return Hesitate;
                default: return Resist;
            }
        }

        public static bool TryParse(string name, out Stance stance)
        {
            switch (name)
            {
                case Comply: stance = Stance.Comply; return true;
                case Hesitate: stance = Stance.Hesitate; return true;
                case Resist: stance = Stance.Resist; return true;
                default: stance = Stance.Hesitate; return false;
            }
        }
    }

    public static class EndReasons
    {
        public const string Scammed = "scammed";
        public const string Escaped = "escaped";
        public const string HungUp = "hung_up";

        public static bool IsValid(string value)
        {
            return value == Scammed || value == Escaped || value == HungUp;
        }
    }

    public static class EndedBy
    {
        public const string PlayerHangUp = "player_hangup";
        public const string PlayerDeclined = "player_declined";
        public const string Scammer = "scammer";
        public const string GoalsComplete = "goals_complete";
        public const string TurnLimit = "turn_limit";
    }

    public static class TurnSources
    {
        public const string Llm = "llm";
        public const string Scripted = "scripted";
        public const string Fallback = "fallback";
    }

    /// <summary>What the scammer has extracted so far. Flags only ever go from false to true.</summary>
    public class GoalProgress
    {
        [JsonProperty("id_number")] public bool IdNumber;
        [JsonProperty("transfer")] public bool Transfer;
        [JsonProperty("link_click")] public bool LinkClick;

        public bool Any()
        {
            return IdNumber || Transfer || LinkClick;
        }

        public bool All()
        {
            return IdNumber && Transfer && LinkClick;
        }

        public void Merge(GoalProgress other)
        {
            if (other == null) return;
            IdNumber |= other.IdNumber;
            Transfer |= other.Transfer;
            LinkClick |= other.LinkClick;
        }

        public GoalProgress Clone()
        {
            return new GoalProgress { IdNumber = IdNumber, Transfer = Transfer, LinkClick = LinkClick };
        }
    }

    /// <summary>One scammer line. Field names match the strict JSON schema in the PRD (2.2).</summary>
    public class ScammerTurn
    {
        [JsonProperty("scammer_line")] public string ScammerLine = "";
        [JsonProperty("pressure_level")] public int PressureLevel = 1;
        [JsonProperty("red_flags_in_this_line")] public List<string> RedFlags = new List<string>();
        [JsonProperty("player_options")] public List<string> PlayerOptions = new List<string>();
        [JsonProperty("goal_progress")] public GoalProgress GoalProgress = new GoalProgress();
        [JsonProperty("call_should_end")] public bool CallShouldEnd;
        [JsonProperty("end_reason")] public string EndReason;
    }

    public static class StanceSources
    {
        public const string Option = "option";
        public const string Heuristic = "heuristic";
        public const string HangUp = "hang_up";
    }

    /// <summary>
    /// The player's answer to one scammer line. It can only be built through the factory methods,
    /// so typed text is always PII-masked and the stance, its source and the flags always agree.
    /// </summary>
    public class PlayerReply
    {
        /// <summary>0..2 when an option was picked (0 comply, 1 hesitate, 2 resist), -1 for free text or hang up.</summary>
        public int OptionIndex { get; private set; }
        public string Text { get; private set; }
        public Stance Stance { get; private set; }
        public string StanceSource { get; private set; }
        public bool HungUp { get; private set; }
        public bool PiiRedacted { get; private set; }
        public bool GaveIdNumber { get; private set; }
        public long ResponseTimeMs { get; private set; }

        PlayerReply()
        {
            OptionIndex = -1;
            Text = "";
        }

        /// <summary>The player picked one of the three offered options. The text comes from the game, not the player.</summary>
        public static PlayerReply FromOption(Stance stance, string optionText, long responseTimeMs)
        {
            return new PlayerReply
            {
                OptionIndex = (int)stance,
                Text = optionText ?? "",
                Stance = stance,
                StanceSource = StanceSources.Option,
                ResponseTimeMs = responseTimeMs
            };
        }

        /// <summary>The player typed a reply. Takes the raw text: masks it, then classifies the masked text.</summary>
        public static PlayerReply FromFreeText(string rawText, long responseTimeMs)
        {
            MaskResult mask = PIIMasker.Mask(rawText);
            bool gaveIdNumber = mask.IdNumbers > 0;
            return new PlayerReply
            {
                Text = mask.Text,
                Stance = StanceClassifier.Classify(mask.Text, gaveIdNumber),
                StanceSource = StanceSources.Heuristic,
                PiiRedacted = mask.AnyRedacted,
                GaveIdNumber = gaveIdNumber,
                ResponseTimeMs = responseTimeMs
            };
        }

        public static PlayerReply HangUp(long responseTimeMs)
        {
            return new PlayerReply
            {
                HungUp = true,
                Stance = Stance.Resist,
                StanceSource = StanceSources.HangUp,
                ResponseTimeMs = responseTimeMs
            };
        }
    }

    public class Exchange
    {
        public ScammerTurn Scammer;
        public PlayerReply Reply;
        /// <summary>order[slot] = option index shown in that slot on screen. Null when no options were shown.</summary>
        public int[] OptionOrder;
        /// <summary>False when the player hung up while this line was still "being typed", so they never read it.</summary>
        public bool LineShown = true;
        public string TimestampUtc;
        public string Source = TurnSources.Scripted;
    }

    public class ConversationState
    {
        public readonly List<Exchange> Exchanges = new List<Exchange>();
        public int Pressure = ConversationManager.StartPressure;
        public GoalProgress Goals = new GoalProgress();
    }
}
