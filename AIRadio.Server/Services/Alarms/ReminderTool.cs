using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Tools;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Alarms;

public sealed class ReminderTool : ITool
{
    private readonly IAlarmService _alarmService;
    public ReminderTool(IAlarmService alarmService) => _alarmService = alarmService;

    public bool HasParameters => true;
    public string Name => "reminder";
    public string Intent => "Create a new reminder.";
    public string GetLlmRequestTemplate() => "{tool:reminder,content=!required!,when=!required!}";

    public string GetLlmInstructions() => """
REMINDER TOOL
Create a reminder. The when value is the complete human date/time or recurrence expression.
Format: {tool:reminder,content=<what to remember>,when=<complete date/time expression>}
Examples:
{tool:reminder,content="Take out the trash",when="tomorrow"}
{tool:reminder,content="Check the pool",when="every Monday Wednesday and Friday"}
{tool:reminder,content="Check the meter",when="the first Thursday of every month"}
Do not split when into date, time, recurrence, weekday, or other fields.
""";

    public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var content = request.GetString("content");
        var when = request.GetString("when");

        if (string.IsNullOrWhiteSpace(content))
            return Task.FromResult(Missing(request, "content", "What should I remind you about?"));

        if (string.IsNullOrWhiteSpace(when))
            return Task.FromResult(Missing(request, "when", "When should I remind you?"));

        try
        {
            var range = RecurrenceTimeRangeParser.Parse(when);
            var scheduled = new ScheduledEvent
            {
                Id = Guid.NewGuid(),
                Type = ScheduledEventType.Reminder,
                Content = content,
                Schedule = range.ToSchedulePattern(),
                Enabled = true,
                CreatedAt = DateTime.Now
            };

            var added = _alarmService.AddEvent(scheduled);
            return Task.FromResult(ToolResult.Successful(Name, "Reminder added.", added));
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
