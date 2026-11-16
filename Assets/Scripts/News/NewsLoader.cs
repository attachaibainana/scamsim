using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace ScamSim.News
{
    /// <summary>
    /// Reads StreamingAssets/news.json (PRD 2.6, v0.1). On WebGL StreamingAssets is a URL, so it is
    /// fetched over HTTP; on desktop it is a plain file. v0.2 can point the same loader at a remote URL.
    /// </summary>
    public static class NewsLoader
    {
        public const string FileName = "news.json";

        public static string Location
        {
            get { return Path.Combine(Application.streamingAssetsPath, FileName); }
        }

        /// <summary>Calls back with the feed (null when unreadable) and any problems found, for the developer log.</summary>
        public static IEnumerator Load(Action<NewsFeed, List<string>> onDone)
        {
            var problems = new List<string>();
            string location = Location;
            string json = null;

            if (location.Contains("://"))
            {
                using (UnityWebRequest request = UnityWebRequest.Get(location))
                {
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    while (!operation.isDone) yield return null;
                    if (request.result == UnityWebRequest.Result.Success) json = request.downloadHandler.text;
                    else problems.Add("could not download: " + request.error);
                }
            }
            else
            {
                json = ReadFile(location, problems);
            }

            NewsFeed feed = json != null ? NewsFeedParser.Parse(json, problems) : null;
            onDone(feed, problems);
        }

        static string ReadFile(string path, List<string> problems)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception e)
            {
                problems.Add("could not read: " + e.Message);
                return null;
            }
        }
    }
}
