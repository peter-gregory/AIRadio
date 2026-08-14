using AIRadio.Server.Models.Tools;
using Newtonsoft.Json;

namespace AIRadio.Server.Models.LLama
{
    internal sealed class LlamaResponseDto
    {
        [JsonProperty("spokenText")]
        public string? SpokenText { get; set; }

        [JsonProperty("soundEvents")]
        public List<string> SoundEvents { get; set; } = [];

        [JsonProperty("toolRequests")]
        public List<ToolRequest> ToolRequests { get; set; } = [];
    }
}
