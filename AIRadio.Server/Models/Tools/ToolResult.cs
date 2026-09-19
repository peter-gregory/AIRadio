using Newtonsoft.Json;

namespace AIRadio.Server.Models.Tools
{
    public enum ToolResultStatus
    {
        Result,
        MissingParameter,
        Preamble
    }

    public sealed class ToolResult
    {
        public string ToolName { get; set; } = string.Empty;

        public ToolResultStatus Status { get; set; }

        public bool Success { get; set; }

        public string? Message { get; set; }

        public object? Data { get; set; }

        /// <summary>
        /// Direct speech for MissingParameter or Preamble.
        /// May contain embedded sound-effect tags.
        /// </summary>
        public string? ExactPrompt { get; set; }

        /// <summary>
        /// Tool request to resume after MissingParameter or Preamble.
        /// </summary>
        public ToolRequest? PendingRequest { get; set; }

        public string? Error { get; set; }

        public string ToJson() =>
            JsonConvert.SerializeObject(this, Formatting.None);

        public static ToolResult Successful(
            string toolName,
            string? message = null,
            object? data = null,
            string? exactPrompt = null)
        {
            return new ToolResult
            {
                ToolName = toolName,
                Status = ToolResultStatus.Result,
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
                Status = ToolResultStatus.Result,
                Success = false,
                Error = error,
                ExactPrompt = exactPrompt,
                PendingRequest = pendingRequest
            };
        }

        public static ToolResult MissingParameter(
            string toolName,
            string prompt,
            ToolRequest pendingRequest)
        {
            ArgumentNullException.ThrowIfNull(pendingRequest);

            return new ToolResult
            {
                ToolName = toolName,
                Status = ToolResultStatus.MissingParameter,
                Success = false,
                ExactPrompt = prompt,
                PendingRequest = pendingRequest
            };
        }

        public static ToolResult Preamble(
            string toolName,
            string prompt,
            ToolRequest continuation)
        {
            ArgumentNullException.ThrowIfNull(continuation);

            return new ToolResult
            {
                ToolName = toolName,
                Status = ToolResultStatus.Preamble,
                Success = true,
                ExactPrompt = prompt,
                PendingRequest = continuation
            };
        }
    }
}