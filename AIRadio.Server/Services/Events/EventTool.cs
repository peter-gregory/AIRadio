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
- Be conversational and natural for speech rather than reading calendar labels like a list.
- Static calendar observances include a short, friendly sentence explaining their meaning or a natural way to recognize the day.
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

                return eventItem.IsStatic
                    ? $"{{sound:event-button}}{specialSound}{eventItem.Content}"
                    : $"{{sound:event-button}}{specialSound}{eventItem.Type}{timeText}: {eventItem.Content}";
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
                Content = BuildStaticSpeech(x.Category, x.Content),
                SpecialSound = x.Sound,
                IsStatic = true
            }));
    }

    private static string BuildStaticSpeech(string category, string content)
    {
        if (category == "awarenessMonth")
            return $"Today is {content}. It is a chance to learn more, raise awareness, and take part in the conversation.";

        if (category == "awarenessWeek")
            return $"This week is {content}. It is a chance to learn more and recognize the people and ideas that make the week meaningful.";

        if (category == "seasonal")
            return content;

        if (StaticSpeech.TryGetValue(content, out var speech))
            return $"Today is {content}. {speech}";

        return category switch
        {
            "nationalDay" => $"Today is {content}. It is a fun opportunity to celebrate and enjoy what makes the day special.",
            "international" => $"Today is {content}. It is a day to recognize the importance of this cause and the people it touches around the world.",
            "holiday" => $"Today is {content}. It is a time to pause, celebrate, and reflect on what the day represents.",
            "traditional" => $"Today is {content}. It is a tradition that gives us a chance to celebrate and enjoy the day.",
            _ => $"Today is {content}."
        };
    }

    private static readonly IReadOnlyDictionary<string, string> StaticSpeech =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["National Coffee Day"] = "Celebrate with a hot cup of coffee and enjoy a moment to savor one of the world's favorite drinks.",
            ["National Pizza Day"] = "Celebrate with a slice of your favorite pizza and enjoy a simple meal that brings people together.",
            ["National Hot Dog Day"] = "Celebrate with a classic hot dog and enjoy one of America's favorite summer foods.",
            ["National Ice Cream Day"] = "Celebrate with a scoop of your favorite ice cream and make the day a little sweeter.",
            ["National Doughnut Day"] = "Treat yourself to a doughnut and enjoy this fun celebration of a classic favorite.",
            ["National Popcorn Day"] = "Grab some popcorn and enjoy a movie, a game, or just a tasty snack.",
            ["National Chocolate Chip Cookie Day"] = "Celebrate with a warm chocolate chip cookie and enjoy a classic sweet treat.",
            ["National Ice Cream Sandwich Day"] = "Cool off with an ice cream sandwich and enjoy a fun combination of cookies and ice cream.",
            ["National Read a Book Day"] = "Take some time to pick up a good book and enjoy a few quiet moments of reading.",
            ["National Family Day"] = "Take a little time to connect with the people you care about and enjoy being together.",
            ["National Aviation Day"] = "It is a chance to appreciate the history of flight and the people who helped make modern aviation possible.",
            ["National Women's Day"] = "It is a chance to recognize the contributions and achievements of women and celebrate the women who make a difference every day.",
            ["International Day of Women and Girls in Science"] = "It highlights the contributions of women and girls in science and encourages more opportunities to explore STEM.",
            ["International Day of Mathematics"] = "It celebrates mathematics and the many ways it helps us understand and solve problems in the world around us.",
            ["World Water Day"] = "It reminds us how important clean and accessible water is to people, communities, and the environment.",
            ["World Health Day"] = "It is an opportunity to reflect on health and the importance of making good health accessible to everyone.",
            ["World Book and Copyright Day"] = "It celebrates books, reading, and the creativity of authors and other people who bring ideas to life.",
            ["World Press Freedom Day"] = "It highlights the importance of independent journalism and the ability to seek and share information.",
            ["International Day of Families"] = "It celebrates families and the important role they play in our communities and everyday lives.",
            ["World Environment Day"] = "It is a chance to think about the environment and the small things we can do to take care of our planet.",
            ["World Oceans Day"] = "It reminds us how important the oceans are and encourages us to help protect marine life and ocean ecosystems.",
            ["World Refugee Day"] = "It recognizes refugees around the world and highlights the importance of safety, dignity, and support for people forced to flee their homes.",
            ["International Day of Friendship"] = "It is a day to appreciate friendship and the connections that bring people and communities together.",
            ["International Youth Day"] = "It highlights the ideas, energy, and contributions of young people around the world.",
            ["World Humanitarian Day"] = "It recognizes humanitarian workers and the people who help communities facing crises and emergencies.",
            ["International Literacy Day"] = "It celebrates the importance of reading and writing and the opportunities that literacy can open for people.",
            ["International Day of Democracy"] = "It highlights the importance of participation, representation, and the principles that support democratic societies.",
            ["International Day of Peace"] = "It is a day to reflect on peace and the importance of resolving differences without violence.",
            ["World Maritime Day"] = "It highlights the importance of shipping and the people who keep the world's maritime transportation moving.",
            ["International Day for Universal Access to Information"] = "It recognizes the importance of being able to find and access information and understand the world around us.",
            ["International Day of Awareness of Food Loss and Waste"] = "It reminds us that reducing food loss and waste can help conserve resources and make better use of the food we produce.",
            ["International Translation Day"] = "It recognizes the important role translation plays in bringing languages, cultures, and people together.",
            ["International Day of Older Persons"] = "It celebrates older people and highlights their contributions, experiences, and place in our communities.",
            ["International Day of Non-Violence"] = "It encourages peaceful ways of resolving differences and reflects on the value of non-violence.",
            ["World Teachers' Day"] = "It is a chance to appreciate teachers and the important role they play in helping people learn and grow.",
            ["World Habitat Day"] = "It reminds us of the importance of safe, healthy, and sustainable places for people to live.",
            ["World Mental Health Day"] = "It encourages conversations about mental health and reminds us that emotional well-being is an important part of overall health.",
            ["International Day of the Girl Child"] = "It highlights the rights, opportunities, and potential of girls around the world.",
            ["World Food Day"] = "It reminds us of the importance of food security and access to nutritious food for everyone.",
            ["International Day for the Eradication of Poverty"] = "It draws attention to poverty and the importance of creating opportunities for people to build secure and productive lives.",
            ["United Nations Day"] = "It marks the anniversary of the United Nations and highlights international cooperation on shared global challenges.",
            ["World Cities Day"] = "It is a chance to think about how cities can become more inclusive, resilient, and sustainable places to live.",
            ["World AIDS Day"] = "It raises awareness about HIV and AIDS, remembers people affected by the disease, and encourages continued support and education.",
            ["International Day of Persons with Disabilities"] = "It highlights inclusion, accessibility, and the contributions of people with disabilities.",
            ["Human Rights Day"] = "It reminds us of the importance of dignity, equality, and fundamental rights for every person.",
            ["International Migrants Day"] = "It recognizes the contributions of migrants and the experiences of people who move across borders in search of safety or opportunity."
        };

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
    public bool IsStatic { get; init; }
}
