using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Tools;
using AIRadio.Server.Services.Alarms;

namespace AIRadio.Server.Services.Events;

public sealed class EventTool : ITool
{
    private readonly IAlarmService _alarmService;
    private readonly IStaticEventCalendar _staticEventCalendar;

    public EventTool(IAlarmService alarmService, IStaticEventCalendar staticEventCalendar)
    {
        _alarmService = alarmService;
        _staticEventCalendar = staticEventCalendar;
    }

    public bool HasParameters => true;
    public string GetLlmRequestTemplate() => "{tool:events,timestamp=<optional>,includeAlarms=<optional>,includeReminders=<optional>}";
    public string Name => "events";
    public string Intent => "Retrieve scheduled events and calendar observances.";

    public string GetLlmInstructions() => """
EVENTS
Retrieve scheduled events and calendar observances.

Parameters:
- timestamp: optional date/time to inspect.
- includeAlarms: optional; defaults to true.
- includeReminders: optional; defaults to true.

IMPORTANT:
- Only provide timestamp when the user explicitly specifies a date or time.
- If the user says "what are the events", "what events do I have", "tell me my events", or otherwise asks for the events without specifying a date, leave timestamp omitted. Do not invent a date.
- When timestamp is omitted, the tool reports today's scheduled events and calendar observances.
- Use for scheduled events and requests such as "what's happening today" when the user means events or calendar observances.
"Events report" means this tool; report is not a separate tool.

Examples:
"What are the events?" -> {tool:events}
"What events do I have?" -> {tool:events}
"What are my events tomorrow?" -> {tool:events,timestamp="tomorrow"}
"What is on my schedule Friday?" -> {tool:events,timestamp="Friday"}
""";

    public string GetLlmResponseInstructions() => """
EVENTS REPORT RESPONSE
- Report only scheduled events and calendar observances contained in the tool result.
- Do not invent events, dates, holidays, observances, or other information.
- Do not reinterpret the date in the tool result.
- Be concise and natural for speech.
- Do not use numbered lists or headings.
- If there are multiple events, speak each event clearly in a natural sequence.
- Begin each individual event segment with the exact speech sound tag {sound:event-button}.
- Place {sound:event-button} immediately before the spoken text for each event.
- The tag must appear before every event, including the first and last event.
- Never invent sound tags such as {sound:event-reminder}; only use sound tags supplied by the tool result.
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
        var scheduledEvents = _alarmService.GetEvents(timestamp);

        var result = new EventReportData
        {
            Date = reportDate,
            Events = scheduledEvents
                .Where(x =>
                    (includeAlarms && x.Type == ScheduledEventType.Alarm) ||
                    (includeReminders && x.Type == ScheduledEventType.Reminder))
                .Select(MapEvent)
                .ToList()
        };

        AddStaticEvents(result);

        if (result.Events.Count == 0)
        {
            var noEventsPrompt = timestamp?.Date == DateTime.Now.Date
                ? "There are no events for today"
                : $"There are no events for {result.Date:MMMM d}";

            return Task.FromResult(
                ToolResult.Successful(
                    Name,
                    "Found 0 event(s).",
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
                $"Found {result.Events.Count} event(s).",
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

    private void AddStaticEvents(EventReportData result)
    {
        var staticEvents = _staticEventCalendar.GetEvents(DateOnly.FromDateTime(result.Date));

        result.Events.InsertRange(
            0,
            staticEvents.Select(x => new EventReportItem
            {
                Id = x.Id,
                Type = FormatCategory(x.Category),
                Content = x.Content,
                SpecialSound = x.Sound
            }));
    }

    private static string FormatCategory(string category) =>
        category switch
        {
            "holiday" => "Holiday",
            "traditional" => "Traditional Observance",
            "nationalDay" => "National Day",
            "awarenessMonth" => "Awareness Month",
            "awarenessWeek" => "Awareness Week",
            "international" => "International Observance",
            "seasonal" => "Seasonal Observance",
            _ => category
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
    public string? SpecialSound { get; init; }
}
