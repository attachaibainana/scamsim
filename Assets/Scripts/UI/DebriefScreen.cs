using System;
using System.Collections.Generic;
using ScamSim.Core;
using ScamSim.Logging;
using ScamSim.Platform;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamSim.UI
{
    /// <summary>
    /// The result of a call (PRD 2.5): what was lost, how many red flags the player caught, three
    /// lessons, and a short timeline that marks each red flag as caught or missed. Everything is read
    /// from the saved session log, so the screen shows exactly what was recorded.
    /// </summary>
    public class DebriefScreen : IDisposable
    {
        readonly CallSession _call;
        readonly VisualElement _lessons;
        readonly VisualElement _timeline;
        readonly Label _lost;
        readonly Label _score;
        readonly Label _logPath;
        readonly Label _exportStatus;
        readonly Button _exportCsv;
        readonly Button _openFolder;

        public VisualElement Root { get; private set; }
        public event Action PlayAgainClicked;
        public event Action HomeClicked;

        /// <summary>Where the last Export CSV wrote its file, or null.</summary>
        public string ExportedCsvPath { get; private set; }

        public DebriefScreen(VisualElement root, CallSession call)
        {
            Root = root;
            _call = call;

            string reason = call.Manager.EndReason ?? EndReasons.HungUp;
            Label result = UiKit.Require<Label>(root, "result");
            result.text = Loc.T("debrief.result." + reason);
            result.AddToClassList(reason);
            UiKit.Require<Label>(root, "result-detail").text = Loc.T("debrief.detail." + reason);
            // The caller's face is revealed only when the scam worked; otherwise PIP is shown safe.
            UiKit.Require<VisualElement>(root, "debrief-character").AddToClassList(reason == EndReasons.Scammed ? "scammer" : "player");

            _lost = UiKit.Require<Label>(root, "lost");
            _score = UiKit.Require<Label>(root, "score");
            _lessons = UiKit.Require<VisualElement>(root, "lessons");
            _timeline = UiKit.Require<VisualElement>(root, "timeline");
            _logPath = UiKit.Require<Label>(root, "log-path");
            _exportStatus = UiKit.Require<Label>(root, "export-status");
            _exportCsv = UiKit.Require<Button>(root, "export-csv");
            _openFolder = UiKit.Require<Button>(root, "open-folder");

            UiKit.Require<Button>(root, "play-again").clicked += () =>
            {
                if (PlayAgainClicked != null) PlayAgainClicked();
            };
            UiKit.Require<Button>(root, "home").clicked += () =>
            {
                if (HomeClicked != null) HomeClicked();
            };
            _exportCsv.clicked += ExportCsv;
            _openFolder.clicked += OpenFolder;

            if (call.IsConcluded) Fill(call.Logger.Latest);
            else
            {
                _exportCsv.SetEnabled(false);
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
            DebriefSummary summary = DebriefSummary.From(log);

            var lost = new List<string>();
            foreach (string goal in summary.Lost) lost.Add(Loc.T("debrief.lost." + goal));
            // When nothing was lost the result line already says so. An AI scammer can end a call as
            // "scammed" without naming a goal; then say so instead of listing nothing.
            if (lost.Count > 0) _lost.text = Loc.T("debrief.lost", string.Join(", ", lost.ToArray()));
            else if (summary.EndReason == EndReasons.Scammed) _lost.text = Loc.T("debrief.lost_unnamed");
            else _lost.text = "";
            UiKit.Show(_lost, _lost.text.Length > 0);

            _score.text = summary.FlagsAnswerable > 0
                ? Loc.T("debrief.score", summary.FlagsNoticed, summary.FlagsAnswerable)
                : Loc.T("debrief.score_none");

            _lessons.Clear();
            foreach (string lesson in summary.Lessons) _lessons.Add(Lesson(lesson));

            _timeline.Clear();
            foreach (DebriefItem item in summary.Items) _timeline.Add(TimelineItem(item));

            if (_call.LastSaveError != null) _logPath.text = Loc.T("debrief.log_failed", _call.LastSaveError);
            else if (WebBridge.IsWeb) _logPath.text = Loc.T("debrief.log_saved_web");
            else if (!string.IsNullOrEmpty(_call.Logger.FilePath)) _logPath.text = Loc.T("debrief.log_saved", _call.Logger.FilePath);
            else _logPath.text = "";

            _exportCsv.SetEnabled(true);
        }

        VisualElement TimelineItem(DebriefItem item)
        {
            var card = new VisualElement();
            card.AddToClassList("timeline-item");

            var line = new Label(item.ScammerLine);
            line.AddToClassList("timeline-line");
            card.Add(line);

            if (item.Flags.Count > 0)
            {
                var chips = new VisualElement();
                chips.AddToClassList("flag-row");
                foreach (DebriefFlag flag in item.Flags) chips.Add(FlagChip(flag));
                card.Add(chips);
            }

            string reply = null;
            if (item.PlayerHungUp) reply = Loc.T("debrief.reply_hung_up");
            else if (item.PlayerText != null) reply = Loc.T("debrief.reply", item.PlayerText);
            if (reply != null)
            {
                var replyLabel = new Label(reply);
                replyLabel.AddToClassList("timeline-reply");
                card.Add(replyLabel);
            }
            return card;
        }

        /// <summary>Frame with the label inside, for the same wrapping reason as chat bubbles (see CallScreen).</summary>
        static VisualElement FlagChip(DebriefFlag flag)
        {
            string key;
            string css;
            switch (flag.Status)
            {
                case FlagStatus.Noticed:
                    key = "debrief.flag_noticed";
                    css = "noticed";
                    break;
                case FlagStatus.Missed:
                    key = "debrief.flag_missed";
                    css = "missed";
                    break;
                default:
                    key = "debrief.flag_ended";
                    css = "ended";
                    break;
            }

            var chip = new VisualElement();
            chip.AddToClassList("flag-chip");
            chip.AddToClassList(css);
            var text = new Label(Loc.T(key, flag.Text));
            text.AddToClassList("flag-chip-text");
            chip.Add(text);
            return chip;
        }

        void ExportCsv()
        {
            // A web page cannot write next to a log file the player can reach: hand the CSV to the browser.
            if (WebBridge.IsWeb)
            {
                byte[] bytes;
                string buildDetail;
                CsvExportError built = CsvExport.TryBuild(_call.Logger.Latest, out bytes, out buildDetail);
                string name = CsvExport.FileNameFor(_call.Logger.FilePath);
                if (built == CsvExportError.None && WebBridge.Download(name, "text/csv", bytes))
                {
                    _exportStatus.text = Loc.T("debrief.export_downloaded", name);
                    return;
                }
                _exportStatus.text = Loc.T(built == CsvExportError.NoLog ? "debrief.export_failed_nolog" : "debrief.export_failed_download");
                if (buildDetail != null) Debug.LogWarning("[ScamSim] CSV export failed: " + buildDetail);
                return;
            }

            string csvPath;
            string detail;
            CsvExportError error = CsvExport.TrySave(_call.Logger.Latest, _call.Logger.FilePath, out csvPath, out detail);

            if (error == CsvExportError.None)
            {
                ExportedCsvPath = csvPath;
                _exportStatus.text = Loc.T("debrief.export_done", csvPath);
                UiKit.Show(_openFolder, IsWindows());
                return;
            }

            // A failed export must not leave the previous file looking current.
            ExportedCsvPath = null;
            UiKit.Show(_openFolder, false);
            _exportStatus.text = Loc.T(error == CsvExportError.WriteFailed ? "debrief.export_failed_write" : "debrief.export_failed_nolog");
            if (detail != null) Debug.LogWarning("[ScamSim] CSV export failed: " + detail);
        }

        /// <summary>
        /// Opens the folder in Explorer with the raw path. A file:// URL would percent-encode a Thai
        /// user name in the path, which Explorer does not always decode.
        /// </summary>
        void OpenFolder()
        {
            if (ExportedCsvPath == null || !IsWindows()) return;
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + ExportedCsvPath + "\"");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ScamSim] Could not open the folder: " + e.Message);
            }
        }

        static bool IsWindows()
        {
            RuntimePlatform platform = Application.platform;
            return platform == RuntimePlatform.WindowsPlayer || platform == RuntimePlatform.WindowsEditor;
        }

        static Label Lesson(string text)
        {
            var label = new Label(text);
            label.AddToClassList("lesson");
            return label;
        }
    }
}
