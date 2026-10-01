using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Tools;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Alarms;

public sealed class EventAddTool : ITool
{
    private readonly IAlarmService _alarmService;

    public EventAddTool(IAlarmService alarmService) => _alarmService = alarmService;

    public bool HasParameters => true;
    public string Name => "event";
    public string Intent => "Create a scheduled event or reminder.";
    public string GetLlmRequestTemplate() => "{tool:event,content=!required!,when=!required!}";

    public string GetLlmInstructions() => """
EVENT TOOL
Create a scheduled event or reminder.

ARGUMENTS
- content: the complete human-language text describing what AIRadio should remember or say when the event occurs. Preserve the user's wording.
- when: the complete human date/time or recurrence expression. Keep it intact.

Format: {tool:event,content=<complete content expression>,when=<complete date/time expression>}

Examples:
{tool:event,content="It's your birthday, celebrate",when="October ninth"}
{tool:event,content="Take out the trash",when="tomorrow"}
{tool:event,content="Check the pool",when="every Monday Wednesday and Friday"}
{tool:event,content="Check the meter",when="the first Thursday of every month"}

The content value is the event's speech/reminder text. Do not replace it with words such as "add an event" or "remind me".
The when value is not a set of date/time fields. Do not split it into date, time, recurrence, weekday, or other fields.
Relative expressions must remain complete.
""";

    public string GetLlmResponseInstructions() => """
EVENT RESPONSE
Confirm only that the event was added and when it will occur.

- Do not repeat or describe the event content unless necessary.
- Use the event's When structure to express the scheduled date or recurrence naturally.
- Keep the response to one short sentence.
""";

    public Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Keep the complete collection of missing arguments in the pending
        // request. Ask for one value at a time in a deterministic order.
        // The remaining !required! values survive each continuation.
        var missing = request.MissingRequiredArguments;

        if (missing.Contains("when", StringComparer.OrdinalIgnoreCase))
            return Task.FromResult(Missing(request, "when", "When should I remember it?"));

        if (missing.Contains("content", StringComparer.OrdinalIgnoreCase))
            return Task.FromResult(Missing(request, "content", "What should I say?"));

        var content = request.GetString("content");
        var when = request.GetString("when");

        // Also handle missing values represented as null/empty rather than
        // !required!, which can occur after a continuation has been parsed.
        if (string.IsNullOrWhiteSpace(when))
            return Task.FromResult(Missing(request, "when", "When should I remember it?"));

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(Missing(request, "content", "What should I say?"));

        try
        {
            var range = RecurrenceTimeRangeParser.Parse(when);

            var scheduled = new ScheduledEvent
            {
                Id = Guid.NewGuid(),
                Type = ScheduledEventType.Reminder,
                Content = content,
                When = range,
                Enabled = true,
                CreatedAt = DateTime.Now
            };

            var added = _alarmService.AddEvent(scheduled);
            var confirmation = FormatConfirmation(range);

            return Task.FromResult(
                ToolResult.Successful(
                    Name,
                    "Event added.",
                    added,
                    exactPrompt: confirmation,
                    complete: true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failed(Name, ex.Message));
        }
    }

    private static string FormatConfirmation(RecurrenceTimeRange range)
    {
        return range.Type switch
        {
            SchedulePatternType.Once when range.StartDate.HasValue =>
                $"Okay, I'll remember that on {FormatDate(range.StartDate.Value)}.",

            SchedulePatternType.Daily =>
                "Okay, I'll remember that every day.",

            SchedulePatternType.Weekly when range.DaysOfWeek.Count > 0 =>
                $"Okay, I'll remember that every {FormatDays(range.DaysOfWeek)}.",

            SchedulePatternType.Monthly when range.WeekOfMonth.HasValue &&
                                             range.WeekdayOfMonth.HasValue =>
                $"Okay, I'll remember that on the {FormatOrdinal(range.WeekOfMonth.Value)} {range.WeekdayOfMonth.Value} of every month.",

            SchedulePatternType.Monthly when range.DayOfMonth.HasValue =>
                $"Okay, I'll remember that on the {FormatOrdinal(range.DayOfMonth.Value)} of every month.",

            SchedulePatternType.Yearly when range.Month.HasValue &&
                                            range.DayOfMonth.HasValue =>
                $"Okay, I'll remember that on {new DateTime(2000, range.Month.Value, range.DayOfMonth.Value):MMMM d} every year.",

            _ => "Okay, I'll remember that."
        };
    }

    private static string FormatDate(DateOnly date)
    {
        return $"{date:MMMM} {date.Day}";
    }

    private static string FormatDays(IReadOnlyList<DayOfWeek> days)
    {
        var names = days.Select(x => x.ToString()).ToArray();

        return names.Length switch
        {
            0 => "the scheduled days",
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names[..^1]) + $", and {names[^1]}"
        };
    }

    private static string FormatOrdinal(int value)
    {
        if (value == -1)
            return "last";

        return value switch
        {
            1 => "1st",
            2 => "2nd",
            3 => "3rd",
            _ => $"{value}th"
        };
    }

    private ToolResult Missing(
        ToolRequest request,
        string parameter,
        string prompt)
    {
        // Preserve every missing argument. Only the selected parameter is
        // requested on this turn; the continuation retains the complete
        // collection for subsequent turns.
        var pending = new ToolRequest
        {
            Name = Name,
            Arguments = (JObject)request.Arguments.DeepClone(),
            State = ToolRequestState.ArgumentParsing
        };

        pending.Arguments[parameter] = ToolRequest.RequiredValue;
        return ToolResult.MissingParameter(Name, prompt, pending);
    }
}
