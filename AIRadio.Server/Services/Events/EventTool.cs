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

        AddHolidayEvents(result);
        
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

    private static void AddHolidayEvents(EventReportData result)
    {
        var holiday = HolidayDefinitions.FirstOrDefault(x =>
        {
            var range = x.GetRange(result.Date.Year);
            return result.Date.Date >= range.Start.ToDateTime(TimeOnly.MinValue).Date &&
                   result.Date.Date <= range.End.ToDateTime(TimeOnly.MinValue).Date;
        });

        if (holiday is null)
            return;

        result.Events.Insert(0, new EventReportItem
        {
            Type = "Holiday",
            Content = holiday.Text,
            SpecialSound = holiday.SoundTag
        });
    }

    private sealed record DateRange(DateOnly Start, DateOnly End);

    private sealed record HolidayDefinition(
        string Text,
        string SoundTag,
        Func<int, DateRange> GetRange);

    private static readonly HolidayDefinition[] HolidayDefinitions =
    [
        new("New Year's Day", "event-new-year", year => SingleDay(year, 1, 1)),
        new("Martin Luther King Jr. Day", "event-special", year => NthWeekday(year, 1, DayOfWeek.Monday, 3)),
        new("Presidents' Day", "event-special", year => NthWeekday(year, 2, DayOfWeek.Monday, 3)),
        new("Memorial Day", "event-special", year => LastWeekday(year, 5, DayOfWeek.Monday)),
        new("Juneteenth", "event-special", year => SingleDay(year, 6, 19)),
        new("Independence Day", "event-fourth-july", year => SingleDay(year, 7, 4)),
        new("Labor Day", "event-special", year => NthWeekday(year, 9, DayOfWeek.Monday, 1)),
        new("Columbus Day", "event-special", year => NthWeekday(year, 10, DayOfWeek.Monday, 2)),
        new("Veterans Day", "event-special", year => SingleDay(year, 11, 11)),
        new("Thanksgiving Day", "event-special", year => NthWeekday(year, 11, DayOfWeek.Thursday, 4)),
        new("Christmas Day", "event-christmas", year => SingleDay(year, 12, 25)),

        new(
            "Tomorrow starts Daylight Saving Time in the US. If you are in a state that observes Daylight Saving Time, be sure to move your clocks one hour ahead.",
            "event-special",
            year => OffsetRange(DaylightSavingStart(year), -1)),

        new(
            "Today starts Daylight Saving Time in the US. If you are in a state that observes Daylight Saving Time, be sure to move your clocks one hour ahead.",
            "event-special",
            year => SingleDayRange(DaylightSavingStart(year))),

        new(
            "Tomorrow ends Daylight Saving Time in the US. If you are in a state that observes Daylight Saving Time, be sure to move your clocks one hour back.",
            "event-special",
            year => OffsetRange(DaylightSavingEnd(year), -1)),

        new(
            "Today ends Daylight Saving Time in the US. If you are in a state that observes Daylight Saving Time, be sure to move your clocks one hour back.",
            "event-special",
            year => SingleDayRange(DaylightSavingEnd(year)))
    ];

    private static DateRange SingleDay(int year, int month, int day) =>
        new(new DateOnly(year, month, day), new DateOnly(year, month, day));

    private static DateRange SingleDayRange(DateOnly date) => new(date, date);

    private static DateRange OffsetRange(DateOnly date, int offsetDays)
    {
        var adjusted = date.AddDays(offsetDays);
        return new(adjusted, adjusted);
    }

    private static DateRange NthWeekday(int year, int month, DayOfWeek day, int occurrence)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)day - (int)first.DayOfWeek + 7) % 7;
        var date = first.AddDays(offset + ((occurrence - 1) * 7));
        return SingleDayRange(date);
    }

    private static DateRange LastWeekday(int year, int month, DayOfWeek day)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)last.DayOfWeek - (int)day + 7) % 7;
        return SingleDayRange(last.AddDays(-offset));
    }

    private static DateOnly DaylightSavingStart(int year)
    {
        var date = new DateOnly(year, 3, 1);
        var offset = ((int)DayOfWeek.Sunday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(offset + 7);
    }

    private static DateOnly DaylightSavingEnd(int year)
    {
        var date = new DateOnly(year, 11, 1);
        var offset = ((int)DayOfWeek.Sunday - (int)date.DayOfWeek + 7) % 7;
        return date.AddDays(offset);
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
