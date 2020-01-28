using Newtonsoft.Json;
using Polly;
using Polly.Retry;
using ScrapeScteDocuments.InputModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ScrapeScteDocuments
{
    internal class Program
    {
        /// <summary>
        /// All .json files will be placed in this subdirectory of the working directory.
        /// The directory will be cleaned on start.
        /// </summary>
        public const string OutputDirectory = "SpecRef";

        public const string OutFile = "scte.json";

        public static string ScrapeUrl = "https://api.scte-website-cms.com/api/v1/standards/category/non-public";

        // Retry if something goes wrong (it shouldn't but the web is the web).
        private static RetryPolicy<string> ScrapePolicy = Policy<string>.Handle<Exception>().Retry(3);

        private sealed class Entry
        {
            public int SortIndex { get; set; }

            public string Url { get; set; }
            public string Title { get; set; }
            public string RawDate { get; set; }
            public string Status { get; set; }

            public string[] Aliases { get; set; }
        }

        // Example document names:
        // ANSI/SCTE 05 2014
        // SCTE 06 2019
        // ANSI/SCTE 24-02 2016
        // ANSI/SCTE 82-2012
        // 
        // The base ID is whatever is after "SCTE" and the year.
        // Sometimes the final space is a dash instead...

        // 1 is base ID, 2 is year
        private static readonly Regex ExtractIdComponentsRegex = new Regex(@"SCTE ([0-9-]+?)(?: |-)(\d{4})", RegexOptions.Compiled);

        public static (string id, string rawDate, string[] aliases) ParseStandardNumber(string standardNumber)
        {
            var match = ExtractIdComponentsRegex.Match(standardNumber);

            if (!match.Success)
                throw new Exception("Failed to parse standard number: " + standardNumber);

            var baseName = match.Groups[1].Value;
            var rawDate = match.Groups[2].Value;

            // It appears that standard numbers of the format 24-02 were renamed with the SCTE website update.
            // Now they are listed as 24-2 and cause broken references. Let's add aliases to keep the old 24-02 working.
            // Aaaaand sometimes it's the opposite, with 135-3 becoming 135-03! Add this direction of alias, too.
            var aliases = new List<string>();
            var lastDashIndex = baseName.LastIndexOf('-');

            if (lastDashIndex != -1)
            {
                var prefinalPart = baseName.Substring(0, lastDashIndex + 1);
                var finalPart = baseName.Substring(lastDashIndex + 1);

                // If it is -1, make an alias -01
                if (finalPart.Length == 1)
                    aliases.Add("scte" + prefinalPart + "0" + finalPart);

                // If it is -01, make an alias -1
                if (finalPart.Length == 2 && finalPart[0] == '0')
                    aliases.Add("scte" + prefinalPart + finalPart[1]);
            }

            return ("scte" + baseName, rawDate, aliases.ToArray());
        }

        private static void Main(string[] args)
        {
            if (Directory.Exists(OutputDirectory))
                Directory.Delete(OutputDirectory, true);
            Directory.CreateDirectory(OutputDirectory);

            Console.WriteLine("Output will be saved in " + Path.GetFullPath(OutputDirectory));

            var client = new HttpClient();

            var entries = new Dictionary<string, Entry>();

            var documentIndex = 1;

            Console.WriteLine($"Loading catalog page: {ScrapeUrl}");

            var pageJson = ScrapePolicy.Execute(() => client.GetStringAsync(ScrapeUrl).Result);

            var categories = JsonConvert.DeserializeObject<Category[]>(pageJson);

            var documents = categories.SelectMany(c => c.Posts).ToArray();

            Console.WriteLine($"Found {documents.Length} documents.");

            var knownUrls = new List<string>();

            foreach (var document in documents)
            {
                var standardNumber = document.Meta.First(m => m.Key == "standardNumber").Value;
                (var id, var rawDate, var aliases) = ParseStandardNumber(standardNumber);
                var absoluteUrl = document.Meta.First(m => m.Key == "PDF").Value;
                var title = document.Title;

                if (string.IsNullOrWhiteSpace(title))
                    throw new Exception("Empty title for " + standardNumber);

                if (string.IsNullOrWhiteSpace(absoluteUrl))
                    throw new Exception("Empty URL for " + standardNumber);

                if (knownUrls.Contains(absoluteUrl))
                {
                    // SCTE catalog seems to have some errors... ??? Whatever, just skip for now.
                    // 231 and 232 are in conflict at time of writing (both use 231 URL).
                    Console.WriteLine($"Skipping {id} because it reuses a URL already used for another document: {absoluteUrl}");
                    continue;
                }

                knownUrls.Add(absoluteUrl);

                Console.WriteLine($"{standardNumber} is titled \"{title}\", available at {absoluteUrl} and will get the ID {id}");

                if (aliases.Length != 0)
                    Console.WriteLine($"It is also called {string.Join(", ", aliases)}");

                var entry = new Entry
                {
                    // We use the same sorting as on the website.
                    SortIndex = documentIndex++,

                    Url = absoluteUrl,
                    Title = $"{standardNumber}: {title}",
                    Status = document.Status,
                    Aliases = aliases
                };

                entries[id] = entry;
            }

            if (entries.Count == 0)
                throw new Exception("Loaded no entries."); // Sanity check.

            // Ok, we got all our entries. Serialize.
            var json = new Dictionary<string, object>(entries.Count);

            foreach (var pair in entries.OrderBy(p => p.Value.SortIndex))
            {
                json[pair.Key] = new
                {
                    href = pair.Value.Url,
                    title = pair.Value.Title,
                    publisher = "SCTE",
                    rawDate = pair.Value.RawDate,
                    status = pair.Value.Status
                };

                foreach (var alias in pair.Value.Aliases)
                {
                    json[alias] = new
                    {
                        aliasOf = pair.Key
                    };
                }
            }

            var outputFilePath = Path.Combine(OutputDirectory, OutFile);
            File.WriteAllText(outputFilePath, JsonConvert.SerializeObject(json, JsonSettings), OutputEncoding);
        }

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Newtonsoft.Json.Formatting.Indented,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

        private static readonly Encoding OutputEncoding = new UTF8Encoding(false);
    }
}
