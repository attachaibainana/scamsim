using System;
using System.Collections.Generic;
using ScamSim.Core;
using ScamSim.Logging;
using UnityEngine.UIElements;

namespace ScamSim.UI
{
    /// <summary>
    /// Short debrief: the result, three lessons and where the log was saved.
    /// The red-flag timeline and CSV export come in the next milestone.
    /// </summary>
    public class DebriefScreen : IDisposable
    {
        readonly CallSession _call;
        readonly VisualElement _lessons;
        readonly Label _logPath;

        public VisualElement Root { get; private set; }
        public event Action PlayAgainClicked;

        public DebriefScreen(VisualElement root, CallSession call)
        {
            Root = root;
            _call = call;

            string reason = call.Manager.EndReason ?? EndReasons.HungUp;
            Label result = UiKit.Require<Label>(root, "result");
            result.text = Loc.T("debrief.result." + reason);
            result.AddToClassList(reason);
            UiKit.Require<Label>(root, "result-detail").text = Loc.T("debrief.detail." + reason);

            _lessons = UiKit.Require<VisualElement>(root, "lessons");
            _logPath = UiKit.Require<Label>(root, "log-path");
            UiKit.Require<Button>(root, "play-again").clicked += () =>
            {
                if (PlayAgainClicked != null) PlayAgainClicked();
            };

            if (call.IsConcluded) Fill(null);
            else
            {
                _lessons.Add(Lesson(Loc.T("debrief.summarizing")));
                call.Concluded += Fill;
            }
        }

        public void Dispose()
        {
            _call.Concluded -= Fill;
        }

        void Fill(SessionLog log)
        {
            _lessons.Clear();
            List<string> lessons = _call.Lessons ?? new List<string>();
            foreach (string lesson in lessons) _lessons.Add(Lesson(lesson));

            if (_call.LastSaveError != null) _logPath.text = Loc.T("debrief.log_failed", _call.LastSaveError);
            else if (!string.IsNullOrEmpty(_call.Logger.FilePath)) _logPath.text = Loc.T("debrief.log_saved", _call.Logger.FilePath);
            else _logPath.text = "";
        }

        static Label Lesson(string text)
        {
            var label = new Label(text);
            label.AddToClassList("lesson");
            return label;
        }
    }
}
