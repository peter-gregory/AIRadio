using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Time;

public abstract class TimeToolBase : ITool
{
    protected readonly ITimeService TimeService;

    protected TimeToolBase(ITimeService timeService) => TimeService = timeService;

    public abstract string Name { get; }

    public abstract string GetLlmInstructions();

    public abstract Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);

    protected static void Validate(ToolRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

public sealed class TimeTool : TimeToolBase
{
    public TimeTool(ITimeService timeService) : base(timeService) { }
    public override string Name => "time";
    public override string GetLlmInstructions() => """
TIME
Use for the current time only.
Parameters: none.
Speak only the returned Time value. Do not include the date, location, timezone, year, or filler.
Example: "What time is it?" -> {tool:time}
""";
    public override Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var now = TimeService.GetNow();
        return Task.FromResult(ToolResult.Successful(Name, "Current local time retrieved.", new { Time = now.ToString("h:mm tt") }));
    }
}

public sealed class DateTool : TimeToolBase
{
    public DateTool(ITimeService timeService) : base(timeService) { }
    public override string Name => "date";
    public override string GetLlmInstructions() => """
DATE
Use for the current date or day only.
Parameters: none.
Speak only the returned Date value. Do not include the time, location, timezone, or filler.
Example: "What date is it?" -> {tool:date}
""";
    public override Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request, cancellationToken);
        var now = TimeService.GetNow();
        return Task.FromResult(ToolResult.Successful(Name, "Current local date retrieved.", new { Date = TimeService.FormatDate(now) }));
    }
}

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
        return Task.FromResult(ToolResult.Successful(Name, "Current local date and time retrieved.", new
        {
            Date = TimeService.FormatDate(now),
            Time = now.ToString("h:mm tt")
        }));
    }
}
