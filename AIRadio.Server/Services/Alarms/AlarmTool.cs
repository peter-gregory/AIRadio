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
The actions value is the complete human-language action expression. Keep it intact.
The when value is the complete human date/time expression. Keep it intact.

Format: {tool:alarm,actions=<complete action expression>,when=<complete date/time expression>}

Examples:
{tool:alarm,actions="wake me up",when="tomorrow at 7:00 AM"}
{tool:alarm,actions="speak a greeting, play the news, weather and any events, then play lightning 100",when="6:30 AM"}
{tool:alarm,actions="turn off the oven",when="in 5 minutes"}

Relative durations such as "in 5 minutes" are egg-timer alarms and must be kept intact.
Do not split actions into tool names or parameters.
Do not split when into date, time, recurrence, weekday, or other fields.
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
            var actions = AlarmActionsParser.Parse(actionsText);
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
