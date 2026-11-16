using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using ScamSim.Core;
using ScamSim.Logging;

namespace ScamSim.LLM
{
    /// <summary>
    /// Offline scammer: walks the branching graph in police_scripted.json.
    /// Stateless — the current node is recomputed from the conversation history,
    /// so a session can be replayed from its log.
    /// </summary>
    public class ScriptedProvider : ILLMProvider
    {
        public ScriptedScenario Scenario { get; private set; }

        public ScriptedProvider(string scenarioJson)
        {
            Scenario = JsonConvert.DeserializeObject<ScriptedScenario>(scenarioJson);
            if (Scenario == null) throw new ArgumentException("Scenario JSON is empty.");
            List<string> errors = Scenario.Validate();
            if (errors.Count > 0) throw new ArgumentException("Invalid scenario: " + string.Join("; ", errors));
        }

        public string ProviderId { get { return "scripted"; } }
        public string ModelId { get { return "scripted"; } }
        public string PromptVersion { get { return Scenario.Version; } }
        public double? Temperature { get { return null; } }
        public string Effort { get { return null; } }
        public string ScenarioId { get { return Scenario.ScenarioId; } }

        public IEnumerator NextTurn(ProviderRequest request, Action<ProviderResult> onDone)
        {
            onDone(new ProviderResult { Success = true, Turn = NextTurnSync(request), Source = TurnSources.Scripted });
            yield break;
        }

        public IEnumerator GenerateLessons(SessionLog log, Action<List<string>> onDone)
        {
            onDone(BuildLessons(log));
            yield break;
        }

        public ScammerTurn NextTurnSync(ProviderRequest request)
        {
            ScriptedNode node = Scenario.Nodes[Scenario.Start];
            var goals = new GoalProgress();

            foreach (Exchange exchange in request.State.Exchanges)
            {
                PlayerReply reply = exchange.Reply;
                if (reply == null || reply.HungUp || node.CallShouldEnd) break;
                ScriptedOption option = PickOption(node, reply);
                goals.Merge(option.Effects);
                node = Scenario.Nodes[option.Next];
            }

            // Losses the script does not know about (an ID number typed as free text) are in the state.
            goals.Merge(request.State.Goals);

            // Walking away after giving something up is not a clean escape: the call counts as
            // scammed, so the ending must say what happened instead of "the scammer failed".
            bool escapedTooLate = node.CallShouldEnd && node.EndReason == EndReasons.Escaped && goals.Any();

            var turn = new ScammerTurn
            {
                ScammerLine = escapedTooLate ? node.ScammerLineIfLost : node.ScammerLine,
                PressureLevel = node.PressureLevel,
                RedFlags = new List<string>(node.RedFlags),
                GoalProgress = goals,
                CallShouldEnd = node.CallShouldEnd,
                EndReason = escapedTooLate ? EndReasons.Scammed : node.EndReason
            };
            foreach (ScriptedOption option in node.Options) turn.PlayerOptions.Add(option.Text);

            if (request.MustEnd && !turn.CallShouldEnd) return ClosingTurn(goals, request.TargetPressure);
            return turn;
        }

        /// <summary>A generic line at the requested pressure, used when the LLM cannot answer.</summary>
        public ScammerTurn FallbackTurn(ProviderRequest request)
        {
            if (request.MustEnd) return ClosingTurn(request.State.Goals.Clone(), request.TargetPressure);

            int level = ConversationManager.Clamp(request.TargetPressure);
            FallbackLine line = Scenario.Fallback.Find(f => f.PressureLevel == level);
            return new ScammerTurn
            {
                ScammerLine = line.ScammerLine,
                PressureLevel = level,
                RedFlags = new List<string>(line.RedFlags),
                PlayerOptions = new List<string>(line.PlayerOptions),
                GoalProgress = request.State.Goals.Clone()
            };
        }

        public List<string> BuildLessons(SessionLog log)
        {
            var lessons = new List<string>();
            ScriptedLessons source = Scenario.Lessons;
            GoalProgress lost = log != null && log.Turns.Count > 0
                ? log.Turns[log.Turns.Count - 1].GoalProgress
                : new GoalProgress();

            if (lost.IdNumber && !string.IsNullOrEmpty(source.IdNumber)) lessons.Add(source.IdNumber);
            if (lost.Transfer && !string.IsNullOrEmpty(source.Transfer)) lessons.Add(source.Transfer);
            if (lost.LinkClick && !string.IsNullOrEmpty(source.LinkClick)) lessons.Add(source.LinkClick);
            foreach (string general in source.General)
            {
                if (lessons.Count >= 3) break;
                lessons.Add(general);
            }
            if (lessons.Count > 3) lessons.RemoveRange(3, lessons.Count - 3);
            return lessons;
        }

        ScammerTurn ClosingTurn(GoalProgress goals, int pressure)
        {
            return new ScammerTurn
            {
                ScammerLine = Scenario.ClosingLine,
                PressureLevel = ConversationManager.Clamp(pressure),
                GoalProgress = goals,
                CallShouldEnd = true,
                EndReason = goals.Any() ? EndReasons.Scammed : EndReasons.Escaped
            };
        }

        static ScriptedOption PickOption(ScriptedNode node, PlayerReply reply)
        {
            if (reply.OptionIndex >= 0 && reply.OptionIndex < node.Options.Count) return node.Options[reply.OptionIndex];
            string stance = StanceNames.ToName(reply.Stance);
            ScriptedOption match = node.Options.Find(o => o.Stance == stance);
            return match ?? node.Options[1];
        }
    }
}
