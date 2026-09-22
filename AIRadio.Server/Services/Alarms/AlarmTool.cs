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
    public string GetLlmRequestTemplate() => "{tool:alarm,content=!required!,when=!required!}";

    public string GetLlmInstructions() => """
ALARM TOOL
Create an alarm. The when value is the complete human date/time expression. Keep it intact.
Format: {tool:alarm,content=<what to say>,when=<complete date/time expression>}
Examples:
{tool:alarm,content="Wake me up",when="tomorrow at 7:00 AM"}
{tool:alarm,content="Take the trash out",when="every weekday at 8:00 AM"}
Do not split when into date, time, recurrence, weekday, or other fields.
""";

    public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var content = request.GetString("content");
        var when = request.GetString("when");

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(Missing(request, "content", "What should the alarm say?"));

        if (string.IsNullOrWhiteSpace(when))
            return Task.FromResult(Missing(request, "when", "When should the alarm go off?"));

        try
        {
            var range = RecurrenceTimeRangeParser.Parse(when);
            if (!range.TimeOfDay.HasValue)
                return Task.FromResult(ToolResult.Failed(Name, "An alarm requires a time."));

            var scheduled = new ScheduledEvent
            {
                Id = Guid.NewGuid(),
                Type = ScheduledEventType.Alarm,
                Content = content,
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
        var pending = new ToolRequest { Name = Name, Arguments = (JObject)request.Arguments.DeepClone() };
        pending.Arguments[parameter] = ToolRequest.RequiredValue;
        return ToolResult.MissingParameter(Name, prompt, pending);
    }

}
