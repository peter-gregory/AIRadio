using Newtonsoft.Json;

namespace AIRadio.Server.Models.Tools
{
    public sealed class ToolResult
    {
        public string ToolName { get; set; } =
            string.Empty;

        public bool Success { get; set; }

        public string? Message { get; set; }

        public object? Data { get; set; }

        /// <summary>
        /// Optional exact speech to send directly to the audio pipeline.
        /// When set, ConversationService can skip the second LLM round.
        /// </summary>
        public string? ExactPrompt { get; set; }

        /// <summary>
        /// Tool request to resume when the tool needs additional user input.
        /// Missing values are represented by ToolRequest.RequiredValue.
        /// </summary>
        public ToolRequest? PendingRequest { get; set; }

        public string? Error { get; set; }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(
                this,
                Formatting.None);
        }

        public static ToolResult Successful(
            string toolName,
            string? message = null,
            object? data = null,
            string? exactPrompt = null)
        {
            return new ToolResult
            {
                ToolName = toolName,
                Success = true,
                Message = message,
                Data = data,
                ExactPrompt = exactPrompt
            };
        }

        public static ToolResult Failed(
            string toolName,
            string error,
            string? exactPrompt = null,
            ToolRequest? pendingRequest = null)
        {
            return new ToolResult
            {
                ToolName = toolName,
                Success = false,
                Error = error,
                ExactPrompt = exactPrompt,
                PendingRequest = pendingRequest
            };
        }
    }
}
