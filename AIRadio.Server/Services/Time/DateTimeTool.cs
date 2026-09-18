using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Time;

public sealed class DateTimeTool : TimeToolBase
{
    public DateTimeTool(ITimeService timeService) : base(timeService) { }

    public override string Name => "datetime";

    public override string GetLlmInstructions() => """
DATETIME
Use when the user explicitly asks for both the current date and time.
Parameters: none.
Speak the date and time naturally. Do not add location, timezone, year, or filler unless requested.
Example: "What's the date and time?" -> {tool:datetime}
""";

    public override Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var now = TimeService.GetNow();
        return Task.FromResult(ToolResult.Successful(
            Name,
            "Current local date and time retrieved.",
            new
            {
                Date = TimeService.FormatDate(now),
                Time = now.ToString("h:mm tt")
            }));
    }
}
