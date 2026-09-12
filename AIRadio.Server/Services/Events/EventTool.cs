using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Alarms;

namespace AIRadio.Server.Services.Events
{
    public sealed class EventTool : ITool
    {
        private readonly IAlarmManagerService _alarmManager;

        public EventTool(IAlarmManagerService alarmManager)
        {
            _alarmManager = alarmManager;
        }

        public string Name => "events";

        public string GetPromptText() => "events{timestamp?;includeAlarms?=true;includeReminders?=true}";

        public async Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();

            var timestamp = request.GetArgument<DateTime?>("timestamp");
            var includeAlarms = request.GetBoolean("includeAlarms") ?? true;
            var includeReminders = request.GetBoolean("includeReminders") ?? true;
            var events = _alarmManager.GetEvents(timestamp);

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
