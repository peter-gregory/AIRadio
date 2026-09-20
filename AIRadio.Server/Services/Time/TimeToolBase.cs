using AIRadio.Server.Models.Tools;

namespace AIRadio.Server.Services.Time;

public abstract class TimeToolBase : ITool
{
    protected readonly ITimeService TimeService;

    protected TimeToolBase(ITimeService timeService) => TimeService = timeService;

    public abstract string Name { get; }
    public abstract string Intent { get; }
    public abstract string GetLlmInstructions();
    public abstract Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);

    protected static void Validate(ToolRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
