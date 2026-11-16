using System;
using System.Collections;
using System.Collections.Generic;
using ScamSim.LLM;
using ScamSim.Logging;

namespace ScamSim.Core
{
    public enum CallState
    {
        /// <summary>The phone is ringing. Accept() or Decline().</summary>
        Ringing,
        /// <summary>Waiting for the provider to produce the scammer's next line.</summary>
        WaitingForScammer,
        /// <summary>The scammer spoke; waiting for the player to pick, type or hang up.</summary>
        AwaitingPlayer,
        /// <summary>The provider timed out. Retry() asks again; the player may also hang up.</summary>
        Dropped,
        /// <summary>The call is over. Run() then writes the lessons and the final log.</summary>
        Ended
    }

    /// <summary>
    /// Runs one phone call: asks the provider for scammer lines, turns the player's input into
    /// replies, shuffles where the options sit on screen, and saves the log after every step.
    /// Pure C# without MonoBehaviour: the game runs Run() as a coroutine and the UI only calls the
    /// action methods, so the whole flow can be tested in EditMode.
    /// </summary>
    public class CallSession
    {
        /// <summary>How long the debrief waits for provider lessons before using the scripted ones.</summary>
        public const long LessonTimeoutMs = 25000;

        static readonly int[] NoOptions = new int[0];

        readonly ILLMProvider _provider;
        readonly ScriptedProvider _fallback;
        readonly Func<long> _clockMs;
        readonly Func<DateTime> _utcNow;

        long _lineAppliedAtMs;
        long? _lineShownAtMs;
        DateTime? _endUtc;

        public CallSession(ILLMProvider provider, ScriptedProvider fallback, SessionMeta meta, string logDirectory,
            Func<long> clockMs = null, Func<DateTime> utcNow = null)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (fallback == null) throw new ArgumentNullException("fallback");
            if (meta == null) throw new ArgumentNullException("meta");

            _provider = provider;
            _fallback = fallback;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _clockMs = clockMs ?? (() => stopwatch.ElapsedMilliseconds);

            Meta = meta;
            Manager = new ConversationManager(_utcNow);
            Logger = new SessionLogger(meta, logDirectory);
            State = CallState.Ringing;
        }

        public SessionMeta Meta { get; private set; }
        public ConversationManager Manager { get; private set; }
        public SessionLogger Logger { get; private set; }
        public CallState State { get; private set; }

        /// <summary>
        /// DisplayOrder[slot] = option index for the line the player is answering; empty when there is
        /// nothing to answer. Read from the exchange, so the screen and the log can never disagree.
        /// </summary>
        public int[] DisplayOrder
        {
            get
            {
                if (!Manager.AwaitingReply || Manager.Current.OptionOrder == null) return NoOptions;
                return (int[])Manager.Current.OptionOrder.Clone();
            }
        }

        /// <summary>Null until Run() has finished the call; then the lessons shown in the debrief.</summary>
        public List<string> Lessons { get; private set; }

        public bool IsConcluded { get; private set; }

        /// <summary>The error from the most recent save, or null when it worked.</summary>
        public string LastSaveError { get; private set; }

        public ScammerTurn CurrentLine
        {
            get { return Manager.Current != null ? Manager.Current.Scammer : null; }
        }

        public event Action<CallState> StateChanged;
        public event Action<ScammerTurn> ScammerSpoke;
        public event Action<PlayerReply> PlayerReplied;
        public event Action<string> SaveFailed;
        public event Action<SessionLog> Concluded;

        // ---- Actions the UI calls --------------------------------------------------------------

        public void Accept()
        {
            if (State != CallState.Ringing) return;
            SetState(CallState.WaitingForScammer);
        }

        public void Decline()
        {
            if (State != CallState.Ringing) return;
            Manager.DeclineCall();
            SetState(CallState.Ended);
        }

        /// <summary>
        /// The scammer's line is fully on screen. The UI must call this: response time is measured from
        /// here, and hanging up before it means the player never saw the line.
        /// </summary>
        public void LineShown()
        {
            if (State == CallState.AwaitingPlayer && !_lineShownAtMs.HasValue) _lineShownAtMs = _clockMs();
        }

        /// <summary>The player tapped the option in this on-screen slot.</summary>
        public void ChooseDisplayed(int slot)
        {
            if (State != CallState.AwaitingPlayer) return;
            int[] order = DisplayOrder;
            if (slot < 0 || slot >= order.Length) throw new ArgumentOutOfRangeException("slot");

            // Only turns with exactly one option per stance get this far, so the index is the stance.
            int index = order[slot];
            Reply(PlayerReply.FromOption((Stance)index, CurrentLine.PlayerOptions[index], ResponseTimeMs()));
        }

        /// <summary>The player typed a reply. Takes the raw text; it is masked before it is stored or sent anywhere.</summary>
        public void SubmitFreeText(string rawText)
        {
            if (State != CallState.AwaitingPlayer) return;
            if (string.IsNullOrWhiteSpace(rawText)) return;
            Reply(PlayerReply.FromFreeText(rawText.Trim(), ResponseTimeMs()));
        }

        public void HangUp()
        {
            if (State == CallState.Ringing)
            {
                Decline();
                return;
            }
            if (State == CallState.Ended) return;

            // Run() saves the final log on its next step, so no save here.
            bool lineSeen = State == CallState.AwaitingPlayer && _lineShownAtMs.HasValue;
            Manager.HangUp(lineSeen ? ResponseTimeMs() : 0, lineSeen);
            SetState(CallState.Ended);
        }

        public void Retry()
        {
            if (State != CallState.Dropped) return;
            SetState(CallState.WaitingForScammer);
        }

        // ---- Coroutine -------------------------------------------------------------------------

        /// <summary>
        /// Drives the call from ringing to the final log. Start it once as a coroutine; it yields null
        /// while it waits for the player.
        /// </summary>
        public IEnumerator Run()
        {
            Save();
            while (State != CallState.Ended)
            {
                if (State == CallState.WaitingForScammer) yield return FetchScammerTurn();
                else yield return null;
            }
            yield return Conclude();
        }

        IEnumerator FetchScammerTurn()
        {
            ProviderRequest request = Manager.BuildRequest();
            ProviderResult result = null;
            Exception error = null;
            yield return Drive(_provider.NextTurn(request, r => result = r), e => error = e,
                () => State != CallState.WaitingForScammer);

            // The player hung up while the provider was still working: drop whatever came back.
            if (Manager.IsOver || State != CallState.WaitingForScammer) yield break;

            ScammerTurn turn;
            string source;
            if (error == null && result != null && result.Success && IsPlayable(result.Turn))
            {
                turn = result.Turn;
                source = string.IsNullOrEmpty(result.Source) ? TurnSources.Llm : result.Source;
            }
            else if (error == null && result != null && result.TimedOut)
            {
                SetState(CallState.Dropped);
                yield break;
            }
            else
            {
                turn = _fallback.FallbackTurn(request);
                source = TurnSources.Fallback;
            }

            ApplyScammerTurn(turn, source);
        }

        IEnumerator Conclude()
        {
            _endUtc = _utcNow();
            Save();

            List<string> lessons = null;
            long askedAt = _clockMs();
            yield return Drive(_provider.GenerateLessons(Logger.Latest, l => lessons = l), null,
                () => _clockMs() - askedAt > LessonTimeoutMs);
            if (lessons == null || lessons.Count == 0) lessons = _fallback.BuildLessons(Logger.Latest);

            Lessons = lessons;
            Save();
            IsConcluded = true;
            if (Concluded != null) Concluded(Logger.Latest);
        }

        /// <summary>
        /// Runs a provider coroutine, stopping early once <paramref name="abandon"/> returns true (the
        /// player hung up, or the provider took too long). An exception inside the provider is
        /// reported instead of killing the game's coroutine.
        /// </summary>
        static IEnumerator Drive(IEnumerator inner, Action<Exception> onError, Func<bool> abandon)
        {
            if (inner == null) yield break;
            try
            {
                while (true)
                {
                    if (abandon != null && abandon()) yield break;

                    bool moved;
                    try
                    {
                        moved = inner.MoveNext();
                    }
                    catch (Exception e)
                    {
                        if (onError != null) onError(e);
                        yield break;
                    }
                    if (!moved) yield break;
                    yield return inner.Current;
                }
            }
            finally
            {
                // Lets the provider dispose a web request it was still waiting on.
                var disposable = inner as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }

        // ---- Internals -------------------------------------------------------------------------

        void ApplyScammerTurn(ScammerTurn turn, string source)
        {
            Manager.ApplyScammerTurn(turn, source);

            Exchange exchange = Manager.Current;
            if (!Manager.IsOver)
                exchange.OptionOrder = OptionOrder.For(Meta.OptionShuffleSeed, Manager.TurnCount - 1, exchange.Scammer.PlayerOptions.Count);

            _lineAppliedAtMs = _clockMs();
            _lineShownAtMs = null;
            Save();

            if (ScammerSpoke != null) ScammerSpoke(exchange.Scammer);
            SetState(Manager.IsOver ? CallState.Ended : CallState.AwaitingPlayer);
        }

        void Reply(PlayerReply reply)
        {
            Manager.ApplyPlayerReply(reply);
            Save();
            if (PlayerReplied != null) PlayerReplied(reply);
            SetState(CallState.WaitingForScammer);
        }

        long ResponseTimeMs()
        {
            long start = _lineShownAtMs ?? _lineAppliedAtMs;
            long elapsed = _clockMs() - start;
            return elapsed < 0 ? 0 : elapsed;
        }

        /// <summary>
        /// A provider line the game can show: either it ends the call, or it offers exactly one option
        /// per stance (comply, hesitate, resist) as the PRD schema requires. Anything else falls back
        /// to a scripted line, so an option index always means the same stance in the log.
        /// </summary>
        static bool IsPlayable(ScammerTurn turn)
        {
            if (turn == null) return false;
            if (turn.CallShouldEnd) return true;
            return turn.PlayerOptions != null && turn.PlayerOptions.Count == OptionOrder.StandardCount;
        }

        void Save()
        {
            string error;
            Logger.Save(Manager, _endUtc, Lessons, out error);
            LastSaveError = error;
            if (error != null && SaveFailed != null) SaveFailed(error);
        }

        void SetState(CallState state)
        {
            if (State == state) return;
            State = state;
            if (StateChanged != null) StateChanged(state);
        }
    }
}
