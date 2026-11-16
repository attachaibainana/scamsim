using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ScamSim.Core;

namespace ScamSim.News
{
    /// <summary>One real scam case (PRD 2.6). Only the headline, our own summary, the link and the source name are kept.</summary>
    public class NewsItem
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("date")] public string Date;
        [JsonProperty("headline")] public string Headline;
        [JsonProperty("summary")] public string Summary;
        [JsonProperty("scam_type")] public string ScamType;
        [JsonProperty("tactics")] public List<string> Tactics = new List<string>();
        [JsonProperty("loss_thb")] public long? LossThb;
        [JsonProperty("victim_profile")] public string VictimProfile;
        [JsonProperty("source_name")] public string SourceName;
        [JsonProperty("source_url")] public string SourceUrl;
        /// <summary>v0.2: a scenario can be generated from this case. Always false in v0.1.</summary>
        [JsonProperty("playable")] public bool Playable;
    }

    public class NewsFeed
    {
        [JsonProperty("updated_at")] public string UpdatedAt;
        [JsonProperty("items")] public List<NewsItem> Items = new List<NewsItem>();
    }

    public static class NewsTypes
    {
        /// <summary>The scam_type values the schema allows, in the order filter chips are shown.</summary>
        public static readonly string[] All = { "call_center", "romance", "investment", "parcel", "job", "loan", "other" };

        public static bool IsValid(string type)
        {
            return Array.IndexOf(All, type) >= 0;
        }
    }

    public static class NewsFeedParser
    {
        /// <summary>
        /// Parses news.json and keeps only items that pass the schema checks; the reasons for any
        /// item that was dropped are returned in problems. Null when the file itself is unreadable.
        /// </summary>
        public static NewsFeed Parse(string json, List<string> problems)
        {
            JObject root;
            try
            {
                // Keep dates as the strings they are; by default Json.NET turns ISO dates into DateTime values.
                using (var reader = new JsonTextReader(new System.IO.StringReader(json)) { DateParseHandling = DateParseHandling.None })
                    root = JObject.Load(reader);
            }
            catch (JsonException e)
            {
                problems.Add("not valid JSON: " + e.Message);
                return null;
            }

            var itemsToken = root["items"] as JArray;
            if (itemsToken == null)
            {
                problems.Add("no items list");
                return null;
            }

            var feed = new NewsFeed { UpdatedAt = root["updated_at"] != null ? root["updated_at"].ToString() : null };
            DateTime updated;
            if (!TryParseIsoDate(DatePart(feed.UpdatedAt), out updated))
            {
                problems.Add("updated_at must start with YYYY-MM-DD");
                feed.UpdatedAt = null;
            }

            // Each item is read on its own, so one item with a wrong-typed field drops only that item.
            var ids = new HashSet<string>();
            for (int i = 0; i < itemsToken.Count; i++)
            {
                NewsItem item;
                try
                {
                    item = itemsToken[i].ToObject<NewsItem>();
                }
                catch (Exception e)
                {
                    problems.Add("item " + i + ": " + e.Message);
                    continue;
                }

                string problem = Check(item);
                if (problem == null && !ids.Add(item.Id)) problem = "duplicate id";
                if (problem != null)
                {
                    problems.Add((item != null && item.Id != null ? item.Id : "item " + i) + ": " + problem);
                    continue;
                }
                if (item.Tactics == null) item.Tactics = new List<string>();
                feed.Items.Add(item);
            }

            // Newest first; the id breaks ties so the order is the same on every load.
            feed.Items.Sort((a, b) =>
            {
                int byDate = string.CompareOrdinal(b.Date, a.Date);
                return byDate != 0 ? byDate : string.CompareOrdinal(a.Id, b.Id);
            });
            return feed;
        }

        public static bool TryParseIsoDate(string text, out DateTime date)
        {
            return DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        /// <summary>"2026-11-16T00:00:00+07:00" → "2026-11-16".</summary>
        public static string DatePart(string isoDateTime)
        {
            if (string.IsNullOrEmpty(isoDateTime)) return "";
            return isoDateTime.Length >= 10 ? isoDateTime.Substring(0, 10) : isoDateTime;
        }

        /// <summary>Null when the item matches the PRD schema, otherwise what is wrong with it.</summary>
        public static string Check(NewsItem item)
        {
            if (item == null) return "empty item";
            if (string.IsNullOrWhiteSpace(item.Id)) return "no id";
            DateTime date;
            if (!TryParseIsoDate(item.Date, out date)) return "date must be YYYY-MM-DD";
            if (string.IsNullOrWhiteSpace(item.Headline)) return "no headline";
            if (string.IsNullOrWhiteSpace(item.Summary)) return "no summary";
            if (!NewsTypes.IsValid(item.ScamType)) return "unknown scam_type '" + item.ScamType + "'";
            if (item.LossThb.HasValue && item.LossThb.Value < 0) return "negative loss_thb";
            if (string.IsNullOrWhiteSpace(item.SourceName)) return "no source_name";
            Uri url;
            if (!Uri.TryCreate(item.SourceUrl, UriKind.Absolute, out url) || url.Scheme != Uri.UriSchemeHttps)
                return "source_url must be an https link";
            return null;
        }
    }

    public static class NewsFilter
    {
        /// <summary>The scam types that appear in the feed, in the schema's order.</summary>
        public static List<string> TypesPresent(IList<NewsItem> items)
        {
            var present = new List<string>();
            foreach (string type in NewsTypes.All)
            {
                foreach (NewsItem item in items)
                {
                    if (item.ScamType != type) continue;
                    present.Add(type);
                    break;
                }
            }
            return present;
        }

        /// <summary>Items of one scam type, or all of them when type is null.</summary>
        public static List<NewsItem> ByType(IList<NewsItem> items, string type)
        {
            var result = new List<NewsItem>();
            foreach (NewsItem item in items)
            {
                if (type == null || item.ScamType == type) result.Add(item);
            }
            return result;
        }
    }

    public static class ThaiFormat
    {
        /// <summary>
        /// 2026-10-10 → "10 ต.ค. 2569" (Buddhist era, as Thai readers expect). Month names come from the
        /// string table (date.month.1 … date.month.12).
        /// </summary>
        public static string Date(string isoDate)
        {
            DateTime date;
            if (!NewsFeedParser.TryParseIsoDate(isoDate, out date)) return isoDate ?? "";
            return date.Day.ToString(CultureInfo.InvariantCulture) + " "
                   + Loc.T("date.month." + date.Month.ToString(CultureInfo.InvariantCulture)) + " "
                   + (date.Year + 543).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>9491000 → "9,491,000".</summary>
        public static string Baht(long amount)
        {
            return amount.ToString("#,0", CultureInfo.InvariantCulture);
        }
    }
}
