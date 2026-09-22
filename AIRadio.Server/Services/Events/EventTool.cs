using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Alarms;

namespace AIRadio.Server.Services.Events;

public sealed class EventTool : ITool
{
    private readonly IAlarmService _alarmService;
    public EventTool(IAlarmService alarmService) => _alarmService = alarmService;

    public bool HasParameters => true;
    public string GetLlmRequestTemplate() => "{tool:events,timestamp=<optional>,includeAlarms=<optional>,includeReminders=<optional>}";
    public string Name => "events";
    public string Intent => "Retrieve scheduled alarms and reminders.";

    public string GetLlmInstructions() => """
EVENTS
Retrieve scheduled alarms and reminders.
Parameters:
- timestamp: optional date/time to inspect.
- includeAlarms: optional; defaults to true.
- includeReminders: optional; defaults to true.
Use for scheduled events and requests such as "what's happening today" when the user means scheduled events.
"Events report" means this tool; report is not a separate tool.
""";

    public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var timestamp = request.GetArgument<DateTime?>("timestamp");
        var includeAlarms = request.GetBoolean("includeAlarms") ?? true;
        var includeReminders = request.GetBoolean("includeReminders") ?? true;
        var events = _alarmService.GetEvents(timestamp);
        var result = new EventReportData
        {
            Date = timestamp ?? DateTime.Now,
            Events = events.Where(x =>
                (includeAlarms && x.Type == ScheduledEventType.Alarm) ||
                (includeReminders && x.Type == ScheduledEventType.Reminder)).Select(MapEvent).ToList()
        };
        return Task.FromResult(ToolResult.Successful(Name, $"Found {result.Events.Count} scheduled event(s).", result));
    }

    private static EventReportItem MapEvent(ScheduledEvent scheduledEvent) => new()
    {
        Id = scheduledEvent.Id,
        Type = scheduledEvent.Type.ToString(),
        Content = scheduledEvent.Content,
        Time = scheduledEvent.When.TimeOfDay
    };
}

public sealed class EventReportData
{
    public DateTime Date { get; init; }
    public List<EventReportItem> Events { get; init; } = [];
}

public sealed class EventReportItem
{
    public Guid Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public TimeSpan? Time { get; init; }
}
