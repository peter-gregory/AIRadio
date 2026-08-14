using AIRadio.Server.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Models.Tools
{
    public sealed class ToolResult
    {
        public string ToolName { get; set; } =
            string.Empty;

        public bool Success { get; set; }

        public string? Message { get; set; }

        public object? Data { get; set; }

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
            object? data = null)
        {
            return new ToolResult
            {
                ToolName = toolName,
                Success = true,
                Message = message,
                Data = data
            };
        }

        public static ToolResult Failed(
            string toolName,
            string error)
        {
            return new ToolResult
            {
                ToolName = toolName,
                Success = false,
                Error = error
            };
        }
    }
}
