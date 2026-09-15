using AIRadio.Server.Models.Tools;
using Newtonsoft.Json;

namespace AIRadio.Server.Models.LLama
{
    public sealed class LlamaChatRequest
    {
        public string Model { get; set; } = string.Empty;
        public IReadOnlyList<LlamaMessage> Messages { get; set; } = Array.Empty<LlamaMessage>();
        public double Temperature { get; set; }
        public bool Stream { get; set; }
    }

    public sealed class LlamaMessage
    {
        [JsonProperty("role")]
        public string Role { get; init; } = string.Empty;

        [JsonProperty("content")]
        public string Content { get; init; } = string.Empty;

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string? Name { get; init; }

        public static LlamaMessage System(string content) => new() { Role = "system", Content = content };
        public static LlamaMessage User(string content) => new() { Role = "user", Content = content };
        public static LlamaMessage Assistant(string content) => new() { Role = "assistant", Content = content };
        public static LlamaMessage Tool(string name, string content) => new() { Role = "tool", Name = name, Content = content };
    }

    public sealed class LlamaResponse
    {
        // SpokenText is the original ordered response stream. Sound tags remain
        // inline so AudioManager can preserve their exact playback position.
        [JsonIgnore]
        public string SpokenText { get; set; } = string.Empty;

        // Tool requests are side-channel directives and are extracted from the
        // response so they can execute independently of spoken playback.
        [JsonIgnore]
        public List<ToolRequest> ToolRequests { get; } = [];

        [JsonIgnore]
        public bool HasToolRequests => ToolRequests.Count > 0;

        [JsonIgnore]
        public bool HasSpeech => !string.IsNullOrWhiteSpace(SpokenText);
    }

    public sealed class LlamaChoice
    {
        public int Index { get; set; }
        public LlamaMessage? Message { get; set; }
    }
}
