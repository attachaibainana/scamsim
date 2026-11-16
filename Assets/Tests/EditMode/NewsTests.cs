using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using ScamSim.Core;
using ScamSim.News;
using UnityEngine;

namespace ScamSim.Tests
{
    public class NewsTests
    {
        const string Item = "{{\"id\":\"{0}\",\"date\":\"{1}\",\"headline\":\"h\",\"summary\":\"s\",\"scam_type\":\"loan\",\"source_name\":\"x\",\"source_url\":\"https://example.org/{0}\"{2}}}";

        [SetUp]
        public void LoadStrings()
        {
            Loc.Load(File.ReadAllText(Path.Combine(Application.dataPath, "Localization/th.json")));
        }

        static NewsFeed LoadRealFeed(List<string> problems)
        {
            string json = File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "news.json"));
            return NewsFeedParser.Parse(json, problems);
        }

        static string Feed(params string[] items)
        {
            return "{\"updated_at\":\"2026-11-16T00:00:00+07:00\",\"items\":[" + string.Join(",", items) + "]}";
        }

        /// <summary>PRD 5: the v0.1 news page shows ten real cases.</summary>
        [Test]
        public void TheBundledFeedHasTenValidItems()
        {
            var problems = new List<string>();
            NewsFeed feed = LoadRealFeed(problems);
            CollectionAssert.IsEmpty(problems);
            Assert.AreEqual(10, feed.Items.Count);
            Assert.IsNotNull(feed.UpdatedAt);

            foreach (NewsItem item in feed.Items)
            {
                Assert.IsNull(NewsFeedParser.Check(item), item.Id);
                Assert.IsFalse(item.Playable, item.Id + ": nothing is playable in v0.1");
                // PRD 2.6: keep a short summary of our own, never the article text.
                Assert.Less(item.Summary.Length, 400, item.Id);
            }
        }

        [Test]
        public void ItemsComeNewestFirstWithAStableOrder()
        {
            var problems = new List<string>();
            NewsFeed feed = NewsFeedParser.Parse(Feed(
                string.Format(Item, "b", "2026-01-02", ""),
                string.Format(Item, "c", "2026-05-01", ""),
                string.Format(Item, "a", "2026-01-02", "")), problems);
            CollectionAssert.AreEqual(new[] { "c", "a", "b" }, feed.Items.ConvertAll(i => i.Id));
        }

        [Test]
        public void BrokenItemsAreDroppedWithAReason()
        {
            var problems = new List<string>();
            NewsFeed feed = NewsFeedParser.Parse(Feed(
                string.Format(Item, "ok", "2026-01-02", ""),
                string.Format(Item, "wrong-type-field", "2026-01-02", ",\"loss_thb\":\"a lot\""),
                string.Format(Item, "tactics-not-list", "2026-01-02", ",\"tactics\":\"fear\""),
                string.Format(Item, "bad-date", "2/1/2026", ""),
                "{\"id\":\"http\",\"date\":\"2026-01-02\",\"headline\":\"h\",\"summary\":\"s\",\"scam_type\":\"loan\",\"source_name\":\"x\",\"source_url\":\"http://example.org/a\"}",
                "{\"id\":\"lottery\",\"date\":\"2026-01-02\",\"headline\":\"h\",\"summary\":\"s\",\"scam_type\":\"lottery\",\"source_name\":\"x\",\"source_url\":\"https://example.org/a\"}",
                string.Format(Item, "ok", "2026-01-03", "")), problems);

            Assert.AreEqual(1, feed.Items.Count, "one bad field must not take the whole page down");
            Assert.AreEqual("ok", feed.Items[0].Id);
            Assert.AreEqual(6, problems.Count);
            Assert.IsNull(NewsFeedParser.Parse("{oops", problems));
        }

        [Test]
        public void AMissingUpdateDateIsReported()
        {
            var problems = new List<string>();
            NewsFeed feed = NewsFeedParser.Parse("{\"items\":[" + string.Format(Item, "a", "2026-01-02", "") + "]}", problems);
            Assert.IsNull(feed.UpdatedAt);
            Assert.AreEqual(1, feed.Items.Count);
            Assert.AreEqual(1, problems.Count);
        }

        [Test]
        public void FilteringKeepsOneTypeAndListsOnlyTypesPresent()
        {
            NewsFeed feed = LoadRealFeed(new List<string>());
            List<string> types = NewsFilter.TypesPresent(feed.Items);
            CollectionAssert.IsSubsetOf(types, NewsTypes.All);

            int total = 0;
            foreach (string type in types)
            {
                List<NewsItem> ofType = NewsFilter.ByType(feed.Items, type);
                Assert.Greater(ofType.Count, 0, type);
                foreach (NewsItem item in ofType) Assert.AreEqual(type, item.ScamType);
                total += ofType.Count;
            }
            Assert.AreEqual(feed.Items.Count, total);
            Assert.AreEqual(feed.Items.Count, NewsFilter.ByType(feed.Items, null).Count);
        }

        [TestCase("2026-10-10", "10 ต.ค. 2569")]
        [TestCase("2025-01-01", "1 ม.ค. 2568")]
        [TestCase("not a date", "not a date")]
        public void DatesUseThaiMonthsAndTheBuddhistEra(string iso, string expected)
        {
            Assert.AreEqual(expected, ThaiFormat.Date(iso));
        }

        [Test]
        public void BahtHasThousandSeparators()
        {
            Assert.AreEqual("9,491,000", ThaiFormat.Baht(9491000));
            Assert.AreEqual("4,999", ThaiFormat.Baht(4999));
        }
    }
}
