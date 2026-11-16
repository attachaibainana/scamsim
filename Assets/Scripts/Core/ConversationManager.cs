using System;
using System.Collections.Generic;
using ScamSim.LLM;

namespace ScamSim.Core
{
    /// <summary>
    /// Owns turns, history, pressure and end conditions for one call.
    /// Pure C# (no Unity types) so the rules can be unit-tested and replayed.
    /// </summary>
    public class ConversationManager
    {
        public const int MaxTurns = 12;
        public const int MinPressure = 1;
        public const int MaxPressure = 5;
        public const int StartPressure = 2;

        readonly Func<DateTime> _utcNow;

        public ConversationState State { get; } = new ConversationState();
        public bool IsOver { get; private set; }
        public string EndReason { get; private set; }
        public string EndedByWhat { get; private set; }

        public ConversationManager(Func<DateTime> utcNow = null)
        {
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public int TurnCount
        {
            get { return State.Exchanges.Count; }
        }

        public Exchange Current
        {
            get { return TurnCount > 0 ? State.Exchanges[TurnCount - 1] : null; }
        }

        public bool AwaitingReply
        {
            get { return !IsOver && Current != null && Current.Reply == null; }
        }

        /// <summary>Resisting raises pressure, complying lowers it, hesitating keeps it (PRD 2.1).</summary>
        public static int NextPressure(int current, Stance? lastStance)
        {
            int next = current;
            if (lastStance == Stance.Resist) next = current + 1;
            else if (lastStance == Stance.Comply) next = current - 1;
            return Clamp(next);
        }

        public static int Clamp(int pressure)
        {
            if (pressure < MinPressure) return MinPressure;
            if (pressure > MaxPressure) return MaxPressure;
            return pressure;
        }

        public ProviderRequest BuildRequest()
        {
            Stance? last = null;
            if (Current != null && Current.Reply != null) last = Current.Reply.Stance;
            return new ProviderRequest
            {
                State = State,
                TargetPressure = NextPressure(State.Pressure, last),
                MustEnd = TurnCount >= MaxTurns - 1
            };
        }

        public void ApplyScammerTurn(ScammerTurn turn, string source)
        {
            if (IsOver) throw new InvalidOperationException("The call is already over.");
            if (AwaitingReply) throw new InvalidOperationException("The previous line has not been answered.");
            if (turn == null) throw new ArgumentNullException("turn");

            // An LLM may send null for any field. Normalise here so nothing downstream has to check.
            if (turn.ScammerLine == null) turn.ScammerLine = "";
            if (turn.RedFlags == null) turn.RedFlags = new List<string>();
            if (turn.PlayerOptions == null) turn.PlayerOptions = new List<string>();

            turn.PressureLevel = Clamp(turn.PressureLevel);
            State.Goals.Merge(turn.GoalProgress);
            turn.GoalProgress = State.Goals.Clone();
            State.Pressure = turn.PressureLevel;
            State.Exchanges.Add(new Exchange
            {
                Scammer = turn,
                TimestampUtc = _utcNow().ToString("o"),
                Source = source
            });

            if (turn.CallShouldEnd) End(EndedBy.Scammer, turn.EndReason);
            else if (State.Goals.All()) End(EndedBy.GoalsComplete, null);
            else if (TurnCount >= MaxTurns) End(EndedBy.TurnLimit, null);
        }

        public void ApplyPlayerReply(PlayerReply reply)
        {
            if (!AwaitingReply) throw new InvalidOperationException("There is no line waiting for a reply.");
            if (reply == null) throw new ArgumentNullException("reply");
            Current.Reply = reply;
            if (!reply.GaveIdNumber) return;

            // The number was given in this turn, so this turn's snapshot must show it: the call can
            // end before the scammer speaks again, and the last snapshot is what the log and lessons use.
            State.Goals.IdNumber = true;
            Current.Scammer.GoalProgress = State.Goals.Clone();
        }

        /// <param name="responseTimeMs">Time from the line appearing to the hang up.</param>
        /// <param name="lineSeen">
        /// False when the latest line was not on screen yet. The hang up is then not a reply to it:
        /// the turn keeps no reply and is marked as never shown.
        /// </param>
        public void HangUp(long responseTimeMs, bool lineSeen = true)
        {
            if (IsOver) return;
            if (AwaitingReply)
            {
                if (lineSeen) Current.Reply = PlayerReply.HangUp(responseTimeMs);
                else Current.LineShown = false;
            }
            End(EndedBy.PlayerHangUp, null);
        }

        /// <summary>The player never picked up. Counts as a safe hang up with zero turns.</summary>
        public void DeclineCall()
        {
            if (IsOver) return;
            End(EndedBy.PlayerDeclined, null);
        }

        void End(string endedBy, string providerHint)
        {
            IsOver = true;
            EndedByWhat = endedBy;
            if (State.Goals.Any() || providerHint == EndReasons.Scammed) EndReason = EndReasons.Scammed;
            else if (endedBy == EndedBy.PlayerHangUp || endedBy == EndedBy.PlayerDeclined) EndReason = EndReasons.HungUp;
            else EndReason = EndReasons.Escaped;
        }
    }
}
