using System;
using System.Collections;
using System.Collections.Generic;
using ScamSim.Core;
using ScamSim.Logging;

namespace ScamSim.LLM
{
    public class ProviderRequest
    {
        /// <summary>Full history, including the player's latest reply.</summary>
        public ConversationState State;
        /// <summary>Pressure level (1-5) the next line should be spoken at.</summary>
        public int TargetPressure;
        /// <summary>True when the turn limit is reached and the next line must close the call.</summary>
        public bool MustEnd;
    }

    public class ProviderResult
    {
        public bool Success;
        public ScammerTurn Turn;
        /// <summary>llm, scripted or fallback (see TurnSources).</summary>
        public string Source = TurnSources.Scripted;
        public bool TimedOut;
        public string Error;
        public int Attempts = 1;
    }

    /// <summary>
    /// A scammer "brain". Coroutine based so the same code runs on WebGL (no threads).
    /// Swap implementations to add other providers (OpenAI, local model) later.
    /// </summary>
    public interface ILLMProvider
    {
        string ProviderId { get; }
        string ModelId { get; }
        string PromptVersion { get; }
        /// <summary>Always null on current models (the API no longer accepts it); kept for the log schema.</summary>
        double? Temperature { get; }
        /// <summary>Reasoning effort sent to the model (low..max), or null for providers without one.</summary>
        string Effort { get; }
        string ScenarioId { get; }

        IEnumerator NextTurn(ProviderRequest request, Action<ProviderResult> onDone);

        /// <summary>Three short lessons for the debrief. Calls back with null when unavailable.</summary>
        IEnumerator GenerateLessons(SessionLog log, Action<List<string>> onDone);
    }
}
