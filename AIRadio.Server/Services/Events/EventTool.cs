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

IMPORTANT:
- Only provide timestamp when the user explicitly specifies a date or time.
- If the user says "what are the events", "what events do I have", "tell me my events", or otherwise asks for the events without specifying a date, leave timestamp omitted. Do not invent a date.
- When timestamp is omitted, the tool reports today's events.
- Use for scheduled events and requests such as "what's happening today" when the user means scheduled events.
"Events report" means this tool; report is not a separate tool.

Examples:
"What are the events?" -> {tool:events}
"What events do I have?" -> {tool:events}
"What are my events tomorrow?" -> {tool:events,timestamp="tomorrow"}
"What is on my schedule Friday?" -> {tool:events,timestamp="Friday"}
""";

    public string GetLlmResponseInstructions() => """
EVENTS REPORT RESPONSE
- Report only scheduled alarms and reminders contained in the tool result.
- Do not invent events, dates, holidays, or other information.
- Do not reinterpret the date in the tool result.
- Be concise and natural for speech.
- Do not use numbered lists or headings.
- If there are multiple events, speak each event clearly in a natural sequence.
- Begin each individual event segment with the exact speech sound tag {sound:event-button}.
- Place {sound:event-button} immediately before the spoken text for each event.
- The tag must appear before every event, including the first and last event.
- The report is for the date shown in the tool result.
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

        if (result.Events.Count == 0)
        {
            var noEventsPrompt = timestamp?.Date == DateTime.Now.Date
                ? "There are no events for today"
                : $"There are no events for {result.Date:MMMM d}";

            return Task.FromResult(
                ToolResult.Successful(
                    Name,
                    $"Found 0 scheduled event(s).",
                    data: result,
                    exactPrompt: noEventsPrompt,
                    complete: true));
        }

        return Task.FromResult(
            ToolResult.Successful(
                Name,
                $"Found {result.Events.Count} scheduled event(s).",
                result,
                completionPrompt: "And that's all the events for today"));
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
