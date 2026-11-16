using System.Collections.Generic;
using Newtonsoft.Json;

namespace ScamSim.Core
{
    /// <summary>
    /// Tiny string table. Every UI string lives in Assets/Localization/th.json so an
    /// English table can be dropped in later without touching code or UXML.
    /// </summary>
    public static class Loc
    {
        static Dictionary<string, string> _table = new Dictionary<string, string>();

        public static void Load(string json)
        {
            _table = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                     ?? new Dictionary<string, string>();
        }

        public static bool Has(string key)
        {
            return _table.ContainsKey(key);
        }

        /// <summary>Returns the string for a key, or the key itself in brackets when missing.</summary>
        public static string T(string key)
        {
            string value;
            return _table.TryGetValue(key, out value) ? value : "[" + key + "]";
        }

        public static string T(string key, params object[] args)
        {
            return string.Format(T(key), args);
        }
    }
}
