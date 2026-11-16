using System;
using UnityEngine.UIElements;

namespace ScamSim.UI
{
    /// <summary>Title, a short "this is practice" note, and the start button.</summary>
    public class IntroScreen
    {
        public VisualElement Root { get; private set; }
        public event Action StartClicked;

        readonly Label _mode;

        public IntroScreen(VisualElement root)
        {
            Root = root;
            _mode = UiKit.Require<Label>(root, "mode");
            UiKit.Require<Button>(root, "start").clicked += () =>
            {
                if (StartClicked != null) StartClicked();
            };
            UiKit.Require<Button>(root, "news").clicked += () =>
            {
                if (NewsClicked != null) NewsClicked();
            };
        }

        public event Action NewsClicked;

        /// <summary>Says whether calls use Claude or the offline script.</summary>
        public void SetMode(string text)
        {
            _mode.text = text;
        }
    }
}
