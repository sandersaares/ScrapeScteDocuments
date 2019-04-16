using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using Newtonsoft.Json;
using Polly;
using Polly.Retry;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace ScrapeEtsiDocuments
{
    internal class Program
    {
        /// <summary>
        /// All .json files will be placed in this subdirectory of the working directory.
        /// The directory will be cleaned on start.
        /// </summary>
        public const string OutputDirectory = "SpecRef";

        public const string Outfile = "etsi.json";

        // version=0 - major versions only
        // version=1 - all versions
        public const string InputUrl = "https://www.etsi.org/?option=com_standardssearch&view=data&format=csv&page=1&search=&title=1&etsiNumber=1&content=1&version=0&onApproval=1&published=1&historical=1&startDate=1988-01-15&endDate=2019-04-16&harmonized=0&keyword=&TB=&stdType=&frequency=&mandate=&collection=&sort=4&x=1555402737775";

        // ETSI website does not seem to fail too often but if it does, we retry.
        private static RetryPolicy<string> ScrapePolicy = Policy<string>.Handle<Exception>().Retry(5);

        private sealed class Entry
        {
            public int SortIndex { get; set; }

            public string Url { get; set; }

            public string Title { get; set; }
            public string Status { get; set; }

            // YYYY[-MM[-DD]]
            public string RawDate { get; set; }

            // Only used for comparison (which one to keep if we have multiple with same ID)
            public Version Version { get; set; }

            public bool IsRetired { get; set; }
        }

        private sealed class CatalogItem
        {
            [Name("id")]
            public int EtsiId { get; set; }

            [Name("ETSI deliverable")]
            public string Title { get; set; }

            [Name("title")]
            public string Summary { get; set; }

            [Name("Status")]
            public string Status { get; set; }

            [Name("Details link")]
            public string DetailsUrl { get; set; }

            [Name("PDF link")]
            public string PdfUrl { get; set; }
        }

        // Example document names:
        // ETSI TR 101 290 V1.2.1 (2001-05)
        // ETSI GS NFV-EVE 005 V1.1.1 (2015-12)
        // ETSI EN 303 347-3 V1.1.0 (2019-04)
        // ETSI ETS 300 347-1/A1 ed.1 (1997-05)

        // The base ID starts after ETSI and ends before the version.
        // Version is either Vx.x.x or ed.x (new VS old style)
        // Special characters and spaces are converted to dash.
        // Text in lowercase.
        // So the above examples would become:
        // tr101290
        // gs-nfv-eve-005
        // en-303-347-3
        // ets-300-347-1-a1

        // 1 is basename, 2 is version, 3 is rawdate
        private static readonly Regex ExtractIdComponentsRegex = new Regex(@"ETSI (.+?) (?:V|ed\.)(\d+\.\d+\.\d+|\d+) \((\d{4}-\d{2})\)", RegexOptions.Compiled);

        public static (string id, Version version, string rawDate) ParseTitle(string title)
        {
            var match = ExtractIdComponentsRegex.Match(title);

            if (!match.Success)
                throw new Exception("Failed to parse document title: " + title);

            var baseName = match.Groups[1].Value;
            var versionString = match.Groups[2].Value;
            var rawDate = match.Groups[3].Value;

            var fixedBaseName = baseName
                .ToLowerInvariant()
                .Replace(" ", "-")
                .Replace("/", "-");

            if (!versionString.Contains("."))
                versionString += ".0"; // Version class requires at least major.minor

            if (!Version.TryParse(versionString, out var version))
                throw new Exception("Unable to parse document version: " + title);

            return ("etsi-" + fixedBaseName, version, rawDate);
        }

        private static void Main(string[] args)
        {
            if (Directory.Exists(OutputDirectory))
                Directory.Delete(OutputDirectory, true);
            Directory.CreateDirectory(OutputDirectory);

            Console.WriteLine("Output will be saved in " + Path.GetFullPath(OutputDirectory));

            var client = new HttpClient
            {
                // Big document, give it some time.
                Timeout = TimeSpan.FromSeconds(300)
            };

            var entries = new Dictionary<string, Entry>();

            Console.WriteLine($"Loading catalog CSV: {InputUrl}");

            var csvString = ScrapePolicy.Execute(() => client.GetStringAsync(InputUrl).Result);

            Console.WriteLine($"Loaded {csvString.Length} characters in response.");

            // There is a "sep=;" line at the start, which confuses CsvReader.
            csvString = csvString.Substring(7);

            var documentIndex = 1;

            using (var reader = new CsvHelper.CsvReader(new StringReader(csvString), new Configuration
            {
                Delimiter = ";",
            }))
            {
                foreach (var item in reader.GetRecords<CatalogItem>())
                {
                    (var id, var version, var rawDate) = ParseTitle(item.Title);

                    Console.WriteLine($"{item.Title} [{item.Status}] is titled \"{item.Summary}\" and will get the ID {id}");

                    if (entries.ContainsKey(id))
                    {
                        if (entries[id].Version < version)
                        {
                            // We only overwrite with "On approval" entries if the old entry is also "On approval".
                            // This is to avoid overwriting published documents with prerelease documents.
                            if (item.Status == "On Approval" && entries[id].Status != "On Approval")
                            {
                                Console.WriteLine($"Skipping {id} because even though it is a newer version, the new version is not yet published.");
                                continue;
                            }

                            Console.WriteLine($"Overwriting {id} because this one is a more newer version ({entries[id].Version} < {version}).");
                        }
                        else if (entries[id].Version > version)
                        {
                            Console.WriteLine($"Skipping {id} because we already have a more preferred version ({entries[id].Version} > {version}).");
                            continue;
                        }
                        else
                        {
                            throw new Exception("Unexpected duplicate ID that does not seem to be a different version of the same document: " + id);
                        }
                    }

                    var entry = new Entry
                    {
                        // We use the same sorting as on the website.
                        SortIndex = documentIndex++,

                        IsRetired = item.Status == "Historical",
                        RawDate = rawDate,
                        Status = item.Status,
                        Title = $"{item.Title}: {item.Summary}",
                        Version = version,
                        Url = item.PdfUrl
                    };

                    entries[id] = entry;
                }
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
                    status = pair.Value.Status,
                    publisher = "European Telecommunications Standards Insitute",
                    isRetired = pair.Value.IsRetired,
                    rawDate = pair.Value.RawDate
                };
            }

            var outputFilePath = Path.Combine(OutputDirectory, Outfile);
            File.WriteAllText(outputFilePath, JsonConvert.SerializeObject(json, JsonSettings), OutputEncoding);
        }

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

        private static readonly Encoding OutputEncoding = new UTF8Encoding(false);
    }
}
