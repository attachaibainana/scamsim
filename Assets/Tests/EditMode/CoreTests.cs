using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ScamSim.Core;
using ScamSim.LLM;
using ScamSim.Logging;
using UnityEngine;

namespace ScamSim.Tests
{
    public class PIIMaskerTests
    {
        [TestCase("เลขบัตร 1234567890123 ครับ")]
        [TestCase("เลขบัตร 1-2345-67890-12-3 ครับ")]
        [TestCase("เลขบัตร 1 2345 67890 12 3 ครับ")]
        [TestCase("เลขบัตร ๑๒๓๔๕๖๗๘๙๐๑๒๓ ครับ")]
        [TestCase("เลขบัตร 1 - 2345 - 67890 - 12 - 3 ครับ")]
        [TestCase("เลขบัตร 1–2345–67890–12–3 ครับ")]
        [TestCase("เลขบัตร 1/2345/67890/12/3 ครับ")]
        [TestCase("เลขบัตร 1  2345  67890  12  3 ครับ")]
        public void MasksThirteenDigitId(string input)
        {
            MaskResult result = PIIMasker.Mask(input);
            Assert.AreEqual("เลขบัตร [REDACTED] ครับ", result.Text);
            Assert.AreEqual(1, result.IdNumbers);
            Assert.IsTrue(result.AnyRedacted);
        }

        [TestCase("โทร 0812345678 นะ")]
        [TestCase("โทร 081-234-5678 นะ")]
        [TestCase("โทร 081 234 5678 นะ")]
        [TestCase("โทร +66812345678 นะ")]
        [TestCase("โทร 02-123-4567 นะ")]
        [TestCase("โทร 081  234  5678 นะ")]
        [TestCase("โทร (02) 123-4567 นะ")]
        [TestCase("โทร 081/234/5678 นะ")]
        [TestCase("โทร 081–234–5678 นะ")]
        [TestCase("โทร 081 - 234 - 5678 นะ")]
        [TestCase("โทร 081.234.5678 นะ")]
        [TestCase("โทร +66 812345678 นะ")]
        [TestCase("โทร (+66) 81 234 5678 นะ")]
        public void MasksPhoneNumbers(string input)
        {
            MaskResult result = PIIMasker.Mask(input);
            Assert.AreEqual("โทร [REDACTED] นะ", result.Text);
            Assert.AreEqual(1, result.PhoneNumbers);
            Assert.AreEqual(0, result.IdNumbers + result.OtherNumbers);
        }

        [Test]
        public void MasksLongAccountNumbers()
        {
            MaskResult result = PIIMasker.Mask("บัญชี 123-4-56789-0 ธนาคาร");
            Assert.AreEqual("บัญชี [REDACTED] ธนาคาร", result.Text);
            Assert.AreEqual(1, result.OtherNumbers);
        }

        [TestCase("ฉันจะโทร 1441")]
        [TestCase("โอน 5,000 บาท ภายใน 30 นาที")]
        [TestCase("ไม่มีตัวเลขเลย")]
        [TestCase("")]
        [TestCase("นัดวันที่ 16-11-2026 14.30 น.")]
        [TestCase("16-11-2026 14.30")]
        [TestCase("วันที่ 16/11/2569 เวลา 9.05")]
        [TestCase("2026-11-16 14.30")]
        public void LeavesShortNumbersAndDatesAlone(string input)
        {
            MaskResult result = PIIMasker.Mask(input);
            Assert.AreEqual(input, result.Text);
            Assert.IsFalse(result.AnyRedacted);
        }

        [Test]
        public void MasksSeveralNumbersInOneMessage()
        {
            MaskResult result = PIIMasker.Mask("บัตร 1234567890123 เบอร์ 0812345678");
            Assert.AreEqual("บัตร [REDACTED] เบอร์ [REDACTED]", result.Text);
            Assert.AreEqual(1, result.IdNumbers);
            Assert.AreEqual(1, result.PhoneNumbers);
        }

        [Test]
        public void CountsTwoNumbersSeparatedOnlyByASpace()
        {
            MaskResult result = PIIMasker.Mask("1234567890123 0812345678");
            Assert.AreEqual("[REDACTED] [REDACTED]", result.Text);
            Assert.AreEqual(1, result.IdNumbers);
            Assert.AreEqual(1, result.PhoneNumbers);
            Assert.AreEqual(0, result.OtherNumbers);
        }

        [Test]
        public void ADateNextToANumberIsKeptAndTheNumberIsStillMasked()
        {
            MaskResult result = PIIMasker.Mask("16-11-2026 081 234 5678");
            Assert.AreEqual("16-11-2026 [REDACTED]", result.Text);
            Assert.AreEqual(1, result.PhoneNumbers);
        }

        [Test]
        public void KeepsBracketsBalanced()
        {
            Assert.AreEqual("([REDACTED])", PIIMasker.Mask("(0812345678)").Text);
            Assert.AreEqual("โอน 500 ([REDACTED])", PIIMasker.Mask("โอน 500 (081 234 5678)").Text);
        }
    }

    public class StanceClassifierTests
    {
        [TestCase("ไม่โอนครับ ผมจะไปแจ้งความ", Stance.Resist)]
        [TestCase("คุณเป็นใคร ทำไมต้องโอน", Stance.Resist)]
        [TestCase("ได้ครับ ตกลง", Stance.Comply)]
        [TestCase("เลขบัตร [REDACTED]", Stance.Comply)]
        [TestCase("[REDACTED]", Stance.Comply)]
        [TestCase("เอ่อ ขอคิดก่อนนะ", Stance.Hesitate)]
        [TestCase("", Stance.Hesitate)]
        [TestCase("ผมจะโทร [REDACTED] หาลูกก่อน ไม่โอนแน่นอน", Stance.Resist)]
        [TestCase("ไม่มีปัญหาครับ โอนให้เลย", Stance.Comply)]
        [TestCase("ไม่เป็นไรครับ ผมยินดีทำตาม", Stance.Comply)]
        [TestCase("แต่ฉันไม่ได้มีเงินเยอะขนาดนั้น", Stance.Hesitate)]
        [TestCase("ไม่ได้ครับ", Stance.Resist)]
        [TestCase("ไม่ยอมหรอก", Stance.Resist)]
        public void ClassifiesFreeText(string text, Stance expected)
        {
            Assert.AreEqual(expected, StanceClassifier.Classify(text));
        }

        [Test]
        public void AnIdNumberInsideASentenceCountsTowardsComply()
        {
            Assert.AreEqual(Stance.Comply, StanceClassifier.Classify("นี่ครับ [REDACTED]", true));
            Assert.AreEqual(Stance.Hesitate, StanceClassifier.Classify("นี่ครับ [REDACTED]", false));
        }

        [Test]
        public void AnIdNumberGivenWhileRefusingIsNotComply()
        {
            const string text = "ไม่โอน แต่เลขบัตรผมคือ [REDACTED] ไปเช็กเองได้เลย";
            Assert.AreNotEqual(Stance.Comply, StanceClassifier.Classify(text, true));
        }

        /// <summary>
        /// The scenario's own option texts are the best labelled Thai sample we have. Typing a comply
        /// or resist option by hand must give the same stance as tapping it; a hesitate option may
        /// read as resist (many are questions) but must never read as going along.
        /// </summary>
        [Test]
        public void AgreesWithTheScenarioOptionLabels()
        {
            ScriptedScenario scenario = TestSessions.LoadProvider().Scenario;
            var texts = new List<KeyValuePair<Stance, string>>();
            foreach (ScriptedNode node in scenario.Nodes.Values)
            {
                for (int i = 0; i < node.Options.Count; i++)
                    texts.Add(new KeyValuePair<Stance, string>((Stance)i, node.Options[i].Text));
            }
            foreach (FallbackLine line in scenario.Fallback)
            {
                for (int i = 0; i < line.PlayerOptions.Count; i++)
                    texts.Add(new KeyValuePair<Stance, string>((Stance)i, line.PlayerOptions[i]));
            }

            Assert.Greater(texts.Count, 30);
            foreach (KeyValuePair<Stance, string> pair in texts)
            {
                Stance actual = StanceClassifier.Classify(pair.Value);
                if (pair.Key == Stance.Hesitate) Assert.AreNotEqual(Stance.Comply, actual, pair.Value);
                else Assert.AreEqual(pair.Key, actual, pair.Value);
            }
        }
    }

    public class PlayerReplyTests
    {
        [Test]
        public void FreeTextIsMaskedClassifiedAndFlaggedTogether()
        {
            PlayerReply reply = PlayerReply.FromFreeText("ผมจะโทร 0812345678 หาลูกก่อน ไม่โอนแน่นอน", 2100);
            Assert.AreEqual("ผมจะโทร [REDACTED] หาลูกก่อน ไม่โอนแน่นอน", reply.Text);
            Assert.AreEqual(-1, reply.OptionIndex);
            Assert.AreEqual(Stance.Resist, reply.Stance);
            Assert.AreEqual(StanceSources.Heuristic, reply.StanceSource);
            Assert.IsTrue(reply.PiiRedacted);
            Assert.IsFalse(reply.GaveIdNumber);
            Assert.AreEqual(2100, reply.ResponseTimeMs);
        }

        [Test]
        public void TypingAnIdNumberIsFlagged()
        {
            PlayerReply reply = PlayerReply.FromFreeText("1-2345-67890-12-3", 0);
            Assert.AreEqual("[REDACTED]", reply.Text);
            Assert.IsTrue(reply.GaveIdNumber);
            Assert.AreEqual(Stance.Comply, reply.Stance);
        }

        [Test]
        public void OptionAndHangUpRepliesCarryTheirSource()
        {
            PlayerReply option = PlayerReply.FromOption(Stance.Resist, "ไม่โอน", 500);
            Assert.AreEqual(2, option.OptionIndex);
            Assert.AreEqual(StanceSources.Option, option.StanceSource);
            Assert.IsFalse(option.PiiRedacted);

            PlayerReply hangUp = PlayerReply.HangUp(700);
            Assert.IsTrue(hangUp.HungUp);
            Assert.AreEqual(-1, hangUp.OptionIndex);
            Assert.AreEqual(StanceSources.HangUp, hangUp.StanceSource);
        }
    }

    public class CsvExporterTests
    {
        [Test]
        public void EscapesSpecialCharacters()
        {
            Assert.AreEqual("plain", CsvExporter.Escape("plain"));
            Assert.AreEqual("\"a,b\"", CsvExporter.Escape("a,b"));
            Assert.AreEqual("\"say \"\"hi\"\"\"", CsvExporter.Escape("say \"hi\""));
            Assert.AreEqual("\"line1\nline2\"", CsvExporter.Escape("line1\nline2"));
            Assert.AreEqual("", CsvExporter.Escape(null));
        }

        [TestCase("=1+1", "'=1+1")]
        [TestCase("+66", "'+66")]
        [TestCase("-5", "'-5")]
        [TestCase("@SUM(A1)", "'@SUM(A1)")]
        [TestCase("=HYPERLINK(\"http://x\",\"y\")", "\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\"")]
        [TestCase("a=b", "a=b")]
        public void TextThatLooksLikeAFormulaIsNeutralised(string field, string expected)
        {
            Assert.AreEqual(expected, CsvExporter.Escape(field));
        }

        [Test]
        public void WritesOneRowPerTurnWithBom()
        {
            SessionLog log = TestSessions.Play(Stance.Comply, Stance.Comply);
            List<List<string>> rows = TestSessions.ParseCsv(CsvExporter.ToCsv(log));
            Assert.AreEqual(log.Turns.Count + 1, rows.Count);
            CollectionAssert.AreEqual(CsvExporter.Columns, rows[0]);
            foreach (List<string> row in rows) Assert.AreEqual(CsvExporter.Columns.Length, row.Count);

            int scammerLine = Array.IndexOf(CsvExporter.Columns, "scammer_line");
            int transfer = Array.IndexOf(CsvExporter.Columns, "goal_transfer");
            Assert.AreEqual(log.Turns[0].ScammerLine, rows[1][scammerLine]);
            Assert.AreEqual("true", rows[rows.Count - 1][transfer]);

            byte[] bytes = CsvExporter.ToBytes(log);
            Assert.AreEqual(0xEF, bytes[0]);
            Assert.AreEqual(0xBB, bytes[1]);
            Assert.AreEqual(0xBF, bytes[2]);
        }

        [Test]
        public void IncludesAppVersionAndShuffleSeed()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            SessionMeta meta = TestSessions.Meta(provider);
            meta.OptionShuffleSeed = 4242;
            ConversationManager manager = TestSessions.Run(provider, Stance.Resist);
            List<List<string>> rows = TestSessions.ParseCsv(CsvExporter.ToCsv(SessionLogger.Build(meta, manager, null, null)));

            Assert.AreEqual("test", rows[1][Array.IndexOf(CsvExporter.Columns, "app_version")]);
            Assert.AreEqual("4242", rows[1][Array.IndexOf(CsvExporter.Columns, "option_shuffle_seed")]);
        }

        [Test]
        public void ASessionWithNoTurnsStillGetsARow()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            var manager = new ConversationManager();
            manager.DeclineCall();
            SessionLog log = SessionLogger.Build(TestSessions.Meta(provider), manager, DateTime.UtcNow, null);
            List<List<string>> rows = TestSessions.ParseCsv(CsvExporter.ToCsv(log));

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual(CsvExporter.Columns.Length, rows[1].Count);
            Assert.AreEqual("test-session", rows[1][Array.IndexOf(CsvExporter.Columns, "session_id")]);
            Assert.AreEqual(EndedBy.PlayerDeclined, rows[1][Array.IndexOf(CsvExporter.Columns, "ended_by")]);
            Assert.AreEqual("0", rows[1][Array.IndexOf(CsvExporter.Columns, "total_turns")]);
            Assert.AreEqual("", rows[1][Array.IndexOf(CsvExporter.Columns, "turn_index")]);
        }
    }

    public class ConversationManagerTests
    {
        [Test]
        public void PressureFollowsStance()
        {
            Assert.AreEqual(3, ConversationManager.NextPressure(2, Stance.Resist));
            Assert.AreEqual(1, ConversationManager.NextPressure(2, Stance.Comply));
            Assert.AreEqual(2, ConversationManager.NextPressure(2, Stance.Hesitate));
            Assert.AreEqual(5, ConversationManager.NextPressure(5, Stance.Resist));
            Assert.AreEqual(1, ConversationManager.NextPressure(1, Stance.Comply));
            Assert.AreEqual(2, ConversationManager.NextPressure(2, null));
        }

        [Test]
        public void EndsAtTurnLimitAsEscapedWhenNothingWasLost()
        {
            var manager = new ConversationManager();
            for (int i = 0; i < ConversationManager.MaxTurns; i++)
            {
                Assert.IsFalse(manager.IsOver);
                manager.ApplyScammerTurn(new ScammerTurn { ScammerLine = "x", PressureLevel = 9 }, TurnSources.Llm);
                Assert.AreEqual(ConversationManager.MaxPressure, manager.State.Pressure);
                if (!manager.IsOver)
                    manager.ApplyPlayerReply(PlayerReply.FromOption(Stance.Hesitate, "x", 0));
            }
            Assert.IsTrue(manager.IsOver);
            Assert.AreEqual(EndReasons.Escaped, manager.EndReason);
            Assert.AreEqual(EndedBy.TurnLimit, manager.EndedByWhat);
        }

        [Test]
        public void GoalsNeverRevertAndAnyLossMeansScammed()
        {
            var manager = new ConversationManager();
            manager.ApplyScammerTurn(new ScammerTurn { ScammerLine = "a", GoalProgress = new GoalProgress { IdNumber = true } }, TurnSources.Llm);
            manager.ApplyPlayerReply(PlayerReply.FromOption(Stance.Resist, "x", 0));
            manager.ApplyScammerTurn(new ScammerTurn { ScammerLine = "b", GoalProgress = new GoalProgress() }, TurnSources.Llm);
            Assert.IsTrue(manager.State.Goals.IdNumber);
            Assert.IsTrue(manager.Current.Scammer.GoalProgress.IdNumber);

            manager.HangUp(1200);
            Assert.AreEqual(EndReasons.Scammed, manager.EndReason);
            Assert.AreEqual(EndedBy.PlayerHangUp, manager.EndedByWhat);
        }

        [Test]
        public void TypingAnIdNumberCountsAsGivingItInThatTurn()
        {
            var manager = new ConversationManager();
            manager.ApplyScammerTurn(new ScammerTurn { ScammerLine = "a" }, TurnSources.Llm);
            manager.ApplyPlayerReply(PlayerReply.FromFreeText("1234567890123", 800));
            Assert.IsTrue(manager.State.Goals.IdNumber);
            Assert.IsTrue(manager.Current.Scammer.GoalProgress.IdNumber);
        }

        [Test]
        public void DecliningTheCallIsASafeHangUp()
        {
            var manager = new ConversationManager();
            manager.DeclineCall();
            Assert.IsTrue(manager.IsOver);
            Assert.AreEqual(EndReasons.HungUp, manager.EndReason);
            Assert.AreEqual(0, manager.TurnCount);
        }

        [Test]
        public void AcceptsATurnWithNullFields()
        {
            var manager = new ConversationManager();
            manager.ApplyScammerTurn(new ScammerTurn
            {
                ScammerLine = null, RedFlags = null, PlayerOptions = null, GoalProgress = null
            }, TurnSources.Llm);

            Assert.AreEqual("", manager.Current.Scammer.ScammerLine);
            Assert.IsNotNull(manager.Current.Scammer.RedFlags);
            Assert.IsNotNull(manager.Current.Scammer.PlayerOptions);
            Assert.IsNotNull(manager.Current.Scammer.GoalProgress);
        }
    }

    public class ScriptedProviderTests
    {
        [Test]
        public void ScenarioFileIsValid()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            CollectionAssert.IsEmpty(provider.Scenario.Validate());
        }

        [Test]
        public void EveryPathReachesAWrittenEnding()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            var endings = new HashSet<string>();
            Walk(provider, new List<Stance>(), endings);
            CollectionAssert.Contains(endings, EndReasons.Scammed);
            CollectionAssert.Contains(endings, EndReasons.Escaped);
        }

        static void Walk(ScriptedProvider provider, List<Stance> path, HashSet<string> endings)
        {
            string trail = string.Join(",", path);
            ConversationManager manager = TestSessions.Run(provider, path.ToArray());
            if (manager.IsOver)
            {
                // Ended by a terminal node of the script, not rescued by the turn limit or its closing line.
                Assert.AreEqual(EndedBy.Scammer, manager.EndedByWhat, trail);
                Assert.AreNotEqual(provider.Scenario.ClosingLine, manager.Current.Scammer.ScammerLine, trail);
                Assert.Less(manager.TurnCount, ConversationManager.MaxTurns, trail);

                // The last line the player reads must agree with the result of the call.
                string lastLine = manager.Current.Scammer.ScammerLine;
                foreach (ScriptedNode node in provider.Scenario.Nodes.Values)
                {
                    if (node.EndReason != EndReasons.Escaped) continue;
                    if (manager.EndReason == EndReasons.Scammed) Assert.AreNotEqual(node.ScammerLine, lastLine, trail);
                    else Assert.AreNotEqual(node.ScammerLineIfLost, lastLine, trail);
                }

                endings.Add(manager.EndReason);
                return;
            }

            foreach (Stance stance in new[] { Stance.Comply, Stance.Hesitate, Stance.Resist })
            {
                path.Add(stance);
                Walk(provider, path, endings);
                path.RemoveAt(path.Count - 1);
            }
        }

        [Test]
        public void AScenarioThatLoopsIsRejected()
        {
            JObject json = JObject.Parse(TestSessions.ScenarioJson());
            json["nodes"]["last_push"]["options"][1]["next"] = "isolate";
            var error = Assert.Throws<ArgumentException>(() => new ScriptedProvider(json.ToString()));
            StringAssert.Contains("loop", error.Message);
        }

        [Test]
        public void RefusingAfterGivingSomethingAwayEndsWithTheTooLateLine()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            ScriptedNode ending = provider.Scenario.Nodes["end_escaped"];

            // open -> id_given -> threat -> end_escaped: the ID number is already gone.
            ConversationManager late = TestSessions.Run(provider, Stance.Comply, Stance.Resist, Stance.Resist);
            Assert.AreEqual(EndReasons.Scammed, late.EndReason);
            Assert.AreEqual(ending.ScammerLineIfLost, late.Current.Scammer.ScammerLine);
            Assert.AreEqual(EndReasons.Scammed, late.Current.Scammer.EndReason);

            // open -> deflect -> link_clicked -> threat -> end_escaped: the link was clicked.
            ConversationManager clicked = TestSessions.Run(provider, Stance.Resist, Stance.Comply, Stance.Hesitate, Stance.Resist);
            Assert.AreEqual(EndReasons.Scammed, clicked.EndReason);
            Assert.AreEqual(ending.ScammerLineIfLost, clicked.Current.Scammer.ScammerLine);

            ConversationManager clean = TestSessions.Run(provider, Stance.Resist, Stance.Resist, Stance.Resist);
            Assert.AreEqual(EndReasons.Escaped, clean.EndReason);
            Assert.AreEqual(ending.ScammerLine, clean.Current.Scammer.ScammerLine);
        }

        [Test]
        public void AnIdNumberTypedOnAnEscapePathAlsoGetsTheTooLateLine()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            ConversationManager manager = TestSessions.Run(provider, Stance.Resist, Stance.Resist);
            manager.ApplyPlayerReply(PlayerReply.FromFreeText("ไม่เชื่อ ไม่โอน ผมจะไปแจ้งความ เลขผมคือ 1234567890123", 5000));
            Assert.AreEqual(Stance.Resist, manager.Current.Reply.Stance);
            manager.ApplyScammerTurn(provider.NextTurnSync(manager.BuildRequest()), TurnSources.Scripted);

            Assert.IsTrue(manager.IsOver);
            Assert.IsFalse(manager.State.Goals.Transfer);
            Assert.AreEqual(EndReasons.Scammed, manager.EndReason);
            Assert.AreEqual(provider.Scenario.Nodes["end_escaped"].ScammerLineIfLost, manager.Current.Scammer.ScammerLine);
        }

        [Test]
        public void AlwaysComplyingGetsScammed()
        {
            SessionLog log = TestSessions.Play(Stance.Comply, Stance.Comply);
            Assert.AreEqual(EndReasons.Scammed, log.EndReason);
            Assert.IsTrue(log.Turns[log.Turns.Count - 1].GoalProgress.Transfer);
            Assert.AreEqual(0, log.RedFlagsPlayerReactedTo);
        }

        [Test]
        public void AlwaysResistingEscapes()
        {
            SessionLog log = TestSessions.Play(Stance.Resist, Stance.Resist, Stance.Resist);
            Assert.AreEqual(EndReasons.Escaped, log.EndReason);
            Assert.Greater(log.RedFlagsPlayerReactedTo, 0);
        }

        [Test]
        public void ResistingRaisesPressureAndComplyingLowersIt()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            ConversationManager resisting = TestSessions.Run(provider, Stance.Resist, Stance.Resist);
            ConversationManager complying = TestSessions.Run(provider, Stance.Comply);
            Assert.Greater(resisting.State.Pressure, complying.State.Pressure);
            Assert.AreEqual(5, resisting.State.Pressure);
            Assert.AreEqual(1, complying.State.Pressure);
        }

        [Test]
        public void HangingUpBeforeLosingAnythingIsHungUp()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            ConversationManager manager = TestSessions.Run(provider, Stance.Hesitate);
            manager.HangUp(900);
            SessionLog log = SessionLogger.Build(TestSessions.Meta(provider), manager, DateTime.UtcNow, null);
            Assert.AreEqual(EndReasons.HungUp, log.EndReason);
            Assert.AreEqual("hang_up", log.Turns[log.Turns.Count - 1].PlayerChoice);
            Assert.AreEqual(true, log.Turns[log.Turns.Count - 1].RedFlagsNoticed);
        }

        [Test]
        public void ATypedRefusalThatMentionsAPhoneNumberIsNotATransfer()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            ConversationManager manager = TestSessions.Run(provider, Stance.Comply);
            manager.ApplyPlayerReply(PlayerReply.FromFreeText("ผมจะโทร 0812345678 หาลูกก่อน ไม่โอนแน่นอน", 3000));
            manager.ApplyScammerTurn(provider.NextTurnSync(manager.BuildRequest()), TurnSources.Scripted);

            Assert.IsFalse(manager.State.Goals.Transfer);
            Assert.IsFalse(manager.IsOver);
            Assert.AreEqual(provider.Scenario.Nodes["threat"].ScammerLine, manager.Current.Scammer.ScammerLine);
        }

        [Test]
        public void AnIdNumberTypedJustBeforeHangingUpIsLoggedAndTaught()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            ConversationManager manager = TestSessions.Run(provider);
            manager.ApplyPlayerReply(PlayerReply.FromFreeText("เลขบัตร 1234567890123", 4000));
            manager.HangUp(500);

            SessionLog log = SessionLogger.Build(TestSessions.Meta(provider), manager, DateTime.UtcNow, null);
            Assert.AreEqual(EndReasons.Scammed, log.EndReason);
            Assert.AreEqual(1, log.Turns.Count);
            Assert.IsTrue(log.Turns[0].GoalProgress.IdNumber);
            Assert.AreEqual("free", log.Turns[0].PlayerChoice);
            Assert.AreEqual(StanceSources.Heuristic, log.Turns[0].PlayerStanceSource);
            Assert.IsTrue(log.Turns[0].PiiRedacted);
            StringAssert.DoesNotContain("1234567890123", SessionLogger.ToJson(log));
            CollectionAssert.Contains(provider.BuildLessons(log), provider.Scenario.Lessons.IdNumber);
        }

        [Test]
        public void FallbackLinesExistForEveryPressure()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            for (int level = 1; level <= 5; level++)
            {
                ScammerTurn turn = provider.FallbackTurn(new ProviderRequest { State = new ConversationState(), TargetPressure = level });
                Assert.AreEqual(level, turn.PressureLevel);
                Assert.AreEqual(3, turn.PlayerOptions.Count);
                Assert.IsFalse(turn.CallShouldEnd);
            }
        }
    }

    public class SessionLoggerTests
    {
        [Test]
        public void LogContainsEveryRequiredField()
        {
            SessionLog log = TestSessions.Play(Stance.Hesitate, Stance.Resist, Stance.Resist);
            JObject json = JObject.Parse(SessionLogger.ToJson(log));

            foreach (string field in new[]
                     {
                         "session_id", "scenario_id", "start_time", "end_time", "end_reason", "total_turns",
                         "red_flags_total", "red_flags_player_reacted_to", "model_id", "prompt_version", "temperature"
                     })
            {
                Assert.IsNotNull(json.Property(field), "missing session field " + field);
            }

            var firstTurn = (JObject)json["turns"][0];
            foreach (string field in new[]
                     {
                         "turn_index", "timestamp", "scammer_line", "pressure_level", "red_flags", "player_options",
                         "player_choice", "player_text", "response_time_ms", "goal_progress"
                     })
            {
                Assert.IsNotNull(firstTurn.Property(field), "missing turn field " + field);
            }

            Assert.AreEqual(log.Turns.Count, (int)json["total_turns"]);
            Assert.AreEqual(1, (int)firstTurn["player_choice"]);
        }

        [Test]
        public void SaveWritesAFileAndRewritesItInPlace()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            string directory = TestSessions.TempDirectory();
            try
            {
                var logger = new SessionLogger(TestSessions.Meta(provider), directory);
                string error;
                logger.Save(TestSessions.Run(provider, Stance.Comply), null, null, out error);
                Assert.IsNull(error);
                Assert.IsNull((string)JObject.Parse(File.ReadAllText(logger.FilePath))["end_reason"]);

                logger.Save(TestSessions.Run(provider, Stance.Comply, Stance.Comply), DateTime.UtcNow, null, out error);
                Assert.IsNull(error);
                JObject json = JObject.Parse(File.ReadAllText(logger.FilePath));
                Assert.AreEqual(EndReasons.Scammed, (string)json["end_reason"]);
                Assert.AreEqual(1, Directory.GetFiles(directory).Length, "the temp file must not be left behind");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void SessionsStartedInTheSameSecondGetDifferentFiles()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            SessionMeta first = TestSessions.Meta(provider);
            SessionMeta second = TestSessions.Meta(provider);
            first.SessionId = "3f2c9a1e-0000-4000-8000-000000000001";
            second.SessionId = "3f2c9a1e-0000-4000-8000-000000000002";

            Assert.AreNotEqual(SessionLogger.FileName(first), SessionLogger.FileName(second));
            StringAssert.StartsWith("session_", SessionLogger.FileName(first));
            StringAssert.EndsWith("_3f2c9a1e-0000-4000-8000-000000000001.json", SessionLogger.FileName(first));

            first.SessionId = "../a:b\\c";
            StringAssert.EndsWith("_abc.json", SessionLogger.FileName(first));
        }

        [Test]
        public void SaveReportsAnErrorInsteadOfThrowing()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            var logger = new SessionLogger(TestSessions.Meta(provider), TestSessions.TempDirectory());
            string error = null;
            Assert.DoesNotThrow(() => logger.Save(null, null, null, out error));
            Assert.IsNotNull(error);
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(logger.FilePath)));
        }

        [Test]
        public void ATurnWhoseListsWereNulledStillSaves()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            ConversationManager manager = TestSessions.Run(provider, Stance.Hesitate);
            manager.Current.Scammer.RedFlags = null;
            manager.Current.Scammer.PlayerOptions = null;
            manager.Current.Scammer.GoalProgress = null;

            var logger = new SessionLogger(TestSessions.Meta(provider), null);
            string error;
            SessionLog log = logger.Save(manager, null, null, out error);
            Assert.IsNull(error);
            Assert.AreEqual(2, log.Turns.Count);
            Assert.AreEqual(0, log.Turns[1].RedFlags.Count);
        }
    }

    public class LocTests
    {
        [Test]
        public void ThaiTableLoadsAndMissingKeysAreVisible()
        {
            Loc.Load(File.ReadAllText(Path.Combine(Application.dataPath, "Localization/th.json")));
            Assert.IsTrue(Loc.Has("intro.title"));
            Assert.AreEqual("[no.such.key]", Loc.T("no.such.key"));
        }
    }

    static class TestSessions
    {
        public static string ScenarioJson()
        {
            return File.ReadAllText(Path.Combine(Application.dataPath, "Scenarios/police_scripted.json"));
        }

        public static ScriptedProvider LoadProvider()
        {
            return new ScriptedProvider(ScenarioJson());
        }

        public static string TempDirectory()
        {
            return Path.Combine(Path.GetTempPath(), "scamsim-tests-" + Guid.NewGuid().ToString("N"));
        }

        public static SessionMeta Meta(ScriptedProvider provider)
        {
            return new SessionMeta
            {
                SessionId = "test-session",
                ScenarioId = provider.ScenarioId,
                AppVersion = "test",
                Provider = provider.ProviderId,
                ModelId = provider.ModelId,
                PromptVersion = provider.PromptVersion,
                Temperature = provider.Temperature,
                StartUtc = new DateTime(2026, 11, 16, 7, 0, 0, DateTimeKind.Utc)
            };
        }

        /// <summary>Plays the scripted scenario answering with the given stances, then stops.</summary>
        public static ConversationManager Run(ScriptedProvider provider, params Stance[] stances)
        {
            var manager = new ConversationManager();
            manager.ApplyScammerTurn(provider.NextTurnSync(manager.BuildRequest()), TurnSources.Scripted);
            foreach (Stance stance in stances)
            {
                if (manager.IsOver) break;
                string text = manager.Current.Scammer.PlayerOptions[(int)stance];
                manager.ApplyPlayerReply(PlayerReply.FromOption(stance, text, 1500));
                manager.ApplyScammerTurn(provider.NextTurnSync(manager.BuildRequest()), TurnSources.Scripted);
            }
            return manager;
        }

        public static SessionLog Play(params Stance[] stances)
        {
            ScriptedProvider provider = LoadProvider();
            ConversationManager manager = Run(provider, stances);
            return SessionLogger.Build(Meta(provider), manager, DateTime.UtcNow, null);
        }

        /// <summary>Minimal RFC 4180 reader, so tests check real columns instead of splitting on commas.</summary>
        public static List<List<string>> ParseCsv(string csv)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < csv.Length; i++)
            {
                char c = csv[i];
                if (quoted)
                {
                    if (c != '"') field.Append(c);
                    else if (i + 1 < csv.Length && csv[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else quoted = false;
                }
                else if (c == '"') quoted = true;
                else if (c == ',')
                {
                    row.Add(field.ToString());
                    field.Length = 0;
                }
                else if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                {
                    row.Add(field.ToString());
                    field.Length = 0;
                    rows.Add(row);
                    row = new List<string>();
                    i++;
                }
                else field.Append(c);
            }

            return rows;
        }
    }
}
