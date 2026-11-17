using System;
using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using ScamSim.Core;
using ScamSim.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace ScamSim.Tests
{
    /// <summary>
    /// Plays the real Main scene through its UI the way a player would, and saves a screenshot of
    /// each screen to Logs/Screenshots so the layout can be checked without opening the Editor.
    /// </summary>
    public class SmokeTests
    {
        static readonly Regex MissingKey = new Regex(@"^\[[a-z0-9_.]+\]$");

        GameSession _session;
        RenderTexture _texture;

        VisualElement Root
        {
            get { return _session.Document.rootVisualElement; }
        }

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            yield return SceneManager.LoadSceneAsync("Main", LoadSceneMode.Single);
            _session = UnityEngine.Object.FindAnyObjectByType<GameSession>();
            Assert.IsNotNull(_session, "Main scene has no GameSession");
            // Never call the real API from tests, even when a developer has a key in config.json.
            _session.UseProvider(null);
            _session.MinTypingSeconds = 0f;
            _texture = UiCapture.Redirect(_session.Document);
            yield return null;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Unload()
        {
            if (_texture != null) _texture.Release();
            yield return null;
        }

        [UnityTest]
        public IEnumerator ResistingFromStartToFinishEscapes()
        {
            Assert.AreEqual(GameSession.GameScreen.Intro, _session.Screen);
            yield return Shot("01_intro");
            AssertAllTextIsLocalized();

            Click("start");
            yield return Frames(2);
            Assert.AreEqual(GameSession.GameScreen.Call, _session.Screen);
            Assert.AreEqual(CallState.Ringing, _session.Call.State);
            yield return Shot("02_ringing");
            AssertAllTextIsLocalized();

            Click("accept");
            yield return WaitForAnswer();
            Assert.AreEqual(3, _session.CallView.OptionButtons.Count);
            AssertCallerFaceMatchesPressure();
            yield return Shot("03_first_line");

            // A typed refusal, then the resist option every turn.
            Root.Q<TextField>("free-text").value = "คุณเป็นใคร ขอเบอร์หน่วยงาน เดี๋ยวผมโทรกลับเอง";
            Click("send");
            yield return WaitForAnswer();
            Assert.AreEqual("free", _session.Call.Logger.Latest.Turns[0].PlayerChoice);

            bool shotHighPressure = false;
            for (int turn = 0; turn < ConversationManager.MaxTurns && _session.Call.State == CallState.AwaitingPlayer; turn++)
            {
                if (!shotHighPressure && _session.Call.CurrentLine.PressureLevel >= 4)
                {
                    shotHighPressure = true;
                    AssertCallerFaceMatchesPressure();
                    Assert.AreNotEqual(CallScreen.ExprCalm, _session.CallView.CallerExpression);
                    yield return new WaitForSecondsRealtime(1f); // let the red tint finish its transition
                    yield return Shot("04_high_pressure");
                }
                ClickOption(Stance.Resist);
                yield return WaitForAnswer();
            }

            Assert.AreEqual(CallState.Ended, _session.Call.State);
            Assert.IsTrue(shotHighPressure, "the scammer never reached pressure 4");
            yield return WaitUntil(() => IsVisible("call-footer"), "the see-result button");
            yield return Shot("05_call_ended");

            Click("see-result");
            yield return WaitUntil(() => _session.Call.IsConcluded, "lessons");
            yield return Frames(2);
            Assert.AreEqual(GameSession.GameScreen.Debrief, _session.Screen);
            Assert.AreEqual(Loc.T("debrief.result.escaped"), Root.Q<Label>("result").text);
            Assert.AreEqual(3, Root.Q("lessons").childCount);
            Assert.AreEqual(DebriefSummary.From(_session.Call.Logger.Latest).Items.Count, Root.Q("timeline").childCount);
            Assert.Greater(Root.Query(className: "flag-chip").Class("noticed").ToList().Count, 0);
            Assert.AreEqual(0, Root.Query(className: "flag-chip").Class("missed").ToList().Count);
            StringAssert.Contains(_session.Call.Logger.FilePath, Root.Q<Label>("log-path").text);
            Assert.IsNull(_session.Call.LastSaveError);
            yield return Shot("06_debrief");
            AssertAllTextIsLocalized();

            Click("export-csv");
            yield return Frames(2);
            string csv = _session.DebriefView.ExportedCsvPath;
            Assert.IsNotNull(csv, Root.Q<Label>("export-status").text);
            Assert.IsTrue(System.IO.File.Exists(csv));
            StringAssert.Contains(csv, Root.Q<Label>("export-status").text);
            Assert.IsTrue(IsVisible("open-folder"));

            ScrollView scroll = Root.Q<ScrollView>("debrief-scroll");
            scroll.scrollOffset = new Vector2(0, scroll.contentContainer.layout.height);
            yield return Frames(2);
            yield return Shot("06b_debrief_timeline");

            Click("play-again");
            yield return Frames(2);
            Assert.AreEqual(GameSession.GameScreen.Call, _session.Screen);
            Assert.AreEqual(CallState.Ringing, _session.Call.State);
        }

        [UnityTest]
        public IEnumerator ComplyingGetsScammedAndDecliningIsSafe()
        {
            Click("start");
            yield return Frames(2);
            Click("accept");
            yield return WaitForAnswer();
            for (int turn = 0; turn < ConversationManager.MaxTurns && _session.Call.State == CallState.AwaitingPlayer; turn++)
            {
                ClickOption(Stance.Comply);
                yield return WaitForAnswer();
            }
            yield return WaitUntil(() => IsVisible("call-footer"), "the see-result button");
            Click("see-result");
            yield return WaitUntil(() => _session.Call.IsConcluded, "lessons");
            yield return Frames(2);
            Assert.AreEqual(Loc.T("debrief.result.scammed"), Root.Q<Label>("result").text);
            Assert.Greater(Root.Query(className: "flag-chip").Class("missed").ToList().Count, 0);
            StringAssert.Contains(Loc.T("debrief.lost.transfer"), Root.Q<Label>("lost").text);
            yield return Shot("07_debrief_scammed");

            Click("home");
            yield return Frames(2);
            Assert.AreEqual(GameSession.GameScreen.Intro, _session.Screen);
            Click("start");
            yield return Frames(2);
            Click("decline");
            yield return WaitUntil(() => _session.Screen == GameSession.GameScreen.Debrief, "debrief after declining");
            yield return WaitUntil(() => _session.Call.IsConcluded, "lessons");
            yield return Frames(2);
            Assert.AreEqual(Loc.T("debrief.result.hung_up"), Root.Q<Label>("result").text);
            Assert.AreEqual(0, _session.Call.Logger.Latest.TotalTurns);
        }

        [UnityTest]
        public IEnumerator NewsPageListsAndFiltersRealCases()
        {
            Click("news");
            yield return WaitUntil(() => _session.NewsView != null && _session.NewsView.CardCount > 0, "news cards");
            yield return Frames(2);
            Assert.AreEqual(GameSession.GameScreen.News, _session.Screen);
            var problems = new System.Collections.Generic.List<string>();
            ScamSim.News.NewsFeed feed = ScamSim.News.NewsFeedParser.Parse(
                System.IO.File.ReadAllText(ScamSim.News.NewsLoader.Location), problems);
            int callCenterCount = ScamSim.News.NewsFilter.ByType(feed.Items, "call_center").Count;
            Assert.AreEqual(feed.Items.Count, _session.NewsView.CardCount);
            foreach (Button practice in Root.Query<Button>("practice").ToList())
                Assert.IsFalse(practice.enabledSelf, "practising a case is not available in v0.1");
            AssertAllTextIsLocalized();
            yield return Shot("08_news");

            Click("filter-call_center");
            yield return Frames(2);
            Assert.AreEqual("call_center", _session.NewsView.ActiveType);
            Assert.AreEqual(callCenterCount, _session.NewsView.CardCount);
            yield return Shot("09_news_filtered");

            Click("filter-all");
            yield return Frames(1);
            Assert.AreEqual(feed.Items.Count, _session.NewsView.CardCount);

            Click("back");
            yield return Frames(2);
            Assert.AreEqual(GameSession.GameScreen.Intro, _session.Screen);
        }

        [UnityTest]
        public IEnumerator HangingUpMidCallShowsTheHangUpResult()
        {
            Click("start");
            yield return Frames(2);
            Click("accept");
            yield return WaitForAnswer();
            ClickOption(Stance.Hesitate);
            yield return WaitForAnswer();

            // The history panel lists every line so far and leaves the answers usable.
            Assert.IsFalse(_session.CallView.HistoryOpen);
            Click("history-toggle");
            yield return Frames(2);
            Assert.IsTrue(_session.CallView.HistoryOpen);
            Assert.AreEqual(3, Root.Q<ScrollView>("messages").contentContainer.childCount, "line, reply, line");
            Assert.IsTrue(IsVisible("answer-area"));
            yield return Shot("03b_history");
            Click("history-toggle");
            yield return Frames(1);
            Assert.IsFalse(_session.CallView.HistoryOpen);

            Click("hangup");
            yield return WaitUntil(() => IsVisible("call-footer"), "the see-result button");
            Click("see-result");
            yield return WaitUntil(() => _session.Call.IsConcluded, "lessons");
            yield return Frames(2);
            Assert.AreEqual(Loc.T("debrief.result.hung_up"), Root.Q<Label>("result").text);
        }

        /// <summary>
        /// The 16:9 stage must fit whole inside any window shape (dark bars at the sides or top), not be
        /// cropped. A browser window is rarely 16:9; the tests above all run at 1920x1080.
        /// </summary>
        [UnityTest]
        public IEnumerator StageFitsInsideWideAndTallWindows()
        {
            // The document already draws through the test copy of the panel settings made in setup;
            // point that copy at a texture of each window shape, then back.
            PanelSettings settings = _session.Document.panelSettings;
            foreach (Vector2Int size in new[] { new Vector2Int(2100, 900), new Vector2Int(1200, 1000) })
            {
                var texture = new RenderTexture(size.x, size.y, 24, RenderTextureFormat.ARGB32);
                texture.Create();
                settings.targetTexture = texture;
                try
                {
                    yield return Frames(3);
                    Rect window = Root.worldBound;
                    Rect stage = Root.Q("stage").worldBound;
                    string where = size.x + "x" + size.y + ": stage " + stage + " in window " + window;
                    Assert.GreaterOrEqual(stage.xMin, window.xMin - 1f, where);
                    Assert.GreaterOrEqual(stage.yMin, window.yMin - 1f, where);
                    Assert.LessOrEqual(stage.xMax, window.xMax + 1f, where);
                    Assert.LessOrEqual(stage.yMax, window.yMax + 1f, where);
                    // It fills the window in one direction, so nothing is shrunk more than needed.
                    Assert.IsTrue(Mathf.Abs(stage.width - window.width) < 1f || Mathf.Abs(stage.height - window.height) < 1f, where);
                    yield return UiCapture.Save(texture, "10_stage_" + size.x + "x" + size.y);
                }
                finally
                {
                    settings.targetTexture = _texture;
                    texture.Release();
                    UnityEngine.Object.Destroy(texture);
                }
            }
        }

        // ---- Helpers ---------------------------------------------------------------------------

        IEnumerator Shot(string name)
        {
            yield return UiCapture.Save(_texture, name);
        }

        void Click(string name)
        {
            Button button = Root.Q<Button>(name);
            Assert.IsNotNull(button, "no button named " + name);
            Click(button);
        }

        static void Click(Button button)
        {
            using (NavigationSubmitEvent evt = NavigationSubmitEvent.GetPooled())
            {
                evt.target = button;
                button.SendEvent(evt);
            }
        }

        void ClickOption(Stance stance)
        {
            foreach (Button button in _session.CallView.OptionButtons)
            {
                if ((int)button.userData == (int)stance)
                {
                    Click(button);
                    return;
                }
            }
            Assert.Fail("no " + stance + " option on screen");
        }

        /// <summary>The caller's face follows the pressure of the line on screen: 1-2 calm, 3-4 stern, 5 angry.</summary>
        void AssertCallerFaceMatchesPressure()
        {
            int level = _session.Call.CurrentLine.PressureLevel;
            string expected = level >= 5 ? CallScreen.ExprAngry : level >= 3 ? CallScreen.ExprStern : CallScreen.ExprCalm;
            Assert.AreEqual(expected, _session.CallView.CallerExpression, "pressure " + level);
        }

        bool IsVisible(string name)
        {
            VisualElement element = Root.Q(name);
            return element != null && !element.ClassListContains(UiKit.Hidden);
        }

        /// <summary>Waits until the player can answer again, or the call is over.</summary>
        IEnumerator WaitForAnswer()
        {
            yield return WaitUntil(() => IsVisible("answer-area") || _session.Call.State == CallState.Ended, "the next line");
            yield return null;
        }

        static IEnumerator WaitUntil(Func<bool> condition, string what, int maxFrames = 300)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                if (condition()) yield break;
                yield return null;
            }
            Assert.Fail("timed out waiting for " + what);
        }

        static IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        void AssertAllTextIsLocalized()
        {
            Root.Query<TextElement>().ForEach(element =>
            {
                string text = element.text ?? "";
                Assert.IsFalse(MissingKey.IsMatch(text), "missing string key: " + text);
                Assert.IsFalse(text.StartsWith(UiKit.LocPrefix), "untranslated: " + text);
            });
        }
    }
}
