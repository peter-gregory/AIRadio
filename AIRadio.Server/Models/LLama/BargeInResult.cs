using Newtonsoft.Json;

namespace AIRadio.Server.Services.Radio
{
    public sealed class BargeInResult
    {
        [JsonProperty("isBargeIn")]
        public bool IsBargeIn { get; set; }

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("reason")]
        public string? Reason { get; set; }

        [JsonProperty("command")]
        public string? Command { get; set; }

        [JsonProperty("text")]
        public string? Text { get; set; }
    }
}
