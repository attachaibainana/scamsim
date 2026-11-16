using System;
using ScamSim.LLM;
using ScamSim.Logging;
using ScamSim.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamSim.Core
{
    /// <summary>
    /// The only MonoBehaviour in the game: loads the text tables, then moves between
    /// Intro → Call → Debrief (PRD section 4). Each call is a fresh CallSession with its own log.
    /// Assets are wired by ProjectSetup.CreateMainScene and loaded as TextAssets, so the same code
    /// runs on Windows and WebGL.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class GameSession : MonoBehaviour
    {
        public enum GameScreen
        {
            None,
            Intro,
            Call,
            Debrief
        }

        [SerializeField] UIDocument document;
        [SerializeField] StyleSheet styles;
        [SerializeField] TextAsset localization;
        [SerializeField] TextAsset scenario;
        [SerializeField] VisualTreeAsset introLayout;
        [SerializeField] VisualTreeAsset callLayout;
        [SerializeField] VisualTreeAsset debriefLayout;

        [Tooltip("Shortest time the scammer appears to be typing before a line shows.")]
        [SerializeField] float minTypingSeconds = 0.9f;

        ScriptedProvider _scripted;
        VisualElement _phone;
        IDisposable _screen;

        public GameScreen Screen { get; private set; }
        public CallSession Call { get; private set; }
        public CallScreen CallView { get; private set; }
        public DebriefScreen DebriefView { get; private set; }
        public string LogDirectory { get; private set; }

        public float MinTypingSeconds
        {
            get { return minTypingSeconds; }
            set { minTypingSeconds = Mathf.Max(0f, value); }
        }

        public UIDocument Document
        {
            get { return document; }
        }

        void Awake()
        {
            if (document == null) document = GetComponent<UIDocument>();
            Loc.Load(localization.text);
            _scripted = new ScriptedProvider(scenario.text);
            LogDirectory = LogLocation.Resolve();
        }

        void Start()
        {
            ShowIntro();
        }

        void OnDestroy()
        {
            CloseScreen();
            if (Call != null) Call.StateChanged -= OnCallStateChanged;
        }

        public void ShowIntro()
        {
            var intro = new IntroScreen(Mount(introLayout));
            intro.StartClicked += StartCall;
            Screen = GameScreen.Intro;
        }

        /// <summary>A new call starts ringing. A previous call keeps running in the background until its log is final.</summary>
        public void StartCall()
        {
            if (Call != null) Call.StateChanged -= OnCallStateChanged;

            var meta = new SessionMeta
            {
                SessionId = Guid.NewGuid().ToString(),
                ScenarioId = _scripted.ScenarioId,
                AppVersion = Application.version,
                Provider = _scripted.ProviderId,
                ModelId = _scripted.ModelId,
                PromptVersion = _scripted.PromptVersion,
                Temperature = _scripted.Temperature,
                // Non-negative, so the seed reads as a plain number in every export.
                OptionShuffleSeed = UnityEngine.Random.Range(0, int.MaxValue),
                StartUtc = DateTime.UtcNow
            };

            Call = new CallSession(_scripted, _scripted, meta, LogDirectory);
            Call.SaveFailed += error => Debug.LogWarning("[ScamSim] Could not save the session log: " + error);
            Call.StateChanged += OnCallStateChanged;

            CallView = new CallScreen(Mount(callLayout), Call, minTypingSeconds);
            CallView.SeeResultClicked += ShowDebrief;
            _screen = CallView;
            Screen = GameScreen.Call;

            StartCoroutine(Call.Run());
        }

        public void ShowDebrief()
        {
            if (Call == null) return;
            var debrief = new DebriefScreen(Mount(debriefLayout), Call);
            debrief.PlayAgainClicked += StartCall;
            debrief.HomeClicked += ShowIntro;
            _screen = debrief;
            DebriefView = debrief;
            Screen = GameScreen.Debrief;
        }

        void OnCallStateChanged(CallState state)
        {
            // Declining never opens the chat, so go straight to the result. Switch on the next tick:
            // the call screen is still handling this same event and must not be torn down under it.
            if (state != CallState.Ended || Call.Manager.EndedByWhat != EndedBy.PlayerDeclined) return;
            CallSession declined = Call;
            _phone.schedule.Execute(() =>
            {
                if (Call == declined && Screen == GameScreen.Call) ShowDebrief();
            });
        }

        VisualElement Mount(VisualTreeAsset layout)
        {
            CloseScreen();
            CallView = null;
            DebriefView = null;
            EnsurePhone();
            _phone.Clear();

            TemplateContainer screen = layout.Instantiate();
            screen.style.flexGrow = 1;
            UiKit.Prepare(screen);
            _phone.Add(screen);
            return screen;
        }

        void CloseScreen()
        {
            if (_screen != null) _screen.Dispose();
            _screen = null;
        }

        void EnsurePhone()
        {
            VisualElement root = document.rootVisualElement;
            if (_phone != null && _phone.parent == root) return;

            if (!root.styleSheets.Contains(styles)) root.styleSheets.Add(styles);
            root.AddToClassList("scamsim-root");
            _phone = new VisualElement { name = "phone" };
            _phone.AddToClassList("phone");
            root.Add(_phone);
        }
    }
}
