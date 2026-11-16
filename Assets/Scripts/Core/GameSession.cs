using System;
using ScamSim.LLM;
using ScamSim.Logging;
using ScamSim.News;
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
            Debrief,
            News
        }

        [SerializeField] UIDocument document;
        [SerializeField] StyleSheet styles;
        [SerializeField] TextAsset localization;
        [SerializeField] TextAsset scenario;
        [SerializeField] TextAsset scammerPrompt;
        [SerializeField] TextAsset lessonsPrompt;
        [SerializeField] VisualTreeAsset introLayout;
        [SerializeField] VisualTreeAsset callLayout;
        [SerializeField] VisualTreeAsset debriefLayout;
        [SerializeField] VisualTreeAsset newsLayout;

        [Tooltip("Shortest time the scammer appears to be typing before a line shows.")]
        [SerializeField] float minTypingSeconds = 0.9f;

        ScriptedProvider _scripted;
        ILLMProvider _provider;
        VisualElement _phone;
        IDisposable _screen;

        /// <summary>The scammer for new calls: Claude when config.json has an API key, otherwise the script.</summary>
        public ILLMProvider Provider
        {
            get { return _provider; }
        }

        public bool IsOnline
        {
            get { return _provider is AnthropicProvider; }
        }

        public GameScreen Screen { get; private set; }
        public CallSession Call { get; private set; }
        public CallScreen CallView { get; private set; }
        public DebriefScreen DebriefView { get; private set; }
        public NewsScreen NewsView { get; private set; }
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
            _provider = CreateProvider();
            LogDirectory = LogLocation.Resolve();
        }

        /// <summary>
        /// Uses Claude only when config.json holds an API key; anything wrong with the setup falls back
        /// to the offline script, so the game always starts (PRD 3: offline fallback).
        /// </summary>
        ILLMProvider CreateProvider()
        {
            string error;
            AppConfig config = AppConfig.Load(out error);
            if (error != null) Debug.LogWarning("[ScamSim] " + error + " Running offline.");
            foreach (string warning in config.Warnings) Debug.LogWarning("[ScamSim] config.json: " + warning);
            if (!config.HasApiKey) return _scripted;

            try
            {
                return new AnthropicProvider(AnthropicSettings.From(config), PromptFile.Parse(scammerPrompt.text),
                    PromptFile.Parse(lessonsPrompt.text), new UnityHttpTransport(), _scripted.ScenarioId);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ScamSim] Could not set up the Claude provider (" + e.Message + "). Running offline.");
                return _scripted;
            }
        }

        /// <summary>For tests and tools: play the next calls against this provider.</summary>
        public void UseProvider(ILLMProvider provider)
        {
            _provider = provider ?? _scripted;
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
            intro.NewsClicked += ShowNews;
            intro.SetMode(IsOnline ? Loc.T("intro.mode_ai", _provider.ModelId) : Loc.T("intro.mode_offline"));
            Screen = GameScreen.Intro;
        }

        /// <summary>A new call starts ringing. A previous call keeps running in the background until its log is final.</summary>
        public void StartCall()
        {
            if (Call != null) Call.StateChanged -= OnCallStateChanged;

            // The API rejected the key or model during an earlier call: play the next calls offline.
            var online = _provider as AnthropicProvider;
            if (online != null && online.IsDisabled) _provider = _scripted;

            var meta = new SessionMeta
            {
                SessionId = Guid.NewGuid().ToString(),
                ScenarioId = _provider.ScenarioId,
                AppVersion = Application.version,
                Provider = _provider.ProviderId,
                ModelId = _provider.ModelId,
                PromptVersion = _provider.PromptVersion,
                Temperature = _provider.Temperature,
                Effort = _provider.Effort,
                // Non-negative, so the seed reads as a plain number in every export.
                OptionShuffleSeed = UnityEngine.Random.Range(0, int.MaxValue),
                StartUtc = DateTime.UtcNow
            };

            Call = new CallSession(_provider, _scripted, meta, LogDirectory);
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

        /// <summary>The news page; the feed is read in the background and shown when it arrives.</summary>
        public void ShowNews()
        {
            var news = new NewsScreen(Mount(newsLayout));
            news.BackClicked += ShowIntro;
            _screen = news;
            NewsView = news;
            Screen = GameScreen.News;

            StartCoroutine(NewsLoader.Load((feed, problems) =>
            {
                foreach (string problem in problems) Debug.LogWarning("[ScamSim] news.json: " + problem);
                if (NewsView == news) news.ShowFeed(feed);
            }));
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
            NewsView = null;
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
