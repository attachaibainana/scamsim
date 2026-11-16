using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ScamSim.Core;
using ScamSim.LLM;
using ScamSim.Logging;
using UnityEngine;

namespace ScamSim.Tests
{
    /// <summary>Every test here uses FakeTransport: nothing calls the real API.</summary>
    public class AnthropicProviderTests
    {
        const string Key = "sk-ant-test-key-never-logged";

        static PromptFile ScammerPrompt()
        {
            return PromptFile.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Prompts/scammer_police.txt")));
        }

        static PromptFile LessonsPrompt()
        {
            return PromptFile.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Prompts/lessons.txt")));
        }

        static AnthropicProvider NewProvider(FakeTransport transport)
        {
            var settings = new AnthropicSettings { ApiKey = Key };
            return new AnthropicProvider(settings, ScammerPrompt(), LessonsPrompt(), transport, "police_cyber_v1")
            {
                RetryDelaySeconds = 0f
            };
        }

        static ProviderResult Ask(AnthropicProvider provider, ProviderRequest request)
        {
            ProviderResult result = null;
            var pump = new CoroutinePump(provider.NextTurn(request, r => result = r));
            pump.RunUntilIdle();
            Assert.IsTrue(pump.Finished);
            return result;
        }

        static ProviderRequest FirstRequest()
        {
            return new ConversationManager().BuildRequest();
        }

        [Test]
        public void RequestUsesTheCurrentApiWithoutTemperature()
        {
            var transport = new FakeTransport();
            transport.Enqueue(FakeTransport.Turn(FakeTransport.Line("สวัสดีครับ", 2)));
            Ask(NewProvider(transport), FirstRequest());

            FakeTransport.Sent sent = transport.Requests[0];
            Assert.AreEqual(AnthropicRequestBuilder.Url, sent.Url);
            Assert.AreEqual("2026-10-21", sent.Headers["anthropic-version"]);
            Assert.AreEqual("server-side-fallback-2026-07-01", sent.Headers["anthropic-beta"]);
            Assert.AreEqual(Key, sent.Headers["x-api-key"]);
            Assert.AreEqual(20, sent.TimeoutSeconds);

            JObject body = sent.Json;
            Assert.AreEqual("claude-sonnet-5-5", (string)body["model"]);
            Assert.AreEqual("low", (string)body["output_config"]["effort"]);
            Assert.AreEqual("json_schema", (string)body["output_config"]["format"]["type"]);
            Assert.AreEqual("default", (string)body["fallbacks"]);
            Assert.AreEqual("ephemeral", (string)body["cache_control"]["type"]);
            foreach (string removed in new[] { "temperature", "top_p", "top_k", "thinking" })
                Assert.IsNull(body[removed], removed + " must not be sent");
            StringAssert.Contains("## 1. Framing", (string)body["system"]);
            Assert.AreEqual(1, ((JArray)body["messages"]).Count);
            Assert.AreEqual("user", (string)body["messages"][0]["role"]);
        }

        [Test]
        public void SchemaMatchesThePrdFieldsAndIsStrict()
        {
            JObject schema = AnthropicRequestBuilder.TurnSchema();
            CollectionAssert.AreEquivalent(
                new[] { "scammer_line", "pressure_level", "red_flags_in_this_line", "player_options", "goal_progress", "call_should_end", "end_reason" },
                ((JArray)schema["required"]).ToObject<string[]>());
            Assert.IsFalse((bool)schema["additionalProperties"]);
            Assert.IsFalse((bool)schema["properties"]["goal_progress"]["additionalProperties"]);
        }

        [Test]
        public void AValidTurnIsReturnedAsIs()
        {
            var transport = new FakeTransport();
            transport.Enqueue(FakeTransport.Turn(FakeTransport.Line("สวัสดีครับ ผมจากกองปราบไซเบอร์กลาง", 2)));
            ProviderResult result = Ask(NewProvider(transport), FirstRequest());

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("สวัสดีครับ ผมจากกองปราบไซเบอร์กลาง", result.Turn.ScammerLine);
            Assert.AreEqual(3, result.Turn.PlayerOptions.Count);
            Assert.AreEqual(1, result.Attempts);
            Assert.AreEqual(TurnSources.Llm, result.Source);
        }

        [Test]
        public void BadOutputIsAskedForTwiceMoreThenFails()
        {
            var transport = new FakeTransport();
            for (int i = 0; i < 3; i++) transport.Enqueue(FakeTransport.Text("ไม่ใช่ JSON"));
            ProviderResult result = Ask(NewProvider(transport), FirstRequest());

            Assert.IsFalse(result.Success);
            Assert.AreEqual("invalid_json", result.Error);
            Assert.AreEqual(3, transport.Requests.Count);
            Assert.AreEqual(3, result.Attempts);
        }

        [Test]
        public void ARuleTheSchemaCannotExpressIsCheckedAndRetried()
        {
            var transport = new FakeTransport();
            JObject twoOptions = FakeTransport.Line("x", 2);
            ((JArray)twoOptions["player_options"]).RemoveAt(2);
            transport.Enqueue(FakeTransport.Turn(twoOptions));
            transport.Enqueue(FakeTransport.Turn(FakeTransport.Line("y", 2)));
            ProviderResult result = Ask(NewProvider(transport), FirstRequest());

            Assert.IsTrue(result.Success);
            Assert.AreEqual("y", result.Turn.ScammerLine);
            Assert.AreEqual(2, result.Attempts);
        }

        [Test]
        public void ARefusalIsNotRetried()
        {
            var transport = new FakeTransport();
            transport.Enqueue(new HttpResult
            {
                Status = 200,
                Body = "{\"type\":\"message\",\"content\":[],\"stop_reason\":\"refusal\",\"stop_details\":{\"type\":\"refusal\",\"category\":\"cyber\"}}"
            });
            ProviderResult result = Ask(NewProvider(transport), FirstRequest());

            Assert.IsFalse(result.Success);
            Assert.AreEqual("refusal:cyber", result.Error);
            Assert.AreEqual(1, transport.Requests.Count);
        }

        [TestCase(401, false)]
        [TestCase(400, false)]
        [TestCase(429, true)]
        [TestCase(529, true)]
        public void HttpErrorsRetryOnlyWhenTheyMayPass(int status, bool retried)
        {
            var transport = new FakeTransport();
            transport.Enqueue(new HttpResult { Status = status, Body = "{\"type\":\"error\"}" });
            transport.Enqueue(FakeTransport.Turn(FakeTransport.Line("ok", 2)));
            ProviderResult result = Ask(NewProvider(transport), FirstRequest());

            Assert.AreEqual(retried, result.Success);
            Assert.AreEqual(retried ? 2 : 1, transport.Requests.Count);
            if (!retried) Assert.AreEqual("http_" + status, result.Error);
        }

        [Test]
        public void ARejectedKeyTurnsTheProviderOff()
        {
            var transport = new FakeTransport();
            transport.Enqueue(new HttpResult { Status = 401, Body = "{\"type\":\"error\"}" });
            AnthropicProvider provider = NewProvider(transport);

            Assert.AreEqual("http_401", Ask(provider, FirstRequest()).Error);
            Assert.IsTrue(provider.IsDisabled);

            ProviderResult second = Ask(provider, FirstRequest());
            Assert.AreEqual("disabled:http_401", second.Error);
            Assert.AreEqual(1, transport.Requests.Count, "no more requests after the key was rejected");

            List<string> lessons = new List<string>();
            new CoroutinePump(provider.GenerateLessons(TestSessions.Play(Stance.Comply), l => lessons = l)).RunUntilIdle();
            Assert.IsNull(lessons);
            Assert.AreEqual(1, transport.Requests.Count);
        }

        [Test]
        public void RunningOutOfTokensIsNotRetried()
        {
            var transport = new FakeTransport();
            transport.Enqueue(new HttpResult { Status = 200, Body = "{\"type\":\"message\",\"content\":[{\"type\":\"text\",\"text\":\"{\\\"scam\"}],\"stop_reason\":\"max_tokens\"}" });
            ProviderResult result = Ask(NewProvider(transport), FirstRequest());
            Assert.AreEqual("max_tokens", result.Error);
            Assert.AreEqual(1, transport.Requests.Count);
        }

        [Test]
        public void TheSchemaLeavesHangingUpToTheGame()
        {
            JArray reasons = (JArray)AnthropicRequestBuilder.TurnSchema()["properties"]["end_reason"]["anyOf"][0]["enum"];
            CollectionAssert.AreEquivalent(new[] { EndReasons.Scammed, EndReasons.Escaped }, reasons.ToObject<string[]>());
        }

        [Test]
        public void ACallWithNothingSaidDoesNotAskForLessons()
        {
            var transport = new FakeTransport();
            var manager = new ConversationManager();
            manager.DeclineCall();
            ScriptedProvider scripted = TestSessions.LoadProvider();
            SessionLog declined = SessionLogger.Build(TestSessions.Meta(scripted), manager, null, null);

            List<string> lessons = new List<string>();
            new CoroutinePump(NewProvider(transport).GenerateLessons(declined, l => lessons = l)).RunUntilIdle();
            Assert.IsNull(lessons);
            Assert.AreEqual(0, transport.Requests.Count);
        }

        [Test]
        public void ATimeoutIsReportedSoThePlayerCanRetry()
        {
            var transport = new FakeTransport();
            transport.Enqueue(new HttpResult { NetworkError = "Request timeout", TimedOut = true });
            ProviderResult result = Ask(NewProvider(transport), FirstRequest());

            Assert.IsFalse(result.Success);
            Assert.IsTrue(result.TimedOut);
            Assert.AreEqual(1, transport.Requests.Count);
        }

        [Test]
        public void ThinkingAndFallbackBlocksAreSkipped()
        {
            string turn = FakeTransport.Line("ok", 2).ToString(Formatting.None);
            var body = new JObject
            {
                ["type"] = "message",
                ["stop_reason"] = "end_turn",
                ["content"] = new JArray(
                    new JObject { ["type"] = "thinking", ["thinking"] = "" },
                    new JObject { ["type"] = "fallback", ["from"] = new JObject { ["model"] = "a" }, ["to"] = new JObject { ["model"] = "b" } },
                    new JObject { ["type"] = "text", ["text"] = turn })
            };
            ParseOutcome<ScammerTurn> outcome = ResponseParser.ParseTurn(new HttpResult { Status = 200, Body = body.ToString() });
            Assert.IsTrue(outcome.Ok, outcome.Error);
            Assert.AreEqual("ok", outcome.Value.ScammerLine);
        }

        [Test]
        public void AFullCallThroughCallSessionLogsTheModelAndMasksTheReply()
        {
            var transport = new FakeTransport();
            transport.Enqueue(FakeTransport.Turn(FakeTransport.Line("ขอเลขบัตรประชาชนด้วยครับ", 2)));
            JObject end = FakeTransport.Line("ได้รับยอดแล้วครับ (สายตัดไป)", 1);
            end["player_options"] = new JArray();
            end["goal_progress"]["id_number"] = true;
            end["call_should_end"] = true;
            end["end_reason"] = "scammed";
            transport.Enqueue(FakeTransport.Turn(end));
            transport.Enqueue(FakeTransport.Text("{\"lessons\":[\"บทเรียน 1\",\"บทเรียน 2\",\"บทเรียน 3\"]}"));

            AnthropicProvider provider = NewProvider(transport);
            ScriptedProvider scripted = TestSessions.LoadProvider();
            string directory = TestSessions.TempDirectory();
            try
            {
                var meta = new SessionMeta
                {
                    SessionId = "anthropic-test", ScenarioId = provider.ScenarioId, AppVersion = "test",
                    Provider = provider.ProviderId, ModelId = provider.ModelId, PromptVersion = provider.PromptVersion,
                    Temperature = provider.Temperature, Effort = provider.Effort, StartUtc = DateTime.UtcNow
                };
                var call = new CallSession(provider, scripted, meta, directory);
                var pump = new CoroutinePump(call.Run());
                call.Accept();
                pump.RunUntilIdle();
                call.SubmitFreeText("เลขบัตรผม 1234567890123 ครับ");
                pump.RunUntilIdle();
                Assert.IsTrue(pump.Finished);

                // The second request carries the player's reply, masked, as the last user message.
                JArray messages = (JArray)transport.Requests[1].Json["messages"];
                CollectionAssert.AreEqual(new[] { "user", "assistant", "user" }, Roles(messages));
                string reply = (string)messages[2]["content"];
                StringAssert.Contains("[REDACTED]", reply);
                StringAssert.DoesNotContain("1234567890123", transport.Requests[1].Body);
                StringAssert.Contains("target_pressure: 1", reply);

                // The earlier messages are sent again exactly as before, so they can be cached.
                Assert.IsTrue(JToken.DeepEquals(transport.Requests[0].Json["messages"][0], messages[0]));

                SessionLog log = call.Logger.Latest;
                Assert.AreEqual("anthropic", log.Provider);
                Assert.AreEqual("claude-sonnet-5-5", log.ModelId);
                Assert.AreEqual("low", log.Effort);
                Assert.IsNull(log.Temperature);
                Assert.AreEqual("police-1.0+lessons-1.0", log.PromptVersion);
                Assert.AreEqual(EndReasons.Scammed, log.EndReason);
                Assert.AreEqual(TurnSources.Llm, log.Turns[0].Source);
                CollectionAssert.AreEqual(new[] { "บทเรียน 1", "บทเรียน 2", "บทเรียน 3" }, log.Lessons);

                string saved = File.ReadAllText(call.Logger.FilePath);
                StringAssert.DoesNotContain(Key, saved);
                StringAssert.DoesNotContain(Key, CsvExporter.ToCsv(log));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ARefusedLineFallsBackToAScriptedOneAndSaysWhy()
        {
            var transport = new FakeTransport();
            transport.Enqueue(new HttpResult
            {
                Status = 200,
                Body = "{\"type\":\"message\",\"content\":[],\"stop_reason\":\"refusal\",\"stop_details\":{\"category\":\"general_harms\"}}"
            });
            AnthropicProvider provider = NewProvider(transport);
            ScriptedProvider scripted = TestSessions.LoadProvider();
            var call = new CallSession(provider, scripted, TestSessions.Meta(scripted), null);
            var pump = new CoroutinePump(call.Run());
            call.Accept();
            pump.RunUntilIdle();

            Assert.AreEqual(CallState.AwaitingPlayer, call.State);
            SessionLog log = call.Logger.Latest;
            Assert.AreEqual(TurnSources.Fallback, log.Turns[0].Source);
            Assert.AreEqual("refusal:general_harms", log.Turns[0].ProviderError);
            Assert.AreEqual(1, log.Turns[0].ProviderAttempts);
        }

        [Test]
        public void LessonsThatAreTooFewFallBackToTheScriptedOnes()
        {
            var transport = new FakeTransport();
            for (int i = 0; i < 3; i++) transport.Enqueue(FakeTransport.Text("{\"lessons\":[\"มีข้อเดียว\"]}"));
            AnthropicProvider provider = NewProvider(transport);

            List<string> lessons = new List<string> { "unchanged" };
            var pump = new CoroutinePump(provider.GenerateLessons(TestSessions.Play(Stance.Comply), l => lessons = l));
            pump.RunUntilIdle();
            Assert.IsNull(lessons);
            Assert.AreEqual(3, transport.Requests.Count);
        }

        [Test]
        public void LessonInputCarriesTheMaskedCallButNoKey()
        {
            SessionLog finished = TestSessions.Play(Stance.Resist, Stance.Resist, Stance.Resist);
            JObject body = AnthropicRequestBuilder.LessonsBody(new AnthropicSettings { ApiKey = Key }, "x", finished);
            string text = body.ToString();
            StringAssert.DoesNotContain(Key, text);
            JObject input = JObject.Parse((string)body["messages"][0]["content"]);
            Assert.AreEqual(EndReasons.Escaped, (string)input["end_reason"]);
            Assert.Greater(((JArray)input["turns"]).Count, 0);
        }

        static string[] Roles(JArray messages)
        {
            var roles = new List<string>();
            foreach (JToken message in messages) roles.Add((string)message["role"]);
            return roles.ToArray();
        }
    }

    public class PromptAndConfigTests
    {
        [Test]
        public void TheScammerPromptHasTheFiveSectionsOfThePrd()
        {
            PromptFile prompt = PromptFile.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Prompts/scammer_police.txt")));
            Assert.AreEqual("police-1.0", prompt.Version);
            foreach (string section in new[] { "## 1. Framing", "## 2. Persona", "## 3. Adaptation rules", "## 4. Hard limits", "## 5. Output format" })
                StringAssert.Contains(section, prompt.Body);
            StringAssert.Contains("กองปราบไซเบอร์กลาง", prompt.Body);
            StringAssert.DoesNotStartWith("prompt_version", prompt.Body);
        }

        [Test]
        public void ThePromptExampleIsAValidTurn()
        {
            PromptFile prompt = PromptFile.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Prompts/scammer_police.txt")));
            string example = prompt.Body.Substring(prompt.Body.LastIndexOf("{\"scammer_line\"", StringComparison.Ordinal)).Trim();
            ScammerTurn turn = JsonConvert.DeserializeObject<ScammerTurn>(example);
            Assert.IsNull(ResponseParser.Validate(turn));
        }

        [TestCase("")]
        [TestCase("no version line\ntext")]
        [TestCase("prompt_version: x")]
        public void APromptFileWithoutAVersionOrTextIsRejected(string text)
        {
            Assert.Throws<ArgumentException>(() => PromptFile.Parse(text));
        }

        [Test]
        public void ConfigFallsBackToSafeDefaults()
        {
            string error;
            AppConfig config = AppConfig.Parse("{\"anthropic_api_key\":\"  sk-ant-x  \",\"effort\":\"turbo\",\"timeout_seconds\":1,\"model\":\"\"}", out error);
            Assert.IsNull(error);
            Assert.AreEqual("sk-ant-x", config.AnthropicApiKey);
            Assert.AreEqual("low", config.Effort);
            Assert.AreEqual(20, config.TimeoutSeconds);
            Assert.AreEqual("claude-sonnet-5-5", config.Model);
            Assert.IsTrue(config.HasApiKey);

            Assert.IsNull(AppConfig.Parse("{not json", out error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void TheExampleConfigHasNoKeyAndLivesOutsideAssets()
        {
            string root = Path.GetDirectoryName(AppConfig.FilePath);
            string error;
            AppConfig example = AppConfig.Parse(File.ReadAllText(Path.Combine(root, "config.example.json")), out error);
            Assert.IsNull(error);
            Assert.IsFalse(example.HasApiKey);
            Assert.IsEmpty(example.Warnings);

            // Everything under Assets/StreamingAssets ships inside the build.
            StringAssert.DoesNotStartWith(Path.GetFullPath(Application.dataPath), AppConfig.FilePath);
            Assert.IsFalse(File.Exists(Path.Combine(Application.streamingAssetsPath, "config.json")),
                "config.json in StreamingAssets would ship the API key");
        }

        [Test]
        public void EffortIsReadCaseInsensitivelyAndReplacementsAreReported()
        {
            string error;
            AppConfig upper = AppConfig.Parse("{\"effort\":\"High\"}", out error);
            Assert.AreEqual("high", upper.Effort);
            Assert.IsEmpty(upper.Warnings);

            AppConfig wrong = AppConfig.Parse("{\"effort\":\"turbo\",\"max_tokens\":10}", out error);
            Assert.AreEqual(2, wrong.Warnings.Count);
        }
    }

    /// <summary>Returns queued responses and records every request.</summary>
    class FakeTransport : IHttpTransport
    {
        public class Sent
        {
            public string Url;
            public Dictionary<string, string> Headers;
            public string Body;
            public int TimeoutSeconds;

            public JObject Json
            {
                get { return JObject.Parse(Body); }
            }
        }

        readonly Queue<HttpResult> _responses = new Queue<HttpResult>();
        public readonly List<Sent> Requests = new List<Sent>();

        public void Enqueue(HttpResult response)
        {
            _responses.Enqueue(response);
        }

        public IEnumerator Post(string url, IDictionary<string, string> headers, string body, int timeoutSeconds, Action<HttpResult> onDone)
        {
            Requests.Add(new Sent { Url = url, Headers = new Dictionary<string, string>(headers), Body = body, TimeoutSeconds = timeoutSeconds });
            onDone(_responses.Count > 0 ? _responses.Dequeue() : new HttpResult { NetworkError = "no more fake responses" });
            yield break;
        }

        public static JObject Line(string text, int pressure)
        {
            return new JObject
            {
                ["scammer_line"] = text,
                ["pressure_level"] = pressure,
                ["red_flags_in_this_line"] = new JArray("ขอเลขบัตรประชาชนทางโทรศัพท์"),
                ["player_options"] = new JArray("ได้ครับ", "ไม่แน่ใจ", "ไม่ให้"),
                ["goal_progress"] = new JObject { ["id_number"] = false, ["transfer"] = false, ["link_click"] = false },
                ["call_should_end"] = false,
                ["end_reason"] = null
            };
        }

        public static HttpResult Turn(JObject turn)
        {
            return Text(turn.ToString(Formatting.None));
        }

        public static HttpResult Text(string text)
        {
            var body = new JObject
            {
                ["id"] = "msg_test",
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = "claude-sonnet-5-5",
                ["stop_reason"] = "end_turn",
                ["content"] = new JArray(
                    new JObject { ["type"] = "thinking", ["thinking"] = "" },
                    new JObject { ["type"] = "text", ["text"] = text })
            };
            return new HttpResult { Status = 200, Body = body.ToString(Formatting.None) };
        }
    }
}
