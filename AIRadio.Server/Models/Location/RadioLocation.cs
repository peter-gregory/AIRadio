using Newtonsoft.Json;

namespace AIRadio.Server.Models.Location
{
    public sealed class RadioLocation
    {
        [JsonProperty("raw")]
        public string Raw { get; set; } = string.Empty;

        [JsonProperty("address")]
        public string? Address { get; set; }

        [JsonProperty("city")]
        public string? City { get; set; }

        [JsonProperty("state")]
        public string? State { get; set; }

        [JsonProperty("postalCode")]
        public string? PostalCode { get; set; }

        [JsonProperty("country")]
        public string? Country { get; set; }

        [JsonProperty("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
