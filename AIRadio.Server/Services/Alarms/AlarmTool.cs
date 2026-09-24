using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Tools;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Alarms;

public sealed class AlarmTool : ITool
{
    private readonly IAlarmService _alarmService;

    public AlarmTool(IAlarmService alarmService) => _alarmService = alarmService;

    public bool HasParameters => true;
    public string Name => "alarm";
    public string Intent => "Create a new alarm.";
    public string GetLlmRequestTemplate() => "{tool:alarm,actions=!required!,when=!required!}";

    public string GetLlmInstructions() => """
ALARM TOOL
Create an alarm that performs one or more actions in order.

ARGUMENTS
- actions: the complete human-language description of everything the alarm should do. Keep the entire action sequence intact.
- when: the complete human date/time or recurrence expression. Keep it intact.

Format: {tool:alarm,actions=<complete action expression>,when=<complete time expression>}

Examples:
{tool:alarm,actions="speak a greeting, get the news, get the weather, then play lightning 100",when="6:30 AM"}
{tool:alarm,actions="turn off the oven",when="in 5 minutes"}

The actions value is not a list of tool calls. Do not convert actions into tool names or parameters.
Preserve every requested action. Do not omit actions joined by "and", commas, or other conjunctions.
For example, "play the news, weather and any events" contains all three requested actions.
The when value is not a set of date/time fields. Do not split it into date, time, recurrence, weekday, or other fields.
Relative durations such as "in 5 minutes" or "5 minutes from now" must remain complete.
""";

    public string GetLlmResponseInstructions() => """
ALARM RESPONSE
Confirm only when the alarm will activate.

- Do not mention or describe the alarm actions.
- Do not repeat the user's request.
- Use the alarm's When structure to express the activation time naturally.
- For a one-shot timer, use the Due At value and give the local time.
- For a one-shot date/time, give the date and time when needed.
- For a recurring alarm, include the recurrence and time when needed.
- Prefer natural spoken time such as "3:37 PM" rather than a 24-hour timestamp.
- Keep the response to one short sentence.
""";

    public Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var actionsText = request.GetString("actions");
        var when = request.GetString("when");

        if (string.IsNullOrWhiteSpace(actionsText))
            return Task.FromResult(Missing(request, "actions", "What should the alarm do?"));

        if (string.IsNullOrWhiteSpace(when))
            return Task.FromResult(Missing(request, "when", "When should the alarm go off?"));

        try
        {
            var actions = AlarmActionsParser.Parse(actionsText).ToList();
            var range = RecurrenceTimeRangeParser.Parse(when);

            if (actions.Count == 0)
                return Task.FromResult(ToolResult.Failed(Name, "At least one alarm action is required."));

            if (!range.TimeOfDay.HasValue)
                return Task.FromResult(ToolResult.Failed(Name, "An alarm requires a time."));

            var scheduled = new ScheduledEvent
            {
                Id = Guid.NewGuid(),
                Type = ScheduledEventType.Alarm,
                Actions = actions,
                When = range,
                Enabled = true,
                CreatedAt = DateTime.Now
            };

            var added = _alarmService.AddEvent(scheduled);
            var confirmation = FormatConfirmation(range);

            return Task.FromResult(
                ToolResult.Successful(
                    Name,
                    "Alarm added.",
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
        var time = range.TimeOfDay.HasValue
            ? TimeSpeechFormatter.Format(range.TimeOfDay.Value)
            : string.Empty;

        return range.Type switch
        {
            SchedulePatternType.Once when range.DueAt.HasValue =>
                $"Okay, your alarm is set for {TimeSpeechFormatter.Format(range.DueAt.Value)}.",

            SchedulePatternType.Once when range.StartDate.HasValue &&
                                         range.StartDate.Value == DateOnly.FromDateTime(DateTime.Now) &&
                                         !string.IsNullOrEmpty(time) =>
                $"Okay, your alarm is set for {time}.",

            SchedulePatternType.Once when range.StartDate.HasValue && !string.IsNullOrEmpty(time) =>
                $"Okay, your alarm is set for {range.StartDate.Value:MMMM d} at {time}.",

            SchedulePatternType.Once when range.StartDate.HasValue =>
                $"Okay, your alarm is set for {range.StartDate.Value:MMMM d}.",

            SchedulePatternType.Daily when !string.IsNullOrEmpty(time) =>
                $"Okay, your alarm is set for every day at {time}.",

            SchedulePatternType.Weekly when !string.IsNullOrEmpty(time) =>
                $"Okay, your alarm is set for every {FormatDays(range.DaysOfWeek)} at {time}.",

            SchedulePatternType.Monthly when range.WeekOfMonth.HasValue &&
                                             range.WeekdayOfMonth.HasValue &&
                                             !string.IsNullOrEmpty(time) =>
                $"Okay, your alarm is set for {FormatOrdinal(range.WeekOfMonth.Value)} {range.WeekdayOfMonth.Value} of every month at {time}.",

            SchedulePatternType.Monthly when range.DayOfMonth.HasValue &&
                                             !string.IsNullOrEmpty(time) =>
                $"Okay, your alarm is set for the {FormatOrdinal(range.DayOfMonth.Value)} of every month at {time}.",

            SchedulePatternType.Yearly when range.Month.HasValue &&
                                            range.DayOfMonth.HasValue &&
                                            !string.IsNullOrEmpty(time) =>
                $"Okay, your alarm is set for {new DateTime(2000, range.Month.Value, range.DayOfMonth.Value):MMMM d} every year at {time}.",

            _ => "Okay, your alarm is set."
        };
    }

    private static string FormatDays(IReadOnlyList<DayOfWeek> days)
    {
        if (days.Count == 0)
            return "the scheduled days";

        var names = days.Select(x => x.ToString()).ToArray();
        return names.Length switch
        {
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

    private ToolResult Missing(ToolRequest request, string parameter, string prompt)
    {
        var pending = new ToolRequest
        {
            Name = Name,
            Arguments = (JObject)request.Arguments.DeepClone()
        };

        pending.Arguments[parameter] = ToolRequest.RequiredValue;
        return ToolResult.MissingParameter(Name, prompt, pending);
    }
}
