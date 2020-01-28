using System.Collections.Generic;

namespace ScrapeScteDocuments.InputModel
{
    public sealed class Post
    {
        public string Title { get; set; }
        public string Status { get; set; }

        // Sometimes duplicate keys exist in here...
        public List<Meta> Meta { get; set; }
    }
}
