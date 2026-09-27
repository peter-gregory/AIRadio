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
- Never invent sound tags such as {sound:event-reminder}; only use {sound:event-button}.
- The report is for the date shown in the tool result.
""";

    public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = request.GetArgument<DateTime?>("timestamp");
        var includeAlarms = request.GetBoolean("includeAlarms") ?? true;
        var includeReminders = request.GetBoolean("includeReminders") ?? true;
        var reportDate = (timestamp ?? DateTime.Now).Date;
        _alarmService.RemoveExpiredEvents();
        var events = _alarmService.GetEvents(timestamp);

        var result = new EventReportData
        {
            Date = reportDate,
            Events = events.Where(x =>
                (includeAlarms && x.Type == ScheduledEventType.Alarm) ||
                (includeReminders && x.Type == ScheduledEventType.Reminder)).Select(MapEvent).ToList()
        };

        AddHolidayEvent(result, DateTime.Now.Date);
        
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

        var eventSpeech = string.Join(
            " ",
            result.Events.Select(eventItem =>
            {
                var timeText = eventItem.Time.HasValue
                    ? $" at {DateTime.Today.Add(eventItem.Time.Value):h:mm tt}"
                    : string.Empty;

                var specialSound = eventItem.SpecialSound is null
                    ? string.Empty
                    : $"{{sound:{eventItem.SpecialSound}}}";

                return $"{{sound:event-button}}{specialSound}{eventItem.Type}{timeText}: {eventItem.Content}";
            }));

        var completionPrompt = result.Date == DateTime.Now.Date
            ? "And that's all the events for today"
            : $"And that's all the events for {result.Date:MMMM d}";

        return Task.FromResult(
            ToolResult.Successful(
                Name,
                $"Found {result.Events.Count} scheduled event(s).",
                data: result,
                exactPrompt: eventSpeech,
                complete: true,
                completionPrompt: completionPrompt));
    }

    private static EventReportItem MapEvent(ScheduledEvent scheduledEvent)
    {
        var content = scheduledEvent.Content;
        var specialSound = scheduledEvent.Type == ScheduledEventType.Reminder &&
            content.Contains("birthday", StringComparison.OrdinalIgnoreCase)
                ? "event-birthday"
                : null;

        return new EventReportItem
        {
            Id = scheduledEvent.Id,
            Type = scheduledEvent.Type.ToString(),
            Content = content,
            Time = scheduledEvent.When.TimeOfDay,
            SpecialSound = specialSound
        };
    }

    private static void AddHolidayEvent(EventReportData result, DateTime currentDate)
    {
        if (result.Date != currentDate.Date)
            return;

        var holiday = HolidayDefinitions.FirstOrDefault(x => x.IsMatch(currentDate));
        if (holiday is null)
            return;

        result.Events.Insert(0, new EventReportItem
        {
            Type = "Holiday",
            Content = holiday.Text,
            SpecialSound = holiday.SoundTag
        });
    }

    private sealed record HolidayDefinition(
        string Text,
        string SoundTag,
        Func<DateTime, bool> IsMatch);

    private static readonly HolidayDefinition[] HolidayDefinitions =
    [
        new("New Year's Day", "event-new-year", date => date.Month == 1 && date.Day == 1),
        new("Martin Luther King Jr. Day", "event-special", date => IsNthWeekday(date, 1, DayOfWeek.Monday, 3)),
        new("Presidents' Day", "event-special", date => IsNthWeekday(date, 2, DayOfWeek.Monday, 3)),
        new("Memorial Day", "event-special", date => IsLastWeekday(date, 5, DayOfWeek.Monday)),
        new("Juneteenth", "event-special", date => date.Month == 6 && date.Day == 19),
        new("Independence Day", "event-fourth-july", date => date.Month == 7 && date.Day == 4),
        new("Labor Day", "event-special", date => IsNthWeekday(date, 9, DayOfWeek.Monday, 1)),
        new("Columbus Day", "event-special", date => IsNthWeekday(date, 10, DayOfWeek.Monday, 2)),
        new("Veterans Day", "event-special", date => date.Month == 11 && date.Day == 11),
        new("Thanksgiving Day", "event-special", date => IsNthWeekday(date, 11, DayOfWeek.Thursday, 4)),
        new("Christmas Day", "event-christmas", date => date.Month == 12 && date.Day == 25)
    ];

    private static bool IsNthWeekday(DateTime date, int month, DayOfWeek day, int occurrence) =>
        date.Month == month &&
        date.DayOfWeek == day &&
        ((date.Day - 1) / 7) + 1 == occurrence;

    private static bool IsLastWeekday(DateTime date, int month, DayOfWeek day)
    {
        if (date.Month != month || date.DayOfWeek != day)
            return false;

        return date.AddDays(7).Month != month;
    }
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
    public string? SpecialSound { get; init; }
}
