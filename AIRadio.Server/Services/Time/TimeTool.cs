using AIRadio.Server.Models.Tools;
using Newtonsoft.Json;

namespace AIRadio.Server.Services.Time
{
    public sealed class TimeTool : ITool
    {
        private readonly ILogger<TimeTool> _logger;

        public TimeTool(ILogger<TimeTool> logger)
        {
            _logger = logger;
            _logger.LogInformation("Finished construction TimeTool");
        }

        public string Name => "time";

        public string GetLlmInstructions() => """
TIME TOOL
Use the time tool when the user asks for the current time or date.

Parameters:
- None. The time tool accepts no parameters.

Rules:
- Call this tool directly for current time or current date requests.
- Do not call the location tool first.
- Do not ask the user for a location.
- The tool uses the radio system's configured local time zone automatically.
- Never guess the current time or date.

Examples:
User: "What time is it?"
{tool:time}

User: "What time is it right now?"
{tool:time}

User: "What's the date today?"
{tool:time}

User: "What day is it?"
{tool:time}
""";

        public Task<ToolResult> ExecuteAsync(
            ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var now = DateTimeOffset.Now;
            var result = new TimeResult
            {
                Timestamp = now,
                Time = now.ToString("h:mm tt"),
                Date = now.ToString("dddd, MMMM d, yyyy")
            };
            _logger.LogInformation("Process Tool: Time. Result = " + JsonConvert.SerializeObject(result));

            return Task.FromResult(
                ToolResult.Successful(Name, "Current local time retrieved.", result));
        }
    }

    public sealed class TimeResult
    {
        public DateTimeOffset Timestamp { get; init; }
        public string Time { get; init; } = string.Empty;
        public string Date { get; init; } = string.Empty;
    }
}
