using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using UnityEngine;

namespace MultiSkyLineII
{
    public static class LocalizationCatalog
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> Cache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static string NormalizeLocale(string locale)
        {
            if (string.Equals(locale, "fr-FR", StringComparison.OrdinalIgnoreCase))
                return "fr-FR";
            return "en-US";
        }

        public static string GetText(string locale, string key, string fallback)
        {
            var current = GetTable(locale);
            if (current.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;

            var english = GetTable("en-US");
            if (english.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
                return value;

            return fallback;
        }

        public static Dictionary<string, string> GetTable(string locale)
        {
            var normalized = NormalizeLocale(locale);
            lock (Sync)
            {
                if (Cache.TryGetValue(normalized, out var existing))
                    return existing;

                var loaded = LoadTableCore(normalized);
                Cache[normalized] = loaded;
                return loaded;
            }
        }

        public static void Invalidate()
        {
            lock (Sync)
            {
                Cache.Clear();
            }
        }

        private static Dictionary<string, string> LoadTableCore(string locale)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var filePath = Path.Combine(Application.persistentDataPath, "Mods", "MultiSkyLinesII", "Localization", $"{locale}.json");
                if (!File.Exists(filePath))
                {
                    filePath = Path.Combine(Application.persistentDataPath, "Mods", "MultiSkyLineII", "Localization", $"{locale}.json");
                }
                if (!File.Exists(filePath))
                    return map;

                var json = File.ReadAllText(filePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                    return map;

                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(LocaleFile));
                    if (!(serializer.ReadObject(stream) is LocaleFile data) || data.entries == null)
                        return map;

                    for (var i = 0; i < data.entries.Count; i++)
                    {
                        var e = data.entries[i];
                        if (string.IsNullOrWhiteSpace(e?.key))
                            continue;
                        map[e.key] = e.value ?? string.Empty;
                    }
                }
            }
            catch (Exception e)
            {
                ModDiagnostics.Write($"Locale load failed for {locale}: {e.Message}");
            }

            return map;
        }

        [DataContract]
        private sealed class LocaleFile
        {
            [DataMember(Name = "entries")]
            public List<LocaleEntry> entries { get; set; }
        }

        [DataContract]
        private sealed class LocaleEntry
        {
            [DataMember(Name = "key")]
            public string key { get; set; }

            [DataMember(Name = "value")]
            public string value { get; set; }
        }
    }
}
