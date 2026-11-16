using System.Collections.Generic;
using Newtonsoft.Json;
using ScamSim.Core;

namespace ScamSim.LLM
{
    public class ScriptedOption
    {
        [JsonProperty("text")] public string Text = "";
        [JsonProperty("stance")] public string Stance = StanceNames.Hesitate;
        [JsonProperty("next")] public string Next;
        [JsonProperty("effects")] public GoalProgress Effects;
    }

    /// <summary>A node uses the same field names as the LLM response, plus branching options.</summary>
    public class ScriptedNode
    {
        [JsonProperty("scammer_line")] public string ScammerLine = "";
        /// <summary>
        /// Optional, for an "escaped" ending only: the line to show instead when the player already
        /// gave something away earlier in the call. The call then counts as scammed, so the last line
        /// must not say the scammer failed.
        /// </summary>
        [JsonProperty("scammer_line_if_lost")] public string ScammerLineIfLost;
        [JsonProperty("pressure_level")] public int PressureLevel = 1;
        [JsonProperty("red_flags_in_this_line")] public List<string> RedFlags = new List<string>();
        [JsonProperty("options")] public List<ScriptedOption> Options = new List<ScriptedOption>();
        [JsonProperty("call_should_end")] public bool CallShouldEnd;
        [JsonProperty("end_reason")] public string EndReason;
    }

    /// <summary>Generic line used when the LLM fails mid-call (one per pressure level).</summary>
    public class FallbackLine
    {
        [JsonProperty("pressure_level")] public int PressureLevel = 1;
        [JsonProperty("scammer_line")] public string ScammerLine = "";
        [JsonProperty("red_flags_in_this_line")] public List<string> RedFlags = new List<string>();
        [JsonProperty("player_options")] public List<string> PlayerOptions = new List<string>();
    }

    public class ScriptedLessons
    {
        [JsonProperty("id_number")] public string IdNumber = "";
        [JsonProperty("transfer")] public string Transfer = "";
        [JsonProperty("link_click")] public string LinkClick = "";
        [JsonProperty("general")] public List<string> General = new List<string>();
    }

    public class ScriptedScenario
    {
        [JsonProperty("scenario_id")] public string ScenarioId = "";
        [JsonProperty("version")] public string Version = "";
        [JsonProperty("start")] public string Start = "";
        [JsonProperty("nodes")] public Dictionary<string, ScriptedNode> Nodes = new Dictionary<string, ScriptedNode>();
        [JsonProperty("fallback")] public List<FallbackLine> Fallback = new List<FallbackLine>();
        [JsonProperty("closing_line")] public string ClosingLine = "";
        [JsonProperty("lessons")] public ScriptedLessons Lessons = new ScriptedLessons();

        /// <summary>Returns a list of authoring mistakes. Empty means the graph is playable.</summary>
        public List<string> Validate()
        {
            var errors = new List<string>();
            if (!Nodes.ContainsKey(Start)) errors.Add("start node '" + Start + "' does not exist");

            foreach (var pair in Nodes)
            {
                string id = pair.Key;
                ScriptedNode node = pair.Value;
                if (string.IsNullOrWhiteSpace(node.ScammerLine)) errors.Add(id + ": empty scammer_line");
                if (node.PressureLevel < ConversationManager.MinPressure || node.PressureLevel > ConversationManager.MaxPressure)
                    errors.Add(id + ": pressure_level out of range");

                bool escapedEnding = node.CallShouldEnd && node.EndReason == EndReasons.Escaped;
                if (escapedEnding && string.IsNullOrWhiteSpace(node.ScammerLineIfLost))
                    errors.Add(id + ": an escaped ending needs scammer_line_if_lost");
                if (!escapedEnding && node.ScammerLineIfLost != null)
                    errors.Add(id + ": scammer_line_if_lost only belongs on an escaped ending");

                if (node.CallShouldEnd)
                {
                    if (!EndReasons.IsValid(node.EndReason)) errors.Add(id + ": terminal node needs a valid end_reason");
                    if (node.Options.Count != 0) errors.Add(id + ": terminal node must not have options");
                    continue;
                }

                if (node.Options.Count != 3)
                {
                    errors.Add(id + ": needs exactly 3 options");
                    continue;
                }

                string[] expected = { StanceNames.Comply, StanceNames.Hesitate, StanceNames.Resist };
                for (int i = 0; i < 3; i++)
                {
                    ScriptedOption option = node.Options[i];
                    if (option.Stance != expected[i]) errors.Add(id + ": option " + i + " must be " + expected[i]);
                    if (string.IsNullOrWhiteSpace(option.Text)) errors.Add(id + ": option " + i + " has no text");
                    if (option.Next == null || !Nodes.ContainsKey(option.Next))
                        errors.Add(id + ": option " + i + " points to missing node '" + option.Next + "'");
                }
            }

            for (int level = ConversationManager.MinPressure; level <= ConversationManager.MaxPressure; level++)
            {
                FallbackLine line = Fallback.Find(f => f.PressureLevel == level);
                if (line == null) errors.Add("fallback: no line for pressure " + level);
                else if (line.PlayerOptions.Count != 3) errors.Add("fallback " + level + ": needs exactly 3 player_options");
            }

            // The path checks follow option links, so they only run on a graph with no broken links.
            if (errors.Count == 0)
            {
                int longest = LongestPath(Start, new Dictionary<string, int>(), errors);
                if (errors.Count == 0 && longest > ConversationManager.MaxTurns)
                    errors.Add("longest path has " + longest + " lines, the turn limit is " + ConversationManager.MaxTurns);
            }

            return errors;
        }

        const int Visiting = -1;

        /// <summary>
        /// Number of scammer lines on the longest path from a node to an ending. Reports a loop as an
        /// error: a loop would only ever stop at the turn limit, never at one of the written endings.
        /// </summary>
        int LongestPath(string id, Dictionary<string, int> depth, List<string> errors)
        {
            int known;
            if (depth.TryGetValue(id, out known))
            {
                if (known == Visiting) errors.Add(id + ": the conversation can loop back to this node");
                return known == Visiting ? 0 : known;
            }

            depth[id] = Visiting;
            int longest = 0;
            foreach (ScriptedOption option in Nodes[id].Options)
            {
                int below = LongestPath(option.Next, depth, errors);
                if (below > longest) longest = below;
            }
            depth[id] = longest + 1;
            return longest + 1;
        }
    }
}
