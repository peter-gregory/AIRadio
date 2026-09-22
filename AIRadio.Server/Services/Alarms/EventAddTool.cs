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
The when value is the complete human date/time or recurrence expression.
Format: {tool:event,content=<what to remember>,when=<complete date/time expression>}
Examples:
{tool:event,content="Take out the trash",when="tomorrow"}
{tool:event,content="Check the pool",when="every Monday Wednesday and Friday"}
{tool:event,content="Check the meter",when="the first Thursday of every month"}
Do not split when into date, time, recurrence, weekday, or other fields.
""";

    public Task<ToolResult> ExecuteAsync(
        ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var content = request.GetString("content");
        var when = request.GetString("when");

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(Missing(request, "content", "What should I remember?"));

        if (string.IsNullOrWhiteSpace(when))
            return Task.FromResult(Missing(request, "when", "When should I remember it?"));

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
            return Task.FromResult(ToolResult.Successful(Name, "Event added.", added));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Failed(Name, ex.Message));
        }
    }

    private ToolResult Missing(
        ToolRequest request,
        string parameter,
        string prompt)
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
