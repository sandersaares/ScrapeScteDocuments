using HtmlAgilityPack;
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

        public static string[] CatalogPagesToScrape = new[]
        {
            "https://www.scte.org/SCTE/Standards/Download_SCTE_Standards.aspx",
            "http://www.scte.org/SCTE/Standards/SCTE_Standards_Page_2.aspx",
            "http://www.scte.org/SCTE/Standards/SCTE_Standards_Page_3.aspx"
        };

        // Retry if something goes wrong (it shouldn't but the web is the web).
        private static RetryPolicy<string> ScrapePolicy = Policy<string>.Handle<Exception>().Retry(3);

        private sealed class Entry
        {
            public int SortIndex { get; set; }

            public string Url { get; set; }
            public string Title { get; set; }
            public string RawDate { get; set; }
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

        public static (string id, string rawDate) ParseTitle(string title)
        {
            var match = ExtractIdComponentsRegex.Match(title);

            if (!match.Success)
                throw new Exception("Failed to parse document title: " + title);

            var baseName = match.Groups[1].Value;
            var rawDate = match.Groups[2].Value;

            return ("scte" + baseName, rawDate);
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

            foreach (var pageUrl in CatalogPagesToScrape)
            {
                var page = new HtmlDocument();

                Console.WriteLine($"Loading catalog page: {pageUrl}");

                var pageHtml = ScrapePolicy.Execute(() => client.GetStringAsync(pageUrl).Result);
                page.LoadHtml(pageHtml);

                // Page 1.
                var dataTable = page.DocumentNode.SelectSingleNode("//div[@class='iMIS-WebPart']/div/div/html/body/table");

                // Pages 2 and 3.
                if (dataTable == null)
                    dataTable = page.DocumentNode.SelectSingleNode("//div[@class='iMIS-WebPart']/div/div/table");

                var documents = dataTable.SelectNodes("tbody/tr");

                Console.WriteLine($"Found {documents.Count} documents.");

                // First 2 are header/spacer rows. Last one is footer row.
                foreach (var document in documents.Skip(2).Take(documents.Count - 3))
                {
                    var link = document.SelectSingleNode("td[1]//a");

                    if (link == null)
                    {
                        if (document.InnerText.Contains("Please note:"))
                            continue; // Some rows are editorial notes. Ignore.

                        if (document.InnerText.Contains("ANSI/SCTE 141 2007"))
                            continue; // They have deliberately omitted link as this document was replaced by another. Okay, whatever.

                        throw new Exception("Unable to find document link in fragment: " + document.InnerText);
                    }

                    // Sometimes there is garbage spacing - remove it.
                    var title = HtmlEntity.DeEntitize(link.InnerText).Trim();

                    // Sometimes there are just a bunch of repeated spaces.
                    while (title.Contains("  "))
                        title = title.Replace("  ", " ");

                    var rawUrl = link.GetAttributeValue("href", null);

                    // Sometimes the URL is bad.
                    rawUrl = rawUrl.Replace(":/www", "://www");

                    var relativeUrl = new Uri(rawUrl, UriKind.RelativeOrAbsolute);
                    var absoluteUrl = new Uri(new Uri(pageUrl), relativeUrl);

                    // Some are in <span>, some are not. Prefer plaintext if available, fallback to span.
                    // Some are in <div>, fallback to that if span gives nothing.
                    // Some are in <p>. Depends on the day/moon/rainbow.
                    var summaryElement = document.SelectSingleNode("td[2]/text()[1]");

                    if (summaryElement == null || string.IsNullOrWhiteSpace(summaryElement.InnerText))
                        summaryElement = document.SelectSingleNode("td[2]/span/text()[1]");

                    if (summaryElement == null || string.IsNullOrWhiteSpace(summaryElement.InnerText))
                        summaryElement = document.SelectSingleNode("td[2]/div/text()[1]");

                    if (summaryElement == null || string.IsNullOrWhiteSpace(summaryElement.InnerText))
                        summaryElement = document.SelectSingleNode("td[2]/p/text()[1]");

                    if (summaryElement == null)
                        throw new Exception("Unable to find summary text for " + title);

                    // Sometimes there is garbage spacing - remove it.
                    var summary = HtmlEntity.DeEntitize(summaryElement.InnerText).Trim();

                    // Sometimes there is newlines.
                    summary = summary.Trim()
                        .Replace('\r', ' ')
                        .Replace('\n', ' ');

                    // Sometimes there are many spaces.
                    while (summary.Contains("  "))
                        summary = summary.Replace("  ", " ");

                    (var id, var rawDate) = ParseTitle(title);

                    Console.WriteLine($"{title} is titled \"{summary}\", available at {absoluteUrl} and will get the ID {id}");

                    var entry = new Entry
                    {
                        // We use the same sorting as on the website.
                        SortIndex = documentIndex++,

                        Url = absoluteUrl.ToString(),
                        Title = $"{title}: {summary}",
                        RawDate = rawDate
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
                    publisher = "SCTE",
                    rawDate = pair.Value.RawDate
                };
            }

            var outputFilePath = Path.Combine(OutputDirectory, OutFile);
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
