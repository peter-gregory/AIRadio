using AIRadio.Server.Models.Tools;

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
- The tool result is authoritative.
- Do not repeat or ask the user's question.
- Never mention the tool or internal processing.

TIME RESULT
- Use only values returned by the tool.
- Time only: speak the time naturally. Example: "The time is 10:05 AM."
- Date/day only: speak the date naturally. Example: "Today is Friday, September 18th."
- Time and date: speak both naturally. Example: "Today is Friday, September 18th. The time is 10:05 AM."
- Never include Time when only Date was requested.
- Never include Date when only Time was requested.
- Do not add location, timezone, year, greeting, filler, or explanation unless requested.
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
