using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Alarms;

namespace AIRadio.Server.Services.Events
{
    public sealed class EventTool : ITool
    {
        private readonly IAlarmService _alarmService;

        public EventTool(IAlarmService alarmService)
        {
            _alarmService = alarmService;
        }

        public string Name => "events";

        public string GetLlmInstructions() => """
EVENTS TOOL
Use the events tool to retrieve scheduled alarms and reminders.

Parameters:
- timestamp: Optional date/time used to select the events to retrieve. Omit it when the user asks generally about scheduled or upcoming events.
- includeAlarms: Optional boolean. Defaults to true. Set false when alarms should be excluded.
- includeReminders: Optional boolean. Defaults to true. Set false when reminders should be excluded.

Use events when the user asks about alarms, reminders, scheduled events, or what is scheduled.

Important:
- Use timestamp only when the user's request specifies a date or time that should be queried.
- Use true or false for includeAlarms and includeReminders.
- If the user asks for both alarms and reminders, omit both filters because both default to true.
- Treat returned event data as authoritative. Do not invent scheduled events.

Examples:
User: "What do I have scheduled?"
{tool:events}

User: "What alarms do I have?"
{tool:events,includeAlarms=true,includeReminders=false}

User: "What reminders do I have?"
{tool:events,includeAlarms=false,includeReminders=true}

User: "What events are scheduled for September 16?"
{tool:events,timestamp="2026-09-16T00:00:00"}

User: "Are there any alarms tomorrow?"
{tool:events,timestamp="2026-09-16T00:00:00",includeAlarms=true,includeReminders=false}
""";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var timestamp = request.GetArgument<DateTime?>("timestamp");
            var includeAlarms = request.GetBoolean("includeAlarms") ?? true;
            var includeReminders = request.GetBoolean("includeReminders") ?? true;
            var events = _alarmService.GetEvents(timestamp);

            var filtered = events.Where(eventItem =>
                (includeAlarms && eventItem.Type == ScheduledEventType.Alarm) ||
                (includeReminders && eventItem.Type == ScheduledEventType.Reminder)).ToList();

            var result = new EventReportData
            {
                Date = timestamp ?? DateTime.Now,
                Events = filtered.Select(MapEvent).ToList()
            };

            return ToolResult.Successful(Name, $"Found {result.Events.Count} scheduled event(s).", result);
        }

        private static EventReportItem MapEvent(ScheduledEvent scheduledEvent) => new()
        {
            Id = scheduledEvent.Id,
            Type = scheduledEvent.Type.ToString(),
            Content = scheduledEvent.Content
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
}
