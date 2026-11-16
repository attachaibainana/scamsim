using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ScamSim.Core;
using ScamSim.LLM;
using ScamSim.Logging;

namespace ScamSim.Tests
{
    public class OptionOrderTests
    {
        [Test]
        public void SameSeedAndTurnGiveTheSameOrder()
        {
            CollectionAssert.AreEqual(OptionOrder.For(12345, 3), OptionOrder.For(12345, 3));
            CollectionAssert.AreEqual(OptionOrder.For(-7, 0), OptionOrder.For(-7, 0));
        }

        [Test]
        public void EveryOrderIsAPermutationAndAllSixAppear()
        {
            var seen = new HashSet<string>();
            for (int turn = 0; turn < 200; turn++)
            {
                int[] order = OptionOrder.For(987654321, turn);
                CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, order);
                seen.Add(string.Join(",", order));
            }
            Assert.AreEqual(6, seen.Count);
        }

        [Test]
        public void TheFirstSlotIsNotAlwaysComply()
        {
            int complyFirst = 0;
            for (int seed = 0; seed < 300; seed++)
            {
                if (OptionOrder.For(seed, 0)[0] == 0) complyFirst++;
            }
            Assert.That(complyFirst, Is.InRange(60, 140), "comply should be first about a third of the time");
        }

        [Test]
        public void OtherCountsWork()
        {
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, OptionOrder.For(5, 1, 2));
            Assert.AreEqual(0, OptionOrder.For(5, 1, 0).Length);
        }
    }

    public class CallSessionTests
    {
        string _directory;
        long _now;

        [SetUp]
        public void SetUp()
        {
            _directory = TestSessions.TempDirectory();
            _now = 0;
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        CallSession NewCall(ILLMProvider provider = null, int seed = 42)
        {
            ScriptedProvider scripted = TestSessions.LoadProvider();
            SessionMeta meta = TestSessions.Meta(scripted);
            meta.OptionShuffleSeed = seed;
            return new CallSession(provider ?? scripted, scripted, meta, _directory,
                () => _now, () => new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc));
        }

        static void Choose(CallSession call, Stance stance)
        {
            int slot = Array.IndexOf(call.DisplayOrder, (int)stance);
            Assert.GreaterOrEqual(slot, 0, "stance not on screen");
            call.ChooseDisplayed(slot);
        }

        static void PlayAll(CallSession call, CoroutinePump pump, Stance stance)
        {
            for (int i = 0; i < ConversationManager.MaxTurns && call.State == CallState.AwaitingPlayer; i++)
            {
                Choose(call, stance);
                pump.RunUntilIdle();
            }
        }

        [Test]
        public void AlwaysComplyingEndsScammed()
        {
            CallSession call = NewCall();
            var pump = new CoroutinePump(call.Run());
            pump.RunUntilIdle();
            Assert.AreEqual(CallState.Ringing, call.State);

            call.Accept();
            pump.RunUntilIdle();
            Assert.AreEqual(CallState.AwaitingPlayer, call.State);

            PlayAll(call, pump, Stance.Comply);
            Assert.IsTrue(pump.Finished);
            Assert.AreEqual(CallState.Ended, call.State);
            Assert.IsTrue(call.IsConcluded);
            Assert.AreEqual(EndReasons.Scammed, call.Logger.Latest.EndReason);
            Assert.AreEqual(EndedBy.Scammer, call.Logger.Latest.EndedBy);
            Assert.IsNotEmpty(call.Lessons);
            Assert.IsNotNull(call.Logger.Latest.EndTime);
        }

        [Test]
        public void AlwaysResistingEscapesAndLogsTheOrderShown()
        {
            CallSession call = NewCall(seed: 7);
            var shown = new List<int[]>();
            call.ScammerSpoke += _ => shown.Add((int[])call.DisplayOrder.Clone());
            var pump = new CoroutinePump(call.Run());
            call.Accept();
            pump.RunUntilIdle();
            PlayAll(call, pump, Stance.Resist);

            SessionLog log = call.Logger.Latest;
            Assert.AreEqual(EndReasons.Escaped, log.EndReason);
            Assert.AreEqual(shown.Count, log.Turns.Count);
            for (int i = 0; i < log.Turns.Count - 1; i++)
            {
                CollectionAssert.AreEqual(shown[i], log.Turns[i].OptionDisplayOrder, "turn " + i);
                CollectionAssert.AreEqual(OptionOrder.For(7, i), log.Turns[i].OptionDisplayOrder);
                Assert.AreEqual(2, log.Turns[i].PlayerChoice);
            }
            Assert.IsNull(log.Turns[log.Turns.Count - 1].OptionDisplayOrder, "the closing line has no options");
        }

        [Test]
        public void TappingASlotAnswersWithTheOptionShownThere()
        {
            CallSession call = NewCall(seed: 99);
            var pump = new CoroutinePump(call.Run());
            call.Accept();
            pump.RunUntilIdle();

            int[] order = (int[])call.DisplayOrder.Clone();
            List<string> options = call.CurrentLine.PlayerOptions;
            PlayerReply reply = null;
            call.PlayerReplied += r => reply = r;
            call.ChooseDisplayed(0);

            Assert.AreEqual(order[0], reply.OptionIndex);
            Assert.AreEqual((Stance)order[0], reply.Stance);
            Assert.AreEqual(options[order[0]], reply.Text);
            Assert.AreEqual(order[0], call.Logger.Latest.Turns[0].PlayerChoice);
        }

        [Test]
        public void HangingUpMidCallIsHungUp()
        {
            CallSession call = NewCall();
            var pump = new CoroutinePump(call.Run());
            call.Accept();
            pump.RunUntilIdle();
            Choose(call, Stance.Hesitate);
            pump.RunUntilIdle();

            call.LineShown();
            call.HangUp();
            pump.RunUntilIdle();
            Assert.IsTrue(pump.Finished);
            SessionLog log = call.Logger.Latest;
            Assert.AreEqual(EndReasons.HungUp, log.EndReason);
            Assert.AreEqual(EndedBy.PlayerHangUp, log.EndedBy);
            Assert.AreEqual("hang_up", log.Turns[1].PlayerChoice);
            Assert.IsTrue(log.Turns[1].LineShown);
            Assert.IsNotEmpty(log.Lessons);
        }

        [Test]
        public void HangingUpBeforeTheLineAppearsIsNotAReplyToIt()
        {
            CallSession call = NewCall();
            var pump = new CoroutinePump(call.Run());
            call.Accept();
            pump.RunUntilIdle();
            Assert.AreEqual(CallState.AwaitingPlayer, call.State, "the line has arrived but is still 'being typed'");

            call.HangUp();
            pump.RunUntilIdle();
            SessionLog log = call.Logger.Latest;
            Assert.AreEqual(1, log.Turns.Count);
            Assert.IsFalse(log.Turns[0].LineShown);
            Assert.IsNull(log.Turns[0].PlayerChoice);
            Assert.IsNull(log.Turns[0].RedFlagsNoticed);
            Assert.AreEqual(0, log.RedFlagsTotal, "flags the player never saw are not counted");
            Assert.AreEqual(EndReasons.HungUp, log.EndReason);
        }

        [Test]
        public void DecliningLogsAnEmptyCall()
        {
            CallSession call = NewCall();
            var pump = new CoroutinePump(call.Run());
            pump.RunUntilIdle();
            call.Decline();
            pump.RunUntilIdle();

            Assert.IsTrue(call.IsConcluded);
            SessionLog log = JObjectLog(call);
            Assert.AreEqual(0, log.TotalTurns);
            Assert.AreEqual(EndedBy.PlayerDeclined, log.EndedBy);
            Assert.AreEqual(EndReasons.HungUp, log.EndReason);
        }

        [Test]
        public void TheLogFileIsRewrittenAfterEveryStep()
        {
            CallSession call = NewCall();
            var pump = new CoroutinePump(call.Run());
            pump.RunUntilIdle();
            Assert.IsTrue(File.Exists(call.Logger.FilePath), "written while ringing");

            call.Accept();
            pump.RunUntilIdle();
            Assert.AreEqual(1, ((JArray)ReadFile(call)["turns"]).Count);
            Assert.AreEqual(JTokenType.Null, ReadFile(call)["turns"][0]["player_choice"].Type);

            Choose(call, Stance.Hesitate);
            Assert.AreEqual(1, (int)ReadFile(call)["turns"][0]["player_choice"], "saved before the scammer answers");
            Assert.IsNull(call.LastSaveError);
        }

        [Test]
        public void TypedNumbersNeverReachTheLog()
        {
            CallSession call = NewCall();
            var pump = new CoroutinePump(call.Run());
            call.Accept();
            pump.RunUntilIdle();
            call.SubmitFreeText("  เลขบัตรผม 1-2345-67890-12-3 เบอร์ 081 234 5678  ");
            pump.RunUntilIdle();

            string file = File.ReadAllText(call.Logger.FilePath);
            StringAssert.DoesNotContain("67890", file);
            StringAssert.DoesNotContain("5678", file);
            Assert.AreEqual("เลขบัตรผม [REDACTED] เบอร์ [REDACTED]", call.Logger.Latest.Turns[0].PlayerText);
            Assert.AreEqual("free", call.Logger.Latest.Turns[0].PlayerChoice);
            Assert.IsTrue(call.Manager.State.Goals.IdNumber);
        }

        [Test]
        public void BlankTextIsIgnored()
        {
            CallSession call = NewCall();
            var pump = new CoroutinePump(call.Run());
            call.Accept();
            pump.RunUntilIdle();
            call.SubmitFreeText("   ");
            Assert.AreEqual(CallState.AwaitingPlayer, call.State);
        }

        [Test]
        public void ResponseTimeStartsWhenTheLineIsShown()
        {
            CallSession call = NewCall();
            var pump = new CoroutinePump(call.Run());
            _now = 1000;
            call.Accept();
            pump.RunUntilIdle();
            _now = 1500;
            call.LineShown();
            _now = 4000;
            Choose(call, Stance.Resist);
            Assert.AreEqual(2500, call.Logger.Latest.Turns[0].ResponseTimeMs);
        }

        [Test]
        public void ATimeoutDropsTheCallUntilRetry()
        {
            var fake = new FakeProvider();
            fake.Results.Enqueue(_ => new ProviderResult { Success = false, TimedOut = true });
            fake.Results.Enqueue(req => new ProviderResult { Success = true, Turn = Line("สวัสดีครับ"), Source = TurnSources.Llm });
            CallSession call = NewCall(fake);
            var pump = new CoroutinePump(call.Run());
            call.Accept();
            pump.RunUntilIdle();
            Assert.AreEqual(CallState.Dropped, call.State);
            Assert.AreEqual(0, call.Manager.TurnCount);

            call.Retry();
            pump.RunUntilIdle();
            Assert.AreEqual(CallState.AwaitingPlayer, call.State);
            Assert.AreEqual(TurnSources.Llm, call.Logger.Latest.Turns[0].Source);
        }

        [Test]
        public void AFailedOrBrokenProviderFallsBackToAScriptedLine()
        {
            var fake = new FakeProvider();
            fake.Results.Enqueue(_ => new ProviderResult { Success = false, Error = "bad json" });
            fake.Results.Enqueue(_ => { throw new InvalidOperationException("boom"); });
            fake.Results.Enqueue(_ => new ProviderResult { Success = true, Turn = null });
            fake.Results.Enqueue(_ =>
            {
                ScammerTurn four = Line("สี่ตัวเลือก");
                four.PlayerOptions.Add("ตัวเลือกที่สี่");
                return new ProviderResult { Success = true, Turn = four };
            });
            fake.Results.Enqueue(_ =>
            {
                ScammerTurn two = Line("สองตัวเลือก");
                two.PlayerOptions.RemoveAt(2);
                return new ProviderResult { Success = true, Turn = two };
            });
            CallSession call = NewCall(fake);
            var pump = new CoroutinePump(call.Run());
            call.Accept();

            for (int i = 0; i < 5; i++)
            {
                pump.RunUntilIdle();
                Assert.AreEqual(CallState.AwaitingPlayer, call.State, "turn " + i);
                Assert.AreEqual(TurnSources.Fallback, call.Logger.Latest.Turns[i].Source);
                Assert.AreEqual(3, call.DisplayOrder.Length);
                Choose(call, Stance.Hesitate);
            }
        }

        [Test]
        public void HangingUpWhileTheScammerIsTypingAbandonsTheRequest()
        {
            var fake = new FakeProvider { Hold = true };
            fake.Results.Enqueue(_ => new ProviderResult { Success = true, Turn = Line("มาช้า") });
            CallSession call = NewCall(fake);
            var pump = new CoroutinePump(call.Run());
            call.Accept();
            pump.RunUntilIdle();
            pump.RunUntilIdle();
            Assert.AreEqual(CallState.WaitingForScammer, call.State);

            call.HangUp();
            Assert.DoesNotThrow(() => pump.RunUntilIdle());
            Assert.IsTrue(pump.Finished);
            Assert.AreEqual(0, call.Manager.TurnCount);
            Assert.AreEqual(1, fake.Results.Count, "the late answer was never applied");
            Assert.AreEqual(EndReasons.HungUp, call.Logger.Latest.EndReason);
            Assert.IsTrue(fake.Disposed, "the provider's coroutine was disposed, so it can abort a web request");
        }

        [Test]
        public void AnAnswerThatArrivesAfterHangingUpIsDropped()
        {
            var fake = new FakeProvider { Hold = true };
            fake.Results.Enqueue(_ => new ProviderResult { Success = true, Turn = Line("มาช้า") });
            CallSession call = NewCall(fake);
            var pump = new CoroutinePump(call.Run());
            call.Accept();
            pump.RunUntilIdle();

            // The answer lands in the same frame as the hang up.
            fake.Hold = false;
            call.HangUp();
            Assert.DoesNotThrow(() => pump.RunUntilIdle());
            Assert.AreEqual(0, call.Manager.TurnCount);
            Assert.AreEqual(CallState.Ended, call.State);
        }

        [Test]
        public void LessonsThatNeverArriveFallBackAfterTheTimeout()
        {
            var fake = new FakeProvider { HoldLessons = true };
            CallSession call = NewCall(fake);
            var pump = new CoroutinePump(call.Run());
            pump.RunUntilIdle();
            call.Decline();
            pump.RunUntilIdle();
            Assert.IsFalse(call.IsConcluded, "still waiting for the provider");

            _now += CallSession.LessonTimeoutMs + 1;
            pump.RunUntilIdle();
            Assert.IsTrue(call.IsConcluded);
            Assert.IsTrue(pump.Finished);
            Assert.IsNotEmpty(call.Lessons);
            Assert.IsTrue(fake.LessonsDisposed);
        }

        static ScammerTurn Line(string text)
        {
            return new ScammerTurn
            {
                ScammerLine = text,
                PressureLevel = 2,
                PlayerOptions = new List<string> { "ได้", "ไม่แน่ใจ", "ไม่ทำ" }
            };
        }

        static JObject ReadFile(CallSession call)
        {
            return JObject.Parse(File.ReadAllText(call.Logger.FilePath));
        }

        static SessionLog JObjectLog(CallSession call)
        {
            return ReadFile(call).ToObject<SessionLog>();
        }
    }

    /// <summary>Provider whose answers are scripted per call. Hold = true keeps it "typing".</summary>
    class FakeProvider : ILLMProvider
    {
        public readonly Queue<Func<ProviderRequest, ProviderResult>> Results = new Queue<Func<ProviderRequest, ProviderResult>>();
        public bool Hold;
        public bool Disposed;
        public bool HoldLessons;
        public bool LessonsDisposed;

        public string ProviderId { get { return "fake"; } }
        public string ModelId { get { return "fake-model"; } }
        public string PromptVersion { get { return "fake-1"; } }
        public double? Temperature { get { return 0.7; } }
        public string ScenarioId { get { return "police_cyber_v1"; } }

        public IEnumerator NextTurn(ProviderRequest request, Action<ProviderResult> onDone)
        {
            try
            {
                while (Hold) yield return null;
                onDone(Results.Dequeue()(request));
            }
            finally
            {
                Disposed = true;
            }
        }

        public IEnumerator GenerateLessons(SessionLog log, Action<List<string>> onDone)
        {
            try
            {
                while (HoldLessons) yield return null;
                onDone(null);
            }
            finally
            {
                LessonsDisposed = true;
            }
        }
    }

    /// <summary>
    /// Steps a coroutine (with nested IEnumerators) the way Unity would, stopping at each
    /// "yield return null" so a test can act between frames.
    /// </summary>
    class CoroutinePump
    {
        readonly Stack<IEnumerator> _stack = new Stack<IEnumerator>();

        public CoroutinePump(IEnumerator root)
        {
            _stack.Push(root);
        }

        public bool Finished
        {
            get { return _stack.Count == 0; }
        }

        public void RunUntilIdle(int maxSteps = 10000)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                if (_stack.Count == 0) return;
                IEnumerator top = _stack.Peek();
                if (!top.MoveNext())
                {
                    _stack.Pop();
                    continue;
                }

                var nested = top.Current as IEnumerator;
                if (nested != null)
                {
                    _stack.Push(nested);
                    continue;
                }
                return;
            }
            Assert.Fail("the coroutine never went idle");
        }
    }
}
