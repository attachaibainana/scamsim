using System;
using System.Collections.Generic;
using ScamSim.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamSim.UI
{
    /// <summary>
    /// The phone screen: ringing, then the chat with the scammer. It only shows what the
    /// CallSession says and forwards the player's taps; all rules and logging live in CallSession.
    /// </summary>
    public class CallScreen : IDisposable
    {
        static readonly string[] TypingFrames = { "•", "••", "•••" };

        readonly CallSession _call;
        readonly float _minTypingSeconds;

        readonly VisualElement _panel;
        readonly VisualElement _ringing;
        readonly VisualElement _inCall;
        readonly ScrollView _messages;
        readonly VisualElement _dropped;
        readonly VisualElement _answerArea;
        readonly VisualElement _options;
        readonly TextField _freeText;
        readonly VisualElement _footer;

        VisualElement _typing;
        IVisualElementScheduledItem _typingAnimation;
        IVisualElementScheduledItem _pendingReveal;
        double _typingSince;

        public VisualElement Root { get; private set; }
        public event Action SeeResultClicked;

        public CallScreen(VisualElement root, CallSession call, float minTypingSeconds)
        {
            Root = root;
            _call = call;
            _minTypingSeconds = Mathf.Max(0f, minTypingSeconds);

            _panel = UiKit.Require<VisualElement>(root, "call");
            _ringing = UiKit.Require<VisualElement>(root, "ringing");
            _inCall = UiKit.Require<VisualElement>(root, "in-call");
            _messages = UiKit.Require<ScrollView>(root, "messages");
            _dropped = UiKit.Require<VisualElement>(root, "dropped");
            _answerArea = UiKit.Require<VisualElement>(root, "answer-area");
            _options = UiKit.Require<VisualElement>(root, "options");
            _freeText = UiKit.Require<TextField>(root, "free-text");
            _footer = UiKit.Require<VisualElement>(root, "call-footer");

            UiKit.Require<Button>(root, "accept").clicked += _call.Accept;
            UiKit.Require<Button>(root, "decline").clicked += _call.Decline;
            UiKit.Require<Button>(root, "hangup").clicked += _call.HangUp;
            UiKit.Require<Button>(root, "retry").clicked += _call.Retry;
            UiKit.Require<Button>(root, "send").clicked += SubmitFreeText;
            UiKit.Require<Button>(root, "see-result").clicked += () =>
            {
                if (SeeResultClicked != null) SeeResultClicked();
            };

            _freeText.textEdition.placeholder = Loc.T("call.type_placeholder");
            _freeText.RegisterCallback<KeyDownEvent>(OnFreeTextKey, TrickleDown.TrickleDown);

            // Keep the newest message in view.
            _messages.contentContainer.RegisterCallback<GeometryChangedEvent>(_ =>
                _messages.verticalScroller.value = _messages.verticalScroller.highValue);

            _call.StateChanged += OnStateChanged;
            _call.ScammerSpoke += OnScammerSpoke;
            _call.PlayerReplied += OnPlayerReplied;
            OnStateChanged(_call.State);
        }

        public void Dispose()
        {
            _call.StateChanged -= OnStateChanged;
            _call.ScammerSpoke -= OnScammerSpoke;
            _call.PlayerReplied -= OnPlayerReplied;
            if (_pendingReveal != null) _pendingReveal.Pause();
            if (_typingAnimation != null) _typingAnimation.Pause();
        }

        /// <summary>The option buttons currently on screen, top to bottom. userData holds the option index.</summary>
        public List<Button> OptionButtons
        {
            get { return _options.Query<Button>().ToList(); }
        }

        void OnStateChanged(CallState state)
        {
            UiKit.Show(_ringing, state == CallState.Ringing);
            UiKit.Show(_inCall, state != CallState.Ringing);
            UiKit.Show(_dropped, state == CallState.Dropped);
            if (state != CallState.AwaitingPlayer) UiKit.Show(_answerArea, false);

            switch (state)
            {
                case CallState.WaitingForScammer:
                    ShowTyping();
                    break;
                case CallState.Dropped:
                    HideTyping();
                    break;
                case CallState.Ended:
                    string endedBy = _call.Manager.EndedByWhat;
                    if (endedBy == EndedBy.PlayerHangUp || endedBy == EndedBy.PlayerDeclined)
                    {
                        // The player left: anything still "being typed" is never shown.
                        if (_pendingReveal != null) _pendingReveal.Pause();
                        _pendingReveal = null;
                        HideTyping();
                        AddBubble(Loc.T("call.you_hung_up"), "system");
                        UiKit.Show(_footer, true);
                    }
                    else if (_pendingReveal == null)
                    {
                        UiKit.Show(_footer, true);
                    }
                    break;
            }
        }

        void OnScammerSpoke(ScammerTurn turn)
        {
            int[] order = (int[])_call.DisplayOrder.Clone();
            double waited = Time.realtimeSinceStartupAsDouble - _typingSince;
            long delayMs = (long)Math.Max(0, (_minTypingSeconds - waited) * 1000.0);

            if (_pendingReveal != null) _pendingReveal.Pause();
            _pendingReveal = Root.schedule.Execute(() => Reveal(turn, order));
            if (delayMs > 0) _pendingReveal.ExecuteLater(delayMs);
        }

        void Reveal(ScammerTurn turn, int[] order)
        {
            _pendingReveal = null;
            HideTyping();
            AddBubble(turn.ScammerLine, "scammer");
            SetPressure(turn.PressureLevel);

            if (_call.State == CallState.AwaitingPlayer)
            {
                BuildOptions(turn.PlayerOptions, order);
                UiKit.Show(_answerArea, true);
                _call.LineShown();
            }
            else if (_call.State == CallState.Ended)
            {
                UiKit.Show(_footer, true);
            }
        }

        void OnPlayerReplied(PlayerReply reply)
        {
            UiKit.Show(_answerArea, false);
            _options.Clear();
            _freeText.SetValueWithoutNotify("");
            AddBubble(reply.Text, "player");
        }

        void BuildOptions(List<string> options, int[] order)
        {
            _options.Clear();
            for (int slot = 0; slot < order.Length; slot++)
            {
                int index = order[slot];
                int chosenSlot = slot;
                string text = index < options.Count ? options[index] : "";
                Button button = UiKit.MakeButton(text, "option-button", () => _call.ChooseDisplayed(chosenSlot));
                button.name = "option-" + slot;
                button.userData = index;
                _options.Add(button);
            }
        }

        void SubmitFreeText()
        {
            _call.SubmitFreeText(_freeText.value);
        }

        void OnFreeTextKey(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            SubmitFreeText();
            evt.StopPropagation();
        }

        void SetPressure(int level)
        {
            for (int i = ConversationManager.MinPressure; i <= ConversationManager.MaxPressure; i++)
                _panel.EnableInClassList("pressure-" + i, i == level);
        }

        void ShowTyping()
        {
            _typingSince = Time.realtimeSinceStartupAsDouble;
            if (_typing != null) return;

            Label dots;
            _typing = MakeBubble(TypingFrames[TypingFrames.Length - 1], "scammer", out dots);
            _typing.AddToClassList("typing");
            _messages.Add(_typing);

            int frame = 0;
            _typingAnimation = dots.schedule.Execute(() =>
            {
                frame = (frame + 1) % TypingFrames.Length;
                dots.text = TypingFrames[frame];
            }).Every(350);
        }

        void HideTyping()
        {
            if (_typingAnimation != null) _typingAnimation.Pause();
            _typingAnimation = null;
            if (_typing != null) _typing.RemoveFromHierarchy();
            _typing = null;
        }

        /// <summary>
        /// A bubble is a frame with the label inside. Padding and border sit on the frame, not on the label:
        /// a wrapping label that also has padding and a % max-width is measured one line too short.
        /// </summary>
        static VisualElement MakeBubble(string text, string speaker, out Label label)
        {
            var bubble = new VisualElement();
            bubble.AddToClassList("bubble");
            bubble.AddToClassList(speaker);
            label = new Label(text);
            label.AddToClassList("bubble-text");
            bubble.Add(label);
            return bubble;
        }

        void AddBubble(string text, string speaker)
        {
            Label label;
            _messages.Add(MakeBubble(text, speaker, out label));
        }
    }
}
