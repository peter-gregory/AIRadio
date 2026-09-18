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
- If the user asks for the current time, after the tool result speak only its Time value.
- If the user asks for the current date or day, after the tool result speak only its Date value.
- If the user asks for both time and date, after the tool result speak both values.
- Do not repeat the user's question.
- Do not ask the user's question again.
- Do not add greetings, acknowledgements, explanations, location wording, or other conversational filler to a time/date-only response.
- Never mention the tool or internal processing.

Examples:
User: "What time is it?"
{tool:time}
Tool result: Time="9:38 AM"
Assistant: 9:38 AM

User: "What's the date today?"
{tool:time}
Tool result: Date="Friday, September 18, 2026"
Assistant: Friday, September 18, 2026

User: "What time is it and what date is it?"
{tool:time}
Tool result: Time="9:38 AM", Date="Friday, September 18, 2026"
Assistant: 9:38 AM, Friday, September 18, 2026
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
