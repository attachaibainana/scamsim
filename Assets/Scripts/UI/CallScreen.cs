using System;
using System.Collections.Generic;
using ScamSim.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamSim.UI
{
    /// <summary>
    /// The call, as a visual-novel scene: the phone rings in the living room, then the caller stands in
    /// front of the player as the police officer he claims to be. The newest line sits in the dialogue
    /// box, the answers on the right, and every line of the call in the history panel. It only shows
    /// what the CallSession says and forwards the player's taps; all rules and logging live in CallSession.
    /// </summary>
    public class CallScreen : IDisposable
    {
        static readonly string[] TypingFrames = { "•", "••", "•••" };

        /// <summary>The caller's face follows the pressure level: 1-2 polite, 3-4 stern, 5 shouting.</summary>
        public const string ExprCalm = "expr-calm";
        public const string ExprStern = "expr-stern";
        public const string ExprAngry = "expr-angry";
        static readonly string[] Expressions = { ExprCalm, ExprStern, ExprAngry };

        const string SpeakerCaller = "caller";
        const string SpeakerYou = "you";
        const long RingPulseMs = 600;

        readonly CallSession _call;
        readonly float _minTypingSeconds;

        readonly VisualElement _panel;
        readonly VisualElement _ringing;
        readonly VisualElement _ringAvatar;
        readonly VisualElement _inCall;
        readonly VisualElement _callerSprite;
        readonly ScrollView _messages;
        readonly Button _historyToggle;
        readonly Label _speaker;
        readonly Label _line;
        readonly Label _typing;
        readonly VisualElement _dropped;
        readonly VisualElement _answerArea;
        readonly VisualElement _options;
        readonly VisualElement _freeTextRow;
        readonly TextField _freeText;
        readonly VisualElement _footer;

        IVisualElementScheduledItem _typingAnimation;
        IVisualElementScheduledItem _pendingReveal;
        IVisualElementScheduledItem _ringPulse;
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
            _ringAvatar = UiKit.Require<VisualElement>(root, "ring-avatar");
            _inCall = UiKit.Require<VisualElement>(root, "in-call");
            _callerSprite = UiKit.Require<VisualElement>(root, "caller-sprite");
            _messages = UiKit.Require<ScrollView>(root, "messages");
            _historyToggle = UiKit.Require<Button>(root, "history-toggle");
            _speaker = UiKit.Require<Label>(root, "speaker");
            _line = UiKit.Require<Label>(root, "line");
            _typing = UiKit.Require<Label>(root, "typing");
            _dropped = UiKit.Require<VisualElement>(root, "dropped");
            _answerArea = UiKit.Require<VisualElement>(root, "answer-area");
            _options = UiKit.Require<VisualElement>(root, "options");
            _freeTextRow = UiKit.Require<VisualElement>(root, "free-text-row");
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
            _historyToggle.clicked += ToggleHistory;

            _freeText.textEdition.placeholder = Loc.T("call.type_placeholder");
            _freeText.RegisterCallback<KeyDownEvent>(OnFreeTextKey, TrickleDown.TrickleDown);

            // Keep the newest message in view.
            _messages.contentContainer.RegisterCallback<GeometryChangedEvent>(_ =>
                _messages.verticalScroller.value = _messages.verticalScroller.highValue);

            ShowLine(null, "");

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
            StopRinging();
        }

        /// <summary>The option buttons currently on screen, top to bottom. userData holds the option index.</summary>
        public List<Button> OptionButtons
        {
            get { return _options.Query<Button>().ToList(); }
        }

        /// <summary>The expression class the caller shows now (ExprCalm, ExprStern or ExprAngry).</summary>
        public string CallerExpression
        {
            get
            {
                foreach (string expression in Expressions)
                    if (_callerSprite.ClassListContains(expression)) return expression;
                return null;
            }
        }

        public bool HistoryOpen
        {
            get { return !_messages.ClassListContains(UiKit.Hidden); }
        }

        void OnStateChanged(CallState state)
        {
            UiKit.Show(_ringing, state == CallState.Ringing);
            UiKit.Show(_inCall, state != CallState.Ringing);
            UiKit.Show(_dropped, state == CallState.Dropped);
            if (state != CallState.AwaitingPlayer) ShowAnswers(false);
            if (state == CallState.Ringing) StartRinging();
            else StopRinging();

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
                        // The player left: anything still "being said" is never shown.
                        if (_pendingReveal != null) _pendingReveal.Pause();
                        _pendingReveal = null;
                        HideTyping();
                        string text = Loc.T("call.you_hung_up");
                        AddBubble(text, "system");
                        ShowLine(null, text);
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
            ShowLine(SpeakerCaller, turn.ScammerLine);
            SetPressure(turn.PressureLevel);

            if (_call.State == CallState.AwaitingPlayer)
            {
                BuildOptions(turn.PlayerOptions, order);
                ShowAnswers(true);
                _call.LineShown();
            }
            else if (_call.State == CallState.Ended)
            {
                UiKit.Show(_footer, true);
            }
        }

        void OnPlayerReplied(PlayerReply reply)
        {
            ShowAnswers(false);
            _options.Clear();
            _freeText.SetValueWithoutNotify("");
            AddBubble(reply.Text, "player");
            ShowLine(SpeakerYou, reply.Text);
        }

        /// <summary>The dialogue box: who speaks (null = a plain notice with no name tag) and the line.</summary>
        void ShowLine(string speaker, string text)
        {
            UiKit.Show(_speaker, speaker != null);
            _speaker.text = speaker == SpeakerYou ? Loc.T("call.speaker_you")
                : speaker == SpeakerCaller ? Loc.T("call.speaker_caller") : "";
            _speaker.EnableInClassList(SpeakerYou, speaker == SpeakerYou);
            _line.text = text;
            _line.EnableInClassList("notice", speaker == null);
        }

        void ShowAnswers(bool visible)
        {
            UiKit.Show(_answerArea, visible);
            UiKit.Show(_freeTextRow, visible);
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

        void ToggleHistory()
        {
            bool open = !HistoryOpen;
            UiKit.Show(_messages, open);
            _historyToggle.text = Loc.T(open ? "call.close_history" : "call.history");
        }

        void SetPressure(int level)
        {
            for (int i = ConversationManager.MinPressure; i <= ConversationManager.MaxPressure; i++)
                _panel.EnableInClassList("pressure-" + i, i == level);

            string expression = level >= 5 ? ExprAngry : level >= 3 ? ExprStern : ExprCalm;
            foreach (string e in Expressions) _callerSprite.EnableInClassList(e, e == expression);
        }

        void ShowTyping()
        {
            _typingSince = Time.realtimeSinceStartupAsDouble;
            if (_typingAnimation != null) return;

            string talking = Loc.T("call.caller_talking") + " ";
            int frame = TypingFrames.Length - 1;
            _typing.text = talking + TypingFrames[frame];
            UiKit.Show(_typing, true);
            _typingAnimation = _typing.schedule.Execute(() =>
            {
                frame = (frame + 1) % TypingFrames.Length;
                _typing.text = talking + TypingFrames[frame];
            }).Every(350);
        }

        void HideTyping()
        {
            if (_typingAnimation != null) _typingAnimation.Pause();
            _typingAnimation = null;
            UiKit.Show(_typing, false);
        }

        void StartRinging()
        {
            if (_ringPulse != null) return;
            _ringPulse = _ringAvatar.schedule.Execute(() => _ringAvatar.ToggleInClassList("pulse")).Every(RingPulseMs);
        }

        void StopRinging()
        {
            if (_ringPulse != null) _ringPulse.Pause();
            _ringPulse = null;
            _ringAvatar.RemoveFromClassList("pulse");
        }

        /// <summary>
        /// A history bubble is a frame with the label inside. Padding and border sit on the frame, not on
        /// the label: a wrapping label that also has padding and a % max-width is measured one line too short.
        /// </summary>
        static VisualElement MakeBubble(string text, string speaker)
        {
            var bubble = new VisualElement();
            bubble.AddToClassList("bubble");
            bubble.AddToClassList(speaker);
            var label = new Label(text);
            label.AddToClassList("bubble-text");
            bubble.Add(label);
            return bubble;
        }

        void AddBubble(string text, string speaker)
        {
            _messages.Add(MakeBubble(text, speaker));
        }
    }
}
