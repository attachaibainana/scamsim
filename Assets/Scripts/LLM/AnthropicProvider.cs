using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using ScamSim.Core;
using ScamSim.Logging;
using UnityEngine;

namespace ScamSim.LLM
{
    /// <summary>
    /// The scammer played by Claude through the Messages API (PRD 2.2). Each line is requested with a
    /// strict JSON schema; output that still breaks a rule is asked for again up to twice, then the
    /// call falls back to a scripted line. A timeout is reported as such so the game can show
    /// "the line dropped" and let the player retry. A rejected key, model or request turns the
    /// provider off, so later lines fall back at once instead of sending more requests that cannot work.
    /// </summary>
    public class AnthropicProvider : ILLMProvider
    {
        public const int MaxRetries = 2;
        public const int LessonCount = 3;

        readonly AnthropicSettings _settings;
        readonly PromptFile _scammerPrompt;
        readonly PromptFile _lessonsPrompt;
        readonly IHttpTransport _transport;
        readonly string _scenarioId;

        public AnthropicProvider(AnthropicSettings settings, PromptFile scammerPrompt, PromptFile lessonsPrompt,
            IHttpTransport transport, string scenarioId)
        {
            if (settings == null || string.IsNullOrWhiteSpace(settings.ApiKey)) throw new ArgumentException("An API key is required.");
            if (scammerPrompt == null) throw new ArgumentNullException("scammerPrompt");
            if (lessonsPrompt == null) throw new ArgumentNullException("lessonsPrompt");
            if (transport == null) throw new ArgumentNullException("transport");

            _settings = settings;
            _scammerPrompt = scammerPrompt;
            _lessonsPrompt = lessonsPrompt;
            _transport = transport;
            _scenarioId = scenarioId;
        }

        /// <summary>Pause before asking again after a rate limit or server error. Tests set it to 0.</summary>
        public float RetryDelaySeconds { get; set; } = 1f;

        /// <summary>Set after the API rejected the key, model or request shape (400/401/403/404); no more requests are sent.</summary>
        public string DisabledReason { get; private set; }

        public bool IsDisabled
        {
            get { return DisabledReason != null; }
        }

        public string ProviderId { get { return "anthropic"; } }
        public string ModelId { get { return _settings.Model; } }
        public string PromptVersion { get { return _scammerPrompt.Version + "+" + _lessonsPrompt.Version; } }
        public double? Temperature { get { return null; } }
        public string Effort { get { return _settings.Effort; } }
        public string ScenarioId { get { return _scenarioId; } }

        public IEnumerator NextTurn(ProviderRequest request, Action<ProviderResult> onDone)
        {
            if (IsDisabled)
            {
                onDone(new ProviderResult { Success = false, Source = TurnSources.Llm, Error = "disabled:" + DisabledReason, Attempts = 0 });
                yield break;
            }

            string body = AnthropicRequestBuilder.TurnBody(_settings, _scammerPrompt.Body, request).ToString(Formatting.None);
            yield return Ask(body, ResponseParser.ParseTurn, (outcome, attempts) => onDone(new ProviderResult
            {
                Success = outcome.Ok,
                Turn = outcome.Value,
                Source = TurnSources.Llm,
                TimedOut = outcome.Error == "timeout",
                Error = outcome.Error,
                Attempts = attempts
            }));
        }

        public IEnumerator GenerateLessons(SessionLog log, Action<List<string>> onDone)
        {
            // Nothing was said (a declined call): there is nothing for the model to review, so do not pay for a request.
            if (IsDisabled || DebriefSummary.From(log).Items.Count == 0)
            {
                onDone(null);
                yield break;
            }

            string body = AnthropicRequestBuilder.LessonsBody(_settings, _lessonsPrompt.Body, log).ToString(Formatting.None);
            yield return Ask(body, http => ResponseParser.ParseLessons(http, LessonCount), (outcome, attempts) =>
            {
                if (!outcome.Ok) Debug.LogWarning("[ScamSim] Lessons from the model failed (" + outcome.Error + "); using the scripted ones.");
                onDone(outcome.Ok ? outcome.Value : null);
            });
        }

        /// <summary>
        /// Sends the request, asking again while the failure is retryable, at most MaxRetries more times.
        /// Yields only null or nested coroutines, so the caller can stop it on any frame.
        /// </summary>
        IEnumerator Ask<T>(string body, Func<HttpResult, ParseOutcome<T>> parse, Action<ParseOutcome<T>, int> onDone)
        {
            Dictionary<string, string> headers = AnthropicRequestBuilder.Headers(_settings);
            for (int attempt = 1; ; attempt++)
            {
                HttpResult http = null;
                yield return _transport.Post(AnthropicRequestBuilder.Url, headers, body, _settings.TimeoutSeconds, r => http = r);
                ParseOutcome<T> outcome = parse(http ?? new HttpResult { NetworkError = "no response" });

                if (IsPermanent(outcome.Error))
                {
                    DisabledReason = outcome.Error;
                    Debug.LogWarning("[ScamSim] The Claude API rejected the request (" + outcome.Error
                                     + "). Check the API key and model in config.json. Using the offline script from now on.");
                }

                if (outcome.Ok || !outcome.Retryable || attempt > MaxRetries)
                {
                    onDone(outcome, attempt);
                    yield break;
                }

                bool serverSide = outcome.Error == "network" || outcome.Error.StartsWith("http_", StringComparison.Ordinal);
                if (serverSide && RetryDelaySeconds > 0f)
                {
                    float until = Time.realtimeSinceStartup + RetryDelaySeconds * attempt;
                    while (Time.realtimeSinceStartup < until) yield return null;
                }
            }
        }

        static bool IsPermanent(string error)
        {
            return error == "http_400" || error == "http_401" || error == "http_403" || error == "http_404";
        }
    }
}
