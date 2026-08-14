using AIRadio.Server.Models.Tools;
using Newtonsoft.Json;

namespace AIRadio.Server.Models.LLama
{
    public sealed class LlamaChatRequest
    {
        public string Model { get; set; } = string.Empty;

        public IReadOnlyList<LlamaMessage> Messages { get; set; }
            = Array.Empty<LlamaMessage>();

        public double Temperature { get; set; }

        public bool Stream { get; set; }
    }

    public sealed class LlamaMessage
    {
        [JsonProperty("role")]
        public string Role { get; init; } = string.Empty;

        [JsonProperty("content")]
        public string Content { get; init; } = string.Empty;

        [JsonProperty(
            "name",
            NullValueHandling = NullValueHandling.Ignore)]
        public string? Name { get; init; }

        public static LlamaMessage System(
            string content)
        {
            return new LlamaMessage
            {
                Role = "system",
                Content = content
            };
        }

        public static LlamaMessage User(
            string content)
        {
            return new LlamaMessage
            {
                Role = "user",
                Content = content
            };
        }

        public static LlamaMessage Assistant(
            string content)
        {
            return new LlamaMessage
            {
                Role = "assistant",
                Content = content
            };
        }

        public static LlamaMessage Tool(
            string name,
            string content)
        {
            return new LlamaMessage
            {
                Role = "tool",
                Name = name,
                Content = content
            };
        }
    }

    public sealed class LlamaResponse
    {
        [JsonIgnore]
        public string SpokenText { get; set; } = string.Empty;

        [JsonIgnore]
        public List<string> SoundEvents { get; } = [];

        [JsonIgnore]
        public List<ToolRequest> ToolRequests { get; } = [];

        [JsonIgnore]
        public bool HasToolRequests =>
            ToolRequests.Count > 0;

        [JsonIgnore]
        public bool HasSpeech =>
            !string.IsNullOrWhiteSpace(SpokenText);

        [JsonIgnore]
        public bool HasSoundEvents =>
            SoundEvents.Count > 0;
    }

    public sealed class LlamaChoice
    {
        public int Index { get; set; }

        public LlamaMessage? Message { get; set; }
    }
}
