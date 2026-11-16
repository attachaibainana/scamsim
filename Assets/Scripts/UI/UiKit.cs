using ScamSim.Core;
using UnityEngine.UIElements;

namespace ScamSim.UI
{
    /// <summary>Small helpers shared by every screen.</summary>
    public static class UiKit
    {
        public const string LocPrefix = "@";
        public const string Hidden = "hidden";

        /// <summary>
        /// Replaces every text that starts with "@" by the string with that key (text="@intro.start"),
        /// so no Thai text is written in UXML, and drops the default theme's button look so the
        /// ScamSim style sheet alone decides how buttons look.
        /// </summary>
        public static void Prepare(VisualElement root)
        {
            root.Query<TextElement>().ForEach(Localize);
            root.Query<Button>().ForEach(b => b.RemoveFromClassList(Button.ussClassName));
        }

        public static void Localize(TextElement element)
        {
            string text = element.text;
            if (text != null && text.StartsWith(LocPrefix)) element.text = Loc.T(text.Substring(LocPrefix.Length));
        }

        public static void Show(VisualElement element, bool visible)
        {
            if (element == null) return;
            element.EnableInClassList(Hidden, !visible);
        }

        public static T Require<T>(VisualElement root, string name) where T : VisualElement
        {
            T element = root.Q<T>(name);
            if (element == null) throw new System.InvalidOperationException("UXML is missing a " + typeof(T).Name + " named '" + name + "'");
            return element;
        }

        public static Button MakeButton(string text, string className, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.RemoveFromClassList(Button.ussClassName);
            button.AddToClassList(className);
            return button;
        }
    }
}
