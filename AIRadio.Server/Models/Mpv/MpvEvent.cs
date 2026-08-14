using System.Text.Json.Serialization;

namespace AIRadio.Server.Models.Mpv
{
    public abstract class MpvEvent : MpvMessage
    {
        [JsonPropertyName("event")]
        public string EventName { get; init; } = string.Empty;
    }
}
