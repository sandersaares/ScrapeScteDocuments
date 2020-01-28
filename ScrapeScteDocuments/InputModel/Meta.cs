using Newtonsoft.Json;

namespace ScrapeScteDocuments.InputModel
{
    public sealed class Meta
    {
        [JsonProperty("meta_key")]
        public string Key { get; set; }

        [JsonProperty("meta_value")]
        public string Value { get; set; }
    }
}
