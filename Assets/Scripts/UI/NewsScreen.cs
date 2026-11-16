using System;
using System.Collections.Generic;
using ScamSim.Core;
using ScamSim.News;
using UnityEngine;
using UnityEngine.UIElements;

namespace ScamSim.UI
{
    /// <summary>
    /// "สถานการณ์ล่าสุด" (PRD 2.6): real scam cases from news.json, newest first, filterable by scam
    /// type, each with a link to the original report. Practising a case is shown but not available yet.
    /// </summary>
    public class NewsScreen : IDisposable
    {
        readonly Label _status;
        readonly VisualElement _filters;
        readonly ScrollView _list;
        List<NewsItem> _items = new List<NewsItem>();

        public VisualElement Root { get; private set; }
        public event Action BackClicked;

        /// <summary>The scam type being shown, or null for all of them.</summary>
        public string ActiveType { get; private set; }

        public int CardCount
        {
            get { return _list.contentContainer.childCount; }
        }

        public NewsScreen(VisualElement root)
        {
            Root = root;
            _status = UiKit.Require<Label>(root, "news-status");
            _filters = UiKit.Require<VisualElement>(root, "filters");
            _list = UiKit.Require<ScrollView>(root, "news-list");
            UiKit.Require<Button>(root, "back").clicked += () =>
            {
                if (BackClicked != null) BackClicked();
            };
            _status.text = Loc.T("news.loading");
        }

        /// <summary>Nothing to release: the screen holds no subscriptions. Kept for the screen contract in GameSession.</summary>
        public void Dispose()
        {
        }

        public void ShowFeed(NewsFeed feed)
        {
            if (feed == null || feed.Items.Count == 0)
            {
                _status.text = Loc.T("news.load_failed");
                return;
            }

            _items = feed.Items;
            _status.text = feed.UpdatedAt != null
                ? Loc.T("news.updated", ThaiFormat.Date(NewsFeedParser.DatePart(feed.UpdatedAt)))
                : Loc.T("news.subtitle");
            BuildFilters();
            Select(null);
        }

        /// <summary>Shows only one scam type (null for all). Used by the filter chips.</summary>
        public void Select(string type)
        {
            ActiveType = type;
            foreach (Button chip in _filters.Query<Button>().ToList())
                chip.EnableInClassList("selected", (chip.userData as string) == type);

            _list.Clear();
            foreach (NewsItem item in NewsFilter.ByType(_items, type)) _list.Add(Card(item));
            _list.scrollOffset = Vector2.zero;
        }

        void BuildFilters()
        {
            _filters.Clear();
            _filters.Add(Chip(Loc.T("news.filter_all"), null));
            foreach (string type in NewsFilter.TypesPresent(_items)) _filters.Add(Chip(Loc.T("news.type." + type), type));
        }

        Button Chip(string text, string type)
        {
            Button chip = UiKit.MakeButton(text, "filter-chip", () => Select(type));
            chip.name = "filter-" + (type ?? "all");
            chip.userData = type;
            return chip;
        }

        static VisualElement Card(NewsItem item)
        {
            var card = new VisualElement { name = "news-" + item.Id };
            card.AddToClassList("news-card");

            var top = new VisualElement();
            top.AddToClassList("news-card-top");
            top.Add(Text(ThaiFormat.Date(item.Date), "news-date"));
            var tag = new VisualElement();
            tag.AddToClassList("news-tag");
            tag.AddToClassList("type-" + item.ScamType);
            tag.Add(Text(Loc.T("news.type." + item.ScamType), "news-tag-text"));
            top.Add(tag);
            card.Add(top);

            card.Add(Text(item.Headline, "news-headline"));
            card.Add(Text(item.Summary, "news-summary"));

            var facts = new List<string>();
            if (item.LossThb.HasValue) facts.Add(Loc.T("news.loss", ThaiFormat.Baht(item.LossThb.Value)));
            if (!string.IsNullOrEmpty(item.VictimProfile)) facts.Add(Loc.T("news.victim", item.VictimProfile));
            facts.Add(Loc.T("news.source", item.SourceName));
            card.Add(Text(string.Join("  ·  ", facts.ToArray()), "news-facts"));

            var actions = new VisualElement();
            actions.AddToClassList("button-row");
            // Works with mouse, touch and keyboard. On WebGL a popup blocker may still stop it; that is
            // checked with the first tested WebGL build.
            Button read = UiKit.MakeButton(Loc.T("news.read_source"), "secondary-button", () => Application.OpenURL(item.SourceUrl));
            read.name = "read-source";
            actions.Add(read);

            // v0.1 has no scenario generator yet: the button is shown but always off (PRD 2.6), whatever playable says.
            Button practice = UiKit.MakeButton(Loc.T("news.practice") + " · " + Loc.T("news.coming_soon"), "secondary-button", null);
            practice.name = "practice";
            practice.SetEnabled(false);
            actions.Add(practice);
            card.Add(actions);
            return card;
        }

        static Label Text(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }
    }
}
