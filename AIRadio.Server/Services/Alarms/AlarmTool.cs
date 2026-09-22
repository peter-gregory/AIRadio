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
            return Task.FromResult(ToolResult.Successful(Name, "Alarm added.", added));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failed(Name, ex.Message));
        }
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
