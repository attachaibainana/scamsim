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
                    yield return new WaitForSecondsRealtime(1f); // let the background colour finish its transition
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
            StringAssert.Contains(_session.Call.Logger.FilePath, Root.Q<Label>("log-path").text);
            Assert.IsNull(_session.Call.LastSaveError);
            yield return Shot("06_debrief");
            AssertAllTextIsLocalized();

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
            yield return Shot("07_debrief_scammed");

            Click("play-again");
            yield return Frames(2);
            Click("decline");
            yield return WaitUntil(() => _session.Screen == GameSession.GameScreen.Debrief, "debrief after declining");
            yield return WaitUntil(() => _session.Call.IsConcluded, "lessons");
            yield return Frames(2);
            Assert.AreEqual(Loc.T("debrief.result.hung_up"), Root.Q<Label>("result").text);
            Assert.AreEqual(0, _session.Call.Logger.Latest.TotalTurns);
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

            Click("hangup");
            yield return WaitUntil(() => IsVisible("call-footer"), "the see-result button");
            Click("see-result");
            yield return WaitUntil(() => _session.Call.IsConcluded, "lessons");
            yield return Frames(2);
            Assert.AreEqual(Loc.T("debrief.result.hung_up"), Root.Q<Label>("result").text);
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
