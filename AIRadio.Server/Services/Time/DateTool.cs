using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Time;

public sealed class DateTool : TimeToolBase
{
    public DateTool(ITimeService timeService) : base(timeService) { }

    public override string Name => "date";
    public override string Intent => "Get the current date.";

    public override string GetLlmInstructions() => """
DATE
Use for the current date or day only.
Parameters: none.
Example: "What date is it?" -> {tool:date}
""";

    public override Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var now = TimeService.GetNow();
        var date = TimeService.FormatDate(now);
        var speech = $"Today's date is {date}.";

        return Task.FromResult(ToolResult.Successful(
            Name,
            "Current local date retrieved.",
            new { Date = date },
            speech));
    }
}
