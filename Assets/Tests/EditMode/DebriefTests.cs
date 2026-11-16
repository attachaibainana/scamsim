using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using ScamSim.Core;
using ScamSim.LLM;
using ScamSim.Logging;

namespace ScamSim.Tests
{
    public class DebriefSummaryTests
    {
        [Test]
        public void ComplyingLosesEverythingAndMissesEveryFlag()
        {
            SessionLog log = TestSessions.Play(Stance.Comply, Stance.Comply);
            DebriefSummary summary = DebriefSummary.From(log);

            CollectionAssert.AreEqual(new[] { "id_number", "transfer" }, summary.Lost);
            Assert.AreEqual(log.Turns.Count, summary.Items.Count);
            Assert.AreEqual(0, summary.FlagsNoticed);
            Assert.Greater(summary.FlagsAnswerable, 0);

            foreach (DebriefFlag flag in summary.Items[0].Flags) Assert.AreEqual(FlagStatus.Missed, flag.Status);
            DebriefItem last = summary.Items[summary.Items.Count - 1];
            Assert.IsNull(last.PlayerText, "the call ended on the last line");
            foreach (DebriefFlag flag in last.Flags) Assert.AreEqual(FlagStatus.CallEnded, flag.Status);
        }

        [Test]
        public void ResistingCatchesEveryAnsweredFlag()
        {
            SessionLog log = TestSessions.Play(Stance.Resist, Stance.Resist, Stance.Resist);
            DebriefSummary summary = DebriefSummary.From(log);

            Assert.IsEmpty(summary.Lost);
            Assert.AreEqual(summary.FlagsAnswerable, summary.FlagsNoticed);
            Assert.AreEqual(log.RedFlagsPlayerReactedTo, summary.FlagsNoticed);
            Assert.IsNotNull(summary.Items[0].PlayerText);
        }

        [Test]
        public void HangingUpIsShownAsAReply()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            ConversationManager manager = TestSessions.Run(provider, Stance.Hesitate);
            manager.HangUp(800);
            DebriefSummary summary = DebriefSummary.From(SessionLogger.Build(TestSessions.Meta(provider), manager, null, null));

            DebriefItem last = summary.Items[summary.Items.Count - 1];
            Assert.IsTrue(last.PlayerHungUp);
            foreach (DebriefFlag flag in last.Flags) Assert.AreEqual(FlagStatus.Noticed, flag.Status);
        }

        [Test]
        public void ALineThePlayerNeverSawIsLeftOut()
        {
            ScriptedProvider provider = TestSessions.LoadProvider();
            ConversationManager manager = TestSessions.Run(provider, Stance.Hesitate);
            manager.HangUp(0, false);
            DebriefSummary summary = DebriefSummary.From(SessionLogger.Build(TestSessions.Meta(provider), manager, null, null));

            Assert.AreEqual(1, summary.Items.Count);
            Assert.AreEqual(0, summary.Items[0].TurnIndex);
        }

        [Test]
        public void EmptyOrMissingLogsDoNotThrow()
        {
            Assert.AreEqual(0, DebriefSummary.From(null).Items.Count);
            var manager = new ConversationManager();
            manager.DeclineCall();
            ScriptedProvider provider = TestSessions.LoadProvider();
            DebriefSummary summary = DebriefSummary.From(SessionLogger.Build(TestSessions.Meta(provider), manager, null, null));
            Assert.AreEqual(0, summary.FlagsAnswerable);
            Assert.IsEmpty(summary.Lost);
        }

        // Cutting at index 6 or 7 would separate ี and ่ from the letter they sit on, so the whole
        // syllable is dropped instead.
        [TestCase("สั้นๆ", 90, "สั้นๆ")]
        [TestCase("กกกกกที่", 6, "กกกกก…")]
        [TestCase("กกกกกกี่", 7, "กกกกก…")]
        [TestCase("ab cd efghij", 7, "ab cd…")]
        [TestCase("กกกกกเงิน", 6, "กกกกก…")]
        [TestCase("กกกกกทำ", 6, "กกกกก…")]
        public void ShortenNeverSplitsAThaiMark(string text, int max, string expected)
        {
            Assert.AreEqual(expected, DebriefSummary.Shorten(text, max));
        }

        [Test]
        public void ShortenedScenarioLinesStayShortAndWhole()
        {
            ScriptedScenario scenario = TestSessions.LoadProvider().Scenario;
            foreach (ScriptedNode node in scenario.Nodes.Values)
            {
                string shortLine = DebriefSummary.Shorten(node.ScammerLine, DebriefSummary.ShortLineLength);
                Assert.LessOrEqual(shortLine.Length, DebriefSummary.ShortLineLength + 1, node.ScammerLine);
                if (!shortLine.EndsWith("…")) continue;

                string kept = shortLine.Substring(0, shortLine.Length - 1);
                Assert.IsTrue(node.ScammerLine.StartsWith(kept), node.ScammerLine);
                char next = node.ScammerLine[kept.Length];
                Assert.AreNotEqual(UnicodeCategory.NonSpacingMark, CharUnicodeInfo.GetUnicodeCategory(next),
                    "a mark was cut off its letter: " + shortLine);
            }
        }
    }

    public class CsvExportTests
    {
        [Test]
        public void WritesTheCsvNextToTheJsonLog()
        {
            string directory = TestSessions.TempDirectory();
            try
            {
                SessionLog log = TestSessions.Play(Stance.Resist);
                string json = Path.Combine(directory, "session_x.json");
                string csv;
                string detail;
                Assert.AreEqual(CsvExportError.None, CsvExport.TrySave(log, json, out csv, out detail), detail);
                Assert.AreEqual(Path.Combine(directory, "session_x.csv"), csv);

                List<List<string>> rows = TestSessions.ParseCsv(File.ReadAllText(csv).TrimStart('﻿'));
                Assert.AreEqual(log.Turns.Count + 1, rows.Count);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ReportsWhyItCouldNotSave()
        {
            string csv;
            string detail;
            Assert.AreEqual(CsvExportError.NoFolder, CsvExport.TrySave(TestSessions.Play(), null, out csv, out detail));
            Assert.AreEqual(CsvExportError.NoLog, CsvExport.TrySave(null, "a.json", out csv, out detail));

            string blocked = TestSessions.TempDirectory();
            try
            {
                // A folder where the CSV file should be makes the write fail.
                Directory.CreateDirectory(Path.Combine(blocked, "session_x.csv"));
                Assert.AreEqual(CsvExportError.WriteFailed,
                    CsvExport.TrySave(TestSessions.Play(), Path.Combine(blocked, "session_x.json"), out csv, out detail));
                Assert.IsNotNull(detail);
            }
            finally
            {
                if (Directory.Exists(blocked)) Directory.Delete(blocked, true);
            }
        }
    }
}
