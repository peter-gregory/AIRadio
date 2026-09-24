using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Time;

public sealed class DateTimeTool : TimeToolBase
{
    public DateTimeTool(ITimeService timeService) : base(timeService) { }

    public override string Name => "datetime";
    public override string Intent => "Get the current date and time.";

    public override string GetLlmInstructions() => """
DATETIME
Use when the user explicitly asks for both the current date and time.
Parameters: none.
Example: "What's the date and time?" -> {tool:datetime}
""";

    public override Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);

        var now = TimeService.GetNow();
        var date = TimeService.FormatDate(now);
        var time = TimeSpeechFormatter.Format(now);
        var speech = $"Today is {date}, {now:yyyy} at {time}.";

        return Task.FromResult(ToolResult.Successful(
            Name,
            "Current local date and time retrieved.",
            new
            {
                Date = date,
                Year = now.Year,
                Time = time
            },
            exactPrompt: speech,
            complete: true));
    }
}
