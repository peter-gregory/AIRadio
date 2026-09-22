using AIRadio.Server.Models.Alarms;
using AIRadio.Server.Models.Tools;
using Newtonsoft.Json.Linq;

namespace AIRadio.Server.Services.Alarms;

public sealed class ExclusionTool : ITool
{
    private readonly IAlarmService _alarmService;
    public ExclusionTool(IAlarmService alarmService) => _alarmService = alarmService;

    public bool HasParameters => true;
    public string Name => "exclude";
    public string Intent => "Exclude one or more alarm occurrences.";
    public string GetLlmRequestTemplate() => "{tool:exclude,alarmId=!required!,when=!required!}";

    public string GetLlmInstructions() => """
EXCLUDE TOOL
Exclude an occurrence or recurrence from an existing alarm.
Format: {tool:exclude,alarmId=<alarm id>,when=<complete date/time or recurrence expression>}
The when value is the complete human expression. Do not split it into date, recurrence, weekday, or other fields.
Examples:
{tool:exclude,alarmId=<known-id>,when="September 30"}
{tool:exclude,alarmId=<known-id>,when="every Monday"}
{tool:exclude,alarmId=<known-id>,when="September 30 through October 4"}
Only use an alarm ID returned by the events tool. Never invent an ID.
""";

    public Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var idText = request.GetString("alarmId");
        var when = request.GetString("when");

        if (string.IsNullOrWhiteSpace(idText))
            return Task.FromResult(Missing(request, "alarmId", "Which alarm should I exclude?"));

        if (!Guid.TryParse(idText, out var id))
            return Task.FromResult(ToolResult.Failed(Name, $"Invalid alarm ID '{idText}'."));

        if (string.IsNullOrWhiteSpace(when))
            return Task.FromResult(Missing(request, "when", "When should I exclude the alarm?"));

        try
        {
            var range = RecurrenceTimeRangeParser.Parse(when);
            var exclusion = ToExclusion(range);
            if (!_alarmService.AddExclusion(id, exclusion))
                return Task.FromResult(ToolResult.Failed(Name, $"Alarm '{id}' was not found."));

            return Task.FromResult(ToolResult.Successful(Name, "Alarm occurrence excluded.", exclusion));
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

    private static ExclusionRule ToExclusion(RecurrenceTimeRange range)
    {
        if (range.Type == SchedulePatternType.Weekly && range.DaysOfWeek.Length > 0)
            return new ExclusionRule
            {
                Type = ExclusionRuleType.Weekly,
                DaysOfWeek = [.. range.DaysOfWeek]
            };

        if (range.Type == SchedulePatternType.Monthly)
            return new ExclusionRule
            {
                Type = ExclusionRuleType.Monthly,
                Month = range.Month,
                DayOfMonth = range.DayOfMonth,
                WeekOfMonth = range.WeekOfMonth,
                WeekdayOfMonth = range.WeekdayOfMonth
            };

        if (range.Type == SchedulePatternType.Yearly)
            return new ExclusionRule
            {
                Type = ExclusionRuleType.Yearly,
                Month = range.Month,
                DayOfMonth = range.DayOfMonth,
                WeekOfMonth = range.WeekOfMonth,
                WeekdayOfMonth = range.WeekdayOfMonth
            };

        return new ExclusionRule
        {
            Type = ExclusionRuleType.DateRange,
            StartDate = range.StartDate,
            EndDate = range.EndDate
        };
    }
}
