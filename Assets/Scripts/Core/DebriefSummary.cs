using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ScamSim.Logging;

namespace ScamSim.Core
{
    public enum FlagStatus
    {
        /// <summary>The player's reply to this line refused, questioned or hung up.</summary>
        Noticed,
        /// <summary>The player replied and went along or hesitated.</summary>
        Missed,
        /// <summary>The call ended on this line, so there was no reply to judge.</summary>
        CallEnded
    }

    public class DebriefFlag
    {
        public string Text;
        public FlagStatus Status;
    }

    public class DebriefItem
    {
        public int TurnIndex;
        /// <summary>The line cut down for the timeline.</summary>
        public string ScammerLine;
        public string FullScammerLine;
        public readonly List<DebriefFlag> Flags = new List<DebriefFlag>();
        /// <summary>What the player said, or null when they did not reply.</summary>
        public string PlayerText;
        public bool PlayerHungUp;
    }

    /// <summary>
    /// What the debrief shows, built only from the saved log (PRD 2.5): the red flags of every line
    /// the player saw, marked noticed or missed by the reply that followed, and what was lost.
    /// </summary>
    public class DebriefSummary
    {
        /// <summary>Thai has no spaces between words, so lines are cut by length; long enough to keep the gist.</summary>
        public const int ShortLineLength = 130;

        public static readonly string[] GoalKeys = { "id_number", "transfer", "link_click" };

        public string EndReason;
        public readonly List<string> Lost = new List<string>();
        /// <summary>Red flags on lines the player replied to (the ones a reaction can be measured on).</summary>
        public int FlagsAnswerable;
        public int FlagsNoticed;
        public readonly List<DebriefItem> Items = new List<DebriefItem>();
        public readonly List<string> Lessons = new List<string>();

        public static DebriefSummary From(SessionLog log)
        {
            var summary = new DebriefSummary();
            if (log == null) return summary;

            summary.EndReason = log.EndReason;
            if (log.Lessons != null) summary.Lessons.AddRange(log.Lessons);

            GoalProgress final = log.Turns.Count > 0 ? log.Turns[log.Turns.Count - 1].GoalProgress : null;
            if (final != null)
            {
                if (final.IdNumber) summary.Lost.Add(GoalKeys[0]);
                if (final.Transfer) summary.Lost.Add(GoalKeys[1]);
                if (final.LinkClick) summary.Lost.Add(GoalKeys[2]);
            }

            foreach (TurnLog turn in log.Turns)
            {
                if (!turn.LineShown) continue;

                bool replied = turn.PlayerChoice != null;
                var item = new DebriefItem
                {
                    TurnIndex = turn.TurnIndex,
                    ScammerLine = Shorten(turn.ScammerLine, ShortLineLength),
                    FullScammerLine = turn.ScammerLine ?? "",
                    PlayerText = replied ? turn.PlayerText : null,
                    PlayerHungUp = replied && "hang_up".Equals(turn.PlayerChoice as string)
                };

                foreach (string flag in turn.RedFlags)
                {
                    FlagStatus status;
                    if (!replied) status = FlagStatus.CallEnded;
                    else if (turn.RedFlagsNoticed == true) status = FlagStatus.Noticed;
                    else status = FlagStatus.Missed;

                    item.Flags.Add(new DebriefFlag { Text = flag, Status = status });
                    if (status == FlagStatus.CallEnded) continue;
                    summary.FlagsAnswerable++;
                    if (status == FlagStatus.Noticed) summary.FlagsNoticed++;
                }
                summary.Items.Add(item);
            }

            return summary;
        }

        /// <summary>
        /// Cuts text to at most maxLength characters and adds "…". Never separates a Thai consonant
        /// from the vowels and tone marks that belong to it: marks above/below and sara am (ำ) after
        /// it, and the leading vowels (เ แ โ ใ ไ) written before it.
        /// </summary>
        public static string Shorten(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text ?? "";

            int cut = maxLength;
            while (cut > 0 && (IsMark(text[cut]) || text[cut] == SaraAm || char.IsLowSurrogate(text[cut]))) cut--;
            while (cut > 0 && IsLeadingVowel(text[cut - 1])) cut--;
            if (cut == 0) return "…";
            // Prefer ending at a space when one is close, so words stay whole.
            int space = text.LastIndexOf(' ', cut - 1, cut);
            if (space > maxLength * 2 / 3) cut = space;
            return text.Substring(0, cut).TrimEnd() + "…";
        }

        const char SaraAm = 'ำ';

        static bool IsLeadingVowel(char c)
        {
            return c >= 'เ' && c <= 'ไ';
        }

        static bool IsMark(char c)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            return category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark
                   || category == UnicodeCategory.EnclosingMark;
        }
    }
}
