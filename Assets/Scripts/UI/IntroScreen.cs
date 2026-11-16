using System;
using UnityEngine.UIElements;

namespace ScamSim.UI
{
    /// <summary>Title, a short "this is practice" note, and the start button.</summary>
    public class IntroScreen
    {
        public VisualElement Root { get; private set; }
        public event Action StartClicked;

        public IntroScreen(VisualElement root)
        {
            Root = root;
            UiKit.Require<Button>(root, "start").clicked += () =>
            {
                if (StartClicked != null) StartClicked();
            };
        }
    }
}
