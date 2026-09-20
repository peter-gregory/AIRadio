using Newtonsoft.Json;

namespace AIRadio.Server.Models.LLama
{
    /// <summary>
    /// An isolated Llama operation requested by a tool.
    /// Each command gets its own system prompt and input so large tool results
    /// can be processed as small, independent model requests.
    /// </summary>
    public sealed class LlmCommand
    {
        [JsonProperty("systemPrompt")]
        public string SystemPrompt { get; init; } = string.Empty;

        [JsonProperty("input")]
        public string Input { get; init; } = string.Empty;

        [JsonProperty("maxTokens")]
        public int MaxTokens { get; init; } = 32;

        public LlmCommand() { }

        public LlmCommand(string systemPrompt, string input, int maxTokens = 32)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
            ArgumentException.ThrowIfNullOrWhiteSpace(input);

            SystemPrompt = systemPrompt.Trim();
            Input = input.Trim();
            MaxTokens = maxTokens > 0
                ? maxTokens
                : throw new ArgumentOutOfRangeException(nameof(maxTokens));
        }
    }
}
