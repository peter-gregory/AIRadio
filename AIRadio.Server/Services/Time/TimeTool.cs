using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Time;

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
        return Task.FromResult(ToolResult.Successful(
            Name,
            "Current local time retrieved.",
            new { Time = now.ToString("h:mm tt") }));
    }
}
